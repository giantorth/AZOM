using System;
using GameReaderCommon;
using MozaPlugin.Telemetry;

namespace MozaPlugin.Telemetry2
{
    // Lifecycle + state contract shared by both telemetry implementations:
    //   - Old pipeline: Telemetry/TelemetrySender.cs (existing god-class)
    //   - New pipeline: Telemetry2/MozaTelemetryHost.cs (greenfield facade)
    //
    // MozaPlugin.cs holds an IMozaTelemetry reference and dispatches lifecycle calls
    // through it. Implementation-specific settings (DashCache, ProtocolVersion, etc.)
    // remain on each concrete type — MozaPlugin gates those accesses on which
    // implementation is active.
    public interface IMozaTelemetry : IDisposable
    {
        // Lifecycle
        void Start();
        void Stop();

        // Game data feed
        void UpdateGameData(StatusDataBase? data);
        void SetGameRunning(bool running);

        // Dashboard control
        void SendDashboardSwitch(uint slotIndex);

        // Atomic switch: stage the new profile (if non-null), emit FF kind=4 to the
        // target slot, and start the renegotiate handshake. Use this when the caller
        // already knows which profile corresponds to the new slot — it avoids the
        // race where setting Profile before SendDashboardSwitch can fire a tier-def
        // for the new dash *before* the wheel has accepted the slot change.
        // For the v2 pipeline the deferred emission matches PitHouse's observed
        // ~1.85s delay between kind=4 and the new tier-def.
        void SwitchToProfile(uint slotIndex, MultiStreamProfile? newProfile);

        // Active dashboard profile. Setter triggers a renegotiate on the next tick.
        MultiStreamProfile? Profile { get; set; }

        // Channel value resolver — when set, channels with a SimHubProperty descriptor
        // read their value via this delegate; otherwise fall back to GameDataSnapshot.
        Func<string, double>? PropertyResolver { get; set; }

        // Mzdash file content + name for upload (when configured). Set before Start();
        // both implementations consume them at session startup.
        byte[]? MzdashContent { get; set; }
        string MzdashName { get; set; }

        // Diagnostics — read-only counters useful for the auto-test harness and
        // the Diagnostics tab.
        int FramesSent { get; }
        bool Enabled { get; }

        // Increments each time a renegotiate (cold-start handshake or warm dashboard
        // switch) reaches its Settled state — i.e., the wheel has accepted the new
        // dashboard, the new tier-def has been emitted, and value frames are flowing
        // for the new channel set. The auto-test harness waits on this to detect
        // "switch complete on the wheel side"; UI/diagnostics may use it to render a
        // settled-state badge. Read with Volatile semantics; writes are serialised
        // through the host's renegotiate state machine.
        int SubscriptionGen { get; }

        // Name of the currently-active dashboard profile (Profile.Name), or null when
        // no profile is loaded. Used by the auto-test to figure out which slot the
        // wheel is currently displaying so it can pick the *other* slot to switch to.
        string? ActiveProfileName { get; }

        // Wheel-reported dashboard names in slot order, parsed from the wheel's
        // WheelDashboardState (session 0x09 ConfigJson response). Null until the
        // wheel has announced. Slot N = WheelReportedDashboards[N]. Used by the
        // auto-test to enumerate enabled slots; falls back to the local DashboardCache
        // when the wheel hasn't yet announced.
        System.Collections.Generic.IReadOnlyList<string>? WheelReportedDashboards { get; }

        // When true, the telemetry stream emits a synthetic triangle-wave test pattern
        // for every channel (BuildTestFrame per tier) regardless of game state. Used by
        // the Diagnostics tab Test Start/Stop buttons to verify wire-side rendering
        // without a running game. Both pipelines implement it.
        bool TestMode { get; set; }

        // True while a Stop+Start cycle is in flight or the post-Stop silence
        // gate is still enforcing wheel-quiet time. UI consumers should
        // disable dashboard-switch affordances (dropdown, Start Test) while
        // this is true so users can't trigger races against the in-flight
        // restart — observed 2026-05-09: a kind=4 leaked onto the wire during
        // silence, putting the wheel into a corrupted catalog state.
        bool IsInSilenceCooldown { get; }

        // Wire-trace phase marker. Used by DashboardSwitchAutoTest to inject an
        // unambiguous sentinel frame at each state transition so the v1↔v2 wire-diff
        // tool can align both captures by phase boundary. The frame must be:
        //   - Cheap (fire-and-forget, ignored by wheel)
        //   - Uniquely identifiable in a wire trace via grep
        //   - Carry the phase id so multiple markers in a session are distinguishable
        // Implementations should send a frame with grp=0x55 dev=0x55 cmd=0x4D 0x4B
        // payload=[phaseId] — group 0x55 / dev 0x55 isn't used by any real wheel
        // command, so it's safe filler that nonetheless lands in SerialTrafficCapture
        // wire trace. ASCII "MK" (0x4D 0x4B) makes the marker greppable.
        void SendPhaseMarker(byte phaseId);
    }
}
