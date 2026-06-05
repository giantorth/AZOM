using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Protocol;

namespace MozaPlugin.Telemetry.Frames
{
    /// <summary>
    /// Builds the tier definition message that Pithouse sends to the wheel via
    /// 7c:00 session data on the telemetry session. Tells the wheel firmware how
    /// to decode the bit-packed telemetry on each flag byte.
    ///
    /// Generic TLV layout — every top-level tag is `[tag:1B][param:u32 LE][data:param bytes]`:
    ///
    ///   [0x00] [01 00 00 00] [enable_flag:1B]                — per-tier enable (repeated)
    ///   [0x01] [size: u32 LE] [flag_byte:1B] [channels]      — tier header + channel table
    ///     [ch_index:u32] [comp_code:u32] [bits:u32] [reserved:u32]   — 16B per channel
    ///   [0x06] [04 00 00 00] [total_channels: u32 LE]        — end marker
    ///
    /// Preamble tags (0x07 protocol version, 0x03 base flag offset) use the same TLV
    /// format but are sent as a SEPARATE 14-byte 7c:00 message before this one — see
    /// TelemetrySender.SendTierDefinition. A parser scanning the session buffer must
    /// therefore treat unknown top-level tags as generic TLV and skip by param_size.
    ///
    /// Channel indices are 1-based, assigned alphabetically by URL across all tiers,
    /// so indices within any single tier are NOT consecutive when a channel's URL
    /// sorts between two channels of a different tier. Compression codes are
    /// firmware-internal IDs mapped from type name strings.
    /// </summary>
    public static class TierDefinitionBuilder
    {
        /// <summary>
        /// Maps compression type name → firmware numeric code.
        /// Confirmed codes from F1 dashboard USB capture analysis.
        /// Unknown types get code 0xFFFF (the wheel may ignore them).
        /// </summary>
        public static uint LookupCompressionCode(string compression)
            => CompressionTable.TryGetByName(compression ?? "", out var entry) ? entry.Code : 0xFFFF;

        public static int DetectSubTiersPerBroadcast(MultiStreamProfile profile)
        {
            // All profiles are now built as a SINGLE broadcast (the Profile
            // setter hardcodes broadcasts=1; the old N-copy multi-broadcast
            // expansion was removed). PitHouse confirms this shape for split
            // dashboards: the FSR2 track-map tier-def emits all 14 sub-tiers
            // (flags 0x04–0x11) back-to-back followed by ONE END marker
            // (END=139), not one broadcast per sub-tier.
            //
            // The old pl-repeat heuristic returned a SMALL count whenever the
            // lowest package_level split into multiple sub-tiers (its pl
            // repeats at index 1), making the builder emit one broadcast — and
            // one END marker — PER sub-tier. That malformed tier-def reshaped
            // the channel layout and caused the wheel to drop the connection.
            // One broadcast = tier count.
            return profile.Tiers.Count;
        }

        /// <summary>
        /// Build the V2 compact tier-def for Type02 firmware in PitHouse's
        /// exact section ordering: each tier-def + enable lives in its own
        /// chunk (separate session-data frame on the wire). Body returned here
        /// is the FULL concatenated TLV stream which the caller then chunks via
        /// <see cref="ChunkMessage"/> with default 54B max-net-per-chunk; the
        /// wheel reassembles before TLV-walking, so chunk boundaries don't
        /// affect parse correctness.
        /// </summary>
        private static byte[] BuildTierDefinitionMessageType02(MultiStreamProfile profile, byte flagBase,
             System.Collections.Generic.IReadOnlyList<string> wheelCatalog,
            uint endMarkerCounter,
            byte? prevFlagBase = null, int prevTierCount = 0, int prevSubPerBroadcast = 0)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            var idxByUrl = ChannelCatalogParser.BuildIdxByUrl(wheelCatalog);

            // END u32 = caller-supplied session-scoped monotonic counter
            // (verified 2026-05-17 from sim/logs/bridge-20260517-070054.jsonl
            // via tools/tierdef-decode: PitHouse emits a monotonically-
            // advancing counter for the END u32 value — observed sequence
            // 0 → 6 → 21 → 33 → 42 → 43 → 68 → 100 across one capture's
            // tier-def emissions, completely unrelated to maxChIdx. For
            // switch #1 maxChIdx=6 but END=33/42; for switch #2 maxChIdx=20
            // but END=43/68.
            //
            // Previously this builder emitted maxChIdxSeen (assumed from a
            // single-capture spot-check in bridge-20260514-204307). That was
            // wrong: the value isn't a channel count. The earlier finding
            // 2026-04-30-dashboard-switch-3f27.md § "End-marker u32
            // semantics" already noted "0, 9, 21, 30, 42, 54, 76, 90, 96,
            // 104 observed. Not a channel count. Likely a wheel-internal
            // cumulative slot counter." That finding is now confirmed.
            void WriteTier(byte flag, IReadOnlyList<ChannelDefinition> channels)
            {
                // Drop channels with chIdx=0 (URL not in wheel catalog) — W17
                // silently rejects the whole tier-def if ANY record carries
                // chIdx=0. Emit in tier.Channels order so the bit-packer in
                // TelemetryFrameBuilder.BuildFrameFromSnapshot lines up with
                // what we declare here. See docs/protocol/findings/.
                var resolved = new List<(int chIndex, ChannelDefinition ch)>(channels.Count);
                foreach (var ch in channels)
                {
                    int chIndex;
                    if (!idxByUrl.TryGetValue(ch.Url ?? "", out chIndex)) chIndex = 0;
                    if (chIndex <= 0) continue;
                    resolved.Add((chIndex, ch));
                }
                int numChannels = resolved.Count;
                if (numChannels == 0) return;
                uint size = (uint)(1 + numChannels * 16);
                w.Write((byte)0x01);
                w.Write(size);
                w.Write(flag);
                foreach (var (chIndex, ch) in resolved)
                {
                    uint compCode = LookupCompressionCode(ch.Compression);
                    w.Write((uint)chIndex);
                    w.Write(compCode);
                    w.Write((uint)ch.BitWidth);
                    w.Write((uint)0);  // reserved
                }
            }

            void WriteEndMarker()
            {
                w.Write((byte)0x06);
                w.Write((uint)4);
                w.Write(endMarkerCounter);
            }

            void WriteEnable(byte flag)
            {
                w.Write((byte)0x00);
                w.Write((uint)1);
                w.Write(flag);
            }

            int tierCount = profile.Tiers.Count;
            byte FlagFor(int i) => (byte)(flagBase + i);

            // PitHouse-exact format (R5 + W17, Rally V4 + Nebula in-game captures
            // 2026-04-29). Broadcast = consecutive sub-tiers covering ALL
            // pkg_levels. Each broadcast emits sub-tiers back-to-back, then a
            // SINGLE end-marker, then enables for that broadcast's flags.
            //
            // Nebula (1 sub-tier × 3 broadcasts): each broadcast = 1 sub-tier.
            //   tier_0  end-marker=0  enable_0
            //   tier_1  end-marker=4  enable_1
            //   tier_2  end-marker=4
            //
            // Rally V4 (3 sub-tiers × 4 broadcasts): each broadcast = 3 sub-tiers.
            //   tier_0 tier_1 tier_2  end-marker=0
            //   enable_0 enable_1 enable_2
            //   tier_3 tier_4 tier_5  end-marker=9   (channel count 5+2+1=8 → wait
            //   enable_3 enable_4 enable_5            should be 8 not 9; PitHouse
            //   ...                                   used 9 because added TCActive
            //                                         in subsequent broadcasts.)
            //
            // To detect broadcast boundaries we look for the SAME flag-offset
            // pattern repeating: profile is built as `subPerBroadcast × N`
            // broadcasts (TelemetrySender.Profile setter assembles it). Detect
            // by finding the pkg_level repeat: when current tier's pkg_level
            // matches profile.Tiers[0]'s pkg_level after the first tier, that's
            // a broadcast boundary.
            int subPerBroadcast = DetectSubTiersPerBroadcast(profile);
            int broadcastCount = tierCount / subPerBroadcast;

            // Total channel count across one broadcast (used as end-marker for
            // subsequent broadcasts; first uses 0). Use the post-filter count
            // (channels actually written by WriteTier — i.e. those whose URLs
            // appear in the wheel catalog with a non-zero idx) so the marker
            // matches the bytes we emit.
            int channelsPerBroadcast = 0;
            for (int j = 0; j < subPerBroadcast; j++)
                foreach (var ch in profile.Tiers[j].Channels)
                    if (idxByUrl.TryGetValue(ch.Url ?? "", out var idx) && idx > 0)
                        channelsPerBroadcast++;

            // On dashboard switch, emit ENABLE records for the previous
            // subscription's last-broadcast flags BEFORE the first new TIER.
            // PitHouse does this to signal the wheel that the old tier's
            // flags are still valid while it processes the new definition.
            if (prevFlagBase.HasValue && prevTierCount > 0 && prevSubPerBroadcast > 0)
            {
                int prevBroadcasts = prevTierCount / prevSubPerBroadcast;
                int prevLastBase = prevFlagBase.Value + (prevBroadcasts - 1) * prevSubPerBroadcast;
                for (int s = 0; s < prevSubPerBroadcast; s++)
                    WriteEnable((byte)(prevLastBase + s));
            }

            for (int b = 0; b < broadcastCount; b++)
            {
                int baseTier = b * subPerBroadcast;
                for (int s = 0; s < subPerBroadcast; s++)
                    WriteTier(FlagFor(baseTier + s), profile.Tiers[baseTier + s].Channels);
                // END u32 = the caller-supplied session counter, identical
                // across every broadcast inside one emission. PitHouse
                // emits the SAME END value for all broadcasts of a single
                // tier-def emission (verified switch #1 broadcasts 2+3
                // both END=42; switch #2 broadcasts 2-13 all END=68 in
                // bridge-20260517-070054.jsonl). The counter advances on
                // the NEXT emission, not within this one.
                WriteEndMarker();
                if (b < broadcastCount - 1)
                {
                    for (int s = 0; s < subPerBroadcast; s++)
                        WriteEnable(FlagFor(baseTier + s));
                }
            }

            return ms.ToArray();
        }

        /// <summary>
        /// PitHouse-shape tier-def body for V2 compact (VGS-class) firmware:
        /// flat layout with prior-flag enables, back-to-back tier records,
        /// single trailing END marker. The structure shape was empirically
        /// verified working at cold-start on VGS firmware (m Formula 1
        /// dashboard renders correctly with this layout); only the END u32
        /// value semantics were corrected post-refactor.
        ///
        /// Layout:
        ///   [ENABLE 0x00 size=1 flag] × flagBase  (prior-flag enables)
        ///   [TIER 0x01 size=1+16N flag channel_records] × profile.Tiers.Count
        ///   [END 0x06 size=4 endMarkerCounter]
        ///
        /// Channel indices: 1-based alphabetic-by-URL across all tiers,
        /// deduped. Pre-Type02 firmware (VGS, GS V2P, F1, FSR) doesn't index
        /// against a wheel catalog — host-assigned alphabetic order is the
        /// only source of truth.
        ///
        /// END value: echoes the wheel's most-recent
        /// <c>0x06 04 00 00 00 &lt;u32&gt;</c> marker from its catalog stream
        /// (handshake version). The previous implementation wrote
        /// <c>maxChIdxSeen</c> which doesn't match what the wheel announces;
        /// post-switch the wheel rejected tier-defs as stale-generation and
        /// stopped committing widget bindings. See
        /// <c>docs/protocol/tier-definition/version-2-compact-vgs.md</c>
        /// "Per-tier end-marker" (2026-05-17 finding). Pass 0 for cold-start
        /// before the wheel has pushed any END marker.
        /// </summary>
        private static byte[] BuildTierDefinitionV2Compact(MultiStreamProfile profile, byte flagBase,
            uint endMarkerCounter)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            // Alphabetic-by-URL across all tiers, deduped (the broadcast-
            // expanded profile repeats URLs N×, which would inflate indices
            // if every occurrence got its own slot).
            var idxByUrl = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unique = new List<ChannelDefinition>();
            foreach (var ch in profile.Tiers.SelectMany(t => t.Channels))
            {
                if (seen.Add(ch.Url)) unique.Add(ch);
            }
            unique.Sort((a, b) => string.Compare(a.Url, b.Url, StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < unique.Count; i++)
                idxByUrl[unique[i].Url] = i + 1;

            // Prior-flag enables: ENABLE record for every flag from 0 up to
            // flagBase-1. Empirically required (pre-refactor) for VGS to
            // accept post-switch tier-defs; keeping until a VGS PH capture
            // proves the broadcast-structured pattern works.
            for (int f = 0; f < flagBase; f++)
            {
                w.Write((byte)0x00);
                w.Write((uint)1);
                w.Write((byte)f);
            }

            for (int t = 0; t < profile.Tiers.Count; t++)
            {
                var tier = profile.Tiers[t];
                byte flag = (byte)(flagBase + t);

                // Drop chIdx=0 entries as a safety guard. With alphabetic
                // indexing this should never fire — every URL in the profile
                // gets a non-zero idx. Kept defensive in case a future
                // caller passes channels with null URLs.
                var resolved = new List<(int chIndex, ChannelDefinition ch)>(tier.Channels.Count);
                foreach (var ch in tier.Channels)
                {
                    int chIndex;
                    if (!idxByUrl.TryGetValue(ch.Url ?? "", out chIndex)) chIndex = 0;
                    if (chIndex <= 0) continue;
                    resolved.Add((chIndex, ch));
                }
                int n = resolved.Count;
                if (n == 0) continue;
                uint size = (uint)(1 + n * 16);
                w.Write((byte)0x01);
                w.Write(size);
                w.Write(flag);
                foreach (var (chIndex, ch) in resolved)
                {
                    uint compCode = LookupCompressionCode(ch.Compression);
                    w.Write((uint)chIndex);
                    w.Write(compCode);
                    w.Write((uint)ch.BitWidth);
                    w.Write((uint)0);
                }
            }

            // Trailing END marker echoing the wheel's counter.
            w.Write((byte)0x06);
            w.Write((uint)4);
            w.Write(endMarkerCounter);

            return ms.ToArray();
        }

        /// <summary>
        /// Build the complete tier definition message bytes from a MultiStreamProfile.
        /// 2-arg overload kept for tests and any caller without per-emission
        /// state (no end-marker echo, no prev-subscription transition).
        /// </summary>
        public static byte[] BuildTierDefinitionMessage(MultiStreamProfile profile, byte flagBase)
            => BuildTierDefinitionMessage(profile, flagBase,
                includeEnableEntries: true,
                useWheelCatalogIndices: false,
                wheelCatalog: null);

        /// <summary>
        /// Build a V2 compact tier-def message. Layout differs by wheel firmware:
        /// Type02-era (W17/W18/KS Pro/R5+) uses the broadcast-structured shape
        /// with wheel-catalog channel indices; VGS-class (VGS / GS V2P / F1 /
        /// FSR) uses the flat prior-flag-enables shape with alphabetic indices.
        /// Both shapes echo the wheel's current END marker.
        /// </summary>
        /// <param name="useWheelCatalogIndices">Selects the wheel-firmware
        /// path. True = Type02 broadcast structure with wheel-catalog
        /// indexing. False = VGS flat structure with alphabetic indexing.</param>
        /// <param name="endMarkerCounter">The wheel's most-recent
        /// <c>0x06 04 00 00 00 &lt;u32&gt;</c> END marker value from its
        /// catalog stream. The wheel treats this as a tier-def version
        /// handshake — a mismatched END is treated as a duplicate / stale
        /// and the wheel does not commit widget bindings. See
        /// <c>docs/protocol/tier-definition/version-2-compact-vgs.md</c>
        /// "Per-tier end-marker" (2026-05-17 finding, verified against
        /// Type02 captures; VGS application of the same handshake is
        /// inferred from the same doc). Pass 0 for cold-start before the
        /// wheel has pushed any END marker.</param>
        public static byte[] BuildTierDefinitionMessage(MultiStreamProfile profile, byte flagBase,
            bool includeEnableEntries,
            bool useWheelCatalogIndices,
            System.Collections.Generic.IReadOnlyList<string>? wheelCatalog,
            uint endMarkerCounter = 0,
            byte? prevFlagBase = null, int prevTierCount = 0, int prevSubPerBroadcast = 0)
        {
            if (profile.Tiers.Count == 0)
                return Array.Empty<byte>();

            if (useWheelCatalogIndices && wheelCatalog != null && wheelCatalog.Count > 0)
            {
                return BuildTierDefinitionMessageType02(profile, flagBase,
                    wheelCatalog,
                    endMarkerCounter,
                    prevFlagBase, prevTierCount, prevSubPerBroadcast);
            }

            // VGS path: flat prior-flag-enable layout with wheelEND echo.
            // includeEnableEntries / prevFlagBase / prevTierCount /
            // prevSubPerBroadcast are Type02-only concepts and are ignored
            // here — VGS's prior-flag enables are computed from flagBase
            // directly.
            return BuildTierDefinitionV2Compact(profile, flagBase, endMarkerCounter);
        }

        /// <summary>
        /// Build a version 0 (URL-based) subscription message.
        /// The host sends channel URLs; the wheel firmware resolves compression internally.
        /// Format (confirmed from CSP captures and VGS incoming channel catalog):
        ///   [0xFF]                                         — sentinel
        ///   [0x03] [04 00 00 00] [01 00 00 00]            — config (value=1)
        ///   [0x04] [size: u32 LE] [ch_index: u8] [url: ASCII]  — per-channel (repeated)
        ///   [0x06] [04 00 00 00] [total_channels: u32 LE] — end marker
        /// </summary>
        public static byte[] BuildV0UrlSubscription(MultiStreamProfile profile)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            var allChannels = profile.Tiers
                .SelectMany(t => t.Channels)
                .OrderBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Sentinel
            w.Write((byte)0xFF);

            // Config: tag 0x03, param_size=4, value=1 (version 0 uses value=1)
            w.Write((byte)0x03);
            w.Write((uint)4);
            w.Write((uint)1);

            // Per-channel URL entries
            for (int i = 0; i < allChannels.Count; i++)
            {
                byte[] urlBytes = System.Text.Encoding.ASCII.GetBytes(allChannels[i].Url);
                uint size = (uint)(1 + urlBytes.Length);

                w.Write((byte)0x04);
                w.Write(size);
                w.Write((byte)(i + 1)); // 1-based channel index
                w.Write(urlBytes);
            }

            // End marker
            w.Write((byte)0x06);
            w.Write((uint)4);
            w.Write((uint)allChannels.Count);

            return ms.ToArray();
        }

        /// <summary>
        /// Chunk a message into 7c:00 session data frames ready to send.
        /// Each chunk: session(1) + type(1) + seq(2 LE) + payload(≤54 net + 4 CRC) inside a moza frame.
        /// ALL chunks have a 4-byte CRC-32 trailer (verified by CRC computation against
        /// every chunk in moza-startup-1 and moza-startup-2 captures, including final chunks).
        ///
        /// <paramref name="deviceId"/> stamps the device-id byte at frame[3]. Defaults
        /// to <see cref="MozaProtocol.DeviceWheel"/> (0x17); pass
        /// <see cref="MozaProtocol.DeviceMain"/> (0x12) for the CM2 standalone
        /// dashboard target.
        /// </summary>
        public static List<byte[]> ChunkMessage(byte[] message, byte session, ref int seq,
            byte deviceId = MozaProtocol.DeviceWheel)
        {
            const int MaxNetPerChunk = 54;  // 58 total - 4 CRC

            var frames = new List<byte[]>();
            int offset = 0;

            while (offset < message.Length)
            {
                int remaining = message.Length - offset;
                int chunkSize = Math.Min(remaining, MaxNetPerChunk);

                var payload = new byte[chunkSize + 4]; // ALL chunks get CRC-32 trailer
                Array.Copy(message, offset, payload, 0, chunkSize);

                {
                    uint crc = Crc32(message, offset, chunkSize);
                    payload[chunkSize]     = (byte)(crc & 0xFF);
                    payload[chunkSize + 1] = (byte)((crc >> 8) & 0xFF);
                    payload[chunkSize + 2] = (byte)((crc >> 16) & 0xFF);
                    payload[chunkSize + 3] = (byte)((crc >> 24) & 0xFF);
                }

                // Build the moza frame: 7E [N] 43 <dev> 7C 00 [session] [type=01] [seq LE] [payload] [checksum]
                int n = 2 + 1 + 1 + 2 + payload.Length; // cmd(2) + session(1) + type(1) + seq(2) + payload
                var frame = new byte[4 + n + 1]; // start(1) + N(1) + group(1) + device(1) + n_payload + checksum(1)
                frame[0] = MozaProtocol.MessageStart;
                frame[1] = (byte)n;
                frame[2] = MozaProtocol.TelemetrySendGroup;
                frame[3] = deviceId;
                frame[4] = 0x7C;
                frame[5] = 0x00;
                frame[6] = session;
                frame[7] = 0x01; // type = data
                frame[8] = (byte)(seq & 0xFF);
                frame[9] = (byte)((seq >> 8) & 0xFF);
                Array.Copy(payload, 0, frame, 10, payload.Length);
                frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame);

                frames.Add(frame);
                offset += chunkSize;
                seq++;
            }

            return frames;
        }

        /// <summary>
        /// Standard CRC-32 (ISO 3309 / zlib / Ethernet).
        /// Polynomial 0xEDB88320 (reflected), init 0xFFFFFFFF, xor-out 0xFFFFFFFF.
        /// </summary>
        public static uint Crc32(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = offset; i < offset + length; i++)
            {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return crc ^ 0xFFFFFFFF;
        }
    }
}
