using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using SimHub.Plugins.Devices;
using SimHub.Plugins.Devices.DeviceExtensions;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;
using MozaPlugin.Devices.Haptics;
using MozaPlugin.Devices.Led;
using MozaPlugin.Devices.Ui;

namespace MozaPlugin.Devices.Extensions
{
    /// <summary>
    /// SimHub device extension for the MOZA wheelbase device. Injects a virtual
    /// LED driver so SimHub's effects UI works for the ambient telemetry strip
    /// (12 LEDs on an R16, 18 on R21/R25/R27 — see BaseModelInfo); the driver
    /// bridges computed colors to the base via group 0x20 / device 0x12.
    ///
    /// When the definition also declares HapticsFeature, the composite carries a
    /// StandardProtocolConnectionDevice whose manager is swapped for
    /// <see cref="MozaBaseConnectionManager"/> — that reports the wheelbase pipe's
    /// state (which gates the LED sub-device) and supplies the motors driver that
    /// receives ShakeIt's mixed output. See MozaBaseHapticsBridge.
    /// </summary>
    internal class MozaBaseDeviceExtension : DeviceExtension
    {
        private MozaBaseExtensionSettings _settings = new MozaBaseExtensionSettings();
        private MozaBaseLedDeviceManager? _ledDriver;
        private bool _driverInjected;
        // Injection target + SimHub's original driver, restored in End().
        private LedModuleSettings? _injectedSettings;
        private object? _originalDriver;

        // Haptics composite: the connection sub-device we took over, our manager,
        // and SimHub's original — all restored in End().
        private object? _connectionDevice;
        private MozaBaseConnectionManager? _connectionManager;
        private object? _originalConnectionManager;
        // Set once the swap has been tried, successfully or not, so an
        // unsupported SimHub doesn't re-walk the composite every frame.
        private bool _connectionSwapAttempted;
        // Same latch for the LED driver swap: without it a host with no
        // DeviceDriver setter never satisfied ledsDone and InjectDrivers built a
        // fresh manager on every 60 Hz frame.
        private bool _ledSwapAttempted;
        // TryEarlyProviderInstall does six reflective reads per call; ~1 Hz is
        // plenty for a re-assert that only matters after a profile switch.
        private int _providerInstallTick = ProviderInstallEveryNFrames;
        private const int ProviderInstallEveryNFrames = 60;
        // SimHub's motors (Haptics) sub-device, present only when the definition
        // declares HapticsFeature. Held so the channels-provider swap can be
        // re-asserted every tick.
        private object? _motorsDevice;

        public override string ExtentionTabTitle => "MOZA Wheel Base";

        /// <summary>Model token this device's definition was written for ("R16"), empty for the legacy shared definition.</summary>
        private string _modelPrefix = "";

        public override void Init(PluginManager pluginManager)
        {
            // Injection is deferred to DataUpdate() — calling it here would run before
            // LedModuleDevice.SetSettings(), causing a KeyNotFoundException in that call.

            pluginManager.AttachDelegate(
                LinkedDevice.DeviceDescriptor.Name + "_MozaBaseAmbientActive",
                this.GetType(),
                () => MozaPlugin.Instance?.IsBaseAmbientLedSupported ?? false);

            _modelPrefix = MozaDeviceConstants.GetBaseModelPrefix(
                LinkedDevice.DeviceDescriptor?.DeviceTypeID ?? "") ?? "";

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
                plugin.BaseAmbientDeviceExtensionActive = true;

            TryEarlyProviderInstall();
            RemoveConnectionSubDevice();
        }

        /// <summary>
        /// Find the LedModuleDevice sub-device and replace its DeviceDriver
        /// with our MozaBaseLedDeviceManager that gates connection on the
        /// runtime base-ambient detection flag.
        /// </summary>
        private void InjectDrivers()
        {
            if (_driverInjected) return;

            bool sawLeds = false;
            bool sawConnection = false;
            try
            {
                foreach (var instance in LinkedDevice.GetInstances())
                {
                    if (!sawLeds && instance is LedModuleDevice lmd && lmd.ledModuleSettings != null)
                    {
                        sawLeds = true;
                        if (!_ledSwapAttempted)
                        {
                            _ledSwapAttempted = true;
                            if (LedDriverInjection.CanInject)
                            {
                                _ledDriver = new MozaBaseLedDeviceManager();
                                _ledDriver.LedModuleSettings = lmd.ledModuleSettings;
                                _ledDriver.ExpectedModelPrefix = _modelPrefix;
                                _injectedSettings = lmd.ledModuleSettings;
                                _originalDriver = LedDriverInjection.Swap(lmd.ledModuleSettings, _ledDriver);
                                MozaLog.Debug("[AZOM] Injected virtual LED driver for wheel base ambient strip");
                            }
                            else
                            {
                                MozaLog.Warn("[AZOM] Could not find DeviceDriver setter on LedModuleSettings (base ambient)");
                            }
                        }
                        continue;
                    }

                    // Present only when the definition declares HapticsFeature.
                    // Its connected state gates the LED sub-device through
                    // PrimaryDeviceMissing, so taking it over is load-bearing —
                    // not just a haptics nicety.
                    if (!sawConnection && IsConnectionDevice(instance))
                    {
                        sawConnection = true;
                        if (!_connectionSwapAttempted)
                            InjectConnectionManager(instance);
                    }
                }

                // Latch only once everything this composite offers is in hand.
                // GetInstances() can be empty before SimHub finishes composing the
                // device, so an empty pass retries on the next tick rather than
                // giving up (the pre-haptics behaviour).
                bool ledsDone = !sawLeds || _ledSwapAttempted;
                bool connectionDone = !sawConnection || _connectionSwapAttempted;
                if (!(sawLeds || sawConnection) || !ledsDone || !connectionDone)
                    return;

                _driverInjected = true;
                if (!sawLeds)
                    MozaLog.Debug("[AZOM] Wheelbase device has no LED module (haptics-only definition)");
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM] Error injecting wheelbase drivers: {ex.Message}");
                _driverInjected = true;   // don't retry a throwing path every frame
            }
        }

        /// <summary>
        /// Install the LFE channels provider as early as SimHub lets us.
        ///
        /// This CANNOT wait for our DataUpdate: the ShakeIt mixer materializes each
        /// effect's channel activations lazily, on its own tick
        /// (PlacementChannelsActivation.Get -> GetOrAdd -> CreateDefaultActivationFor),
        /// and the motors sub-device ticks independently of this extension. Any
        /// effect that renders before the swap gets SimHub's stock default — every
        /// channel enabled — baked into the saved profile, which on a summing base
        /// is a silent 3x. Hence the attempt from Init/SetSettings/LoadDefaultSettings
        /// as well; whichever runs first and finds the hosted plugin wins, and the
        /// rest are no-ops.
        /// </summary>
        private void TryEarlyProviderInstall()
        {
            try
            {
                if (_motorsDevice == null)
                    _motorsDevice = FindMotorsDevice();
                if (_motorsDevice == null) return;

                MozaBaseHapticsBridge.TryInstallChannelsProvider(_motorsDevice);
                ApplyShippedEffectDefaultsOnce();
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] Early LFE provider install skipped: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply the plugin's shipped LFE effect defaults to the profile SimHub seeds
        /// at device creation — all effects off, oscillator 1 only. See
        /// <see cref="Haptics.MozaLfeEffectDefaults"/> for why both a shipped-file
        /// path and this pass are needed.
        ///
        /// Runs once per device instance (flag persisted with the extension's
        /// settings), latched on containers actually seen, so it cannot fire against
        /// a profile SimHub has not populated yet and cannot re-clobber later edits.
        /// </summary>
        private void ApplyShippedEffectDefaultsOnce()
        {
            if (_settings.LfeChannelDefaultsNormalized || _motorsDevice == null) return;

            var (containers, _) = MozaBaseHapticsBridge.ApplyShippedEffectDefaults(_motorsDevice);
            if (containers > 0)
                _settings.LfeChannelDefaultsNormalized = true;
        }


        private object? FindMotorsDevice()
        {
            try
            {
                foreach (var instance in LinkedDevice.GetInstances())
                {
                    if (MozaBaseHapticsBridge.IsMotorsDeviceExtension(instance))
                        return instance;
                }
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] Could not locate the Haptics sub-device: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Remove SimHub's "Connection" sub-device from the composite.
        ///
        /// It exists only because the definition declares HapticsFeature, and it
        /// contributes nothing a MOZA user can act on: a serial-number picker for
        /// hardware that has none, plus a connection state the LEDs tab already
        /// shows. Its real cost is that it is the composite's ONLY primary, so its
        /// state gates every other sub-device through PrimaryDeviceMissing.
        ///
        /// Removing it leaves every remaining child non-primary, which sends
        /// CompositeDeviceInstance.DataUpdate down its uniform path — no
        /// PrimaryDeviceMissing gating at all, so the ambient LEDs can no longer be
        /// switched off by a connection state that was never meaningful here.
        ///
        /// Assigns a new list rather than mutating in place: Devices has a public
        /// setter, and a reference swap cannot trip an enumeration already running
        /// on the data thread.
        /// </summary>
        private void RemoveConnectionSubDevice()
        {
            try
            {
                if (!(LinkedDevice is CompositeDeviceInstance composite)) return;

                var kept = composite.Devices.Where(d => !IsConnectionDevice(d)).ToList();
                if (kept.Count == composite.Devices.Count) return;

                composite.Devices = kept;
                MozaLog.Info("[AZOM] Removed the wheelbase device's redundant Connection tab "
                           + "(its state and identity are on the LEDs tab)");
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Could not remove the Connection sub-device: {ex.Message}");
            }
        }

        private static bool IsConnectionDevice(object instance) =>
            string.Equals(instance?.GetType().FullName,
                "SimHub.Plugins.OutputPlugins.CommonDevices.Devices.StandardProtocolConnectionDevice",
                StringComparison.Ordinal);

        private void InjectConnectionManager(object instance)
        {
            _connectionSwapAttempted = true;

            // Guarded: the manager type binds 9.12-era SimHub/BA63 members, so it
            // must never be loaded on a build that lacks them.
            if (!MozaBaseHapticsBridge.IsSupported) return;

            try
            {
                var manager = (MozaBaseConnectionManager)MozaBaseHapticsBridge.CreateConnectionManager();
                var previous = LedDriverInjection.SwapConnectionManager(instance, manager);
                if (previous == null) return;

                _connectionDevice = instance;
                _connectionManager = manager;
                _originalConnectionManager = previous;
                MozaLog.Info("[AZOM] Took over the wheelbase device's connection manager (LEDs + LFE haptics)");
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Could not take over the wheelbase connection manager: {ex.Message}");
            }
        }

        public override void End(PluginManager pluginManager)
        {
            pluginManager.DetachDelegate(
                LinkedDevice.DeviceDescriptor.Name + "_MozaBaseAmbientActive",
                this.GetType());

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
            {
                plugin.BaseAmbientDeviceExtensionActive = false;
                MozaLog.Debug("[AZOM] Base ambient device extension ended");
            }

            // Restore SimHub's original driver and drop ours so neither
            // outlives the extension.
            LedDriverInjection.Restore(_injectedSettings, _ledDriver, _originalDriver);
            _injectedSettings = null;
            _originalDriver = null;
            try { _ledDriver?.Close(); } catch { }
            _ledDriver = null;

            LedDriverInjection.RestoreConnectionManager(
                _connectionDevice, _connectionManager, _originalConnectionManager);
            _connectionDevice = null;
            _connectionManager = null;
            _originalConnectionManager = null;
            _connectionSwapAttempted = false;
            _motorsDevice = null;

            _driverInjected = false;
        }

        public override void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // Inject here (not Init) so LedModuleDevice.SetSettings() has already run.
            if (!_driverInjected)
                InjectDrivers();

            // Notify SimHub when detection state changes so it resumes/pauses Display() calls
            _ledDriver?.UpdateConnectionState();
            _connectionManager?.UpdateConnectionState();

            // Haptics composite only (the connection sub-device and the motors one
            // are created together). Looked up here rather than in the one-shot
            // injection walk so ordering inside GetInstances() can't lose it.
            // Re-asserted about once a second: a profile switch or a settings reload
            // runs CreateOutputManager again and stamps SimHub's stock provider back
            // on, but that is a rare event and each check is six reflective reads.
            if (++_providerInstallTick >= ProviderInstallEveryNFrames)
            {
                _providerInstallTick = 0;
                TryEarlyProviderInstall();
            }
        }

        public override void LoadDefaultSettings()
        {
            _settings = new MozaBaseExtensionSettings();
            TryEarlyProviderInstall();

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
                _settings.CaptureFromCurrent(plugin.Settings, plugin.Data, plugin.Settings?.ProfileStore?.CurrentProfile);
        }

        public override JToken GetSettings()
        {
            var plugin = MozaPlugin.Instance;
            if (plugin != null)
                _settings.CaptureFromCurrent(plugin.Settings, plugin.Data, plugin.Settings?.ProfileStore?.CurrentProfile);

            return JToken.FromObject(_settings);
        }

        public override void SetSettings(JToken settings, bool isDefault)
        {
            _settings = settings.ToObject<MozaBaseExtensionSettings>() ?? new MozaBaseExtensionSettings();
            TryEarlyProviderInstall();

            if (!isDefault)
            {
                var plugin = MozaPlugin.Instance;
                if (plugin != null)
                    plugin.HardwareApplier.ApplyBaseExtensionSettings(_settings);
            }
        }

        public override Control CreateSettingControl()
        {
            return new MozaBaseSettingsControl();
        }

        public override IEnumerable<DynamicButtonAction> GetDynamicButtonActions()
        {
            yield break;
        }
    }
}
