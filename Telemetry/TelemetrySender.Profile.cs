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

        private void SetProfileLocked(MultiStreamProfile? value)
        {
            // Idempotency guard, layer 1 — reference identity. Callers
            // (ApplyTelemetrySettings, OnWheelInitiatedSwitch,
            // OnDashboardSwitched, MaybeSwapProfileForCatalog) can fire
            // this setter several times in close succession with the
            // same source profile — e.g. UI combo click →
            // ApplyTelemetrySettings → next tick
            // MaybeSwapProfileForCatalog sees catalog unchanged but
            // assigns the cached synthesised profile back in. Each
            // re-assignment re-runs the multi-broadcast expansion (~N
            // new DashboardProfile allocations), rebuilds every per-
            // tier FrameBuilder, and resets the OriginalChannels
            // pristine snapshot. Reference-compare so the same input
            // → no-op; null-to-null also short-circuits cleanly.
            if (ReferenceEquals(value, _lastProfileSourceRef)) return;

            // Idempotency guard, layer 2 — content equality. The
            // catalog-synthesis path (MaybeSwapProfileForCatalog) builds
            // a fresh MultiStreamProfile instance every call from the
            // wheel's current catalog. If catalog content hasn't
            // changed, the new instance is byte-equivalent to the
            // previously-assigned one even though their object refs
            // differ — and that triggers the same full FrameBuilder
            // rebuild + tier-def re-emission as a real change. The
            // wheel firmware has been observed (2026-05-26
            // moza-wire-...-043633) to wedge sess=0x01 into a close-
            // reopen loop when bombarded with 9 functionally-identical
            // tier-defs in a few seconds. Catching content-equivalent
            // assignments here means downstream rebuild + emission
            // only fires on actual change. Hot-switch / cold-start
            // emissions still work because they call
            // ApplySubscription which sends tier-def directly, not via
            // the Profile setter side effect.
            //
            // Gate on _profile being live too. If _profile was cleared
            // (Stop/Reset path) but _lastProfileSourceRef still points
            // at the prior assignment, the content match would no-op
            // away the rebuild we actually need to bring _profile back.
            if (value != null && _profile != null && _lastProfileSourceRef != null
                && AreProfileContentsEquivalent(value, _lastProfileSourceRef))
            {
                // Swap the source ref to the latest instance so
                // ReferenceEquals catches further repeats at layer 1.
                _lastProfileSourceRef = value;
                return;
            }
            // Off→on transition (telemetry was disabled, user re-enabled
            // it / selected a profile from the empty state): treat as an
            // explicit "fresh attempt" signal and forgive any prior
            // recovery-budget exhaustion so the new attempt starts with a
            // clean budget. The existing wheel-hot-swap path covers the
            // hardware-change case (ResetBindingTracking); this covers
            // the user-driven case.
            if (value != null && _lastProfileSourceRef == null)
            {
                try { _recovery.Reset(); } catch { }
            }
            _lastProfileSourceRef = value;

            if (value != null && value.Tiers.Count > 0)
            {
                // Single broadcast: each tier fires at its own package rate.
                // The prior strategy replicated every sub-tier into max(4, N+1)
                // copies with parallel flag bytes to push slow-pkg channels at
                // the base rate, but it also replicated the already-fast tier,
                // multiplying wire load ~4× and saturating the shared base wire.
                var subTiers = new System.Collections.Generic.List<DashboardProfile>(value.Tiers);
                subTiers.Sort((a, b) => a.PackageLevel.CompareTo(b.PackageLevel));
                int subCount = subTiers.Count;
                int broadcasts = 1;
                var expanded = new System.Collections.Generic.List<DashboardProfile>(subCount * broadcasts);
                // One Channels copy per source sub-tier, shared across
                // its broadcast replicas. COPY so the in-place mutate in
                // SortTierChannelsByCatalogIdx doesn't corrupt the cached
                // profile; SHARE so the dedup-by-reference in that sort
                // still mutates each unique list exactly once.
                var copiedChannelsForSrc =
                    new System.Collections.Generic.Dictionary<DashboardProfile, System.Collections.Generic.List<ChannelDefinition>>();
                foreach (var src in subTiers)
                {
                    copiedChannelsForSrc[src] =
                        new System.Collections.Generic.List<ChannelDefinition>(src.Channels);
                }
                for (int b = 0; b < broadcasts; b++)
                {
                    foreach (var src in subTiers)
                    {
                        expanded.Add(new DashboardProfile
                        {
                            Name = $"{src.Name}@b{b}",
                            Channels = copiedChannelsForSrc[src],
                            TotalBits = src.TotalBits,
                            TotalBytes = src.TotalBytes,
                            PackageLevel = src.PackageLevel,
                            FlagByte = src.FlagByte,
                        });
                    }
                }
                value = new MultiStreamProfile
                {
                    Name = value.Name,
                    PageCount = value.PageCount,
                    Tiers = expanded,
                    // Strings are out-of-band; carry through unchanged
                    // (the expanded profile becomes the live _profile).
                    StringChannels = value.StringChannels,
                };
            }
            if (value == null || value.Tiers.Count == 0)
            {
                _tiers = null;
                _baseTickMs = 33;
                _profile = value;
                return;
            }

            if (value.StringChannels.Count > 0)
            {
                var urls = string.Join(", ", value.StringChannels.Select(c =>
                    string.IsNullOrEmpty(c.SimHubProperty)
                        ? c.Url
                        : $"{c.Url}→{c.SimHubProperty}"));
                MozaLog.Debug(
                    $"[AZOM] Profile '{value.Name}' has {value.StringChannels.Count} " +
                    $"string channels (sess=0x01 type=0x05): {urls}");
            }

            // Base tick = fastest tier's pkg_level (smallest).
            int minPkg = int.MaxValue;
            foreach (var t in value.Tiers)
                if (t.PackageLevel > 0 && t.PackageLevel < minPkg) minPkg = t.PackageLevel;
            int baseTickMs = (minPkg == int.MaxValue) ? 30 : minPkg;
            _baseTickMs = baseTickMs;

            // Do NOT apply the catalog-driven sort+filter here. The
            // catalog state at Profile-set time is often stale (cold
            // start: catalog hasn't arrived; post-dashboard-switch:
            // catalog still has the PREVIOUS dashboard's URLs because
            // the wheel sends new catalog over ~1s after the
            // Stop+Start cycle). Defer to ApplySubscription which
            // (a) has a fresher catalog and (b) re-runs from the
            // pristine OriginalChannels every time, so a stale-catalog
            // filter result doesn't permanently strip channels.

            // Built into a local and published only when fully filled — the
            // tick thread samples _tiers unsynchronized and must never see
            // null elements.
            var tiers = new TierState[value.Tiers.Count];
            var tierDiag = new System.Text.StringBuilder();
            tierDiag.Append($"[AZOM] Profile setter: \"{value.Name}\" {value.Tiers.Count}t baseTickMs={baseTickMs}");
            for (int i = 0; i < value.Tiers.Count; i++)
            {
                var tier = value.Tiers[i];
                int tickInterval = Math.Max(1, tier.PackageLevel / baseTickMs);
                // Lowest per-car radar/track-map slot in this tier (0 if none) —
                // gates dynamic per-grid emission below. A tier whose lowest slot
                // is above the live car count carries only absent cars this frame.
                int minRadarSlot = 0;
                bool tierHasSlots = false;
                foreach (var ch in tier.Channels)
                {
                    int s = Dashboard.DashboardProfileStore.RadarTrackMapSlotIndex(ch.Url);
                    if (s < 0) continue;
                    if (!tierHasSlots || s < minRadarSlot) { minRadarSlot = s; tierHasSlots = true; }
                }
                tierDiag.Append($" | t[{i}]={tier.Name} {tier.Channels.Count}ch pkg={tier.PackageLevel} bits={tier.TotalBits} bytes={tier.TotalBytes}");
                tiers[i] = new TierState
                {
                    MinRadarSlot = tierHasSlots ? minRadarSlot : 0,
                    // PitHouse capture 2026-04-29 in-game shows N=14 (legacy
                    // convention 8+data) on this firmware, NOT Type02 N=16.
                    // Hardcoding type02NConvention=false until per-firmware
                    // detection is correct — the previous heuristic wrongly
                    // pinned Type02 N for this wheel.
                    Builder = new TelemetryFrameBuilder(tier, PropertyResolver,
                        type02NConvention: false,
                        deviceId: _targetDeviceId),
                    TickInterval = tickInterval,
                    // Snapshot the pristine (pre-filter) channel list so
                    // ApplySubscription can refilter from scratch each call.
                    OriginalChannels = new System.Collections.Generic.List<ChannelDefinition>(tier.Channels),
                    OriginalTotalBits = tier.TotalBits,
                    OriginalTotalBytes = tier.TotalBytes,
                };
            }
            _builtWithResolverTarget = PropertyResolver?.Target;
            MozaLog.Debug(tierDiag.ToString());
            _profile = value;
            _tiers = tiers;

            // Apply the catalog-driven filter + sort + FrameBuilder
            // rebuild NOW if the catalog is available. Each Profile
            // setter call builds initial FrameBuilders from the
            // UNFILTERED 10-channel profile (so that OriginalChannels
            // captures the pristine state). Without this immediate
            // re-filter, value frames between Profile setter and the
            // next ApplySubscription leak out at the wrong (10-channel,
            // 37-byte) size — verified 2026-05-15. ApplySubscription
            // will reset + re-filter again later when needed; calls
            // are idempotent.
            var catalog = _catalogParser.Catalog;
            if (catalog != null && catalog.Count > 0)
            {
                _tierDefEmitter.SortTierChannelsByCatalogIdx(value, catalog);
                _tierDefEmitter.RebuildFrameBuildersFromProfile();
            }
        }
        private MultiStreamProfile? _profile;
        // Last UNEXPANDED profile reference passed to the Profile setter.
        // Used for the idempotency short-circuit at the top of the setter so
        // repeat calls with the same source don't re-expand. Tracked separately
        // from _profile because the setter mutates value (multi-broadcast
        // expansion) before assigning to _profile, so we'd never match the
        // original input against the post-expansion stored profile.
        private MultiStreamProfile? _lastProfileSourceRef;

        /// <summary>Structural equality check for two MultiStreamProfile
        /// instances. Returns true iff the wire shape they would produce
        /// (tier count, per-tier channel set + bit widths, string channel
        /// URLs) is identical. Used by the Profile setter to no-op
        /// content-equivalent re-assignments — different instance, same
        /// payload from the wheel's perspective.
        ///
        /// Intentionally compares only the fields that affect the emitted
        /// tier-def + value-frame layout. Fields like SimHubProperty and
        /// SimHubPropertyScale are NOT compared because user-mapping
        /// changes mutate them in-place on the existing profile (rebinding
        /// the host's value source without changing the wire format) —
        /// see ChannelDefinition.SimHubProperty's class doc for the
        /// invariant.</summary>
        private static bool AreProfileContentsEquivalent(
            MultiStreamProfile a,
            MultiStreamProfile b)
        {
            if (!string.Equals(a.Name, b.Name, System.StringComparison.Ordinal)) return false;
            if (a.Tiers.Count != b.Tiers.Count) return false;
            for (int i = 0; i < a.Tiers.Count; i++)
            {
                var ta = a.Tiers[i];
                var tb = b.Tiers[i];
                if (ta.PackageLevel != tb.PackageLevel) return false;
                if (ta.TotalBits != tb.TotalBits) return false;
                if (ta.FlagByte != tb.FlagByte) return false;
                if (ta.Channels.Count != tb.Channels.Count) return false;
                for (int j = 0; j < ta.Channels.Count; j++)
                {
                    var ca = ta.Channels[j];
                    var cb = tb.Channels[j];
                    if (ca.BitWidth != cb.BitWidth) return false;
                    if (!string.Equals(ca.Url, cb.Url, System.StringComparison.OrdinalIgnoreCase)) return false;
                    if (!string.Equals(ca.Compression, cb.Compression, System.StringComparison.OrdinalIgnoreCase)) return false;
                }
            }
            if (a.StringChannels.Count != b.StringChannels.Count) return false;
            for (int i = 0; i < a.StringChannels.Count; i++)
            {
                if (!string.Equals(a.StringChannels[i].Url, b.StringChannels[i].Url,
                                   System.StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        // Per-string-channel emission state: last-sent value and tick timestamp.
        // Keyed by channel URL (case-insensitive). The wheel re-indexes URLs per
        // dashboard so idx is volatile, but the URL string is stable across
        // catalog updates — keying by URL keeps the cadence/dedup state correct
        // when a dashboard switch reshuffles idx assignments.
        private readonly System.Collections.Generic.Dictionary<string, (string lastValue, int lastTickMs)>
            _stringChannelState =
                new System.Collections.Generic.Dictionary<string, (string, int)>(
                    System.StringComparer.OrdinalIgnoreCase);

        private volatile int _framesSent;
        public int FramesSent => _framesSent;

        /// <summary>
        /// Base offset into the connection's stream-slot array for this sender's
        /// periodic frames, so two senders can share one <see cref="MozaSerialConnection"/>
        /// without colliding. 0 (default) = the wheel/primary lane (slots 0..10); a
        /// bus-attached CM2 pipeline sharing the wheelbase connection uses 18 (slots
        /// 18..28). A pipeline on its own connection stays at 0. See
        /// <c>MozaSerialConnection</c> stream-slot layout.
        /// </summary>
        internal int StreamSlotBase { get; set; }

        /// <summary>
        /// When true, the inbound dispatcher accepts only frames whose device id
        /// matches this sender's target exactly (no CM2 fan-in) — required when two
        /// pipelines share one connection so each consumes only its own device's
        /// replies. See <see cref="Lifecycle.TelemetryInboundDispatcher"/>.
        /// </summary>
        public volatile bool StrictInboundFilter;

        /// <summary>
        /// Page GUID this sender's channel mappings are stored under (null = the
        /// current wheel page). A CM2 sender sets this to the CM2 device GUID so its
        /// catalog-synth applies the CM2's mappings, not the wheel's.
        /// </summary>
        internal Guid? MappingPageGuid;

        /// <summary>
        /// Fixed dashboard-key list this sender uses to look up channel mappings
        /// (null = the wheel's ChannelMappingCoordinator.GetActiveDashboardKeyCandidates). A CM2 sender uses a
        /// single fixed key since it catalog-synthesises one dashboard.
        /// </summary>
        internal System.Collections.Generic.IReadOnlyList<string>? MappingDashKeys;

        /// <summary>Send a periodic frame on this sender's lane (slot = base + logical).</summary>
        private void SendStreamSlot(int logicalSlot, byte[] frame) =>
            _connection.SendStream((StreamKind)(StreamSlotBase + logicalSlot), frame);

        /// <summary>Number of stream slots a tier-def pipeline occupies (TierDash0-7 +
        /// Enable + Sequence + Mode).</summary>
        private const int StreamBlockSize = 11;

        /// <summary>
        /// True when this sender shares its <see cref="MozaSerialConnection"/> with a
        /// second pipeline (wheel + bus-CM2). When set, Stop() clears only this
        /// sender's slot lane instead of flushing the whole connection, so stopping/
        /// restarting one pipeline doesn't blank the co-resident one.
        /// </summary>
        public volatile bool SharesConnection;

        /// <summary>
        /// When true, the no-catalog engagement watchdog is suppressed for this sender.
        /// Set while a base-bridged dash's type is undetermined: a CM1 (group-0x35) dash
        /// never advertises a tier-def catalog, so the watchdog would otherwise loop
        /// restarts. <see cref="MozaPlugin.TickCm1Discriminator"/> clears it (or hands off
        /// to the CM1 driver) once the type is known.
        /// </summary>
        public volatile bool SuppressDisplayWatchdog;

        // FSR V1 (group-0x42) display push is handled by the standalone
        // Telemetry/Fsr1DisplayDriver — this sender is pure tier-def.

        /// <summary>True between Start() and Stop(). Exposed for diagnostics panel.
        /// Preserves the prior `_enabled` boolean's external semantics — anything
        /// other than Idle counts as "running".</summary>
        public bool Enabled => _state != TelemetryState.Idle;

        /// <summary>
        /// Atomic state transition with audit logging. Use this everywhere instead
        /// of writing to <see cref="_state"/> directly so every change is visible
        /// in the debug log with its trigger reason. Idempotent: a no-op transition
        /// (next == current) is silently dropped.
        /// </summary>
        private void TransitionTo(TelemetryState next, string reason)
        {
            var prev = _state;
            if (prev == next) return;
            _state = next;
            try { MozaLog.Debug($"[AZOM] state {prev} → {next} ({reason})"); }
            catch { /* logging may not be initialised in tests */ }
        }
        // Read-only accessors for DashboardSwitchAutoTest
        internal byte? ActiveFlagBase => _activeSubscription?.FlagBase;
        internal int ActiveTierCount => _tiers?.Length ?? 0;
        public string? ActiveProfileName => _profile?.Name;
        internal int CatalogChannelCount => _catalogParser.Count;

        // Catalog-parser internals for the Diagnostics tab. Exposes "why is the
        // wheel-catalog list empty in diag" answers without forcing the user to
        // enable debug logging — at a glance you can tell whether chunks are
        // arriving (BufferLength>0), being rejected (CrcRejects>0), or simply
        // never reaching the catalog session in the first place (LastActivity
        // is "never").
        internal int CatalogLiveCount => _catalogParser.LiveCount;
        internal int CatalogBufferLength => _catalogParser.BufferLength;
        internal int CatalogLastParsedBufferLen => _catalogParser.LastParsedBufferLen;
        internal int CatalogLastActivityTickMs => _catalogParser.LastActivityMs;

        private DashboardSwitchAutoTest? _autoTest;

        /// <summary>
        /// Raised once when the sess=0x09 retry budget exhausts and the dashboard
        /// pipeline is parked. The plugin uses this to clear its
        /// <c>_telemetryStartRequested</c> gate so a future
        /// <c>StartTelemetryIfReady</c> (e.g. wheel hot-swap, user toggle) can
        /// re-attempt cleanly. Fires after the sender has called <see cref="Stop"/>
        /// internally.
        /// </summary>
        public event EventHandler? DashboardPipelineParked;
    }
}
