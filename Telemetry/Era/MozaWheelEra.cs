namespace MozaPlugin.Telemetry.Era
{
    /// <summary>
    /// Coarse firmware era of the connected wheel. Drives every wire-protocol
    /// axis (tier-def session, encoding, preamble policy, blind-retransmit,
    /// upload header, init handshake) via <see cref="EraPolicy"/>.
    ///
    /// Three live eras supported. <see cref="Auto"/> probes the wheel and
    /// picks one at session start (see <c>TelemetrySender.ResolveAutoPolicy</c>).
    /// </summary>
    /// <remarks>
    /// Era families (verified against bridge captures and the wheel simulator
    /// in <c>sim/wheel_sim.py</c>):
    ///
    /// <list type="bullet">
    /// <item><see cref="Era2024"/> — V0 URL subscription. R9, older CSP.
    /// Tier-def TLV is a flat URL list; wheel resolves compression internally.</item>
    /// <item><see cref="Era2025"/> — V2 compact tier-def, FlagByte session,
    /// preamble on every send, no blind retx. VGS, GS V2P, F1. Matches the
    /// 0.8.0 (commit 5692099) "Compact numeric (VGS-style)" path.</item>
    /// <item><see cref="Era2026"/> — V2 compact + Type02 metadata. Tier-def
    /// on management session 0x01, FF init kinds on 0x02, preamble gated,
    /// blind retx on. Post-2026-04 CSP, R5+W17, KS Pro.</item>
    /// </list>
    ///
    /// Values are contiguous (0..3) so UI index ↔ enum is a direct cast.
    /// </remarks>
    public enum MozaWheelEra
    {
        /// <summary>Probe the wheel and pick an era at session start.</summary>
        Auto = 0,

        /// <summary>V0 URL subscription. R9, older CSP.</summary>
        Era2024 = 1,

        /// <summary>V2 compact tier-def. VGS, GS V2P, F1.</summary>
        Era2025 = 2,

        /// <summary>V2 compact + Type02 metadata. Post-2026-04 CSP, R5+W17, KS Pro.</summary>
        Era2026 = 3,
    }
}
