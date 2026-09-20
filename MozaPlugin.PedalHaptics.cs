using System;
using MozaPlugin.Devices.Extensions;
using MozaPlugin.Devices.PedalHaptics;

namespace MozaPlugin
{
    /// <summary>
    /// Pedal-haptics coordination: the registry that owns every unit, the
    /// routed-lane registration, and the ShakeIt surface its channels provider
    /// posts into.
    ///
    /// Deliberately thin. The wheelbase LFE equivalent in
    /// <c>MozaPlugin.Haptics.cs</c> carries a source toggle, a firmware gate and
    /// a scope feed because the plugin has its own LFE tab competing for the
    /// same actuator. Here ShakeIt is the only way to drive the unit, so there
    /// is nothing to arbitrate.
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
            try
            {
                string? pid = controller.DiscoveredPid ?? _deviceManager?.Connection?.DiscoveredPid;
                if (DeviceDefinitionDeployer.DeployForPedalHaptics(pid))
                    DeviceDefinitionDeployed = true;
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM/PedalHaptics] Definition deploy: {ex.Message}");
            }
        }

        /// <summary>
        /// Latest ShakeIt output for one channel (gain 0..1, frequency Hz),
        /// from the provider on the SimHub data thread. Channels are pedal-major:
        /// 0-8 throttle, 9-17 brake, 18-26 clutch, each pedal's nine effect slots in order.
        /// </summary>
        internal void PostShakeItPedalHapticsChannel(int channel, double gain01, double freqHz)
            => _pedalHapticsRegistry?.PostChannel(channel, gain01, freqHz);

        /// <summary>Drop every channel to silent, for the provider Stop path and the driver Clear.</summary>
        internal void ClearShakeItPedalHaptics()
            => _pedalHapticsRegistry?.ClearChannels();
    }
}
