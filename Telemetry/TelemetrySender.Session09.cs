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

        // ── Session management ──────────────────────────────────────────────
        // Session open/close/ack/prime/end frame builders + the open/close
        // state machine live in Sessions/SessionLifecycle.cs. The prime shim
        // stays for DisplayWatchdog + the sess=0x09 keepalive callers.

        internal void SendSessionPrime(byte session, ushort seq) => _sessionLife.SendSessionPrime(session, seq);

        /// <summary>
        /// Per-tick V0 telemetry: one value frame per profile channel on
        /// session 0x02. Wheel firmware indexes channels 1-based by their
        /// position in the catalog it advertised during subscription.
        /// Each frame format documented in
        /// <see cref="TelemetryFrameBuilder.BuildV0ValueFrame"/>; chunked
        /// through the session-data layer with monotonically advancing seq.
        /// </summary>
        // Per-URL host channel lookup for the V0 path, cached by profile
        // reference (Profile setter is the only reassignment site; mapping edits
        // mutate channel fields in place but never the URL set).
        private System.Collections.Generic.Dictionary<string, ChannelDefinition>? _v0ByUrl;
        private MultiStreamProfile? _v0ByUrlProfile;
        // Per-channel change dedup (indexed by catalog position): V0 re-sent
        // every channel at full tick rate; unchanged values now ride a 1 s
        // keepalive floor instead. Changed values still go out immediately,
        // and the floor re-covers the full set within 1 s of a session
        // restart.
        private double[]? _v0LastValue;
        private int[]? _v0LastSentMs;
        private const int V0KeepaliveFloorMs = 1000;

        private void SendV0ValueFrames(GameDataSnapshot snapshot)
        {
            var profile = _profile;
            var catalog = _catalogParser.Catalog;

            // Catalog-less fallback: in TestMode without a wheel-advertised
            // channel catalog, iterate the loaded profile's channels and
            // synthesize 1-based indices. Lets the test button exercise every
            // channel the host knows about even if the wheel hasn't (or
            // hasn't yet) advertised its URL list. Lives mode without catalog
            // still returns silent — no point sending zeroed V0 frames at idle.
            if (catalog == null || catalog.Count == 0)
            {
                if (!TestMode) return;
                if (profile == null || profile.Tiers == null) return;
                var profileChannels = new System.Collections.Generic.List<string>();
                foreach (var tier in profile.Tiers)
                    foreach (var ch in tier.Channels)
                        if (!string.IsNullOrEmpty(ch.Url))
                            profileChannels.Add(ch.Url);
                if (profileChannels.Count == 0) return;
                catalog = profileChannels;
            }

            // Per-URL host channel lookup, rebuilt only when the profile changes
            // (not every tick). Channels not in the host profile still get a
            // frame — wheel's dashboard may bind to URLs the host doesn't have
            // local metadata for, and missing values block widget render.
            // Default compression = uint32_t, resolved value = 0 (live) or test.
            if (_v0ByUrl == null || !ReferenceEquals(_v0ByUrlProfile, profile))
            {
                var map = new System.Collections.Generic.Dictionary<string, ChannelDefinition>(
                    StringComparer.OrdinalIgnoreCase);
                if (profile != null)
                {
                    foreach (var tier in profile.Tiers)
                        foreach (var ch in tier.Channels)
                            if (!string.IsNullOrEmpty(ch.Url) && !map.ContainsKey(ch.Url))
                                map[ch.Url] = ch;
                }
                _v0ByUrl = map;
                _v0ByUrlProfile = profile;
            }
            var byUrl = _v0ByUrl;

            // Test-mode wall-clock once per tick (was recomputed per channel).
            long nowMs = TestMode
                ? System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency
                : 0L;

            // Compute every per-channel value frame BEFORE entering the lock
            // so SimHub property resolution (PluginManager.GetPropertyValue
            // — can hit slow paths under load) doesn't block UI-thread
            // brightness/dashboard-switch property pushes that contend for
            // _session02SeqLock. Holding the lock over IO-bound work was
            // observable as a UI freeze during teardown.
            int nowTickMs = Environment.TickCount;
            if (_v0LastValue == null || _v0LastValue.Length < catalog.Count)
            {
                _v0LastValue = new double[catalog.Count];
                _v0LastSentMs = new int[catalog.Count];
                for (int k = 0; k < _v0LastSentMs.Length; k++)
                    _v0LastSentMs[k] = nowTickMs - 2 * V0KeepaliveFloorMs;   // force first send
            }

            var prebuilt = new System.Collections.Generic.List<byte[]>(catalog.Count);
            for (int i = 0; i < catalog.Count; i++)
            {
                string url = catalog[i];
                if (string.IsNullOrEmpty(url)) continue;
                uint wheelIdx = (uint)(i + 1); // 1-based per docs

                ChannelDefinition? ch = byUrl.TryGetValue(url, out var found) ? found : null;
                string compression = ch?.Compression ?? "uint32_t";

                double value;
                if (TestMode)
                {
                    if (ch != null)
                    {
                        value = TestSignalGenerator.Compute(ch.TestSignal, nowMs);
                    }
                    else
                    {
                        // Unknown URL not in host profile — emit 0 so the
                        // wheel sees "nothing mapped" rather than a fake percent.
                        value = 0.0;
                    }
                }
                else
                {
                    value = ch != null ? ResolveV0ChannelValue(ch, snapshot) : 0.0;
                }

                if (value == _v0LastValue[i]
                    && unchecked(nowTickMs - _v0LastSentMs![i]) < V0KeepaliveFloorMs)
                    continue;
                _v0LastValue[i] = value;
                _v0LastSentMs![i] = nowTickMs;

                byte[] valueBytes = TelemetryFrameBuilder.EncodeV0Value(compression, value);
                prebuilt.Add(TelemetryFrameBuilder.BuildV0ValueFrame(wheelIdx, valueBytes));
            }

            if (prebuilt.Count == 0)
            {
                return;
            }

            // Reserve the seq range for the whole burst under the session
            // lock so a concurrent FF property push (UI thread) or tier-def
            // re-emit (background thread) can't slip a seq into the middle
            // of our per-channel value frame train. Lock scope is now bounded
            // to the chunking + send, not the value-resolution loop above.
            bool anySent = false;
            lock (_session02SeqLock)
            {
                int seq = _session02OutboundSeq;
                foreach (var vframe in prebuilt)
                {
                    var frames = TierDefinitionBuilder.ChunkMessage(vframe, FlagByte, ref seq, _targetDeviceId);
                    foreach (var frame in frames)
                    {
                        if (_state == TelemetryState.Idle || !_connection.IsConnected)
                        {
                            _session02OutboundSeq = seq;
                            if (anySent) _framesSent++;
                            return;
                        }
                        SendAndTrackChunk(frame);
                    }
                    anySent = true;
                }
                _session02OutboundSeq = seq;
            }
            if (anySent) _framesSent++;
        }

        private double ResolveV0ChannelValue(ChannelDefinition ch, GameDataSnapshot snapshot)
        {
            if (!string.IsNullOrEmpty(ch.SimHubProperty) && PropertyResolver != null)
            {
                double scale = ch.SimHubPropertyScale == 0.0 ? 1.0 : ch.SimHubPropertyScale;
                return PropertyResolver(ch.SimHubProperty) * scale;
            }
            return snapshot.GetField(ch.SimHubField);
        }

        /// <summary>
        /// Periodic empty-data ping on session 0x09 to keep the configJson channel
        /// alive. Mirrors PitHouse start-game capture
        /// (`wireshark/csp/start-game-change-dash.pcapng`) which emits
        /// `7c 00 09 01 [seq++] 00 00 00 00 00` at ~1Hz; without it the wheel
        /// closes session 0x09 and stops pushing dashboard state, leaving the
        /// plugin's "Wheel Files" tab empty. Fires once per active-phase slow
        /// tick alongside other 1Hz heartbeats.
        /// </summary>
        private void SendSession09Keepalive() => SendSession09CounterPrime();

        /// <summary>Seed the sess=0x09 outbound seq from the wheel's 0x81
        /// device-init: the wheel tracks h2b data from open-seq + 3 (ack floor
        /// open-seq + 2, as on the FT sessions). Re-seeding drops tracked
        /// chunks of the prior seq generation — a retransmit of those would be
        /// unackable — so a repeat of the SAME device-init is ignored rather
        /// than discarding legitimately in-flight sends.</summary>
        internal void SeedSession09OutboundSeq(int openSeq)
        {
            lock (_session09SeqLock)
            {
                if (_session09SeqSeeded && _session09SeedOpenSeq == openSeq) return;
                _session09OutboundSeq = openSeq + 3;
                _session09SeedOpenSeq = openSeq;
                _session09SeqSeeded = true;
                // Inside the lock: dropping outside it can discard a chunk a
                // concurrent emitter just tracked on the NEW base, leaving that
                // seq with no retransmit cover. Seq-lock → retransmitter-lock is
                // the established order.
                _retransmitter.DropSession(0x09);
            }
            MozaLog.Info(
                $"[AZOM] sess=0x09 outbound seq seeded from device-init: " +
                $"open-seq={openSeq} → first data seq={openSeq + 3}");
        }

        /// <summary>Prime the config session with a zero-length data frame.
        /// Post-devinit this MUST draw from the shared 0x09 counter —
        /// a synthetic seq collides with counter seqs and corrupts the wheel's
        /// rx stream; pre-devinit there is no base yet, so the caller's
        /// solicitation seq is used.</summary>
        internal void SendConfigSessionPrime(byte session, ushort solicitSeq)
        {
            if (session == 0x09 && _session09SeqSeeded) SendSession09CounterPrime();
            else SendSessionPrime(session, solicitSeq);
        }

        /// <summary>Emit one zero-length sess=0x09 data frame from the shared
        /// outbound counter. Tracked only once seeded — before that the seq
        /// is below the wheel's tracking window, so it is solicitation /
        /// session-liveness traffic only.</summary>
        internal void SendSession09CounterPrime()
        {
            // Reserve seq + emit under _session09SeqLock so a concurrent
            // MaybeSendConfigJsonReply (read-thread) can't slip its multi-chunk
            // train in between this seq reservation and the wire send.
            lock (_session09SeqLock)
            {
                int seq = _session09OutboundSeq;
                var frame = _sessionLife.SendSessionPrime(0x09, (ushort)seq);
                _session09OutboundSeq = seq + 1;
                if (_session09SeqSeeded)
                    _retransmitter.Track(frame, anyDevice: true);
            }
        }

        internal bool Session09SeqSeeded => _session09SeqSeeded;
    }
}
