# Dashboard switch: tier-def protocol alignment with PitHouse

Date: 2026-05-07. Source: 35 PitHouse bridge captures in `sim/logs/`,
616+ dashboard switch events, and wire trace `moza-wire-20260507-122406.jsonl`.

## Problem

Dashboard switching failed: garbage value frames, prolonged mute causing
wheel dormancy, FIFO priority starvation. Prior work fixed deferred
profile changes and FIFO drain ordering. This document covers the
remaining tier-def protocol gaps found by comparing our wire output to
PitHouse bridge captures.

## Method

### Bridge capture analysis tools

Reusable tools created in `tools/`:

- `tools/moza_bridge.py` -- shared loader/decoder for PitHouse bridge
  JSONL. Defines `BFrame` dataclass with session, value-frame, and
  FF-record properties.
- `tools/bridge-inventory` -- full traffic inventory of a capture
  (groups, sessions, FF-records, value frame flags, commands).
- `tools/bridge-switch` -- detailed traffic timeline around FF-record
  kind=4 (DASH_SWITCH) events with configurable window.

One-shot analysis scripts in `/tmp/`:

- `/tmp/find_bridge_switches.py` -- scan all captures for switch
  indicators (multi-flag, kind=4, session re-opens).
- `/tmp/bridge_switch_decode.py` -- decode session 0x01 TLV data around
  switches (TIER_DEF, ENABLE_PREV, KEEPALIVE).
- `/tmp/bridge_tierdef_flags.py` -- flag progression and ENABLE_PREV
  retirements across switches.
- `/tmp/decode_tag03.py` -- deep decode of all tag 0x03 on session 0x01
  with surrounding context, direction, seq numbers, and raw hex.
- `/tmp/tag03_vs_switches.py` -- categorize tag 0x03 by context
  (cold-start, catalog re-advert, preamble, near-switch).
- `/tmp/tag03_real_vs_chunk.py` -- distinguish real standalone tag 0x03
  from chunk-continuation artifacts.
- `/tmp/verify_h2b_tags_post_switch.py` -- classify all h2b session 0x01
  frames post-switch as real TLV records vs chunk continuations.
- `/tmp/bridge_tierdef_decode.py` -- reassemble and decode PitHouse
  tier-def TLV stream from bridge captures.

### Wire trace verification

Built plugin with changes, deployed, ran auto-test (Core -> Grids
dashboard switch). Examined wire trace with `tools/trace-summary` and
`tools/tierdef-decode`. Compared tier-def TLV structure side-by-side
with PitHouse bridge captures.

## Tag 0x03: not "UNSUBSCRIBE" -- decoded as FLAG_BASE / catalog status

Earlier analysis scripts labeled session 0x01 tag 0x03 as "UNSUBSCRIBE".
This was wrong. Verified by decoding all tag 0x03 frames across 35
captures with `decode_tag03.py` and `tag03_vs_switches.py`.

### h2b tag 0x03: FLAG_BASE preamble terminator

Format: `tag=0x03 size=0` (5 bytes net + 4 CRC = 9 bytes sess_data).
Always at seq=3, preceded by tag=0x07 (PROTO_VER). Part of the tier-def
TLV preamble, emitted once per session at cold start. Not a standalone
message -- it is a TLV record within the tier-def stream.

Example from every capture's cold start:
```
seq=2: 07 04000000 02000000   → PROTO_VER=2
seq=3: 03 00000000             → FLAG_BASE (preamble end)
seq=4: 01 51000000 00 ...     → TIER flag=0x00
```

Already correctly implemented: `TelemetrySender.cs:1457-1466` emits
this preamble once (`_tierDefPreambleSent` gate).

### b2h tag 0x03: wheel subscription/catalog status

Format: `tag=0x03 size=4 value=u32LE` (9 bytes net + 4 CRC = 13 bytes).
Two observed values:

| Value | Meaning | Context |
|-------|---------|---------|
| 1 | "catalog ready, awaiting subscription" | After tag=0xFF reset, before CHANNEL_INFO records |
| 2 | "subscription acknowledged" | After host sends tier-def |

Pattern at cold start (every capture):
```
seq=4: FF 000000FF             → reset sentinel
seq=5: 03 04000000 01000000   → catalog status = 1 (ready)
seq=6: 04 17000000 01 ...     → CHANNEL_INFO #1
seq=7: 04 1B000000 02 ...     → CHANNEL_INFO #2
  ...
seq=12: 06 04000000 ...       → KEEPALIVE
seq=13: 03 04000000 02000000  → catalog status = 2 (subscription accepted)
```

This cycle repeats 1-3 times at startup (~1s apart) until the host
sends a tier-def. Occasionally reappears during session re-opens
(~30-80s into a capture).

### tag 0x03 does not participate in the switch protocol

Across 616+ dashboard switches in all captures, only 2 real standalone
b2h tag 0x03 frames appeared within 5s of a switch event, both
coincidental (cold-start catalog re-advertisements that happened near
switch timing). The host never sends a standalone tag 0x03 during
switches -- the only h2b 0x03 is the FLAG_BASE within the preamble,
which is not re-sent on switch.

Verified by `tag03_real_vs_chunk.py` filtering real TLV records (size=0
or size=4) from chunk continuations (garbage sizes from multi-chunk TIER
records).

## Tag 0x05: does not exist as a message type

All observed instances of sess_data[0] == 0x05 were chunk-continuation
bytes, not standalone TLV records.

`ChunkMessage` splits the tier-def into 54-byte net chunks. A TIER
record with 5 channels is 86 bytes (tag + size + flag + 5×16). The
first chunk (54B) fits at seq=N, the continuation (32B) starts at
seq=N+1. When byte 54 of the TIER happens to be 0x05 (a channel
index), the bridge analysis scripts misidentified it as a "tag 0x05
CHANNEL_CATALOG" message.

Proof: `verify_h2b_tags_post_switch.py` classified all h2b session 0x01
frames within 5s after each of 616 switches. Results:

| Classification | tag=0x00 | tag=0x01 | tag=0x06 | tag=0xFF | Others |
|----------------|----------|----------|----------|----------|--------|
| Real TLV record | 2679 | 1407 | 1314 | 30 | 0 |
| Chunk continuation | 1330 | 1391 | 31 | 0 | 1006 |

The "Others" column (chunk continuations) includes byte values 0x02
through 0xFF with no TLV structure. These are interior bytes of
multi-chunk TIER records.

## Verified h2b switch protocol

The host sends exactly three TLV record types during a dashboard switch
on session 0x01:

1. **ENABLE (tag=0x00)**: Retire old subscription's last-broadcast flags
2. **TIER (tag=0x01)**: New tier definitions with channel records
3. **END_MARKER (tag=0x06)**: Section terminators between broadcasts

No preamble (tag=0x07 + tag=0x03) on switch -- preamble is cold-start
only.

## Gaps found and fixed

### Gap 1: flat tier-def format vs PitHouse per-broadcast-section format

`SendTierDefinition()` always called `BuildTierDefinitionV2` which emits
a flat layout: all ENABLEs up front, then all TIERs, then one END.

PitHouse uses per-broadcast sections where each broadcast round gets its
own TIER block + END marker + ENABLE block:
```
TIER b0_t0, TIER b0_t1, END=0
ENABLE b0_t0, ENABLE b0_t1, TIER b1_t0, TIER b1_t1, END=N
...
```

`BuildTierDefinitionMessageType02` at `TierDefinitionBuilder.cs:50-178`
already implemented this format but was never called from production.

**Fix**: Route `SendTierDefinition()` to `BuildTierDefinitionMessage`
(which dispatches to Type02) when `cspIdx` is true. Non-Type02 firmware
retains the flat path.

### Gap 2: no previous-subscription ENABLEs on switch

PitHouse sends ENABLE records for the old subscription's last-broadcast
flags before new tier-defs. Example from `bridge-20260429-163951.jsonl`
switch at t=557s:
```
ENABLE 0x09, ENABLE 0x0A, ENABLE 0x0B   ← old last-broadcast flags
TIER flag=0x0C ...                        ← new subscription
```

`BuildTierDefinitionMessageType02` already accepted `prevFlagBase`,
`prevTierCount`, `prevSubPerBroadcast` parameters and computed the
correct ENABLEs (lines 156-162), but `SendTierDefinition()` never
passed them.

**Fix**: Snapshot `_activeSubscription` before building the new message
and pass its fields as prev-subscription parameters.

### Gap 3: SubTiersPerBroadcast stored wrong value

At `TelemetrySender.cs:1512`:
```csharp
subTiersPerBroadcast: profile.Tiers.Count  // total=8, should be per-broadcast=2
```

This stored the total wire tier count (e.g. 8 for 2 logical × 4
broadcasts) instead of the per-broadcast count (2). Needed for correct
prev-sub ENABLE computation.

**Fix**: Added `DetectSubTiersPerBroadcast` public static helper to
`TierDefinitionBuilder`. Detects broadcast boundaries by
`PackageLevel` repeat pattern. Refactored `BuildTierDefinitionMessageType02`
to use the same helper.

## Code changes

| File | Change |
|------|--------|
| `Telemetry/Frames/TierDefinitionBuilder.cs` | Added `DetectSubTiersPerBroadcast()` public static method. Refactored `BuildTierDefinitionMessageType02` inline detection (lines 130-143) to call it. |
| `Telemetry/TelemetrySender.cs` | In `SendTierDefinition()` V2 branch: snapshot `_activeSubscription` as `prevSub`, route to `BuildTierDefinitionMessage` (Type02 path) when `cspIdx`, pass prev-sub fields for switch ENABLEs. Fixed `SubTiersPerBroadcast` to use `DetectSubTiersPerBroadcast(profile)`. Updated log line with format type and prev-sub state. |

## Wire trace verification

Wire trace: `moza-wire-20260507-122406.jsonl` (97472 frames, 253s).
Auto-test: Core (slot 0) -> Grids (slot 1), both pre- and post-switch
tests PASS (167 value frames each).

### Cold start tier-def (E0, flags 0x00-0x07)

```
PROTO_VER=2
FLAG_BASE
TIER flag=0x00  5ch → TIER flag=0x01  1ch → END=0
ENABLE 0x00, 0x01 → TIER 0x02  5ch → TIER 0x03  1ch → END=6
ENABLE 0x02, 0x03 → TIER 0x04  5ch → TIER 0x05  1ch → END=6
ENABLE 0x04, 0x05 → TIER 0x06  5ch → TIER 0x07  1ch → END=6
```

2 sub-tiers × 4 broadcasts, 6 channels/broadcast. Per-broadcast
section format. No prev-ENABLEs (cold start). Preamble present.

### Switch tier-def (E1, flags 0x08-0x0F)

```
ENABLE 0x06, 0x07                                    ← prev-sub retire
TIER flag=0x08  8ch → TIER flag=0x09  12ch → END=0
ENABLE 0x08, 0x09 → TIER 0x0A  8ch → TIER 0x0B 12ch → END=20
ENABLE 0x0A, 0x0B → TIER 0x0C  8ch → TIER 0x0D 12ch → END=20
ENABLE 0x0C, 0x0D → TIER 0x0E  8ch → TIER 0x0F 12ch → END=20
```

Prev-sub last-broadcast ENABLEs (0x06, 0x07) before new TIERs. No
preamble. Flags monotonically increasing (0x08 continues from 0x07).
Computed from `prev=0x00/8t/2spb`: lastBase = 0 + (4-1)×2 = 6.

### Comparison with PitHouse bridge captures

PitHouse cold start (`bridge-20260429-163951.jsonl`, Rally V4):
```
PROTO_VER=2 → FLAG_BASE
TIER 0x00 4ch → TIER 0x01 1ch → TIER 0x02 1ch → END=0
ENABLE 0x00,0x01,0x02 → TIER 0x03..0x05 → END=6
ENABLE 0x03,0x04,0x05 → TIER 0x06..0x08 → END=6
ENABLE 0x06,0x07,0x08 → TIER 0x09..0x0B → END=6
```

3 sub-tiers × 4 broadcasts (Rally V4 dashboard). Structural format
identical to ours despite different channel/tier counts:

| Property | PitHouse | Ours | Match |
|----------|----------|------|-------|
| Per-broadcast sections | yes | yes | yes |
| First END=0, rest=channel count | yes | yes | yes |
| No trailing ENABLEs after last broadcast | yes | yes | yes |
| Preamble on cold start only | yes | yes | yes |
| Prev-sub ENABLEs on switch | yes | yes | yes |
| Flags monotonically increasing | yes | yes | yes |

### SimHub log confirmation

```
[12:24:13] Sending type02-section tier definition: flagBase=0x00, prev=none, ...
[12:24:18] Sent dashboard-switch FF-record: slot=1 on session 0x02
[12:24:18] Sending type02-section tier definition: flagBase=0x08, prev=0x00/8t/2spb, ...
[12:24:23] AUTO-TEST: state=DONE
```

Auto-test result: pre-switch PASS (167 frames), post-switch PASS (167
frames). Both using `type02-section` format.
