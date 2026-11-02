using System;

namespace MozaPlugin.Telemetry.TestMode
{
    /// <summary>
    /// Auto test-mode latch driven by <see cref="TelemetrySender"/>. Once the
    /// initial subscription has settled (SubscriptionGen ≥ 1) it injects a
    /// phase-marker frame (grp=0x55 dev=0x55 cmd="MK" + phase id) via
    /// <see cref="TelemetrySender.SendPhaseMarker(byte)"/> and turns TestMode
    /// on for the rest of the run, so the wheel streams TestSignal data on a
    /// single dashboard. The wheel ignores the marker; it lets post-mortem
    /// tooling align wire traces.
    ///
    /// Triggered by <see cref="MozaPlugin.Settings.MozaPluginSettings.EnableAutoTestOnConnect"/>.
    /// </summary>
    internal sealed class DashboardSwitchAutoTest
    {
        // Phase id written to the wire trace. Stable value so post-mortem
        // tooling can search for it.
        private const byte PhaseTestModeOn = 0x11;

        private readonly TelemetrySender _telemetry;
        private bool _done;

        public DashboardSwitchAutoTest(TelemetrySender telemetry)
        {
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        }

        public void Tick()
        {
            if (_done) return;
            int gen = _telemetry.SubscriptionGen;
            if (gen == 0) return; // wait for first subscription

            string currentName = _telemetry.ActiveProfileName ?? "?";
            _telemetry.SendPhaseMarker(PhaseTestModeOn);
            _telemetry.TestMode = true;
            MozaLog.Debug(
                $"[AZOM] AUTO-TEST: TestMode-only enabled dash=\"{currentName}\" " +
                $"subGen={gen} — staying in TestMode, no switching");
            _done = true;
        }
    }
}
