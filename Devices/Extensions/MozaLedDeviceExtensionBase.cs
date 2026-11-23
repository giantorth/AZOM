using System;
using GameReaderCommon;
using SimHub.Plugins;
using SimHub.Plugins.Devices;
using SimHub.Plugins.Devices.DeviceExtensions;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;
using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;
using MozaPlugin.Devices.Led;

namespace MozaPlugin.Devices.Extensions
{
    /// <summary>
    /// SimHub device extension for an LED-only MOZA device (the LedModuleDevice is the
    /// composite's primary): swaps the LED module's driver for ours on the first
    /// <see cref="DataUpdate"/> where it exists, keeps SimHub's connected state in step
    /// every frame, and restores the original driver on <see cref="End"/>.
    /// </summary>
    internal abstract class MozaLedDeviceExtensionBase<TDriver> : DeviceExtension
        where TDriver : class, ILedDeviceManager
    {
        protected TDriver? LedDriver { get; private set; }
        private bool _driverInjected;
        // Latched when the host has no DeviceDriver setter, so the per-frame retry
        // stops constructing a manager.
        private bool _injectionGaveUp;
        // Injection target + SimHub's original driver, restored in End().
        private LedModuleSettings? _injectedSettings;
        private object? _originalDriver;

        /// <summary>Short device label for log lines ("dash", "fcs").</summary>
        protected abstract string LogLabel { get; }
        protected abstract TDriver CreateDriver();
        /// <summary>Fire OnConnect/OnDisconnect on a detection change.</summary>
        protected abstract void UpdateDriverConnection(TDriver driver);
        /// <summary>Drop the driver from any keepalive/diagnostic registry.</summary>
        protected abstract void ReleaseDriver(TDriver driver);
        /// <summary>Per-device module setup once the driver is in (e.g. button count).</summary>
        protected virtual void OnInjected(LedModuleDevice lmd) { }

        protected string DeviceName => LinkedDevice?.DeviceDescriptor?.Name ?? "?";

        // Deferred to DataUpdate(): in Init() it would run before
        // LedModuleDevice.SetSettings(), causing a KeyNotFoundException there.
        private void InjectLedDriver()
        {
            if (_driverInjected || _injectionGaveUp) return;

            try
            {
                foreach (var instance in LinkedDevice.GetInstances())
                {
                    if (instance is LedModuleDevice lmd && lmd.ledModuleSettings != null)
                    {
                        if (!LedDriverInjection.CanInject)
                        {
                            _injectionGaveUp = true;
                            MozaLog.Warn($"[AZOM] Could not find DeviceDriver setter on LedModuleSettings ({LogLabel})");
                            return;
                        }

                        var driver = CreateDriver();
                        driver.LedModuleSettings = lmd.ledModuleSettings;
                        _injectedSettings = lmd.ledModuleSettings;
                        _originalDriver = LedDriverInjection.Swap(lmd.ledModuleSettings, driver);
                        LedDriver = driver;
                        _driverInjected = true;
                        OnInjected(lmd);
                        MozaLog.Debug($"[AZOM] Injected virtual LED driver for {LogLabel}");
                        return;
                    }
                }
                // Retried every DataUpdate until injected, so repeat-suppressed.
                MozaLog.DebugIfChanged($"{LogLabel}-ext-nolmd:" + DeviceName,
                    $"[AZOM] No LedModuleDevice found on {LogLabel} device instance ({DeviceName})");
            }
            catch (Exception ex)
            {
                MozaLog.ErrorIfChanged($"{LogLabel}-ext-inject:" + DeviceName,
                    $"[AZOM] Error injecting {LogLabel} LED driver ({DeviceName}): {ex.Message}");
            }
        }

        /// <summary>Restore SimHub's original driver and drop ours. Call from End().</summary>
        protected void RestoreLedDriver()
        {
            LedDriverInjection.Restore(_injectedSettings, LedDriver, _originalDriver);
            _injectedSettings = null;
            _originalDriver = null;
            var d = LedDriver;
            if (d != null) { try { ReleaseDriver(d); } catch { } }
            LedDriver = null;
            _driverInjected = false;
        }

        public override void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (!_driverInjected)
                InjectLedDriver();

            // Notify SimHub when detection changes so it resumes/pauses Display() calls.
            var d = LedDriver;
            if (d != null) UpdateDriverConnection(d);
        }
    }
}
