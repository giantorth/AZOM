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
        // Pre-1.6 settings import: reached a terminal decision (imported, skipped
        // because the device was already configured, or nothing to import). Until
        // then it re-checks on the same ~1 Hz cadence, because the Haptics section
        // only appears after the user restarts SimHub with the new definition.
        private bool _legacyImportSettled;
        private int _legacyImportTick;
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

        private LedModuleDevice? FindLedDevice()
        {
            try
            {
                foreach (var instance in LinkedDevice.GetInstances())
                {
                    if (instance is LedModuleDevice lmd && lmd.ledModuleSettings != null)
                        return lmd;
                }
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] Could not locate the LEDs sub-device: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Carry the pre-1.6 wheelbase device's settings into this one.
        ///
        /// Up to v1.5.7 the wheelbase was two SimHub devices — a code-registered
        /// "Wheelbase LFE haptics" one and the shared "MOZA Wheel Base" LED one. v1.6
        /// replaced both with this per-model composite, orphaning the originals and
        /// everything the user had configured on them. Their saved settings survive on
        /// disk (see <see cref="LegacyBaseDeviceMigration"/>) and both old and new
        /// sub-devices serialise identically, so the transfer is a straight
        /// <c>SetSettings</c> handoff of a JToken.
        ///
        /// Three things make this safe to run unattended:
        ///
        /// • It never overwrites configured state. The Haptics half runs only while
        ///   <see cref="MozaBaseHapticsBridge.IsProfilePristine"/> holds; a user who
        ///   already moved across by hand is left alone and the migration is marked
        ///   done rather than retried.
        /// • It runs from DataUpdate, not Init. SimHub calls the extension's Init
        ///   BEFORE the sub-devices' own SetSettings during LoadDevices, so importing
        ///   any earlier would just be overwritten by SimHub's own load.
        /// • The writes go through Dispatcher.BeginInvoke. Both SetSettings paths
        ///   expect the UI thread (the motors one does a blocking Dispatcher.Invoke
        ///   internally, which would deadlock if called from the data thread), and
        ///   BeginInvoke keeps this tick non-blocking.
        /// </summary>
        private void TryImportLegacySettings()
        {
            try
            {
                var plugin = MozaPlugin.Instance;
                var settings = plugin?.Settings;
                if (plugin == null || settings == null) return;

                // Already handled for this device instance, or Init found nothing to do.
                // Still worth a retire pass: an upgrade that imported on an earlier
                // build, or one that never had anything to import, can be sitting on
                // a legacy definition whose one-shot delete failed.
                if (_settings.LegacyLfeImported || string.IsNullOrEmpty(settings.LegacyLfeMigrationInstanceId))
                {
                    _legacyImportSettled = true;
                    TryRetireLegacyDefinition(plugin);
                    return;
                }

                var legacy = LegacyBaseDeviceMigration.Scan();
                if (!legacy.HasAnything)
                {
                    Settle(plugin, imported: false);
                    return;
                }

                var motors = legacy.Haptics != null ? FindMotorsDevice() as DeviceInstance : null;
                // The Haptics section only exists once the user restarts SimHub with a
                // definition carrying HapticsFeature. Keep waiting rather than settling.
                if (legacy.Haptics != null && motors == null) return;

                if (motors != null && !MozaBaseHapticsBridge.IsProfilePristine(motors))
                {
                    MozaLog.Info("[AZOM] Wheelbase Haptics already has effects configured — "
                               + "leaving it alone and marking the pre-1.6 migration done");
                    Settle(plugin, imported: false);
                    return;
                }

                // The legacy shared definition was a fixed 18-LED strip; per-model
                // definitions carry the real geometry (12 on an R16). Transplanting
                // across a size change would address LEDs that do not exist.
                LedModuleDevice? leds = null;
                if (legacy.Leds != null)
                {
                    int modelLeds = BaseModelInfo.LedsPerStripForPrefix(_modelPrefix) * 2;
                    if (modelLeds == LegacyBaseDeviceMigration.LegacyBaseLedCount)
                        leds = FindLedDevice();
                    else
                        MozaLog.Info(
                            $"[AZOM] Not importing the pre-1.6 ambient LED profile: it was written for "
                            + $"{LegacyBaseDeviceMigration.LegacyBaseLedCount} LEDs, this base has {modelLeds}");
                }

                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null) return;   // no WPF yet; retry next pass

                // Settle before dispatching: the callback is asynchronous and this
                // method runs again ~1 s later, which would otherwise queue a second
                // import over the first.
                Settle(plugin, imported: motors != null);

                dispatcher.BeginInvoke((Action)(() => ImportOnUiThread(motors, leds, legacy)));
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Pre-1.6 wheelbase settings import failed: {ex.Message}");
                _legacyImportSettled = true;
            }
        }

        /// <summary>
        /// Mark the import handled for this device instance and clear the banner.
        ///
        /// <paramref name="imported"/> also latches the shipped-defaults pass, because
        /// MozaLfeEffectDefaults.ApplyTo is authoritative rather than shape-matched —
        /// it rewrites every container to "all effects off, oscillator 1" and would
        /// wipe what was just imported. It must stay false on every other path: a
        /// device that imported nothing still needs that pass, or it keeps SimHub's
        /// stock all-channels-on defaults and sums to a silent 3x.
        /// </summary>
        private void Settle(MozaPlugin plugin, bool imported)
        {
            _legacyImportSettled = true;
            _settings.LegacyLfeImported = true;
            if (imported)
                _settings.LfeChannelDefaultsNormalized = true;

            var s = plugin.Settings;
            if (s != null && s.LegacyLfeMigrationPending)
            {
                s.LegacyLfeMigrationPending = false;
                try { plugin.PersistSettings(); } catch { /* best-effort */ }
            }

            TryRetireLegacyDefinition(plugin);
        }

        /// <summary>
        /// Delete the pre-1.6 shared "MOZA Wheel Base" definition now that this
        /// device has taken over, so the user stops seeing two wheelbases.
        ///
        /// The migration banner asks the user to ADD the model-named device while
        /// leaving the old one live, so following it is what produces the pair —
        /// which makes retiring the old definition part of the migration, not a
        /// side effect of whichever boot happened to write the new one. Both remain
        /// bound to this extension in the meantime (MozaDeviceConstants.IsBaseDevice
        /// still matches BaseAmbientGuid), so both drive the ambient strip until the
        /// duplicate is gone.
        ///
        /// Two guards. It runs only from a PER-MODEL device — the legacy device's own
        /// extension must never delete the definition it is running from. And it
        /// waits for the import to settle: the legacy instance's saved settings are
        /// what the migration reads, and orphaning the instance before they are
        /// carried across risks SimHub reaping them first.
        /// </summary>
        private void TryRetireLegacyDefinition(MozaPlugin plugin)
        {
            if (_modelPrefix.Length == 0) return;

            var settings = plugin.Settings;
            if (settings == null || settings.LegacyLfeMigrationPending) return;

            if (DeviceDefinitionDeployer.RemoveLegacyBaseDefinition())
                plugin.DeviceDefinitionDeployed = true;   // raises the restart hint
        }

        private void ImportOnUiThread(DeviceInstance? motors, LedModuleDevice? leds, LegacyBaseScanResult legacy)
        {
            if (motors != null && legacy.Haptics != null)
            {
                try
                {
                    motors.SetSettings(legacy.Haptics, isDefault: false);
                    // SetSettings rebuilds the hosted ShakeIt plugin wholesale, so
                    // SimHub's stock channels provider is back on. Re-assert ours now
                    // rather than waiting for the ~1 Hz tick.
                    MozaBaseHapticsBridge.TryInstallChannelsProvider(motors);
                    MozaLog.Info("[AZOM] Imported the pre-1.6 wheelbase LFE effects into this device's Haptics section");
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] Could not import the pre-1.6 wheelbase LFE effects: {ex.Message}");
                }
            }

            if (leds != null && legacy.Leds != null)
            {
                try
                {
                    leds.SetSettings(legacy.Leds, isDefault: false);
                    MozaLog.Info("[AZOM] Imported the pre-1.6 wheelbase ambient LED profile");
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] Could not import the pre-1.6 wheelbase ambient LED profile: {ex.Message}");
                }
            }
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

            // Not reset: _settings.LegacyLfeImported. It is persisted with the device
            // profile precisely so the import survives a reload; only the in-memory
            // retry latch clears with the extension.
            _legacyImportSettled = false;
            _legacyImportTick = 0;

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

            // Same cadence, its own counter: this one keeps retrying until the
            // Haptics section exists (it only appears after the user restarts SimHub
            // with a definition carrying HapticsFeature), then settles for good.
            if (!_legacyImportSettled && ++_legacyImportTick >= ProviderInstallEveryNFrames)
            {
                _legacyImportTick = 0;
                TryImportLegacySettings();
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
