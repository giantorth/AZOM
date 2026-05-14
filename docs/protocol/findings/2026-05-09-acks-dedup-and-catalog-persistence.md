# 2026-05-09 — Per-seq acks, retransmit dedup, and catalog persistence

Investigation of post-switch dashboard halts, garbled tire-channel URLs, and recurring `Stop+Start` cycles led to four protocol-layer corrections to the v1 telemetry pipeline.

## 1. Per-seq acks for inbound chunks (was running-max)

`OnMessageDuringPreamble` previously acked the *running max* `_sessionAckSeq` / `_mgmtAckSeq` instead of the specific seq of the inbound chunk. Symptom: when the wheel retransmitted an older seq (e.g. `seq=5..14` after we had already advanced our running max to 21), the gate `if (seq > _sessionAckSeq)` was false, the `_sessionAckSeq` stayed at 21, and `SendSessionAck(_, _sessionAckSeq)` echoed `21` back — which the wheel doesn't interpret as a cumulative ack for `5..14`. The wheel re-pushed the same chunks every ~1 s indefinitely, bleeding ~4 KB/s of unsolicited inbound.

**Fix:** `SendSessionAck(session, (ushort)seq)` per chunk on every branch (telemetry session, mgmt session). `_sessionAckSeq` / `_mgmtAckSeq` are kept up-to-date for diagnostics only.

Also extended ack coverage to **sess=0x04..0x08** (was only `_uploader.ActiveSession`). The wheel device-inits multiple FT sessions in the same connect cycle (e.g. 0x05 *and* 0x07); chunks on whichever session wasn't `ActiveSession` were never acked, producing a 20-second retransmit cadence visible in any healthy trace.

`SendStatusPush()` (the ~1 Hz "harmless re-ack of running max") is now a no-op — per-chunk acks cover what's needed and the periodic re-ack was the only remaining stale-running-max emission site.

## 2. Catalog chunk dedup by seq

`ChannelCatalogParser.AppendChunk` blindly appended every received chunk. The wheel retransmits each chunk 2-3× before our ack lands (verified 2026-05-09: seqs 5-14 received 3× each on a clean connect). Each retransmit was being **appended again** to the rolling buffer, doubling/tripling the byte count for early seqs.

The TLV walk uses size-prefix arithmetic (`i += 5 + param`); duplicated regions misalign the walk and any record whose body straddled a duplicated chunk boundary parsed as garbage. Symptom: tire URLs at indexes 10/11/13/14 rendered as `v1/gameDa???????t???` deterministically every cold-start (indexes 9 and 12 happened to land cleanly inside record boundaries).

**Fix:** new `AppendChunkIfNew(byte session, int seq, …)` overload tracks per-session highest seen seq and silently drops retransmits.

## 3. Catalog persists across Stop+Start

`StartInner → InitTickStateAndTransitionToStarting` was calling `_catalogParser.Reset()` which wipes the resolved `_catalog`. Post-switch the wheel uses **back-reference records** (size=1, just the idx byte) to say "URL at idx X is unchanged from before". With `_catalog` wiped, every back-reference resolved to `backRefFail` and the entry stayed empty.

Symptom: cold-start → 20 channels parsed cleanly. Switch to another dashboard and back → only the channels the wheel re-announced as full URLs survived; back-referenced channels were missing from the post-switch catalog. User-visible as "we always seem to get all the channels at first launch but gaps after switching".

**Fix:** swap `Reset()` → `ClearBuffer()` in `InitTickStateAndTransitionToStarting`. `ClearBuffer` drops the in-progress reassembly buffer and the per-session seq dedup map but **preserves `_catalog`**. `Reset()` is still available for true hot-swap scenarios.

## 4. Per-chunk CRC32 validation on inbound catalog / tile-server

The catalog feed (sess=0x01 / FlagByte) and tile-server feed (sess=0x03 / 0x0b) were stripping the trailing 4 bytes blindly without verifying the CRC32 over the remaining payload. Wine SerialPort R/W contention produces 1000+ frame-start-scan resyncs in a clean trace; a corrupted chunk that survives wire-level checksum (8-bit) but mangles the body would feed garbled bytes to the parser.

**Fix:** both feeds now compute `Crc32(payload[0..netLen])` and compare to the wire trailer; on mismatch the chunk is dropped silently and the wheel re-pushes naturally (per item 1 above). Reject counts are exposed as `CatalogCrcRejects` and `TileServerCrcRejects` for diagnostics.

A defensive **strict URL byte check** was also added in the catalog parser: every byte of an accepted URL must be printable ASCII (0x20..0x7E). Catches the case where a stray `0x04` byte inside another record's data passes the lenient "first 3 bytes look like 'v1/'" plausibility check and starts a fake URL parse.

## 5. Tier-def blind-retransmit early-exit via ack-state

Old early-exit gate compared `_catalogParser.LastActivityMs > _tierDefBlindLastTickCount` — but catalog activity is timestamped *before* tier-def emission, so the comparison never tripped and all 6 blind rounds always fired. ~6 KB extra h2b per connect; dominant cause of cold-start saturation events.

**Fix:** every tier-def chunk is tracked via `SendAndTrackChunk → SessionRetransmitter.Track`. New helper `AllBlindChunksAcked()` walks `_tierDefBlindFrames`, parses each chunk's `(session, seq)` from the frame header, and queries `SessionRetransmitter.Contains(session, seq)`. If none are still pending, the wheel acked everything and additional rounds are skipped.

Retry budget on `SessionRetransmitter.DueRetransmits` raised 8 → 30 (with exponential backoff capped at 2 s), so a stuck chunk that genuinely needs many attempts has the budget to recover.

## 6. configJson resilience refactor

`ConfigJsonClient.Reset()` previously cleared both reassembly buffer AND `_lastState`. Now split:

- `ClearBuffer()` — only clears the in-progress reassembly buffer. Preserves `_lastState` (the cached `WheelDashboardState`). Called from `Stop()` so the dashboard library cache survives Stop+Start cycles.
- `HardReset()` — full clear including `_lastState`. Used only on plugin instance dispose / hot-swap.
- `Reset()` — backwards-compatible alias for `ClearBuffer()` so old call sites get the safer default.

`HandleConfigJsonGap` is now tiered:

- **LastState present, any gap count**: log a warning, no recovery action. Cached state is still authoritative; the wheel won't re-burst on a re-issued OpenRequest once it considers the session initialised, so forced recovery just thrashes the link.
- **LastState absent, first gap**: prime + open-request (mimics cold-start). Some firmwares respond by resetting their sess=0x09 state machine and re-bursting.
- **LastState absent, ≥4 gaps within an 8s cooldown**: full `RestartForSwitch`. The Stop+11s-settle+Start sequence is the only reliable way to make a wheel that's stuck mid-burst burst from scratch.

`TickConfigJsonStuckWatchdog` (the 30 s no-progress backstop) was over-aggressive and triggered Stop+Start whenever `LastState` was null even if catalog and active subscription were healthy. It now skips when **catalog is populated AND `_activeSubscription != null`** — dashboard rendering doesn't need configJson library state, and forcing 11 s of dashboard blank because the library cache is empty was throwing away a working session.

## 7. WriteBudget retargeting

The token-bucket `WriteBudget` was sized at 8000 B/s sustained / 12000 B/s burst — well below the 11520 B/s wire ceiling (115200 baud / 10 bits per byte). Real telemetry routinely exceeds that target (P99 ~9 kB/s, post-switch bursts to 12 kB/s), and `MayDrainStreams()` was returning `false` whenever the rolling 1-s window crossed the target. While gated, **the entire stream lane was held back**, value frames stopped reaching the wheel for 700 ms-1 s windows, and the wheel could disengage the dashboard or drop configJson chunks under the resulting wire pressure.

**Fix:** target raised to 11000 B/s, soft threshold to 9500 B/s, burst ceiling to 14000 B/s. `MayDrainStreams()` removed entirely — latest-wins coalescing in the stream slots already provides natural backpressure (fresh `SendStream` overwrites pending slots before the writer can drain them), and `SerialPort.Write` blocks when the OS write buffer (16 KB on Wine SerialPort) fills, providing a hard physical-layer gate.

The peak counter (`_peakBytesInWindow`) is now monotonic per session — it was reset-on-read previously, so the diagnostics UI peak field jumped between every poll. Manual `ResetPeak()` available for explicit baseline reset.

## 8. State-machine guards added

- **Kind=4 emission blocked during cooldown** — `SendDashboardSwitch` no-ops when `_state != Active` or in the post-Stop silence window. Rapid double-clicks during the 11 s `MinSilenceAfterStopMs` wait were leaking kind=4 frames mid-restart and corrupting wheel-side state (observed: wheel responded with backref-only catalog stuck at 8 entries until next plugin restart).
- **`IsInSilenceCooldown`** exposed on `TelemetrySender`. UI dropdown + Test Start/Stop disabled while true so users can't trigger races.
- **Preamble extension when catalog empty** — `TickPreamble` holds the Active transition until `_catalogParser.Count > 0` (capped at 3 s). Going Active with `catalog=0` produced an `idx=alpha` tier-def with all unbound channels which the post-Active growth re-apply then had to clean up.

## 9. Catalog-growth re-apply

New `TickGrowSubscriptionIfCatalogStable` mimics PitHouse's growing-subscription pattern: when the catalog has grown by ≥1 entry since the last subscription emission AND the parser has been quiet (no new records) for 400 ms, re-emit the tier-def. Closes the window where URLs that arrive after the initial preamble→Active tier-def emission would otherwise stay at `chIndex=0`.

## 10. Catalog re-sync probe (kind=4 to current slot)

When `BuildTierDefinitionV2` produces a tier-def with unbound channels (chIndex=0 — i.e. URLs in the profile that aren't in the wheel's advertised catalog), `ScheduleCatalogResyncProbe` schedules a kind=4 dashboard-switch emit for the **current** slot ~800 ms later. Some firmwares respond by re-running the dashboard-load sequence and re-advertising the catalog, picking up channels that weren't pushed in the initial burst. 8-second cooldown so a chronically-incomplete catalog can't trigger a switch storm.

## Net effect

Cold-start traces post-fix:

- catalog grows 0→8→12 (or 0→8→20 for tire-channel dashboards) cleanly without retransmit storms
- tier-def emission has zero unbound channels on healthy firmwares
- post-switch catalogs preserve back-referenced URLs from prior dashboards
- per-chunk seq acks stop the every-20-second sess=05 burst-and-retransmit pattern
- `Catalog buffer dump` + `Catalog parse stats: full=20 plausReject=0` consistently after Active

User-visible: dashboard switches no longer halt the test pattern post-cycle; tire channels render the same triangle sweep as the rest of the dashboard; no more `v1/gameDa???????t???` corruption on Grids.

## Code touchpoints

| Area | File | What changed |
|------|------|--------------|
| Per-seq acks | `Telemetry/TelemetrySender.cs` (~2315, ~2380, ~2406) | Send `(ushort)seq`, ack 0x04..0x08, no-op `SendStatusPush` |
| Retransmit dedup | `Telemetry/Frames/ChannelCatalogParser.cs` | New `AppendChunkIfNew(session, seq, …)` |
| Catalog persist | `Telemetry/TelemetrySender.cs:898` | `Reset()` → `ClearBuffer()` |
| Inbound CRC | `Telemetry/TelemetrySender.cs` (~2516, ~2580) | Validate CRC32 LE; expose reject counters |
| Strict URL bytes | `Telemetry/Frames/ChannelCatalogParser.cs` | All-printable-ASCII check before accept |
| Blind retransmit early-exit | `Telemetry/TelemetrySender.cs:2837` + `Diagnostics/SessionRetransmitter.cs` | New `Contains` API; `AllBlindChunksAcked` helper |
| ConfigJson resilience | `Telemetry/Dashboard/ConfigJsonClient.cs` + `Telemetry/TelemetrySender.cs` | Split `Reset` / `HardReset`; tiered gap recovery; narrowed watchdog |
| WriteBudget | `Protocol/WriteBudget.cs` + `Protocol/MozaSerialConnection.cs` | Raise targets, remove stream-lane gate, monotonic peak |
| State guards | `Telemetry/TelemetrySender.cs` + `Devices/MozaWheelSettingsControl.xaml.cs` | Cooldown gate on kind=4; UI lock; preamble extension |
| Growth + re-sync | `Telemetry/TelemetrySender.cs` | `TickGrowSubscriptionIfCatalogStable` + `ScheduleCatalogResyncProbe` |
