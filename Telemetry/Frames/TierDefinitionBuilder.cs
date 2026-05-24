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
            int tierCount = profile.Tiers.Count;
            if (tierCount <= 1) return tierCount;
            int firstPkg = profile.Tiers[0].PackageLevel;
            for (int j = 1; j < tierCount; j++)
            {
                if (profile.Tiers[j].PackageLevel == firstPkg)
                    return j;
            }
            return tierCount;
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
        /// PitHouse-shape tier-def body (no preamble — caller emits the
        /// preamble TLV separately on first send per session). Layout per
        /// docs/protocol/findings/2026-05-03-pithouse-tierdef-reference.md:
        ///   [ENABLE 0x00 size=1 flag] × flagBase  (prior-flag enables)
        ///   [TIER 0x01 size=1+16N flag channel_records] × profile.Tiers.Count
        ///   [END 0x06 size=4 max_channel_idx]
        ///
        /// Channel index resolution: when <paramref name="wheelCatalog"/> is
        /// non-null + non-empty, indices come from the wheel's advertised
        /// catalog (1-based position). Otherwise indices fall back to
        /// alphabetic ordering of channel URLs across all tiers (legacy).
        /// END value uses the max channel idx referenced in this message —
        /// "max-ever-seen" semantics noted in findings doc require session
        /// state the caller must track separately.
        /// </summary>
        public static byte[] BuildTierDefinitionV2(MultiStreamProfile profile, byte flagBase,
            System.Collections.Generic.IReadOnlyList<string>? wheelCatalog)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            Dictionary<string, int> idxByUrl;
            if (wheelCatalog != null && wheelCatalog.Count > 0)
            {
                idxByUrl = ChannelCatalogParser.BuildIdxByUrl(wheelCatalog);
            }
            else
            {
                // Fallback: alphabetic-by-URL across all tiers, deduped (the
                // broadcast-expanded profile repeats URLs N×, which would
                // inflate indices if every occurrence got its own slot).
                idxByUrl = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var unique = new List<ChannelDefinition>();
                foreach (var ch in profile.Tiers.SelectMany(t => t.Channels))
                {
                    if (seen.Add(ch.Url)) unique.Add(ch);
                }
                unique.Sort((a, b) => string.Compare(a.Url, b.Url, StringComparison.OrdinalIgnoreCase));
                for (int i = 0; i < unique.Count; i++)
                    idxByUrl[unique[i].Url] = i + 1;
            }

            for (int f = 0; f < flagBase; f++)
            {
                w.Write((byte)0x00);
                w.Write((uint)1);
                w.Write((byte)f);
            }

            int maxIdx = 0;
            for (int t = 0; t < profile.Tiers.Count; t++)
            {
                var tier = profile.Tiers[t];
                byte flag = (byte)(flagBase + t);

                // Drop channels with chIdx=0 — W17 silently rejects the
                // whole tier-def if any record carries chIdx=0. See
                // docs/protocol/findings/.
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
                    if (chIndex > maxIdx) maxIdx = chIndex;
                    uint compCode = LookupCompressionCode(ch.Compression);
                    w.Write((uint)chIndex);
                    w.Write(compCode);
                    w.Write((uint)ch.BitWidth);
                    w.Write((uint)0);
                }
            }

            w.Write((byte)0x06);
            w.Write((uint)4);
            w.Write((uint)maxIdx);

            return ms.ToArray();
        }

        /// <summary>
        /// Build the complete tier definition message bytes from a MultiStreamProfile.
        /// Production callers use <see cref="BuildTierDefinitionV2"/> directly; this
        /// 2-arg overload is the legacy entry point retained for tests and emits the
        /// same V2 shape with alphabetic channel indices (no wheel catalog).
        /// </summary>
        public static byte[] BuildTierDefinitionMessage(MultiStreamProfile profile, byte flagBase)
            => BuildTierDefinitionV2(profile, flagBase, wheelCatalog: null);

        /// <summary>
        /// Build the V2 compact tier-def message.
        /// </summary>
        /// <param name="includeEnableEntries">When true, prepends paired `[0x00] [4B=1] [1B flag]`
        /// enable entries for each tier (legacy 2025-11 firmware behavior). Post-2026-04 CSP
        /// PitHouse captures (`wireshark/csp/startup, change knob colors, ...pcapng`) omit
        /// these — newer firmware only sees `0x01`-tag tier defs + `0x06`-tag end marker
        /// per the host outbound stream on session 0x01.</param>
        /// <param name="useWheelCatalogIndices">When true, the channel index in the tier-def
        /// body is taken from the wheel's advertised catalog (1-based position) instead of
        /// the host-assigned alphabetic index. PitHouse always uses wheel-catalog indices —
        /// matching them is a prerequisite for the wheel correlating value frames against
        /// the subscription.</param>
        public static byte[] BuildTierDefinitionMessage(MultiStreamProfile profile, byte flagBase,
            bool includeEnableEntries,
            bool useWheelCatalogIndices,
            System.Collections.Generic.IReadOnlyList<string>? wheelCatalog,
            uint endMarkerCounter = 0,
            byte? prevFlagBase = null, int prevTierCount = 0, int prevSubPerBroadcast = 0)
        {
            // Type02 path: emit PitHouse-exact section ordering observed in
            // `wireshark/csp/startup, change knob colors, ...pcapng`:
            //   tier 0 def
            //   end count=0 separator
            //   enable flag 0
            //   tier 1 def
            //   tier 2 def
            //   end count=0 final
            //   enable flag 1
            //   enable flag 2
            // Detected by useWheelCatalogIndices=true (only Type02 callers set
            // that). Other callers fall through to the legacy flat layout.
            if (useWheelCatalogIndices && wheelCatalog != null && profile.Tiers.Count > 0)
            {
                return BuildTierDefinitionMessageType02(profile, flagBase, wheelCatalog,
                    endMarkerCounter, prevFlagBase, prevTierCount, prevSubPerBroadcast);
            }

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            // Assign 1-based channel indices. Default is alphabetic across all tiers,
            // but when targeting a wheel that has advertised a catalog we must use
            // its order so the wheel can correlate subscription with value frames.
            var allChannels = profile.Tiers
                .SelectMany(t => t.Channels)
                .OrderBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var channelIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (useWheelCatalogIndices && wheelCatalog != null)
            {
                for (int i = 0; i < wheelCatalog.Count; i++)
                {
                    string url = wheelCatalog[i];
                    if (!string.IsNullOrEmpty(url))
                        channelIndexMap[url] = i + 1; // 1-based per wheel catalog order
                }
            }
            else
            {
                for (int i = 0; i < allChannels.Count; i++)
                    channelIndexMap[allChannels[i].Url] = i + 1;
            }

            // PitHouse-exact format (R5 base, W17 wheel, Nebula in-game capture
            // 2026-04-29 bridge-20260429-201848.jsonl): tier-def stream is
            //   [tier_def 0x01] [end-marker 0x06 val=0x00000000]
            //   [tier_def 0x01] [end-marker 0x06 val=0x04000000] [enable 0x00 val=tier_idx]
            //   [tier_def 0x01] [end-marker 0x06 val=0x04000000]
            // i.e. each subsequent tier preceded by a per-tier enable, and each
            // tier followed by an end-marker with constant value (0 for first,
            // 4 for subsequent — interpretation TBD). The previous plugin shape
            // emitted N enables up front then all tier_defs back-to-back with a
            // single trailing 0x06; wheel parser rejects that (no widget
            // updates verified live 2026-04-29).
            for (int i = 0; i < profile.Tiers.Count; i++)
            {
                // Enable entry preceding tiers 1..N-1 (not before tier 0).
                if (includeEnableEntries && i > 0)
                {
                    w.Write((byte)0x00);     // tag
                    w.Write((uint)1);        // size
                    w.Write((byte)(i - 1));  // tier index of the PREVIOUS tier
                }

                var tier = profile.Tiers[i];
                byte flag = (byte)(flagBase + i);
                int numChannels = tier.Channels.Count;
                uint size = (uint)(1 + numChannels * 16); // flag byte + 16 per channel

                w.Write((byte)0x01);         // tag
                w.Write(size);               // size (LE)
                w.Write(flag);               // flag byte for this tier

                foreach (var ch in tier.Channels)
                {
                    int chIndex;
                    if (!channelIndexMap.TryGetValue(ch.Url, out chIndex)) chIndex = 0;
                    uint compCode;
                    compCode = LookupCompressionCode(ch.Compression);
                    w.Write((uint)chIndex);   // channel index (LE)
                    w.Write(compCode);        // compression code (LE)
                    w.Write((uint)ch.BitWidth); // bit width (LE)
                    w.Write((uint)0);         // reserved
                }

                // Per-tier end-marker. PitHouse: 0 for first tier, 4 for rest.
                w.Write((byte)0x06);
                w.Write((uint)4);
                w.Write((uint)(i == 0 ? 0 : 4));
            }

            return ms.ToArray();
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
