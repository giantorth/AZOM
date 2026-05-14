using System;
using MozaPlugin.Telemetry.Dashboard;

namespace MozaPlugin.Telemetry.Era
{
    /// <summary>
    /// Which session number tier-def chunks ride on.
    /// </summary>
    public enum TierDefSessionPolicy
    {
        /// <summary>
        /// Send tier-def on the probed telemetry FlagByte (typically 0x02).
        /// 2025-era VGS firmware accepts tier-def here. Matches 0.8.0
        /// behavior (commit 5692099).
        /// </summary>
        FlagByte,

        /// <summary>
        /// Send tier-def on the management port (typically 0x01). 2026-era
        /// firmware (R5+W17, KS Pro, post-2026-04 CSP) puts tier-def TLV on
        /// 0x01 and FF init records on 0x02. Verified by 2026-05-03 PitHouse
        /// captures (see <c>docs/protocol/findings/2026-05-04-init-sequence.md</c>).
        /// </summary>
        MgmtPort,
    }

    /// <summary>
    /// Tier-definition body shape. Picks which builder produces the message.
    /// </summary>
    public enum TierDefEncoding
    {
        /// <summary>Flat URL subscription list (V0). Wheel resolves compression internally.</summary>
        V0Url,

        /// <summary>
        /// V2 compact: tier records with alphabetic channel indices. No wheel-catalog
        /// lookup. Used by 2025-era VGS-class firmware.
        /// </summary>
        V2Compact,

        /// <summary>
        /// V2 compact + Type02 metadata layout: channel indices come from the
        /// wheel's advertised catalog, sub-msg structure includes per-broadcast
        /// end markers and previous-flag enable records. Used by post-2026-04
        /// firmware (R5+W17, KS Pro, post-2026-04 CSP).
        /// </summary>
        V2Type02,
    }

    /// <summary>
    /// Materialized per-era runtime policy. Owned mutably by
    /// <c>TelemetrySender</c>; some axes (e.g. <see cref="UploadWireFormat"/>)
    /// are mutated in place during runtime fallback under <see cref="IsAuto"/>.
    /// All other axes are derived by <see cref="For(MozaWheelEra)"/> and
    /// shouldn't be touched after construction.
    /// </summary>
    public sealed class EraPolicy
    {
        /// <summary>The era this policy was built from. After auto-resolution,
        /// reflects the resolved era (e.g. Era2025), not the user's pick.</summary>
        public MozaWheelEra Era;

        /// <summary>True when the user picked <see cref="MozaWheelEra.Auto"/>.
        /// Enables runtime auto-resolution and upload-wire-format fallback.</summary>
        public bool IsAuto;

        public TierDefSessionPolicy TierDefSession;
        public TierDefEncoding Encoding;

        /// <summary>When true, the V2 preamble (tag=0x07 size=4 ver=2; tag=0x03
        /// size=4 val=0) is emitted on every <c>SendTierDefinition</c> call.
        /// When false, it's gated by <c>_tierDefPreambleSent</c> (once per
        /// connect). 2025-era VGS firmware needs every-send.</summary>
        public bool SendV2PreambleEverySend;

        /// <summary>When true, tier-def chunks are tracked for blind
        /// retransmission (~10 rounds at 200ms). 2026-era only.</summary>
        public bool BlindRetransmitTierDef;

        /// <summary>Wire format for the dashboard upload sub-msg headers.</summary>
        public FileTransferWireFormat UploadWireFormat;

        /// <summary>When true, the upload sub-msg-1 ack timeout flips
        /// <see cref="UploadWireFormat"/> between 8B and 6B. Only set by Auto.</summary>
        public bool AutoFallbackUploadWireFormat;

        /// <summary>0 (V0 URL subscription) or 2 (V2 compact). Used by V0
        /// value-frame paths (<c>SendV0ValueFrames</c>) outside tier-def.</summary>
        public int ProtocolVersion;

        /// <summary>
        /// Build a policy for <paramref name="era"/>. <see cref="MozaWheelEra.Auto"/>
        /// returns an Era2026-shaped provisional policy with <see cref="IsAuto"/>
        /// set; <c>TelemetrySender.ResolveAutoPolicy</c> replaces it at session
        /// start once the wheel reveals itself.
        /// </summary>
        public static EraPolicy For(MozaWheelEra era)
        {
            switch (era)
            {
                case MozaWheelEra.Era2024:
                    return new EraPolicy
                    {
                        Era = MozaWheelEra.Era2024,
                        IsAuto = false,
                        // Tier-def on management port (0x01) — same architecture
                        // as Era2025/Era2026 so the wheel's tier-def parser sees
                        // a clean TLV stream uncontaminated by FF/session-state
                        // chunks on 0x02.
                        TierDefSession = TierDefSessionPolicy.MgmtPort,
                        Encoding = TierDefEncoding.V0Url,
                        SendV2PreambleEverySend = false, // n/a for V0
                        BlindRetransmitTierDef = false,
                        UploadWireFormat = FileTransferWireFormat.New2026_04,
                        AutoFallbackUploadWireFormat = false,
                        ProtocolVersion = 0,
                    };

                case MozaWheelEra.Era2025:
                    return new EraPolicy
                    {
                        Era = MozaWheelEra.Era2025,
                        IsAuto = false,
                        // Tier-def rides on the management port (0x01), same as
                        // Era2026. Real VGS firmware (and the wheel simulator
                        // which emulates it) parses tier-def from a clean TLV
                        // stream, so it must NOT share a session with FF init
                        // records (which use 0x02). The 5692099 plugin sent
                        // tier-def on FlagByte (often 0x02) which co-mingled
                        // with FF records — VGS firmware tolerated this in
                        // some configurations but the sim's parser gets
                        // misaligned by the FF CRC envelopes. The actual
                        // VGS-vs-Type02 differences are below this line:
                        // preamble-each-send, no blind retransmit, 8B upload.
                        TierDefSession = TierDefSessionPolicy.MgmtPort,
                        Encoding = TierDefEncoding.V2Compact,
                        SendV2PreambleEverySend = true,
                        BlindRetransmitTierDef = false,
                        // 8B header matches 0.8.0 working behavior for VGS users.
                        // No fallback — users on Type02 firmware should pick Era2026.
                        UploadWireFormat = FileTransferWireFormat.Legacy2025_11,
                        AutoFallbackUploadWireFormat = false,
                        ProtocolVersion = 2,
                    };

                case MozaWheelEra.Era2026:
                    return new EraPolicy
                    {
                        Era = MozaWheelEra.Era2026,
                        IsAuto = false,
                        TierDefSession = TierDefSessionPolicy.MgmtPort,
                        Encoding = TierDefEncoding.V2Type02,
                        SendV2PreambleEverySend = false, // gated by _tierDefPreambleSent
                        BlindRetransmitTierDef = true,
                        UploadWireFormat = FileTransferWireFormat.New2026_04_Type02,
                        AutoFallbackUploadWireFormat = false,
                        ProtocolVersion = 2,
                    };

                case MozaWheelEra.Auto:
                default:
                    // Optimistic Era2026 default with auto-fallback enabled.
                    // ResolveAutoPolicy at session start replaces this with the
                    // proper era policy once the wheel reveals itself.
                    return new EraPolicy
                    {
                        Era = MozaWheelEra.Era2026,
                        IsAuto = true,
                        TierDefSession = TierDefSessionPolicy.MgmtPort,
                        Encoding = TierDefEncoding.V2Type02,
                        SendV2PreambleEverySend = false,
                        BlindRetransmitTierDef = true,
                        UploadWireFormat = FileTransferWireFormat.New2026_04_Type02,
                        AutoFallbackUploadWireFormat = true,
                        ProtocolVersion = 2,
                    };
            }
        }

        /// <summary>
        /// Heuristic era guess from a wheel-model identity string. Returns null
        /// when no entry matches — callers fall back to other signals
        /// (typically default to Era2025 — most-likely-VGS-class).
        /// </summary>
        /// <remarks>
        /// Matched substrings, case-insensitive. The wheel's identity string
        /// (<c>MozaData.WheelModelName</c>) is set by the device-info probe
        /// during connect and contains entries like "RS V2", "GS V2P",
        /// "FSR-MC SW", "RS21-W17-MC SW". Be permissive — newer firmware may
        /// add prefixes/suffixes.
        /// </remarks>
        public static MozaWheelEra? GuessFromWheelModel(string model)
        {
            if (string.IsNullOrEmpty(model)) return null;
            string m = model.ToUpperInvariant();

            // Era2026 — Type02 metadata firmware. Verified live (R5 + W17, KS Pro)
            // and post-2026-04 CSP. The W17 / W18 / KSP-Pro identity strings
            // distinguish from older W08-era hardware.
            if (m.Contains("W17") || m.Contains("W18")) return MozaWheelEra.Era2026;
            if (m.Contains("KSPRO") || m.Contains("KS PRO") || m.Contains("KSP-PRO"))
                return MozaWheelEra.Era2026;
            if (m.Contains("R5"))   return MozaWheelEra.Era2026;

            // Era2024 — V0 URL subscription. R9 wheel-base / older CSP firmware.
            if (m.Contains("R9"))   return MozaWheelEra.Era2024;

            // Era2025 — VGS-class V2 compact tier-def. VGS, GS V2P, F1, FSR.
            if (m.Contains("VGS"))  return MozaWheelEra.Era2025;
            if (m.Contains("GS V2P") || m.Contains("GSV2P")) return MozaWheelEra.Era2025;
            if (m.Contains("FSR"))  return MozaWheelEra.Era2025;
            if (m.Contains("F1"))   return MozaWheelEra.Era2025;

            return null;
        }
    }
}
