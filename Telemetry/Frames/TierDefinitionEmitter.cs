using System;
using System.Collections.Generic;
using System.Threading;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.Telemetry.TestMode;

namespace MozaPlugin.Telemetry.Frames
{
    /// <summary>
    /// Builds and emits the tier-definition on the wheel's tier-def session.
    /// Also owns blind retransmit (Era2026 doesn't ack sess=0x01), subscription
    /// diagnostics, and catalog-aware filter/sort helpers.
    /// </summary>
    internal sealed class TierDefinitionEmitter
    {
        private readonly TelemetrySender _sender;

        // Wire-protocol concern: has the V2 tier-def preamble (tag 0x07/0x03)
        // been emitted this session. PitHouse only sends preamble once at
        // connect; subsequent tier-def re-sends (dashboard switch) omit it.
        // 2025-era firmware accepts preamble on every send; 2026-era rejects.
        private bool _tierDefPreambleSent;

        // Blind retransmission for session 0x01 tier-def chunks. PitHouse
        // retransmits each chunk ~10× regardless of acks. See
        // docs/protocol/findings/2026-05-02-tier-def-retransmission.md.
        private byte[][]? _tierDefBlindFrames;
        private int _tierDefBlindSentRounds;
        private int _tierDefBlindLastTickCount;
        private static int TierDefBlindMaxRounds
            => global::MozaPlugin.Protocol.RetryBackoff.TierDefBlindMs.Length;

        // Tier-def binding completeness (last emission). Channels whose URL
        // isn't in the wheel's catalog get chIndex=0 → wheel can't bind them.
        // Plugin uses this to decide if a catalog re-sync is still needed.
        private int _lastTierDefUnboundCount = -1;
        private int _lastTierDefTotalCount = -1;

        public int LastTierDefUnboundCount => _lastTierDefUnboundCount;
        public int LastTierDefTotalCount => _lastTierDefTotalCount;
        public bool IsTierDefFullyBound =>
            _lastTierDefUnboundCount == 0 && _lastTierDefTotalCount > 0;

        // Subscription diagnostics for the Diagnostics tab.
        private volatile TelemetrySender.SubscriptionDiagnostics? _lastSubscriptionDiag;
        public TelemetrySender.SubscriptionDiagnostics? LastSubscription => _lastSubscriptionDiag;

        public TierDefinitionEmitter(TelemetrySender sender)
        {
            _sender = sender;
        }

        /// <summary>Reset all state on sender Start/Stop boundary.</summary>
        public void Reset()
        {
            _tierDefPreambleSent = false;
            _tierDefBlindFrames = null;
            _tierDefBlindSentRounds = 0;
            _tierDefBlindLastTickCount = 0;
            _lastTierDefUnboundCount = -1;
            _lastTierDefTotalCount = -1;
            _lastSubscriptionDiag = null;
        }

        /// <summary>
        /// Spin-wait for the wheel's catalog push to go quiet. Returns when
        /// last catalog activity is older than <paramref name="quietMs"/>.
        /// </summary>
        public void WaitForChannelCatalogQuiet(int quietMs, int timeoutMs)
        {
            int deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                if (_sender.StateIsIdle || !_sender.ConnectionIsConnected) return;
                int lastAct = _sender.CatalogParser.LastActivityMs;
                int idle = lastAct == 0 ? 0 : Environment.TickCount - lastAct;
                int bufCount = _sender.CatalogParser.BufferLength;
                if (bufCount > 0 && idle >= quietMs)
                    return;
                Thread.Sleep(20);
            }
        }

        /// <summary>Filter a profile to channels in the catalog (last-segment match accepted).</summary>
        public static MultiStreamProfile FilterProfileToCatalog(
            MultiStreamProfile profile,
            IReadOnlyList<string> catalog)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in catalog)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                set.Add(entry);
                int slash = entry.LastIndexOf('/');
                if (slash >= 0 && slash < entry.Length - 1)
                    set.Add(entry.Substring(slash + 1));
            }

            bool ChannelMatches(ChannelDefinition ch)
            {
                if (set.Contains(ch.Url)) return true;
                int slash = ch.Url.LastIndexOf('/');
                if (slash >= 0 && slash < ch.Url.Length - 1
                    && set.Contains(ch.Url.Substring(slash + 1))) return true;
                return false;
            }

            var result = new MultiStreamProfile
            {
                Name = profile.Name,
                PageCount = profile.PageCount,
            };
            foreach (var tier in profile.Tiers)
            {
                var kept = new List<ChannelDefinition>();
                foreach (var ch in tier.Channels)
                    if (ChannelMatches(ch)) kept.Add(ch);
                if (kept.Count == 0) continue;
                result.Tiers.Add(new DashboardProfile
                {
                    Name = tier.Name,
                    Channels = kept,
                    PackageLevel = tier.PackageLevel,
                    TotalBits = tier.TotalBits,
                    TotalBytes = tier.TotalBytes,
                    FlagByte = tier.FlagByte,
                });
            }
            // Empty filter → fall back to original (wheel rejects empty tier-defs).
            return result.Tiers.Count == 0 ? profile : result;
        }

        /// <summary>Build a V0 subscription profile from the wheel's full catalog (host metadata reused per URL).</summary>
        public static MultiStreamProfile BuildV0ProfileFromCatalog(
            MultiStreamProfile hostProfile,
            IReadOnlyList<string> catalog)
        {
            var hostByUrl = new Dictionary<string, ChannelDefinition>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var tier in hostProfile.Tiers)
                foreach (var ch in tier.Channels)
                    if (!string.IsNullOrEmpty(ch.Url) && !hostByUrl.ContainsKey(ch.Url))
                        hostByUrl[ch.Url] = ch;

            int packageLevel = hostProfile.Tiers.Count > 0
                ? hostProfile.Tiers[0].PackageLevel
                : 30;

            var channels = new List<ChannelDefinition>();
            foreach (var url in catalog)
            {
                if (string.IsNullOrEmpty(url)) continue;
                if (hostByUrl.TryGetValue(url, out var existing))
                {
                    channels.Add(existing);
                }
                else
                {
                    string fallbackName = url.Substring(url.LastIndexOf('/') + 1);
                    channels.Add(new ChannelDefinition
                    {
                        Name = fallbackName,
                        Url = url,
                        Compression = "uint32_t",
                        BitWidth = 32,
                        SimHubField = SimHubField.Zero,
                        SimHubProperty = "",
                        SimHubPropertyScale = 1.0,
                        PackageLevel = packageLevel,
                        TestSignal = TestSignalCatalog.Resolve(fallbackName, null, null, "uint32_t"),
                    });
                }
            }

            return new MultiStreamProfile
            {
                Name = hostProfile.Name,
                PageCount = hostProfile.PageCount,
                Tiers = new List<DashboardProfile>
                {
                    new DashboardProfile
                    {
                        Name = "V0Catalog",
                        Channels = channels,
                        PackageLevel = packageLevel,
                    },
                },
            };
        }

        /// <summary>
        /// Filter to catalog + sort by catalog idx per unique Channels list.
        /// Builds a FRESH list per unique source-list reference rather than
        /// mutating in place — the tick thread iterates the same list from
        /// <see cref="TelemetryFrameBuilder.BuildFrameFromSnapshot"/> and an
        /// in-place RemoveAll/Sort there would trip "Collection was modified"
        /// or emit a torn frame. Broadcast replicas that share a source list
        /// still share the resulting list, so per-tick FrameBuilder behavior
        /// is unchanged. Recomputes TotalBits/Bytes from the new lists.
        ///
        /// The atomic publication happens via the subsequent
        /// <see cref="RebuildFrameBuildersFromProfile"/> call: each
        /// <c>TierState.Builder</c> is replaced with a fresh
        /// <see cref="TelemetryFrameBuilder"/> bound to the new list, and
        /// reference assignment of a class field is atomic in C#. A tick that
        /// captured the old builder before the swap reads the old list to
        /// completion; the next tick sees the new builder + new list.
        /// </summary>
        public void SortTierChannelsByCatalogIdx(
            MultiStreamProfile profile,
            IReadOnlyList<string> catalog)
        {
            var idxByUrl = ChannelCatalogParser.BuildIdxByUrl(catalog);

            int IdxFor(ChannelDefinition c)
                => idxByUrl.TryGetValue(c.Url ?? "", out var ix) ? ix : 0;

            // Build one fresh filtered+sorted list per unique source-list
            // reference. List<T> doesn't override Equals/GetHashCode, so a
            // Dictionary keyed on the list uses reference identity, which is
            // exactly the dedup invariant the broadcast-replica share assumes.
            var resultsBySourceList =
                new Dictionary<List<ChannelDefinition>, List<ChannelDefinition>>();
            foreach (var tier in profile.Tiers)
            {
                if (tier.Channels == null || tier.Channels.Count == 0) continue;
                var src = (List<ChannelDefinition>)tier.Channels;
                if (resultsBySourceList.ContainsKey(src)) continue;
                var filtered = new List<ChannelDefinition>(src.Count);
                foreach (var c in src)
                    if (IdxFor(c) > 0) filtered.Add(c);
                filtered.Sort((a, b) => IdxFor(a).CompareTo(IdxFor(b)));
                resultsBySourceList[src] = filtered;
            }

            // Publish the new lists. Each tier sharing a source ref gets the
            // same fresh result ref, preserving the broadcast-replica share.
            // TotalBits/Bytes are recomputed from the NEW list so the
            // FrameBuilder buffer (sized from these in RebuildFrameBuilders…)
            // matches the published channel set exactly.
            foreach (var tier in profile.Tiers)
            {
                if (tier.Channels != null
                    && tier.Channels.Count > 0
                    && resultsBySourceList.TryGetValue(
                        (List<ChannelDefinition>)tier.Channels, out var fresh))
                {
                    tier.Channels = fresh;
                }
                int bits = 0;
                if (tier.Channels != null)
                    foreach (var ch in tier.Channels) bits += ch.BitWidth;
                tier.TotalBits = bits;
                tier.TotalBytes = (bits + 7) / 8;
            }
        }

        /// <summary>
        /// Rebuild per-tier FrameBuilders from the sender's current profile.
        /// Called after <see cref="SortTierChannelsByCatalogIdx"/> mutates
        /// the live profile post-Profile-setter.
        /// </summary>
        public void RebuildFrameBuildersFromProfile()
        {
            var profile = _sender.ProfileRef;
            var tiers = _sender.Tiers;
            if (profile == null || tiers == null) return;
            if (tiers.Length != profile.Tiers.Count) return;
            for (int i = 0; i < profile.Tiers.Count; i++)
            {
                tiers[i].Builder = new TelemetryFrameBuilder(
                    profile.Tiers[i], _sender.PropertyResolver,
                    type02NConvention: false,
                    deviceId: _sender.TargetDeviceId);
            }
        }

        /// <summary>Send the tier-definition (7c:00 type=0x01 chunks on sess=0x02).</summary>
        /// <param name="reuseFlagBase">
        /// When true and an ActiveSubscription already exists, emit the new tier-def
        /// at the SAME flagBase as the prior emission and do NOT advance
        /// <see cref="TelemetrySender.NextFlagBase"/>. Used by the catalog-growth
        /// re-emit path so the wheel's existing channel bindings stay valid while
        /// newly-discovered URLs get bound at the same base. Without this, the wheel
        /// keeps rendering at the prior base while host value frames arrive at the
        /// new base — dashboard freezes on last good values. Falls through to fresh-
        /// base behavior if ActiveSubscription is null (defensive; shouldn't occur
        /// in practice since the growth path only fires in Active state).
        /// </param>
        public void SendTierDefinition(bool reuseFlagBase = false)
        {
            var profile = _sender.ProfileRef;
            if (profile == null || profile.Tiers.Count == 0)
                return;
            if (!_sender.ConnectionIsConnected)
                return;

            // V0: synthesize from catalog. V2/Type02: send all channels. V2 legacy: filter to catalog.
            // NEVER assign Profile property here — its setter re-expands tiers exponentially.
            var catalog = _sender.CatalogParser.Catalog;
            var policy = _sender.Policy;
            if (catalog != null && catalog.Count > 0)
            {
                if (policy.Encoding == TierDefEncoding.V0Url)
                {
                    profile = BuildV0ProfileFromCatalog(profile, catalog);
                    int catalogCh = profile.Tiers[0].Channels.Count;
                    MozaLog.Debug(
                        $"[AZOM] V0 subscription expanded to wheel catalog: " +
                        $"{catalogCh} channels");
                }
            }

            // Session pick: emit the tier-def on whichever session carries the
            // wheel's REAL catalog+END (and echo THAT session's END below) —
            // resolved centrally so the FF/property-push session stays mirrored.
            // Both 0x01 and 0x02 are valid under the right conditions: the wheel
            // chooses by where it pushes its catalog (Form A=0x01, Form B=0x02),
            // so we follow it deterministically rather than hardcoding a session
            // (which oscillated across prior attempts and left this W17/CSP wheel
            // rejected — its catalog+END=9 land on 0x02 while we emitted on 0x01
            // with END=0, so the wheel acked then CLOSED 0x01). Era-agnostic.
            byte tierDefSession = _sender.ResolveTierDefSession();
            byte flagByte = _sender.FlagByte;
            bool onFlagByte = tierDefSession == flagByte && flagByte != 0;
            object seqLock = onFlagByte ? _sender.Session02SeqLock : _sender.Session01SeqLock;

            // Reserve seq range under the per-session lock so no other writer
            // (V0 value frames, FF property pushes, RPC reply) interleaves a
            // seq into the middle of our chunk train.
            lock (seqLock)
            {
            int seq = onFlagByte
                ? Math.Max(2, _sender.Session02OutboundSeq)
                : Math.Max(2, _sender.Session01OutboundSeq);

            // Send wrapper: under blind-retransmit policy (Era2026), every
            // tier-def chunk is also tracked by the retransmitter for the
            // tick-loop blind-retx replay. Era2024 sends raw.
            void Send(byte[] frame)
            {
                if (policy.BlindRetransmitTierDef)
                    _sender.SendAndTrackChunkInternal(frame);
                else
                    _sender.SendRawFrame(frame);
            }

            switch (policy.Encoding)
            {
                case TierDefEncoding.V0Url:
                {
                    // V0: URL-based subscription. Sentinel 0xFF + tag 0x03 inline. No separate preamble.
                    byte[] message = TierDefinitionBuilder.BuildV0UrlSubscription(profile);
                    var frames = TierDefinitionBuilder.ChunkMessage(message, tierDefSession, ref seq, _sender.TargetDeviceId);

                    int channelCount = 0;
                    foreach (var t in profile.Tiers) channelCount += t.Channels.Count;
                    MozaLog.Debug(
                        $"[AZOM] Sending v0 URL subscription: " +
                        $"{message.Length} bytes in {frames.Count} chunks " +
                        $"on session 0x{tierDefSession:X2} ({channelCount} channels)");

                    foreach (var frame in frames)
                        Send(frame);

                    if (policy.BlindRetransmitTierDef)
                    {
                        _tierDefBlindFrames = frames.ToArray();
                        _tierDefBlindSentRounds = 0;
                        _tierDefBlindLastTickCount = Environment.TickCount;
                    }

                    CaptureSubscriptionDiag(tierDefSession, "v0-url",
                        Array.Empty<byte>(), message, profile);
                    break;
                }

                case TierDefEncoding.V2Type02:
                {
                    // V2 preamble: tag 0x07 (version=2), tag 0x03 (value=0).
                    // Gated to once per connect.
                    bool emitPreamble = !_tierDefPreambleSent;
                    int preambleChunkCount = 0;
                    if (emitPreamble)
                    {
                        byte[] preambleMsg = new byte[]
                        {
                            0x07, 0x04, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00,
                            0x03, 0x00, 0x00, 0x00, 0x00
                        };
                        var preambleFrames = TierDefinitionBuilder.ChunkMessage(preambleMsg, tierDefSession, ref seq, _sender.TargetDeviceId);
                        foreach (var frame in preambleFrames)
                            Send(frame);
                        preambleChunkCount = preambleFrames.Count;
                        _tierDefPreambleSent = true;
                    }

                    // Type02 indexes channels by wheel-catalog position. Without
                    // a catalog, fall through to alphabetic indices.
                    bool cspIdx = policy.Encoding == TierDefEncoding.V2Type02;
                    if (cspIdx && (_sender.CatalogParser.Catalog == null || _sender.CatalogParser.Catalog.Count == 0))
                    {
                        MozaLog.Debug(
                            "[AZOM] No wheel catalog — using alphabetic indices for initial tier-def. " +
                            "Wheel will push corrected catalog after receiving this.");
                        cspIdx = false;
                    }

                    // Restore pristine pre-filter snapshot before re-filtering,
                    // so a prior call against a stale catalog hasn't permanently
                    // stripped channels. Makes ApplySubscription idempotent w.r.t. catalog.
                    var tiers = _sender.Tiers;
                    if (tiers != null)
                    {
                        for (int i = 0; i < profile.Tiers.Count && i < tiers.Length; i++)
                        {
                            var st = tiers[i];
                            if (st?.OriginalChannels == null) continue;
                            var tier = profile.Tiers[i];
                            // Assign a FRESH list rather than Clear()+AddRange() on
                            // the existing tier.Channels: the tick thread may still
                            // be iterating the old list through the previous
                            // FrameBuilder, and in-place mutation here would race.
                            // The old list is left untouched; it stays valid for
                            // any in-flight read and is GC'd after the FrameBuilder
                            // gets rebound below in RebuildFrameBuildersFromProfile.
                            // A copy keeps OriginalChannels from being aliased onto
                            // the live tier (so a future in-place edit, however
                            // unlikely, can't corrupt the pristine snapshot).
                            tier.Channels = new List<ChannelDefinition>(st.OriginalChannels);
                            tier.TotalBits = st.OriginalTotalBits;
                            tier.TotalBytes = st.OriginalTotalBytes;
                        }
                    }

                    // Now re-filter+re-sort against current catalog.
                    if (_sender.CatalogParser.Catalog != null
                        && _sender.CatalogParser.Catalog.Count > 0)
                    {
                        SortTierChannelsByCatalogIdx(profile, _sender.CatalogParser.Catalog);
                        RebuildFrameBuildersFromProfile();
                    }

                    var prevSub = _sender.ActiveSubscription;
                    // Reuse path: emit at the prior subscription's flagBase so the
                    // wheel's existing channel→idx bindings stay valid. Pass
                    // prevFlagBase=null/prevTierCount=0 to BuildTierDefinitionMessage
                    // to suppress the prior-broadcast ENABLE records — those would
                    // re-arm flags the SAME tier records are about to redefine.
                    bool doReuse = reuseFlagBase && prevSub != null;
                    // doReuse guarantees prevSub != null; null-forgiving is safe.
                    byte flagBase = doReuse ? prevSub!.FlagBase : _sender.NextFlagBase;

                    // END u32: echo of the wheel's most-recent END marker. PitHouse
                    // handshake: wheel sends tag=0x06 size=4 value=u32 announcing a
                    // tier-def version; PitHouse emits tier-def with the SAME u32
                    // in its END marker. Mismatched END = wheel treats as duplicate
                    // and does not commit widget bindings (verified across both
                    // Type02 and VGS firmware, see version-2-compact-vgs.md). 0 is
                    // the cold-start fallback before the wheel has pushed any END.
                    //
                    // Echo the END the wheel announced on THIS tier-def's session,
                    // NOT the cross-session-merged LastWheelEndMarker. On the cold
                    // R5 base the real catalog+END (END=4) is on sess=0x02 while
                    // sess=0x01 has no valid END; echoing sess=0x02's END=4 on the
                    // sess=0x01 tier-def made the wheel reject it and close
                    // sess=0x01. GetEndMarkerForSession returns 0 when this session
                    // has no valid END yet — matching PitHouse's cold-start END=0.
                    uint endForThisEmission =
                        _sender.CatalogParser.GetEndMarkerForSession(tierDefSession);
                    byte[] message = TierDefinitionBuilder.BuildTierDefinitionMessage(
                        profile, flagBase,
                        includeEnableEntries: true,
                        useWheelCatalogIndices: cspIdx,
                        wheelCatalog: _sender.CatalogParser.Catalog,
                        endMarkerCounter: endForThisEmission,
                        prevFlagBase: doReuse ? (byte?)null : prevSub?.FlagBase,
                        prevTierCount: doReuse ? 0 : (prevSub?.TierCount ?? 0),
                        prevSubPerBroadcast: doReuse ? 0 : (prevSub?.SubTiersPerBroadcast ?? 0));
                    var frames = TierDefinitionBuilder.ChunkMessage(message, tierDefSession, ref seq, _sender.TargetDeviceId);

                    MozaLog.Debug(
                        $"[AZOM] Sending v2 tier definition ({(cspIdx ? "type02" : "compact")}): " +
                        $"flagBase=0x{flagBase:X2}{(doReuse ? " (reused)" : "")}, " +
                        $"end={endForThisEmission} (echoing wheel), " +
                        $"prev={(prevSub != null ? $"0x{prevSub.FlagBase:X2}/{prevSub.TierCount}t/{prevSub.SubTiersPerBroadcast}spb" : "none")}, " +
                        $"preamble ({preambleChunkCount} chunks)" +
                        $" + {message.Length} bytes in {frames.Count} chunks " +
                        $"on session 0x{tierDefSession:X2} ({profile.Tiers.Count} tiers, " +
                        $"idx={(cspIdx ? "wheel-catalog" : "alpha")})");

                    _sender.ActiveSubscription = new TelemetrySender.SubscriptionState(
                        flagBase: flagBase,
                        tierCount: profile.Tiers.Count,
                        subTiersPerBroadcast: TierDefinitionBuilder.DetectSubTiersPerBroadcast(profile),
                        profileName: profile.Name);
                    _sender.IncrementSubscriptionGen();
                    if (!doReuse)
                    {
                        _sender.NextFlagBase = (byte)(flagBase + profile.Tiers.Count);
                    }
                    // Snapshot catalog size for grow-subscription detection.
                    _sender.CatalogCountAtLastSubscription = _sender.CatalogParser.Count;

                    // Tier-def binding completeness check.
                    //
                    // Type02 (cspIdx): wheel indexes channels by catalog position;
                    // URLs not in the wheel's catalog resolve to chIndex=0 and W17
                    // silently rejects the entire tier-def. We schedule a kind=4
                    // probe to nudge Type02 firmware to re-publish its catalog —
                    // verified to actually re-burst on Type02 captures.
                    //
                    // Compact fallback (!cspIdx — no wheel catalog / VGS-class):
                    // wheel doesn't index by catalog position, so an "unbound"
                    // URL is informational only — the tier-def still
                    // emits the URL with an alphabetic chIdx and the wheel either
                    // accepts or ignores per its own dashboard JSON. We log the
                    // count for diagnostics and feed it to LastTierDefTotalCount /
                    // HotSwitchCoordinator.MarkEmission adaptive burst, but do NOT
                    // fire the kind=4 probe — VGS response to kind=4 is unverified,
                    // and an over-eager probe causes sess=0x09 state-push storms
                    // that overwhelm the serial reassembler.
                    var catalogSnapshot = _sender.CatalogParser.Catalog;
                    if (catalogSnapshot != null && catalogSnapshot.Count > 0)
                    {
                        var have = new HashSet<string>(
                            catalogSnapshot, StringComparer.OrdinalIgnoreCase);
                        int unbound = 0, total = 0;
                        string? firstUnboundUrl = null;
                        foreach (var t2 in profile.Tiers)
                        foreach (var c2 in t2.Channels)
                        {
                            total++;
                            if (string.IsNullOrEmpty(c2.Url) || !have.Contains(c2.Url))
                            {
                                unbound++;
                                if (firstUnboundUrl == null) firstUnboundUrl = c2.Url;
                            }
                        }
                        _lastTierDefUnboundCount = unbound;
                        _lastTierDefTotalCount = total;
                        if (unbound > 0)
                        {
                            if (cspIdx)
                            {
                                MozaLog.Warn(
                                    $"[AZOM] Tier-def has {unbound}/{total} unbound channels " +
                                    $"(chIndex=0; wheel catalog has {catalogSnapshot.Count} entries). " +
                                    $"First unbound: {firstUnboundUrl ?? "(null)"}. " +
                                    "Scheduling kind=4 re-emit to nudge wheel re-advertise.");
                                _sender.ScheduleCatalogResyncProbeInternal();
                            }
                            else
                            {
                                MozaLog.Debug(
                                    $"[AZOM] Tier-def has {unbound}/{total} URLs absent from " +
                                    $"wheel catalog ({catalogSnapshot.Count} entries; alphabetic " +
                                    $"indexing in use). First absent: {firstUnboundUrl ?? "(null)"}.");
                            }
                        }
                    }

                    foreach (var frame in frames)
                        Send(frame);

                    if (policy.BlindRetransmitTierDef)
                    {
                        _tierDefBlindFrames = frames.ToArray();
                        _tierDefBlindSentRounds = 0;
                        _tierDefBlindLastTickCount = Environment.TickCount;
                    }

                    CaptureSubscriptionDiag(tierDefSession,
                        cspIdx ? "v2-type02" : "v2-compact",
                        Array.Empty<byte>(), message, profile);
                    break;
                }
            }

            // Persist the new seq counter on whichever session we actually
            // emitted on (matches the seqLock pick above).
            if (onFlagByte)
                _sender.Session02OutboundSeq = seq;
            else
                _sender.Session01OutboundSeq = seq;
            } // lock (seqLock)
        }

        private void CaptureSubscriptionDiag(byte session, string format,
            byte[] preamble, byte[] body, MultiStreamProfile profile)
        {
            var diag = new TelemetrySender.SubscriptionDiagnostics
            {
                SessionByte = $"0x{session:X2}",
                Format = format,
                PreambleBytes = preamble,
                BodyBytes = body,
                CapturedAt = DateTime.Now,
            };
            int idx = 1;
            foreach (var tier in profile.Tiers)
            {
                foreach (var ch in tier.Channels)
                {
                    uint comp = TierDefinitionBuilder.LookupCompressionCode(ch.Compression);
                    diag.Channels.Add((idx, ch.Url, comp, (uint)ch.BitWidth));
                    idx++;
                }
            }
            _lastSubscriptionDiag = diag;

            // Open a 5 s capture window on the sender for inbound chunks on
            // session 0x02 — the wheel returns its channel-token TLVs there
            // right after subscription.
            _sender.OpenSubscriptionResponseCapture(System.Diagnostics.Stopwatch.GetTimestamp()
                + System.Diagnostics.Stopwatch.Frequency * 5);
        }

        /// <summary>Re-emit blind tier-def chunks (Era2024 doesn't ack sess=0x01).</summary>
        public void TickEmitTierDefBlindRetransmits()
        {
            if (_tierDefBlindFrames == null) return;
            if (_tierDefBlindSentRounds >= TierDefBlindMaxRounds)
            {
                _tierDefBlindFrames = null;
                return;
            }
            // Early-exit: every tier-def chunk was tracked via SendAndTrackChunk,
            // so SessionRetransmitter.Contains tells us per-chunk ack state.
            if (_tierDefBlindSentRounds > 0 && AllBlindChunksAcked())
            {
                MozaLog.Debug(
                    $"[AZOM] Blind retransmit early-exit after round {_tierDefBlindSentRounds}/{TierDefBlindMaxRounds} " +
                    "(all blind chunks acked by wheel)");
                _tierDefBlindFrames = null;
                return;
            }
            var schedule = global::MozaPlugin.Protocol.RetryBackoff.TierDefBlindMs;
            int gateMs = schedule[Math.Min(_tierDefBlindSentRounds, schedule.Length - 1)];
            if (Environment.TickCount - _tierDefBlindLastTickCount < gateMs) return;

            _tierDefBlindSentRounds++;
            _tierDefBlindLastTickCount = Environment.TickCount;
            // Selective retransmit on rounds > 1: skip frames whose seqs are
            // no longer in SessionRetransmitter (i.e., the wheel has already
            // acked them). Round 1 always sends every frame because the seq
            // tracker is freshly populated and acks haven't arrived yet.
            // Reduces queue pressure proportional to ack rate — PH bridge
            // captures show wheels acking 15-90% of sess=0x01 chunks, so
            // dropping acked re-sends is a multiplicative bandwidth saving
            // without changing behavior for unresponsive wheels (which still
            // get the full re-send budget).
            int sent = 0, skipped = 0;
            for (int i = 0; i < _tierDefBlindFrames.Length; i++)
            {
                if (_sender.StateIsIdle || !_sender.ConnectionIsConnected) break;
                var frame = _tierDefBlindFrames[i];
                if (frame == null) continue;
                if (_tierDefBlindSentRounds > 1 && frame.Length >= 10)
                {
                    byte sess = frame[6];
                    int seq = frame[8] | (frame[9] << 8);
                    if (!_sender.Retransmitter.Contains(sess, seq))
                    {
                        // Already acked — don't waste a slot in the priority-
                        // free one-shot lane.
                        skipped++;
                        continue;
                    }
                }
                _sender.SendRawFrame(frame);
                sent++;
            }
            MozaLog.Debug(
                $"[AZOM] Blind retransmit round {_tierDefBlindSentRounds}/{TierDefBlindMaxRounds} " +
                $"({sent}/{_tierDefBlindFrames.Length} chunks sent, {skipped} already acked, " +
                $"next gate {gateMs}ms)");
            if (_tierDefBlindSentRounds >= TierDefBlindMaxRounds)
                _tierDefBlindFrames = null;
        }

        private bool AllBlindChunksAcked()
        {
            if (_tierDefBlindFrames == null) return true;
            foreach (var frame in _tierDefBlindFrames)
            {
                if (frame == null || frame.Length < 10) continue;
                byte session = frame[6];
                int seq = frame[8] | (frame[9] << 8);
                if (_sender.Retransmitter.Contains(session, seq))
                    return false;
            }
            return true;
        }
    }
}
