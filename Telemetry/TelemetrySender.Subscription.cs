using System;
using System.Linq;
using System.Threading;
using System.Timers;
using GameReaderCommon;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.Telemetry.Frames;
using MozaPlugin.Telemetry.Sessions;
using MozaPlugin.Telemetry.TestMode;
using MozaPlugin.Telemetry.TileServer;
using Timer = System.Timers.Timer;

namespace MozaPlugin.Telemetry
{
    public partial class TelemetrySender
    {

        // ── Port probing ────────────────────────────────────────────────────


        /// <summary>
        /// Wait for the wheel's pre-tier-def channel registration burst to stop
        /// arriving. Polls <see cref="ChannelCatalogParser.LastActivityMs"/> — once the
        /// last activity is older than <paramref name="quietMs"/>, we assume the
        /// wheel is done pushing its channel URLs.
        /// </summary>
        
        /// <summary>
        /// Build a new <see cref="MultiStreamProfile"/> with only the channels
        /// whose <c>Url</c> appears in <paramref name="catalog"/>. Tiers that
        /// end up empty are dropped. URL match is case-insensitive and also
        /// accepts catalog entries matching the last path segment (the wheel
        /// sometimes advertises bare names where the profile uses a full URL).
        /// </summary>
        
        /// <summary>
        /// Build a V0 subscription profile from the wheel's full channel catalog.
        /// Each catalog URL becomes a channel; metadata (compression, SimHub
        /// property/field, scale) is borrowed from the host's profile when a
        /// URL match exists, otherwise sane defaults (uint32_t, Zero field) are
        /// applied so the channel is still subscribed and value frames go out.
        /// Single tier at the host profile's base PackageLevel — V0 firmware
        /// resolves per-channel update cadence internally so per-tier scheduling
        /// is irrelevant.
        /// </summary>
        
        /// <summary>
        /// PitHouse byte-faithful tier-channel transform: for each unique
        /// Channels list in the profile, REMOVE channels whose URLs aren't
        /// in the wheel's catalog (chIdx would be 0 → tier-def can't
        /// reference them) and SORT the remaining channels by catalog idx
        /// ascending. Recomputes <c>tier.TotalBits</c>/<c>tier.TotalBytes</c>
        /// so downstream FrameBuilders size their bit-pack buffers
        /// correctly.
        ///
        /// MultiStreamProfile's tier-broadcast expansion shares Channels
        /// lists by reference across tier replicas, so mutating each unique
        /// list once propagates to every replica.
        ///
        /// IMPORTANT: this method does NOT touch <see cref="_tiers"/> or
        /// rebuild FrameBuilders — callers do that explicitly. Two call
        /// sites:
        ///   * Profile setter (pre-FrameBuilder-construction): mutate the
        ///     incoming profile in-place so the immediately-following
        ///     `new TelemetryFrameBuilder(tier, …)` loop builds sized-
        ///     correctly buffers against the filtered Channels list.
        ///   * ApplySubscription (post-catalog-grow): mutate the live
        ///     profile and then rebuild <see cref="_tiers"/> Builders via
        ///     <see cref="RebuildFrameBuildersFromProfile"/>.
        /// </summary>
        
        /// <summary>
        /// Rebuild per-tier FrameBuilders from <see cref="_profile"/>.
        /// Called after <see cref="SortTierChannelsByCatalogIdx"/> mutates
        /// the live profile post-Profile-setter (e.g. ApplySubscription
        /// after catalog growth). Safe to call when _tiers is null/empty.
        /// </summary>
        
        /// <summary>
        /// Single point of entry for (re-)subscribing to the wheel's channel
        /// catalog. Swaps the active profile to match the catalog, sends
        /// tier-def + channel config, and atomically publishes the new
        /// subscription state for the telemetry tick handler.
        /// </summary>
        /// <param name="force">When true, bypass the no-op early-out in
        /// <see cref="MaybeSwapProfileForCatalog"/> and re-emit unconditionally.</param>
        /// <param name="reuseFlagBase">When true and an ActiveSubscription
        /// already exists, the new tier-def emits at the SAME flagBase as the
        /// prior emission (and does not advance <see cref="NextFlagBase"/>).
        /// Set by the catalog-growth re-emit path so newly-discovered URLs bind
        /// into the existing subscription instead of starting a new one.
        /// Also suppresses the destructive in-body clears
        /// (<see cref="_retransmitter"/>, <see cref="_propertyPushQueue"/>,
        /// <see cref="_subscriptionResponseChunks"/>) which would otherwise wipe
        /// pending chunks the wheel hasn't acked yet on a same-base re-emit.</param>
        internal void ApplySubscription(bool force, bool reuseFlagBase = false)
        {
            // First-call era resolution. Auto-mode picks Era2024/2025/2026
            // here based on the wheel's catalog push (or absence thereof) and
            // its identity probe. After this returns, _policy is the final
            // policy used for tier-def emission and value-frame routing.
            ResolveAutoPolicy();

            MaybeSwapProfileForCatalog(force: force);
            if (_profile == null || _profile.Tiers.Count == 0)
                return;

            // Note: don't defer on missing URLs. Wheel only pushes the new
            // dashboard's URL→idx mapping AFTER it sees the plugin's tier-def
            // (presumably as a correction). Deferring creates deadlock: wheel
            // waits for tier-def, plugin waits for catalog (verified
            // moza-wire 164047 — post-FF wheel sent only end-marker 06 04 ...
            // val=8 until plugin emitted, then nothing came back). Send with
            // whatever catalog we have; renegotiate-on-grow re-emits when
            // wheel pushes corrected mappings.

            // Preamble is one-shot per session (captures: bridge-20260503-*).
            // Don't reset _tierDefPreambleSent here — session start handles it.
            // Same-base re-emits (reuseFlagBase=true) skip these clears: the
            // wheel's existing binding stays valid, so wiping in-flight retx
            // entries and queued FF property pushes mid-flight would kill the
            // blind-retx safety net for chunks already on the wire.
            //
            // The retx drop is scoped to the superseded tier-def generation.
            // A blanket queue Clear() here also wiped current-generation chunks
            // — the FF init handshake most damagingly, since a lost init chunk
            // leaves the tier-def uncommitted and the dash renders empty for the
            // rest of the session (wake-from-sleep bundle 1KY1PZ4M).
            if (!reuseFlagBase)
            {
                _tierDefEmitter.DropTrackedTierDefChunks();
                _propertyPushQueue.Clear();
                lock (_subscriptionResponseChunks) _subscriptionResponseChunks.Clear();
                Interlocked.Exchange(ref _subscriptionResponseDeadlineTicks, 0);
            }

            // If the catalog revealed a different tier-def session than when we
            // first sent the FF-init handshake (Form B: catalog landed on 0x02,
            // flipping the FF session from 0x02 to 0x01), the wheel acked the
            // init on the OLD session and won't accept kind=4 / commit the
            // tier-def on the new FF session — symptom: dash renders but shows
            // no data. Re-send the init handshake on the now-correct FF session
            // before the tier-def below so the binding actually commits. Fires
            // at most once per cycle (after re-send the sessions match).
            byte ffNow = ResolveFfSession();
            if (_initHandshakeSession != 0 && _initHandshakeSession != ffNow)
            {
                MozaLog.Debug(
                    $"[AZOM] FF session moved 0x{_initHandshakeSession:X2}→0x{ffNow:X2} after catalog " +
                    "arrived — re-sending init handshake on the corrected FF session so kind=4/tier-def commit.");
                SendSessionInitHandshake();
            }

            _tierDefEmitter.SendTierDefinition(reuseFlagBase);
            SendChannelConfig();

            int chCount = 0;
            foreach (var t in _profile.Tiers) chCount += t.Channels.Count;
            MozaLog.Debug(
                $"[AZOM] Subscription applied: \"{_profile.Name}\" " +
                $"{chCount}ch/{_profile.Tiers.Count}t " +
                $"catalog={_catalogParser.Count}");
        }

        /// <summary>
        /// One-shot auto-era resolution. The policy is always built from
        /// <see cref="MozaWheelEra.Auto"/>, so this walks the available signals
        /// and replaces the provisional policy with a pinned one. Idempotent:
        /// guarded by <see cref="_autoResolutionDone"/> so subsequent
        /// dashboard-switch re-applications don't re-resolve mid-session.
        /// </summary>
        /// <remarks>
        /// Decision order (per plan §3):
        ///   1. <c>_catalogParser.Catalog</c> non-empty → Era2026 (catalog push
        ///      is the strongest signal that the wheel speaks Type02).
        ///   2. <c>EraPolicy.GuessFromWheelModel(WheelModelName)</c> hits →
        ///      use that.
        ///   3. Default to Era2026 (the live V2/Type02 path; its compact
        ///      builder fallback covers catalog-less wheels too).
        /// </remarks>
        private void ResolveAutoPolicy()
        {
            if (!_policy.IsAuto) return;
            if (_autoResolutionDone) return;
            _autoResolutionDone = true;

            MozaWheelEra resolved;
            string reason;
            int catalogCount = _catalogParser.Count;
            if (IsStandaloneDashboardTarget)
            {
                // CM2 standalone dashboard runs Type02-era firmware (verified by
                // dev_id=0x12 / group=0x32 protocol surface). Without this pin,
                // the policy resolver waits for a wheel catalog that never arrives.
                resolved = MozaWheelEra.Era2026;
                reason = $"standalone dashboard target {TargetDescription}";
            }
            else if (catalogCount > 0)
            {
                resolved = MozaWheelEra.Era2026;
                reason = $"wheel-catalog={catalogCount}";
            }
            else
            {
                string modelName = MozaPlugin.Instance?.Data?.WheelModelName ?? "";
                var guess = EraPolicy.GuessFromWheelModel(modelName);
                if (guess.HasValue)
                {
                    resolved = guess.Value;
                    reason = $"wheel-model=\"{modelName}\"";
                }
                else
                {
                    resolved = MozaWheelEra.Era2026;
                    reason = $"default (no catalog, model=\"{modelName}\" unmatched)";
                }
            }

            var newPolicy = EraPolicy.For(resolved);
            // Preserve the Auto-mark so the upload-wire-format fallback stays
            // available even after resolving. The wheel may accept tier-def
            // under one wire format and need the other for the dashboard
            // upload (different sub-msg layouts on different boards).
            newPolicy.IsAuto = true;
            if (resolved == MozaWheelEra.Era2026)
                newPolicy.AutoFallbackUploadWireFormat = true;

            _policy = newPolicy;
            MozaLog.Debug($"[AZOM] Auto era resolved → {resolved} ({reason})");
        }

        /// <summary>
        /// Send the tier definition message on the telemetry session.
        /// This is the critical config data that tells the wheel firmware how to
        /// decode each flag byte's bit-packed telemetry data: which channels are
        /// in each tier, their compression codes, and bit widths.
        ///
        /// Pithouse sends this as 7c:00 data chunks (type=0x01) on session 0x02
        /// during the first ~1s after session open. Without it, the wheel silently
        /// ignores all 7d:23 telemetry frames.
        /// </summary>
        
        /// <summary>
        /// Swap the active <see cref="Profile"/> for one synthesized from the
        /// wheel's advertised channel catalog when the Type02 wire format is in
        /// use. Removes the mzdash dependency on the subscription axis — plugin
        /// subscribes to whatever the wheel declared and feeds those channels
        /// from SimHub via the URL→property mapping in
        /// <see cref="DashboardProfileStore"/>. No-op when catalog isn't parsed
        /// yet, when era isn't Type02, or when the synthesized profile would be
        /// empty (e.g. wheel advertised zero channels).
        /// </summary>
        /// <remarks>
        /// Public entry point for <see cref="MozaPlugin.ApplyTelemetrySettings"/>
        /// to swap immediately after setting Profile, so the UI reads the
        /// catalog-based channel list instead of the builtin's fixed set.
        /// </remarks>
        public void SwapProfileForCatalogIfType02() => MaybeSwapProfileForCatalog(force: true);

        private void MaybeSwapProfileForCatalog(bool force = false)
        {
            // Fallback path: synthesise a profile from the wheel-advertised
            // channel catalog when no mzdash-derived profile is loaded.
            // Existing mzdash flow (Profile != null after ApplyTelemetrySettings)
            // takes precedence — this hook does nothing when the user has a
            // local mzdash folder configured. For users with no folder
            // configured, this lets telemetry flow from whatever the wheel
            // declared in its tag=0x04 catalog on b2h sess=0x01.
            //
            // The earlier disable (clobbered user-selected profile, dropped
            // channels post-switch on incomplete back-ref catalog) doesn't
            // apply: (a) we only synthesise when _profile is null, so a
            // user-selected mzdash profile is never replaced; (b) we gate
            // on LastWheelEndMarker != 0 so the wheel has committed to a
            // tier-def generation before we build — back-ref-only catalogs
            // (END marker still 0) defer until the wheel emits full URLs.
            bool currentIsSynthesised =
                _profile != null && _profile.Name == CatalogProfileName;

            // User-loaded mzdash profile: never replace.
            if (_profile != null && _profile.Tiers.Count > 0 && !currentIsSynthesised)
                return;

            // Prefer LiveCatalog (the wheel's currently-loaded dashboard's
            // idxs only, with stale prior-dash slots blanked) so dashboard
            // switches produce a profile sized to the new dash. Fall back
            // to Catalog on cold start before the first END-marker commit.
            var catalog = _catalogParser.LiveCatalog ?? _catalogParser.Catalog;
            if (catalog == null || catalog.Count == 0)
                return;

            if (_catalogParser.LastWheelEndMarker == 0)
                return;

            // Re-synthesis trigger: catalog count grew or the catalog body
            // changed (signature — e.g. the parser corrected back-ref/
            // abbreviated URLs in place, or a dashboard switch rebound idxs to
            // new URLs). Skip when BOTH count and signature are unchanged.
            //
            // The wheel's END marker is DELIBERATELY excluded: it is a
            // cumulative counter that advances on every catalog re-advertisement
            // (keepalive), even when the channel set is byte-for-byte identical
            // (observed END 68→136→244 for the same 68-channel catalog). Keying
            // re-synthesis on END advance made every keepalive look like a new
            // generation → re-arm → re-emit → the wheel re-advertised (END++) →
            // re-arm again: a feedback loop that marched the flag base without
            // end (0x00→0x3F over 64 emissions) and let partial re-advertisements
            // collapse the bound channel set. A real dashboard switch always
            // changes count or signature, so content-keying still catches it.
            ulong catalogHash = ComputeCatalogHash(catalog);
            if (currentIsSynthesised
                && _catalogCountAtSynthesis == catalog.Count
                && _catalogHashAtSynthesis == catalogHash
                && !force)
                return;

            // Debounce the progressive catalog burst into a single emit (see the
            // field comment). The catalog differs from the last synthesis; hold
            // off until it has stopped changing for CatalogDebounceTicks. Each
            // change restarts the timer; once quiet, fall through and synthesise
            // once. force bypasses (explicit swap / hot-switch must act now).
            if (!force)
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                if (_pendingCatalogCount != catalog.Count
                    || _pendingCatalogHash != catalogHash)
                {
                    // Catalog just changed (or changed again mid-burst) — (re)arm.
                    _pendingCatalogCount = catalog.Count;
                    _pendingCatalogHash = catalogHash;
                    _pendingCatalogSinceTicks = nowTicks;
                    return;
                }
                if (nowTicks - _pendingCatalogSinceTicks < CatalogDebounceTicks)
                    return;   // still settling — wait for the burst to finish
                // Quiet long enough: fall through and synthesise this catalog once.
            }

            var store = MozaPlugin.Instance?.DashProfileStore;
            if (store == null)
                return;

            MultiStreamProfile synthesised;
            try
            {
                bool includeRadar = MozaPlugin.Instance?.Settings?.EnableRadarTrackMapChannels ?? false;
                synthesised = store.BuildProfileFromCatalog(catalog, CatalogProfileName, includeRadar);
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Catalog-only profile synthesis failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (synthesised?.Tiers == null || synthesised.Tiers.Count == 0)
                return;

            // Apply user channel overrides (same selector the binding
            // coordinator uses for the mzdash path). DashboardBindingCoordinator
            // skipped this branch when profile was null at apply time, so the
            // synthesised path owns it. Resolved per active dashboard key
            // candidate (wheel:<id> > file:<name>:<sha> > builtin:<name>).
            //
            // Cold-start race: the wheel:<id> candidate needs the configJson
            // state (EnabledDashboards), which can land AFTER this catalog burst
            // — so a first synth here may resolve 0 overrides. The catalog-keyed
            // dedup at the top would then pin the mapping-less profile until a
            // dashboard switch. ReapplyUserChannelMappingsAfterConfigJson (called
            // from the inbound configJson StateReady handler) closes that race by
            // re-applying to the live profile once the state arrives.
            int mappedCount = ApplyUserChannelMappings(synthesised);

            int chCount = 0;
            foreach (var t in synthesised.Tiers) chCount += t.Channels.Count;
            MozaLog.Debug(
                $"[AZOM] Synthesised catalog-only profile: {chCount}ch in {synthesised.Tiers.Count}t " +
                $"+ {synthesised.StringChannels.Count} strings (catalog={catalog.Count}, " +
                $"endMarker={_catalogParser.LastWheelEndMarker}, userMappings={mappedCount})");

            // Did the catalog actually advance to a new generation? Capture
            // BEFORE updating the tracking fields. NOTE: the dedup near the top
            // of this method is bypassed when force=true (the hot-switch burst
            // re-emits with force), so reaching here does NOT imply a real
            // change — we must compare explicitly or the re-arm below loops
            // forever on every forced burst frame.
            // Content-keyed (NOT END-keyed) — see the dedup guard above: the
            // wheel's END marker advances on every keepalive re-advertisement, so
            // re-arming on END advance self-perpetuates a flag-base-marching
            // feedback loop. A genuine new generation (catalog grew, or switch
            // rebound idxs) always changes count or signature.
            bool generationChanged =
                _catalogCountAtSynthesis != catalog.Count
                || _catalogHashAtSynthesis != catalogHash;

            _catalogEndMarkerAtSynthesis = _catalogParser.LastWheelEndMarker;
            _catalogCountAtSynthesis = catalog.Count;
            _catalogHashAtSynthesis = catalogHash;

            // Going through the Profile setter so multi-broadcast expansion
            // and _tiers allocation match the mzdash path exactly. The setter
            // preserves Name, so subsequent calls can detect the synthesised
            // profile by Profile.Name == CatalogProfileName.
            Profile = synthesised;

            // Re-arm the hot-switch burst ONLY when the catalog genuinely
            // advanced to a new generation. On a dashboard switch the wheel
            // builds its catalog up across several generations (e.g. END
            // 6→80→97 over ~3 s), but the switch's emission burst is a fixed
            // 3–8 frames ~1 s apart — so the burst can finish on a PARTIAL
            // generation (5 ch) before the final one (the 21-ch radar set)
            // commits, and the complete tier-def never reaches the wheel until
            // the user switches again. Re-arming on each real advance fixes
            // that, and converges because a stable catalog stops advancing.
            // Gating on generationChanged is essential: the forced burst
            // re-emissions hit this path with the SAME generation, and an
            // unconditional re-arm there self-perpetuates the burst forever
            // (flagBase marches without end — observed 2026-06-04).
            if (generationChanged)
                _hotSwitch.ArmBurst(countsAsSwitch: false);

            // Notify the UI that the channel set has changed. The
            // DashboardSelectionChanged event is normally raised at the
            // moment the switch is INITIATED (by either the dropdown handler
            // or OnWheelInitiatedSwitch), but at that point sender.Profile
            // is either null or the prior dash's profile — so the channel-
            // mapping grid populates with stale data and waits on the 500 ms
            // RefreshTelemetryStatus polling tick to notice the new synth.
            // Hash collisions in ComputeMappingDataSignature (24-bit hash +
            // count masks) or a second switch arriving inside the 500 ms
            // window can leave the grid stuck on the prior dash's channels.
            // Raising here means the UI re-runs PopulateChannelMappingList
            // against the live Profile deterministically. The early return
            // at the top of MaybeSwapProfileForCatalog (catalog count +
            // endMarker dedup) ensures this only fires when the wheel
            // actually advanced state, not on every timer tick.
            //
            // Previously rolled back when the wheel was independently
            // wedged at startup, but the wedge had a different root cause
            // (sess=0x01/0x02 not engaging — now handled by the hard-
            // recovery path in ProbeAndOpenSessions). Restoring with the
            // same gating: only fires after a real catalog/endMarker advance.
            MozaPlugin.Instance?.RaiseDashboardSelectionChangedInternal();
        }

        /// <summary>
        /// Resolve the active channel-mapping overrides (profile × page ×
        /// dashboard key) and apply them to <paramref name="profile"/>'s channels
        /// in place, overriding each matched URL's
        /// <see cref="ChannelDefinition.SimHubProperty"/>. Returns the number of
        /// override entries applied; 0 when none resolve (e.g. the wheel:&lt;id&gt;
        /// dashboard key can't be resolved yet because the configJson state hasn't
        /// arrived). Only the per-channel property binding changes — the wire
        /// layout is untouched, so callers need no tier-def re-emit (the frame
        /// builder reads ch.SimHubProperty live each frame). The dashboard key is
        /// resolved per candidate (wheel:&lt;id&gt; > file:&lt;name&gt;:&lt;sha&gt;
        /// > builtin:&lt;name&gt;), or the fixed <see cref="MappingDashKeys"/> for
        /// a CM2 sender.
        /// </summary>
        private int ApplyUserChannelMappings(MultiStreamProfile profile)
        {
            if (profile == null) return 0;
            var plugin = MozaPlugin.Instance;
            if (plugin == null) return 0;
            var channelMap = plugin.GetActiveChannelMappings(MappingPageGuid);
            if (channelMap == null) return 0;
            var keys = MappingDashKeys ?? plugin.ChannelMapping.GetActiveDashboardKeyCandidates();
            foreach (var dashKey in keys)
            {
                if (channelMap.TryGetValue(dashKey, out var overrides) && overrides != null)
                {
                    DashboardProfileStore.ApplyUserMappings(profile, overrides);
                    return overrides.Count;
                }
            }
            return 0;
        }

        /// <summary>
        /// Re-resolve and re-apply user channel mappings to the live profile after
        /// the wheel's configJson state (EnabledDashboards) arrives. Invoked from
        /// the inbound configJson StateReady handler.
        ///
        /// The catalog-only synth (<see cref="MaybeSwapProfileForCatalog"/>)
        /// applies user mappings keyed on the active dashboard key, which
        /// <see cref="Telemetry.ChannelMappingCoordinator.GetActiveDashboardKeyCandidates"/> resolves from
        /// the wheel's configJson (the wheel:&lt;id&gt; candidate). On cold start
        /// the wheel's catalog burst can land BEFORE its configJson burst
        /// (verified: catalog at T, configJson ~1.2 s later), so the first synth
        /// resolves 0 overrides and the catalog-keyed dedup then pins the
        /// mapping-less profile until a dashboard switch forces a re-synth — the
        /// reported "custom mappings don't load until I switch dashboards" bug.
        /// Re-applying here closes that race with no tier-def re-emit: only the
        /// per-channel SimHubProperty binding changes, picked up on the next value
        /// frame (see <see cref="TelemetryFrameBuilder"/>'s live property read).
        /// Idempotent when mappings were already applied or none are configured.
        /// </summary>
        internal void ReapplyUserChannelMappingsAfterConfigJson()
        {
            var profile = _profile;
            if (profile == null || profile.Tiers.Count == 0) return;
            int n = ApplyUserChannelMappings(profile);
            if (n > 0)
                MozaLog.Debug(
                    $"[AZOM] Re-applied {n} user channel mapping(s) to live \"{profile.Name}\" " +
                    "after configJson state arrived (cold-start catalog-before-configJson race)");
        }

        /// <summary>
        /// Rebind the live catalog-synth profile's per-channel SimHubProperty to the
        /// CURRENTLY-active dashboard (catalog default + this dashboard's user
        /// overrides), in place, then raise <see cref="MozaPlugin.DashboardSelectionChanged"/>
        /// so the channel-mapping UI repaints.
        ///
        /// Catalog-only switches between same-catalog dashboards differ ONLY in the
        /// host-side SimHubProperty bindings — the tier-def wire content (URLs,
        /// compression, structure) is identical. Both swap guards key on wire content
        /// and ignore SimHubProperty: the <see cref="Profile"/> setter's
        /// <see cref="AreProfileContentsEquivalent"/> check no-ops the reassignment,
        /// and <see cref="MaybeSwapProfileForCatalog"/> dedups on catalog count +
        /// URL signature. So a host (UI) switch leaves the live profile carrying the
        /// PRIOR dashboard's bindings and never notifies the UI. (The wheel-initiated
        /// path sidesteps both guards by nulling Profile first — which we must not do
        /// on host switches, as rapid nulling is the 2026-05-26 sess=0x01 wedge that
        /// <c>keepExistingSynth</c> prevents.)
        ///
        /// This rebinds each live channel by URL to catalog-default + active overrides,
        /// RESETTING stale prior-dashboard overrides on channels the new dashboard does
        /// not map (the correctness gain over <see cref="ReapplyUserChannelMappingsAfterConfigJson"/>,
        /// which only adds overrides). Property AND scale are copied: an override forces
        /// scale 1 (<c>DashboardProfileStore.ResolveDefaultBinding</c>), so carrying only
        /// the property would leave the prior dashboard's scale on the new binding.
        /// No Profile setter, no frame-builder rebuild
        /// (the builder reads SimHubProperty live per frame), no wire writes — so the
        /// value stream picks up the new bindings on the next frame and the wedge can't
        /// fire. No-op until the wheel has committed a catalog generation.
        /// </summary>
        internal void ReResolveActiveDashboardMappings()
        {
            var profile = _profile;
            if (profile == null || profile.Tiers.Count == 0) return;
            var catalog = _catalogParser.LiveCatalog ?? _catalogParser.Catalog;
            if (catalog == null || catalog.Count == 0) return;
            var store = MozaPlugin.Instance?.DashProfileStore;
            if (store == null) return;

            // Fresh catalog-default bindings for every URL, then this dashboard's
            // user overrides on top — identical resolution to a MaybeSwap rebuild.
            MultiStreamProfile resolved;
            try
            {
                bool includeRadar = MozaPlugin.Instance?.Settings?.EnableRadarTrackMapChannels ?? false;
                resolved = store.BuildProfileFromCatalog(catalog, CatalogProfileName, includeRadar);
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] ReResolveActiveDashboardMappings: synth failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }
            if (resolved?.Tiers == null || resolved.Tiers.Count == 0) return;
            ApplyUserChannelMappings(resolved);

            var byUrl = new System.Collections.Generic.Dictionary<string, (string prop, double scale)>(
                System.StringComparer.OrdinalIgnoreCase);
            foreach (var t in resolved.Tiers)
                foreach (var ch in t.Channels)
                    if (!string.IsNullOrEmpty(ch.Url))
                        byUrl[ch.Url] = (ch.SimHubProperty ?? "", ch.SimHubPropertyScale);
            foreach (var sc in resolved.StringChannels)
                if (!string.IsNullOrEmpty(sc.Url))
                    byUrl[sc.Url] = (sc.SimHubProperty ?? "", sc.SimHubPropertyScale);

            // Copy resolved bindings onto the LIVE profile's existing channel objects
            // in place (reference-atomic string writes; same pattern as
            // ReapplyUserChannelMappingsAfterConfigJson). Plugin-locked channels keep
            // their internal sentinel.
            int changed = 0;
            foreach (var t in profile.Tiers)
                foreach (var ch in t.Channels)
                {
                    if (DashboardProfileStore.IsInternalChannel(ch.SimHubProperty)) continue;
                    if (ch.Url != null && byUrl.TryGetValue(ch.Url, out var b)
                        && (!string.Equals(ch.SimHubProperty ?? "", b.prop, StringComparison.Ordinal)
                            || !ch.SimHubPropertyScale.Equals(b.scale)))
                    {
                        // Scale first — see ChannelDefinition.SimHubPropertyScale.
                        ch.SimHubPropertyScale = b.scale;
                        ch.SimHubProperty = b.prop;
                        changed++;
                    }
                }
            foreach (var sc in profile.StringChannels)
            {
                if (DashboardProfileStore.IsInternalChannel(sc.SimHubProperty)) continue;
                if (sc.Url != null && byUrl.TryGetValue(sc.Url, out var b)
                    && (!string.Equals(sc.SimHubProperty ?? "", b.prop, StringComparison.Ordinal)
                        || !sc.SimHubPropertyScale.Equals(b.scale)))
                {
                    sc.SimHubPropertyScale = b.scale;
                    sc.SimHubProperty = b.prop;
                    changed++;
                }
            }

            MozaLog.Debug(
                $"[AZOM] ReResolveActiveDashboardMappings: rebound {changed} live channel " +
                $"binding(s) to active dashboard for \"{profile.Name}\" (catalog={catalog.Count})");
            MozaPlugin.Instance?.RaiseDashboardSelectionChangedInternal();
        }
    }
}
