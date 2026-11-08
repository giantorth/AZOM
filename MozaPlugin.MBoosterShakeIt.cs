using System;
using MozaPlugin.Devices.Extensions;
using MozaPlugin.Devices.MBooster;
using MozaPlugin.Settings;

namespace MozaPlugin
{
    /// <summary>
    /// mBooster vibrations from SimHub ShakeIt: one combined device with a channel
    /// per pedal role. Exactly one source drives the vibrations (Options): ShakeIt
    /// or the plugin's mBooster tab. G-Force and Brake Fade stay plugin-driven in
    /// both modes.
    /// </summary>
    public partial class MozaPlugin
    {
        // Created in Init before the registry; every effect worker reads it.
        private MBoosterShakeItSink? _mboosterShakeItSink;

        /// <summary>
        /// True when ShakeIt drives the vibrations. Gated on SimHub supporting the
        /// device haptics feature; the stored value stays a preference and applies
        /// by itself once SimHub is updated.
        /// </summary>
        internal bool MBoosterRoutedToShakeIt =>
            _settings?.MBoosterHapticsSource == MBoosterHapticsSource.ShakeIt
            && Devices.Haptics.MozaBaseHapticsBridge.IsSupported;

        /// <summary>True when the plugin's mBooster tab drives the vibrations.</summary>
        internal bool MBoosterRoutedToPluginTab => _settings != null && !MBoosterRoutedToShakeIt;

        /// <summary>At least one mBooster lane has answered.</summary>
        internal bool IsMBoosterDetected => _mboosterRegistry?.FirstDetected != null;

        /// <summary>A unit is up and SimHub can host the Haptics section.</summary>
        internal bool IsMBoosterShakeItReady =>
            _mboosterRegistry?.AnyReady == true && Devices.Haptics.MozaBaseHapticsBridge.IsSupported;

        /// <summary>What the ShakeIt device's IsConnected reports.</summary>
        internal bool IsMBoosterShakeItActive => IsMBoosterShakeItReady && MBoosterRoutedToShakeIt;

        /// <summary>PID the definition's detection anchor uses, from the first detected lane.</summary>
        internal string? MBoosterDetectionPid()
            => _mboosterRegistry?.FirstDetected?.DiscoveredPid ?? _deviceManager?.Connection?.DiscoveredPid;

        /// <summary>
        /// Switch the vibration source. Workers silence the old source on their
        /// next tick; the definition is deployed when switching to ShakeIt with a
        /// unit present.
        /// </summary>
        /// <returns>True when a definition was written and SimHub needs a restart.</returns>
        internal bool SetMBoosterHapticsSource(MBoosterHapticsSource source)
        {
            if (_settings == null || _settings.MBoosterHapticsSource == source) return false;
            _settings.MBoosterHapticsSource = source;
            SaveSettings();
            return ApplyMBoosterHapticsSource();
        }

        /// <summary>The side effects of a source change, for paths that already wrote the setting.</summary>
        internal bool ApplyMBoosterHapticsSource()
        {
            _mboosterShakeItSink?.Clear();
            // Tests belong to the plugin tab's effect cards.
            var registry = _mboosterRegistry;
            if (registry != null)
                foreach (var c in registry.Devices)
                    c.StopAllTests();

            if (!MBoosterRoutedToShakeIt || !IsMBoosterDetected) return false;
            bool written = DeviceDefinitionDeployer.DeployForMBooster(MBoosterDetectionPid());
            if (written) DeviceDefinitionDeployed = true;
            return written;
        }

        /// <summary>
        /// Deploy the definition once a unit actually answers, so users without an
        /// mBooster never get a permanently-disconnected device.
        /// </summary>
        private void DeployMBoosterShakeItDefinition(MBoosterDeviceController controller)
        {
            if (!MBoosterRoutedToShakeIt) return;
            try
            {
                string? pid = controller.DiscoveredPid ?? _deviceManager?.Connection?.DiscoveredPid;
                if (DeviceDefinitionDeployer.DeployForMBooster(pid))
                    DeviceDefinitionDeployed = true;
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM/mBooster] Definition deploy: {ex.Message}");
            }
        }

        /// <summary>
        /// Latest ShakeIt output for one pedal role (0 throttle, 1 brake, 2 clutch),
        /// from the provider on the SimHub data thread.
        /// </summary>
        internal void PostShakeItMBooster(int role, double gain01, double freqHz)
        {
            // Hard gate: with the plugin tab driving, a stray ShakeIt device must not play.
            if (!MBoosterRoutedToShakeIt) return;
            _mboosterShakeItSink?.Post(role, gain01, freqHz);
        }

        /// <summary>Drop every role to silent, for the provider Stop path and the driver Clear.</summary>
        internal void ClearShakeItMBooster()
        {
            if (!MBoosterRoutedToShakeIt) return;
            _mboosterShakeItSink?.Clear();
        }

        /// <summary>The clutch-role mBooster pedal's position (0..1), for the ShakeIt Bite Point effect.</summary>
        /// <returns>False when no motorized pedal holds the Clutch role.</returns>
        internal bool TryGetMBoosterClutchPosition(out double position01)
        {
            position01 = 0;
            var registry = _mboosterRegistry;
            return registry != null && registry.TryGetEffectPedalPosition(MBoosterRole.Clutch, out position01);
        }

        /// <summary>Latest fresh ShakeIt gain per role, for diagnostics; 0 when stale.</summary>
        internal double[] MBoosterShakeItGains()
        {
            var gains = new double[MBoosterShakeItSink.RoleCount];
            var sink = _mboosterShakeItSink;
            if (sink == null) return gains;
            for (int r = 0; r < gains.Length; r++)
                if (sink.TryGet(r, out double g, out _)) gains[r] = g;
            return gains;
        }
    }
}
