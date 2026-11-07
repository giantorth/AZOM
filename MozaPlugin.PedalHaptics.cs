using System;
using MozaPlugin.Devices.Extensions;
using MozaPlugin.Devices.PedalHaptics;
using MozaPlugin.Settings;

namespace MozaPlugin
{
    /// <summary>
    /// Pedal-haptics coordination: the registry that owns every unit, the
    /// routed-lane registration, the ShakeIt surface its channels provider posts
    /// into, and the plugin's own S12 tab worker.
    ///
    /// Exactly one source drives the unit (Options): ShakeIt, through the
    /// per-pedal or combined device definitions, or the plugin's S12 tab. Both
    /// would write the same slots, so the inactive one posts nothing.
    /// </summary>
    public partial class MozaPlugin
    {
        // Constructed in Init (MozaPlugin.Bootstrap.cs), refreshed by the 5 s
        // poll, disposed in the ordered teardown.
        internal MozaPedalHapticsRegistry? PedalHapticsRegistry => _pedalHapticsRegistry;

        /// <summary>
        /// True when at least one pedal-haptics unit has answered its presence
        /// probe and SimHub is new enough to host the Haptics section. Drives
        /// both the connection manager's state and the motors driver's.
        /// </summary>
        internal bool IsPedalHapticsReady =>
            _pedalHapticsRegistry?.AnyReady == true
            && Devices.Haptics.MozaPedalHapticsBridge.IsSupported;

        /// <summary>True when the one all-pedals device drives the unit rather than the per-pedal devices.</summary>
        internal bool PedalHapticsCombined => _settings?.PedalHapticsCombinedDevice == true;

        /// <summary>
        /// True when ShakeIt drives the S12. Gated on the bridge: on a SimHub
        /// without the device haptics feature (pre-9.12) the definitions can't
        /// work, so the plugin tab takes over. The stored value stays a
        /// preference and applies by itself once SimHub is updated.
        /// </summary>
        internal bool PedalHapticsRoutedToShakeIt =>
            _settings?.PedalHapticsSource != PedalHapticsSource.PluginTab
            && Devices.Haptics.MozaPedalHapticsBridge.IsSupported;

        /// <summary>True when the plugin's S12 tab drives the unit.</summary>
        internal bool PedalHapticsRoutedToPluginTab => _settings != null && !PedalHapticsRoutedToShakeIt;

        /// <summary>
        /// Ready, ShakeIt routed, and the given layout is the one selected. The
        /// other layout's devices stay in SimHub (so their effects can be copied
        /// across) but read as disconnected and post nothing.
        /// </summary>
        internal bool IsPedalHapticsActive(bool combined) =>
            IsPedalHapticsReady && PedalHapticsRoutedToShakeIt && PedalHapticsCombined == combined;

        /// <summary>
        /// Switch source and/or ShakeIt layout: silence whatever the old one left
        /// playing, then deploy the new layout's definition if it is missing.
        /// </summary>
        /// <returns>True when a definition was written and SimHub needs a restart.</returns>
        internal bool SetPedalHapticsMode(PedalHapticsSource source, bool combined)
        {
            if (_settings == null) return false;
            if (_settings.PedalHapticsSource == source && _settings.PedalHapticsCombinedDevice == combined) return false;
            _settings.PedalHapticsSource = source;
            _settings.PedalHapticsCombinedDevice = combined;
            SaveSettings();
            return ApplyPedalHapticsLayout();
        }

        /// <summary>The side effects of a source/layout change, for paths that already wrote the setting.</summary>
        internal bool ApplyPedalHapticsLayout()
        {
            bool combined = PedalHapticsCombined;

            // The scheduler re-sends the latest state until told otherwise, and
            // the old source's devices stop posting the moment they go inactive.
            var registry = _pedalHapticsRegistry;
            if (registry == null) return false;
            for (int p = 0; p < Protocol.MozaPedalHapticsProtocol.PedalCount; p++)
                registry.ClearPedal(p);

            // The plugin tab needs no definition; existing ones are left in place.
            if (!PedalHapticsRoutedToShakeIt) return false;

            // Same rule as detection: no definition until a unit has answered.
            if (!registry.AnyDetected) return false;
            string? pid = null;
            foreach (var controller in registry.Devices)
            {
                if (!controller.Detected) continue;
                pid = controller.DiscoveredPid;
                break;
            }
            pid ??= _deviceManager?.Connection?.DiscoveredPid;

            bool written = DeviceDefinitionDeployer.DeployForPedalHaptics(pid, combined);
            if (written) DeviceDefinitionDeployed = true;
            return written;
        }

        /// <summary>
        /// Register a routed lane against a connected base/hub pipe, so the unit
        /// can be probed on it.
        ///
        /// Registration is speculative — the device prober has nothing to key
        /// off. A routed unit has no port of its own and does not announce
        /// itself; it simply answers at the extended address when asked. So a
        /// lane goes on every pipe and its own presence query decides. Nothing
        /// reaches the wire until the unit answers: the lane sends queries only,
        /// and <c>SendEffectStream</c> is gated on Detected.
        ///
        /// Idempotent per identity; called from the 5 s poll so a pipe that
        /// connects later still gets a lane.
        /// </summary>
        internal void EnsureRoutedPedalHapticsLane()
        {
            var registry = _pedalHapticsRegistry;
            if (registry == null || IsShuttingDown) return;

            var connection = _deviceManager?.Connection;
            if (connection?.IsConnected != true) return;

            string port = connection.LastPortName ?? "";
            string identity = "routedhaptics:" + (string.IsNullOrEmpty(port) ? "pipe" : port);
            registry.AddRoutedLane(
                identity, connection, portLabel: string.IsNullOrEmpty(port) ? "via base" : $"via {port}");
        }

        /// <summary>
        /// Deploy the SimHub device definition once a unit actually answers.
        /// Writing it unconditionally would leave a permanently-disconnected
        /// device in every user's list.
        ///
        /// The PID is the detection anchor only. A USB unit supplies its own; a
        /// routed one has none, so the host wheelbase's PID is used — exactly
        /// how the wheel definition binds the base's PID via
        /// <c>__DETECT_PID__</c>. Either way the extension swaps our driver in
        /// and no HID report is ever written.
        /// </summary>
        private void OnPedalHapticsDeviceDetected(PedalHapticsDeviceController controller)
        {
            // The plugin tab needs no SimHub device.
            if (!PedalHapticsRoutedToShakeIt) return;
            try
            {
                string? pid = controller.DiscoveredPid ?? _deviceManager?.Connection?.DiscoveredPid;
                if (DeviceDefinitionDeployer.DeployForPedalHaptics(pid, PedalHapticsCombined))
                    DeviceDefinitionDeployed = true;
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM/PedalHaptics] Definition deploy: {ex.Message}");
            }
        }

        /// <summary>
        /// Latest ShakeIt output for one channel (gain 0..1, frequency Hz),
        /// from the provider on the SimHub data thread. <paramref name="channel"/>
        /// is the oscillator within that pedal.
        /// </summary>
        internal void PostShakeItPedalHapticsChannel(int pedalIndex, int channel, double gain01, double freqHz)
        {
            // Hard gate: with the S12 routed to the plugin tab, a stray ShakeIt
            // device must not overwrite the tab's slots.
            if (!PedalHapticsRoutedToShakeIt) return;
            _pedalHapticsRegistry?.PostChannel(pedalIndex, channel, gain01, freqHz);
        }

        /// <summary>Drop every channel to silent, for the provider Stop path and the driver Clear.</summary>
        internal void ClearShakeItPedalHaptics(int pedalIndex)
        {
            if (!PedalHapticsRoutedToShakeIt) return;
            _pedalHapticsRegistry?.ClearPedal(pedalIndex);
        }

        // ── S12 tab ───────────────────────────────────────────────────────────

        /// <summary>Play one S12 tab card's test pattern on its ticked pedals.</summary>
        public void TriggerPedalHapticsTest(Protocol.PedalHapticsEffectSlot slot)
            => _pedalHapticsWorker?.PostTest(slot);

        /// <summary>Latest (freq, amplitude 0..1) per pedal — throttle, brake, clutch — for the S12 tab scope.</summary>
        public (double freq, double amp)[] GetPedalHapticsScopeSamples()
        {
            var w = _pedalHapticsWorker;
            if (w == null || !PedalHapticsRoutedToPluginTab)
                return new[] { (0.0, 0.0), (0.0, 0.0), (0.0, 0.0) };
            return w.ScopeSamples();
        }
    }
}
