using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Controls;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using SimHub.Plugins.Devices;
using SimHub.Plugins.Devices.DeviceExtensions;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// SimHub device extension for MOZA wheels.
    /// Injects a fake LED device driver so SimHub's effects UI works,
    /// then bridges computed LED colors to MOZA hardware via the plugin's serial protocol.
    /// Also provides per-game profile persistence through GetSettings()/SetSettings().
    /// </summary>
    internal class MozaWheelDeviceExtension : DeviceExtension
    {
        private MozaWheelExtensionSettings _settings = new MozaWheelExtensionSettings();
        private MozaLedDeviceManager? _ledDriver;
        private bool _driverInjected;
        // Last applied button count, used to detect when the source-of-truth
        // WheelModelInfo has changed and a fresh apply is needed. -1 = never
        // applied. Replaces a prior bool latch that one-shot-set the count
        // from whatever wheel happened to be attached during the first
        // injection, then never updated — produced cross-wheel-extension
        // pollution: the KS extension would latch with the W17's 8-button
        // count if the W17 happened to be attached at that moment, and a
        // later hot-swap to a KS left the SimHub effects pipeline pushing
        // 8 button updates to a 10-button wheel.
        private int _lastAppliedButtonsCount = -1;
        private int _lastAppliedEncodersCount = -1;

        /// <summary>
        /// Expected wheel model prefix resolved from the DeviceTypeID.
        /// Null = unknown device (won't connect). Empty = generic (any wheel).
        /// </summary>
        private string? _expectedModelPrefix;

        public override string ExtentionTabTitle => "MOZA Wheel";

        public override void Init(PluginManager pluginManager)
        {
            // Resolve which wheel model this device instance represents
            var typeId = LinkedDevice.DeviceDescriptor.DeviceTypeID ?? "";
            _expectedModelPrefix = MozaDeviceConstants.GetWheelModelPrefix(typeId);

            MozaLog.Debug(
                $"[AZOM] WheelDeviceExtension Init — DeviceTypeID={typeId}, modelPrefix={_expectedModelPrefix ?? "(null)"}");

            // Injection is deferred to DataUpdate() — calling it here would run before
            // LedModuleDevice.SetSettings(), causing a KeyNotFoundException in that call.

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
            {
                plugin.DeviceExtensionActive = true;
                if (_expectedModelPrefix != null && _expectedModelPrefix.Length > 0)
                    plugin.RegisterActiveModelPrefix(_expectedModelPrefix);
            }

            // Report active only when the matching wheel model is connected
            pluginManager.AttachDelegate(
                LinkedDevice.DeviceDescriptor.Name + "_MozaWheelActive",
                this.GetType(),
                () => _ledDriver?.IsConnected() ?? false);
        }

        /// <summary>
        /// Find the LedModuleDevice sub-device and replace its DeviceDriver
        /// with our MozaLedDeviceManager that always reports connected.
        /// This enables SimHub's LED effects configuration UI.
        /// </summary>
        private void InjectLedDriver()
        {
            if (_driverInjected) return;

            try
            {
                foreach (var instance in LinkedDevice.GetInstances())
                {
                    if (instance is LedModuleDevice lmd && lmd.ledModuleSettings != null)
                    {
                        _ledDriver = new MozaLedDeviceManager();
                        _ledDriver.ExpectedModelPrefix = _expectedModelPrefix;
                        _ledDriver.LedModuleSettings = lmd.ledModuleSettings;

                        // DeviceDriver setter is protected — use reflection
                        var prop = typeof(LedModuleSettings).GetProperty(
                            "DeviceDriver",
                            BindingFlags.Public | BindingFlags.Instance);

                        if (prop?.GetSetMethod(nonPublic: true) != null)
                        {
                            prop.GetSetMethod(nonPublic: true)!.Invoke(lmd.ledModuleSettings, new object[] { _ledDriver });
                            _driverInjected = true;

                            // Expose button LEDs — count is THIS extension's model
                            // (resolved from DeviceTypeID), not whichever wheel happens
                            // to be attached right now. See ResolveModelInfo().
                            var modelInfoNow = ResolveModelInfo();
                            if (modelInfoNow != null)
                                ApplyCounts(lmd.ledModuleSettings, modelInfoNow);

                            var plugin = MozaPlugin.Instance;
                            if (plugin != null)
                                plugin.DeviceExtensionActive = true;

                            MozaLog.Debug("[AZOM] Injected virtual LED driver — effects UI should be available");
                        }
                        else
                        {
                            MozaLog.Warn("[AZOM] Could not find DeviceDriver setter on LedModuleSettings");
                        }
                        return;
                    }
                }
                MozaLog.Debug("[AZOM] No LedModuleDevice found on device instance");
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM] Error injecting LED driver: {ex.Message}");
            }
        }

        public override void End(PluginManager pluginManager)
        {
            pluginManager.DetachDelegate(
                LinkedDevice.DeviceDescriptor.Name + "_MozaWheelActive",
                this.GetType());

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
            {
                plugin.DeviceExtensionActive = false;
                if (_expectedModelPrefix != null)
                    plugin.UnregisterActiveModelPrefix(_expectedModelPrefix);
                MozaLog.Debug("[AZOM] Device extension ended");
            }

            // Drop the LED driver from the static instance registry so it isn't
            // retained for the process lifetime after the device detaches; reset
            // the latch so a re-attach re-injects a fresh driver.
            try { _ledDriver?.Close(); } catch { }
            _ledDriver = null;
            _driverInjected = false;
        }

        public override void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // LED forwarding happens in MozaLedDeviceManager.Display() —
            // SimHub calls it directly as part of its LED pipeline.
            // Inject here (not Init) so LedModuleDevice.SetSettings() has already run.
            if (!_driverInjected)
                InjectLedDriver();

            // Notify SimHub when detection state changes so it resumes/pauses Display() calls
            _ledDriver?.UpdateConnectionState();

            // Re-apply counts whenever the resolved WheelModelInfo's layout
            // changes. For known-model extensions the resolved info is fixed
            // (so this is at most a single apply per session). For generic /
            // __old__ extensions the resolved info changes with the currently
            // attached wheel, so this loop handles wheel hot-swap re-binding
            // without a separate watchdog.
            if (_driverInjected)
            {
                var modelInfo = ResolveModelInfo();
                if (modelInfo != null
                    && (_lastAppliedButtonsCount != modelInfo.ButtonLedCount
                        || _lastAppliedEncodersCount != modelInfo.KnobCount))
                {
                    foreach (var instance in LinkedDevice.GetInstances())
                    {
                        if (instance is LedModuleDevice lmd && lmd.ledModuleSettings != null)
                        {
                            ApplyCounts(lmd.ledModuleSettings, modelInfo);
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Pick the right <see cref="WheelModelInfo"/> for this extension:
        /// for known-model extensions (DeviceTypeID resolves to a wheel
        /// prefix in <see cref="WheelModelInfo.KnownModels"/>) resolve
        /// statically from the prefix so the per-model extension always
        /// reflects ITS model's layout regardless of which wheel is
        /// currently attached. For generic / <c>__old__</c> extensions
        /// (prefix is null, empty, or the old-protocol marker), defer to
        /// the plugin's currently-detected wheel — that's the only signal
        /// we have for those device types.
        /// </summary>
        private WheelModelInfo? ResolveModelInfo()
        {
            if (!string.IsNullOrEmpty(_expectedModelPrefix)
                && _expectedModelPrefix != MozaDeviceConstants.OldProtocolMarker)
            {
                var info = WheelModelInfo.FromModelName(_expectedModelPrefix!);
                // FromModelName returns Default for unrecognized prefixes —
                // treat that as "no static match" and fall through to the
                // plugin's runtime info so we still get a reasonable layout.
                if (!ReferenceEquals(info, WheelModelInfo.Default))
                    return info;
            }
            return MozaPlugin.Instance?.WheelModelInfo;
        }

        /// <summary>
        /// Apply ButtonsCount + EncodersCount to the LedModuleSettings and
        /// stamp the per-extension trackers so DataUpdate can detect later
        /// changes (only meaningful for the deferred-resolution case where
        /// the plugin's WheelModelInfo can change across a wheel hot-swap).
        /// </summary>
        private void ApplyCounts(LedModuleSettings lmd, WheelModelInfo modelInfo)
        {
            lmd.ButtonsCount = modelInfo.ButtonLedCount;
            if (modelInfo.KnobCount > 0)
                SetEncodersCount(lmd, modelInfo.KnobCount);
            _lastAppliedButtonsCount = modelInfo.ButtonLedCount;
            _lastAppliedEncodersCount = modelInfo.KnobCount;
            MozaLog.Debug(
                $"[AZOM] Set ButtonsCount={modelInfo.ButtonLedCount}, " +
                $"EncodersCount={modelInfo.KnobCount} for " +
                $"{(string.IsNullOrEmpty(_expectedModelPrefix) ? "(generic)" : _expectedModelPrefix)}");
        }

        public override void LoadDefaultSettings()
        {
            _settings = new MozaWheelExtensionSettings();

            var plugin = MozaPlugin.Instance;
            var settings = plugin?.Settings;
            if (plugin != null && settings != null)
            {
                var profile = settings.ProfileStore?.CurrentProfile;
                _settings.CaptureFromCurrent(settings, plugin.Data, profile, _expectedModelPrefix);
            }
        }

        public override JToken GetSettings()
        {
            var plugin = MozaPlugin.Instance;
            var settings = plugin?.Settings;
            if (plugin != null && settings != null)
            {
                var profile = settings.ProfileStore?.CurrentProfile;
                _settings.CaptureFromCurrent(settings, plugin.Data, profile, _expectedModelPrefix);
            }

            return JToken.FromObject(_settings);
        }

        public override void SetSettings(JToken settings, bool isDefault)
        {
            _settings = settings.ToObject<MozaWheelExtensionSettings>() ?? new MozaWheelExtensionSettings();

            if (!isDefault)
            {
                var plugin = MozaPlugin.Instance;
                if (plugin != null)
                    plugin.ApplyWheelExtensionSettings(_settings, _expectedModelPrefix);
            }
        }

        public override Control CreateSettingControl()
        {
            return new MozaWheelSettingsControl { LinkedLedDriver = _ledDriver };
        }

        public override IEnumerable<DynamicButtonAction> GetDynamicButtonActions()
        {
            yield break;
        }

        /// <summary>
        /// Set EncodersCount via reflection — property is private in SimHub's LedModuleSettings.
        /// </summary>
        private static void SetEncodersCount(LedModuleSettings settings, int count)
        {
            try
            {
                var prop = typeof(LedModuleSettings).GetProperty(
                    "EncodersCount",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                prop?.SetValue(settings, count);
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Could not set EncodersCount: {ex.Message}");
            }
        }
    }
}
