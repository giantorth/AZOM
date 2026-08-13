## Tier definition protocol (group 0x43, session data on 7c:00)

Tier configuration uses TLV (tag-length-value) encoding exchanged as 7c:00 session data chunks. **Two-way handshake**: wheel declares channel catalog, host tells wheel how to decode incoming telemetry.

### Handshake sequence (from bidirectional frame traces)

Before Pithouse opens sessions, wheel already advertises channel catalog via `7c:23` display config frames. Full handshake traced frame-by-frame from VGS (`moza-startup-1.pcapng`) and CSP (`pithouse-complete.txt`):

```
Phase 1 — Wheel advertisement (before session opens):
  Wheel sends 7c:23 display config frames at ~10Hz (alternating payloads)

Phase 2 — Session open + wheel channel catalog:
  Host  >>> 7C:00 SESSION_OPEN port=0x01, port=0x02 (same USB packet)
  Wheel <<< FC:00 ACK for both sessions (immediate)
  Wheel <<< 7C:00 session 0x01: tag 0x07 (version=0) + tag 0x0c (device hash)
                                + tag 0x01 + tag 0x05 + tag 0x04 ch=0 + tag 0x06 END
  Wheel <<< 7C:00 session 0x02: tag 0xff (sentinel) + tag 0x03 (value=1)
                                + tag 0x04 × N channel URLs + tag 0x06 END (total=N)
  Host  >>> FC:00 ACKs for wheel's channel data (incremental)

Phase 3 — Host tier config (format depends on wheel model):
  Host  >>> 7C:00 session 0x02: tier definition (version 0 = CSP, see [`version-0-url-csp.md`](version-0-url-csp.md); version 2 = VGS/CS, see [`version-2-compact-vgs.md`](version-2-compact-vgs.md))
  Host  >>> FC:00 ACKs continue for any remaining wheel data

Phase 4 — Telemetry starts:
  Host  >>> 7D:23 telemetry frames (~30 Hz)
  Host  >>> FD:DE enable signal (~30 Hz, starts ~1s after session open)

Phase 5 — Channel config burst (~1s after session open):
  Host  >>> 0x40 1E:xx channel enables, 28:00, 28:01, 09:00, 28:02
  Host  >>> Second batch of tier definitions (real dashboard tiers at higher flags)
```

Both VGS and CSP follow this sequence. Wheel always declares version 0 (`tag 0x07 param=1 value=0x00`) — both models send identical version tags. Pithouse decides host→wheel response format based on wheel's model name (from 0x87 identity response), not from version tag.

**Timing note:** On VGS, Pithouse starts telemetry (flag=0x00, 11B probe tier) at t+0.3s after session open, BEFORE enable signal or channel config. Enable starts at t+1.0s. Real dashboard telemetry (flag=0x03, 16B) starts at t+1.5s after second tier definition batch.

### In-game dashboard switch and page change (post-startup)

Synthesised from four 2026-05-17 bridge captures (13-dashboard wheel, R5 base + W17 wheel, PitHouse 1.2.6.17):
- `sim/logs/bridge-20260517-070054.jsonl` — 5 mixed user switches
- `sim/logs/bridge-20260517-080546.jsonl` — 13 forward wheel switches
- `sim/logs/bridge-20260517-081336.jsonl` — 14 backward wheel switches
- `sim/logs/bridge-20260517-082046.jsonl` — 10 wheel-side page changes within Grids

Open items still pending more captures: full `28:00` register behaviour, FF-record kind=16/0x10 semantics, `b8 XX 02` byte 2 meaning for dashboard cases. (kind=14/15 resolved 2026-08-07 — the device log pull, see [`../sessions/session-0x02-ff-init.md`](../sessions/session-0x02-ff-init.md) § Device log pull.)

A dashboard or page switch can be initiated by either the host (PitHouse UI) or the wheel hardware (knob/button on the rim). The wire signature differs by initiator; the post-switch re-bind sequence is largely the same when a dashboard changes.

#### Wheel-side input events (`b8` family)

Every wheel-side switch input (dashboard or page) is announced by a 3-byte `b8 AA BB` payload on `(b2h, grp=0xC3, dev=0x71)`, immediately followed by a b2h kind=4 FF-record on session 0x02. The `b8 AA BB` bytes decode as:

| `AA` | `BB` | Meaning | Wheel control | Effect |
|------|------|---------|---------------|--------|
| `0x00` | `0x02` | dashboard: next | rotary "next" (or equivalent forward cycle) | slot+1, wraps from last to first |
| `0x01` | `0x02` | dashboard: previous | rotary "previous" (or equivalent reverse cycle) | slot−1, wraps from first to last |
| `0x02` | `0x00` | page within dashboard: next | page-forward control | page+1, wraps within the dashboard's page count |
| `0x02` | `0x01` | page within dashboard: previous | page-back control | page−1, wraps within the dashboard's page count |

Verified across 40 events in four captures (dashboard forward 14, dashboard backward 16, page changes 10) with 40/40 prediction match and 0 counterexamples. 0 occurrences in 50 prior captures (~6.5 M lines).

For dashboard cases (`AA` = `0x00` / `0x01`), byte `BB` is always `0x02` — matches the session id of the kind=4 carrier that immediately follows but causation is unproven. For page cases (`AA` = `0x02`), byte `BB` is the page-direction argument.

#### Initiator distinguishing signal

| Initiator | Trigger frame | Precursor | Wire signature |
|-----------|---------------|-----------|----------------|
| PitHouse (host) — dashboard | h2b kind=4 FF-record on session 0x02 | none | wheel echoes byte-identical b2h kind=4 within +20-80 ms |
| Wheel (hardware) — dashboard | b2h kind=4 FF-record on session 0x02 | b2h `b8 [00\|01] 02` (~0.1 ms before kind=4) | no host h2b kind=4 emitted |
| Wheel (hardware) — page | b2h kind=4 FF-record on session 0x02 | b2h `b8 02 [00\|01]` (~351 ms before kind=4) | no host h2b kind=4 emitted; PitHouse continues 27:NN polling but does not echo |

A PitHouse-initiated page change has NOT been observed yet (PitHouse UI may not expose a page-control directly). If it exists, the wire signature is unknown.

See [`../devices/wheel-0x17.md`](../devices/wheel-0x17.md) § Group 0x43 for the `b8 ?? 02` entry in the wheel device table.

#### kind=4 FF-record wire format

Identical in both directions. 25-byte payload sent inside a SerialStream chunk on session 0x02:

```
SerialStream wrapper:
  7c 00 02 01 [seq:LE16] <payload...>

Payload (25 bytes):
  ff
  0c 00 00 00          // data_size = 12 (LE32)
  [data_crc:LE32]      // CRC32 of (field1 || field2 || field3)
  [field1:LE32]        // = 4 for switch ops (= 7 at session start, different cmd)
  [field2:LE32]        // 0-based slot index into configJsonList (current dashboard)
  [field3:LE32]        // 0-based page index within the current dashboard
  [body_crc:LE32]      // CRC32 of (ff || data_size || data_crc || field1..field3)
```

`field2` = 0-based index into `configJsonList` (alphabetical dashboard names from the session 0x09 state push). See [`../findings/2026-04-30-dashboard-switch-3f27.md`](../findings/2026-04-30-dashboard-switch-3f27.md) § "Slot index source" for the verified-against-live-wheel proof and the `configJsonList` vs `enableManager.dashboards` distinction.

`field3` = 0-based page index within the current dashboard. `0` for kind=4 records emitted on a dashboard switch (the wheel resets to page 0 on dashboard change). For kind=4 records emitted on a page-only change, `field3` carries the new page number — verified on a 2-page Grids dashboard across 10 page-change events: field3 alternated `01, 00, 01, 00, 01, 00, 01, 00, 01, 00` matching the user's page toggles.

#### Wire timing — b8 to kind=4 delay differs by action

| Action category | b8→kind=4 delay |
|-----------------|-----------------|
| dashboard switch (wheel-initiated) | ~0.1 ms (back-to-back, same bridge tick) |
| page change (wheel-initiated) | ~351 ms (consistent across 10 events in `bridge-20260517-082046.jsonl`) |

The longer page-change delay suggests the wheel performs page-state work locally before announcing commitment. Cause unverified.

#### Post-trigger re-negotiation (same for both initiators)

```
Phase 1 — Trigger (see table above)
Phase 2 — Wheel sends knob/page state reads on grp=0x40 (+20-200 ms)
          (PitHouse-initiated switches show heavier grp=0x40 chatter than wheel-initiated)
Phase 3 — Wheel re-pushes channel catalog on session 0x01 b2h with \x01-prefix
          shorthand URLs (+50-1000 ms, 12-18 chunks). The final record of
          this stream is an `0x06 04 00 00 00 <u32>` END marker (the
          tier-def version handshake — see Phase 4).
Phase 4 — PitHouse emits a TIER-DEF BURST of multiple emissions paced
          ~1 s apart, each ENABLE×N + TIER×M + END TLVs — NO tag 0x07/0x03
          preamble. Each emission's END u32 echoes the wheel's
          most-recent END marker from Phase 3. Flag-base advances per
          emission. First emission at +150-960 ms; subsequent emissions
          rebuild with the wheel's latest END value so a slow wheel push
          is still picked up by emission 2/3/4. Total emissions per
          switch: 3-13 (scales with dashboard sub-tier count).
Phase 5 — b2h FF-record kind=14 (~600-820 B zlib payload) at +2-3 s: the
          display's application log, answering a host kind=14 pull (the
          switch generates fresh log lines). See sessions/session-0x02-ff-init.md
          § Device log pull. Not part of the switch handshake.
Phase 6 — Value-frame payload length recovers; live telemetry resumes (~+1.2 s).
```

**END u32 handshake** — verified 2026-05-17:

The wheel pushes its own END marker (`0x06 04 00 00 00 <u32>`) as the
final record of the b2h sess=0x01 catalog stream. PitHouse echoes this
exact u32 on every tier-def emission of the burst. Mismatch = wheel
treats the tier-def as a stale duplicate and does not commit widget
bindings — observed symptom: the wheel's physical test-pattern button
renders nothing after a hot switch when END echo is wrong.

Observed END sequence across one session (PitHouse `bridge-20260517-070054`):
`6 → 16 → 23 → 32 → 41 → 43 → 64` (monotonically advancing, exact step
varies, not derivable from dashboard contents). Plugin
implementation: `ChannelCatalogParser.LastWheelEndMarker` is updated
every time the wheel's catalog stream includes the marker; tier-def
emit reads it and passes to `TierDefinitionBuilder`.

**Measured timings across 5 switches in `bridge-20260517-070054.jsonl`:**

| Switch | Initiator | 1st tier-def chunk (+ms) | Tier-def emissions (+10 s) | Live VF size recovers (+ms) |
|--------|-----------|--------------------------|-----------------------------|------------------------------|
| Core → Simple Rally | wheel | 331 | 3 | (len stayed 13) |
| Simple Rally → Rally V6 | wheel | 174 | 3 | 4925 |
| Rally V6 → Rally V5 | PitHouse | 960 | 5 | 1973 |
| Rally V5 → Grids (2-pkg) | PitHouse | 153 | 8+ | 1217 |
| Grids → Mono | wheel | 151 | 14+ | 1163 |

Observed range: 151-960 ms for first tier-def emission, 3-14 emissions over ~5-10 s total. Both vary by destination dashboard size and apparently by initiator.

**Each emission rebuilds with current state** — the burst is NOT byte-identical retransmission. Each emission advances `flagBase` by the tier count and re-reads the wheel's latest END marker. So even if the first emission echoes a stale END (the wheel's new END hadn't arrived yet), a later emission picks up the updated value and the wheel commits binding then. Verified PitHouse switch #1 emission cadence:
- #116 at +959 ms — END=33 (stale, from prior emission), flags 0x13-0x14
- #117 at +1972 ms — END=42 (wheel pushed END=42 at +1023 ms), flags 0x15-0x17
- #118 at +2986 ms — END=42 (same), flags 0x18-0x1A

**Flag-base is monotonic across the session** — both plugin and PitHouse advance `flagBase` by tier count on each emission, never reset in-session. Stop+Start (cold-start path) resets to 0; hot switches continue advancing. The wheel handles any flag-base.

**No session close/reopen.** Sessions 0x01-0x03 stay open throughout. No LVGL re-upload, no display probe.

**PitHouse does NOT re-parse the Phase 3 catalog push.** It builds the tier-def from its own locally-cached mzdash channel metadata + the initial preamble catalog indices (which stay valid for the entire session). The wheel's post-switch catalog push is informational — the wheel confirms what it switched to, but PitHouse already knows. See [`../dashboard-upload/download-session-0x0b.md`](../dashboard-upload/download-session-0x0b.md) for how PitHouse downloads mzdash files from the wheel at cold-start.

#### Plugin implementation (hot re-negotiation, gated on `MozaPluginSettings.EnableHotRenegotiation`)

PitHouse-initiated switch (UI dropdown):

1. `MozaPlugin.OnDashboardSwitched(slot)` → `ApplyTelemetrySettings` (stages new profile in sender) → `TelemetrySender.SwitchToProfile(slot, null)`.
2. `SwitchToProfile` emits kind=4 via `SendDashboardSwitch`, then arms the hot-reneg burst: `_pendingHotTierDefReemit = HotSwitchEmissionCount = 4`.
3. `TickGrowSubscriptionIfCatalogStable` runs in the tick loop; once the wheel's post-switch catalog activity has been seen and quieted (or fallback timer at 1500 ms), it calls `ApplySubscription(force:true)` which builds a fresh tier-def reading `_catalogParser.LastWheelEndMarker` and emits on sess=0x01. Decrements the counter; subsequent emissions pace `HotSwitchEmissionSpacingMs = 1000` ms apart.
4. After 4 emissions the counter clears.

Wheel-initiated switch (user pressed wheel hardware control):

1. `MaybeUpdateWheelReportedSlot` parses the wheel's b2h type-04 record. If the new slot ≠ `_lastEmittedKind4Slot` and `prevSlot >= 0` (post-cold-start) and `EnableHotRenegotiation` is on, the handler:
   - Updates `_lastEmittedKind4Slot` to the new slot (so subsequent echoes don't re-trigger).
   - Arms the same hot-reneg burst (`_pendingHotTierDefReemit = HotSwitchEmissionCount`) **without emitting kind=4** — the wheel already did.
   - Raises `WheelInitiatedSwitch` event with the new slot.
2. `MozaPlugin.OnWheelInitiatedSwitch` resolves slot → dashboard name via `WheelStateForDiagnostics.ConfigJsonList`, updates `ActiveTelemetryProfileName`, calls `ApplyTelemetrySettings` (stages the new profile in the sender), persists settings, and raises `DashboardSelectionChanged` so the UI dropdown reflects the wheel's choice.
3. From here the same `TickGrowSubscriptionIfCatalogStable` tick loop fires the 4-emission tier-def burst at the wheel's new flag-base, echoing the END marker the wheel just pushed.

Wheel-initiated page change uses the same `b8 02 [00|01]` → b2h kind=4 path with `field3 != 0`; same hot-reneg burst applies, but the channel set doesn't change (same dashboard) so the tier-def's flag-base advances yet binds the same channels at a higher flag.
