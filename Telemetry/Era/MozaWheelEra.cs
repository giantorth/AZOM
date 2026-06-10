namespace MozaPlugin.Telemetry.Era
{
    /// <summary>
    /// Coarse firmware era of the connected wheel. Drives every wire-protocol
    /// axis (tier-def session, encoding, preamble policy, blind-retransmit,
    /// upload header, init handshake) via <see cref="EraPolicy"/>.
    ///
    /// Two live eras supported. <see cref="Auto"/> probes the wheel and
    /// picks one at session start (see <c>TelemetrySender.ResolveAutoPolicy</c>).
    /// </summary>
    /// <remarks>
    /// Era families (verified against bridge captures and the wheel simulator
    /// in <c>sim/wheel_sim.py</c>):
    ///
    /// <list type="bullet">
    /// <item><see cref="Era2024"/> — V0 URL subscription. R9, older CSP.
    /// Tier-def TLV is a flat URL list; wheel resolves compression internally.</item>
    /// <item><see cref="Era2026"/> — V2 compact + Type02 metadata. Tier-def and
    /// FF init kinds ride the dynamically-resolved sessions, preamble gated,
    /// blind retx on. Post-2026-04 CSP, R5+W17, KS Pro, and VGS-class wheels
    /// (the compact builder is reused when no wheel catalog is advertised).</item>
    /// </list>
    ///
    /// Value 2 is a retired hole (the defunct Era2025); it is never written
    /// anymore, so a persisted 2 unambiguously means a legacy Era2025 pick and
    /// is migrated to <see cref="Auto"/> on read. Because the values are NOT
    /// contiguous, the settings UI maps combo index ↔ enum explicitly rather
    /// than casting (see <c>SettingsControl.EraComboOrder</c>).
    /// </remarks>
    public enum MozaWheelEra
    {
        /// <summary>Probe the wheel and pick an era at session start.</summary>
        Auto = 0,

        /// <summary>V0 URL subscription. R9, older CSP.</summary>
        Era2024 = 1,

        // 2 = retired Era2025 (hole). Migrated to Auto on read.

        /// <summary>V2 compact + Type02 metadata. Post-2026-04 CSP, R5+W17,
        /// KS Pro, and VGS-class wheels.</summary>
        Era2026 = 3,
    }
}
