# PitHouse cold-start init sequence + FF record kind inventory

> **Canonical reference:** [`../sessions/session-0x02-ff-init.md`](../sessions/session-0x02-ff-init.md).
> An implementation attempt 2026-05-13 emitted captured kind=8/11 bytes
> verbatim. Result: the W17 wheel locked after a few restart cycles and
> required a physical power-cycle. The .bin files contain session-bound
> state; replaying them across sessions is a verified dead-end. Before
> re-attempting, see the canonical doc for the required body-decode
> work-list.

Byte-exact reference for what FF records PitHouse sends at session start, in what order, with what byte content, on which session.

## Critical correction vs HANDOVER-INVESTIGATION.md

**HANDOVER-INVESTIGATION.md (lines 257–276) claims PitHouse sends FF records on session 0x01 and tier-def TLV on session 0x02.** That is **wrong** for these 2026-05-03 captures.

The bridge-capture session-layout audit (`/tmp/session_layout.py` against all four 2026-05-03 captures) confirms:

| Direction | Session | What it carries | Evidence |
|-----------|--------:|:----------------|:---------|
| h2b | 0x01 | tier-def TLV | first-byte distribution dominated by 0x00 ENABLE / 0x01 TIER / 0x06 END / 0x07 PROTO_VER (~1900 chunks per capture) |
| h2b | 0x02 | FF records | first-byte 0x?? overwhelmingly 0xff (380+ FF records per cold-start capture); init kind=2/7/8/11 at low seqs |
| b2h | 0x02 | FF records | wheel pushes its own catalog (260+ FF records starting with 0xff) |
| h2b | 0x03 | tile-server JSON (zlib FF-wrapped) | fits the existing TileServerStateBuilder usage |
| h2b | 0x04 | file-transfer (mzdash upload) | size-tagged transfer payload |
| h2b | 0x05 | file-transfer (download response stream) | similar shape |
| h2b | 0x09 | configJson RPC | matches existing ConfigJsonClient |

**The plugin's current layout (tier-def on 0x01, FF records on 0x02) already matches PitHouse.** No session swap is required. The HANDOVER-INVESTIGATION recommendation to swap sessions was a phantom rabbit hole.

This finding meaningfully changes the refactor plan: the new architecture inherits the current session mapping rather than inverting it. The dashboard-switch bug must therefore lie elsewhere — and the most likely culprit, given the 2026-05-04 tier-def reference, is the END_MARKER advance rule (max channel index ever in session) which the existing plugin does not implement correctly.

## FF wire format (confirmed)

```
ff <size:u32LE> <inner_crc32:u32LE> <kind:u32LE> <value:size-4 bytes>
```

- `ff` = sentinel byte
- `size` = total bytes of `kind || value` (so value is `size - 4` bytes long)
- `inner_crc32` = `zlib.crc32(kind_bytes || value_bytes)` stored little-endian

CRC verified across every record in all four captures: `crc_ok = True` for 100% of parsed records. The new builder must produce a correct CRC; the existing `Telemetry/SessionPropertyPushBuilder.cs` already does this for the 4 kinds it supports.

## Init sequence (cold-start order)

Confirmed identical across `bridge-20260503-{112940, 113616, 115840}.jsonl` (the 2026-05-03 cold-start captures; 113353 is mid-session and lacks the init phase).

PitHouse sends these on **h2b session 0x02** at session start, in this exact order, before any other FF record:

### 1. kind=2 (timestamp init) — pos=0, size=16

```
ff [10 00 00 00] [crc:LE] [02 00 00 00] [unix_ts:LE u32] [00 00 00 00] [tz_offset_sec:LE i32]
```

Captured values:

| Capture | hex value (12B after kind) | unix_ts | tz_offset |
|---------|:---------------------------|--------:|----------:|
| 112940  | `1e94f76900000000909dffff` | 1777719326 (0x69f7941e) | -25200 (PST) |
| 113616  | `a495f76900000000909dffff` | 1777719716 (0x69f795a4) | -25200 (PST) |
| 115840  | `e29af76900000000909dffff` | 1777721058 (0x69f79ae2) | -25200 (PST) |

Builder rule: `[unix_ts = current_time_seconds] [zero u32] [tz_offset = local_offset_seconds_signed]`. The middle 4 bytes are always zero across all captures.

### 2. kind=7 (init command) — pos=25, size=12

```
ff [0c 00 00 00] [crc:LE] [07 00 00 00] [03 00 00 00] [00 00 00 00]
```

Constant across all captures: value is `[3:u32LE] [0:u32LE]`. Builder emits literal `0300000000000000` after the `kind=7` header.

### 3. kind=8 (channel catalog upload) — pos=46, size varies (~2003–2010 bytes)

```
ff [size:LE] [crc:LE] [08 00 00 00] [zlib-compressed channel catalog]
```

- 112940: size=2003
- 113616: size=2010
- 115840: size=2007

Variation across captures is in the zlib stream content — PitHouse uploads its current channel-catalog snapshot, which depends on what dashboards it has loaded locally. The wheel uses this to map URL → catalog index for tier-def emission.

The decompressed body is the same channel catalog format the wheel echoes back on `b2h` session 0x02 (decoded by `/tmp/parse_catalog_full.py` per HANDOVER-INVESTIGATION.md). Existing `Telemetry/Dashboard/DashboardProfileStore.cs:54` builds equivalent data — the new builder can call into it.

### 4. kind=11 (action catalog upload) — pos≈2065, size=2572 (every capture)

```
ff [0c 0a 00 00] [crc:LE] [0b 00 00 00] [zlib-compressed 2568B action catalog]
```

**Identical zlib payload across all three cold-start captures** (`78da9d5a05785b47129e9732a5cccc94e6d2a449...`). This is firmware-static — PitHouse hardcodes the action catalog. The builder can ship this as an embedded resource.

The action catalog defines which "actions" (button presses, knob rotations, dashboard switches, etc.) exist. The wheel uses it to map FF kind=4 dashboard-switch slots to internal IDs.

### After init: heartbeats begin

Once init completes, kind=14 + kind=15 heartbeats begin (~2s cadence). They continue throughout the session.

## Runtime FF kinds inventory

Across all four captures combined, these FF kinds appear on h2b session 0x02. Counts are aggregated across all 4 captures.

| kind | # observed | size | role | value format | builder priority |
|:----:|-----------:|:-----|:-----|:-------------|:-----------------|
| 2 | 3 (one per cold-start) | 16 | timestamp init | `[unix_ts u32][0 u32][tz_offset i32]` | required (init) |
| 7 | 3 | 12 | init command | `[3 u32][0 u32]` constant | required (init) |
| 8 | 3 | ~2KB | channel catalog | zlib(catalog blob) | required (init) |
| 11 | 3 | 2572 | action catalog | zlib(static 2568B blob) | required (init) |
| 14 | 263 | 8 | heartbeat A | `[100 u32]` (= 0x64) | required (keepalive) |
| 15 | 230 | 8 | heartbeat B | `[N u32]` varies (24, 18, 3, 2, 1, …) — appears to be tick-counter | required (keepalive) |
| 9 | 171 | 22 | LED color push | `[00 90 00 00 00 43 00 01] [4× rgb16]` etc — runtime LED writes | optional (LED control) |
| 4 | 7 | 12 | dashboard switch | `[slot_idx u32][0 u32]` | required (switch) |
| 1 | 0 | — | brightness u32 (per `SessionPropertyPushBuilder.cs:44`) | not seen in these captures | optional |
| 10 | 0 | — | standby timeout u64 (per `SessionPropertyPushBuilder.cs:56`) | not seen in these captures | optional |

Note: kinds 1 and 10 are emitted from the existing plugin via UI brightness sliders / standby timeout settings. They aren't observed in these capture sessions because the user didn't move those sliders during recording. The new `FfRecordBuilder` must support them per `Protocol/SessionPropertyPushBuilder.cs:44,56`.

### kind=14 + kind=15 heartbeat pattern

Sample sequence from `bridge-20260503-113616.jsonl`:

```
14 val=64000000     (= 100)
14 val=64000000
14 val=64000000
14 val=64000000
15 val=18000000     (= 24)
15 val=18000000
15 val=18000000
15 val=18000000
14 val=64000000
15 val=03000000     (= 3)
14 val=64000000
14 val=64000000
15 val=02000000     (= 2)
14 val=64000000
15 val=01000000     (= 1)
14 val=64000000
```

kind=14 carries a constant value 100 across all captures. kind=15 carries a variable u32 (1, 2, 3, 24 observed). The semantics are not yet decoded — possibly an idle-time counter, possibly a tick number. The new `KeepaliveOp` should emit kind=14=100 every ~2s and replicate kind=15's pattern (start at the highest observed value and decrement, or experiment with constant value first; live-test against real wheel).

### kind=4 dashboard switch

Format confirmed: `[slot:u32LE] [0:u32LE]` (12 bytes value).

Slots observed in captures:
- 113616: slot 7 (`07 00 00 00`)
- 113353: slot 8 (`08 00 00 00`)
- 115840: 5 occurrences (5 different switches in the multi-switch capture)

Existing `Protocol/SessionPropertyPushBuilder.cs:72 BuildDashboardSwitchBody` already builds this; the new builder can reuse it.

### kind=9 LED color push

Sample `0090000000430001ffff0000232332320000` (22B value):
- `0090000000430001` (8B header — possibly `[group:u32][addr:u16][type:u16]`)
- `ffff0000` (4B — first LED RGB16?)
- `232332320000` (6B — additional LED bytes?)

Out of scope for Phase 0 — LED color encoding is its own decode task. The existing plugin LED system in `Devices/MozaLedDeviceManager.cs` writes LEDs over a different non-FF path (group 41/40 raw frames), so kind=9 may be for the dashboard's own LED pipeline rather than wheel LEDs. Defer until LED parity work.

## Operational rules for the new WheelHandshakeOp

1. On `Start()`:
   1. Emit kind=2 with `[current_unix_seconds][0][local_tz_offset_seconds]`.
   2. Emit kind=7 with constant `0300000000000000`.
   3. Emit kind=8 with zlib-compressed channel catalog. Catalog content comes from `DashboardProfileStore.GetTelemetryMap()` filtered to channels referenced by loaded dashboards (existing logic).
   4. Emit kind=11 with the firmware-static action catalog (removed as embedded resource `Data/ActionCatalog.zlib` caused hardware crash).
   5. Emit close-sequence chunk on session 0x02 only after all 4 are ack'd? — TBD; in captures the next FF record (kind=14 heartbeat) follows ~2s later with no explicit close. The new operation can simply transition to `KeepaliveOp` after kind=11 send completes.

2. All four init records ship via `SessionEndpoint(0x02).SendChunk(payload)` with FF wire format. Blind retransmit policy may not be needed on session 0x02 — captures show wheel FC-acks session 0x02 chunks.

3. Failure handling: if no `b2h` activity follows kind=11 within ~2s, retry the entire init sequence. Out of scope for Phase 0 — Phase 4 will define retry behavior based on live testing.

## Operational rules for the new KeepaliveOp

1. Emit kind=14 with value `64000000` (constant 100) every ~2s.
2. Emit kind=15 with value matching whatever counter PitHouse uses — Phase 4 will reverse-engineer the kind=15 value semantics if the wheel cares. Initial implementation: emit kind=15 with constant 0x18 (24) and verify wheel doesn't disconnect.

## Test fixtures for Phase 6

| # | Capture | Pos | Kind | Bytes (full FF record) |
|--:|---------|----:|-----:|:-----------------------|
| 1 | 112940 | 0 | 2 | `ff10000000<crc>020000001e94f76900000000909dffff` (25B total) |
| 2 | 113616 | 25 | 7 | `ff0c000000<crc>070000000300000000000000` (21B total) |
| 3 | 115840 | 0 | 2 | `ff10000000<crc>02000000e29af76900000000909dffff` (25B total) |

The new `FfRecordBuilder` must produce these exact bytes given the inputs. CRC must match — Phase 6 tests will verify.

## What this changes vs the refactor plan

An earlier plan listed "Inverted session layout — tier-def on 0x01 should be 0x02; FF records on 0x02 should be 0x01" as a key tangle (citation: HANDOVER-INVESTIGATION.md). **That tangle does not exist in 2026-05-03 captures.** The plugin's current session layout is already correct.

This means:
- Tier-def stays on session 0x01 (matches the `_session01OutboundSeq` path).
- FF records (init + heartbeat + switch + LED + brightness + standby) go on session 0x02 (matches the `_session02OutboundSeq` path).

The session swap was a non-issue; `END_MARKER` cumulative max-channel-idx rule and proper blind-retransmit policy on tier-def session are the real structural concerns.

## Open items

- **kind=15 value semantics** — appears to decrement irregularly. Worth a deeper decode pass during Phase 4 if the wheel turns out to care about the exact value.
- **kind=9 LED color schema** — out of scope for telemetry refactor.
- **CRC algorithm verification** — confirmed `zlib.crc32(kind || value)` matches all observed `inner_crc` fields. The existing `TierDefinitionBuilder.Crc32` produces the same result; new builder shares this primitive.
