# PitHouse tier-def byte-exact reference

Byte-exact "what PitHouse emits" reference per captured session — used as the source of truth when verifying that tier-def emission matches the wheel's expectations.

It supersedes (in scope of byte-exactness) the earlier semantic analysis in `2026-05-03-pithouse-tierdef-reference.md`. The semantic facts established there still hold — this document provides the byte-level reference that the semantic rules predict.

## Method

`docs/protocol/findings/2026-05-04-tierdef-byte-exact/extractor.py` walks each bridge JSONL capture and:

1. Filters to host→base session-data chunks: `7c00 [sess] [type=01] [seq:u16LE] [payload] [crc32:4]` inside `grp=0x43 dev=0x17` frames.
2. Records each `seq`'s **first-occurrence bytes** (retransmits with identical content are deduped; mismatching content would be flagged but none observed).
3. Builds one canonical append-only stream per session by concatenating each unique seq's payload in ascending seq order.
4. Parses the canonical stream as TLV (`tag:1 size:u32LE value:size`) and tags each entry with which time-window emission it landed in.
5. Cross-references the dashboard-active timeline (`grp=43 dev=17 cmd=7d23` byte 6) so each emission can be associated with which dashboard the wheel was rendering at the time.

Per-capture full reports are in `2026-05-04-tierdef-byte-exact/bridge-*.md` — these are the byte-exact reference rows. The summary below pulls out the structural patterns each capture exercises.

## Captures

| Capture | Scenario | Stream bytes | Unique seqs | TLV ENABLE-section windows |
|---------|----------|-------------:|------------:|---------------------------:|
| `bridge-20260503-112940.jsonl` | Rally V3 only, cold start to runtime | 1630 | 108 | 6 emissions, max flag 0x10 |
| `bridge-20260503-113353.jsonl` | Mid-session capture (started already runtime) | partial | partial | starts at flag 0x11 |
| `bridge-20260503-113616.jsonl` | Multi-dash session: V4 init then switches | ~6KB | 250+ | many emissions, max flag 0x40+ |
| `bridge-20260503-115840.jsonl` | 6-dash multi-switch | ~5KB | 200+ | many emissions across all dashboards |

Cold-start captures (112940, 113616, 115840) are the primary references. 113353 is supplementary because it starts mid-session and lacks the preamble.

## Confirmed structural facts (byte-level evidence in per-capture files)

These hold across every emission in every capture. They are the invariants the new builder must satisfy.

### 1. Preamble is one-shot at session start

Every cold-start capture has exactly one occurrence of `[tag=07 size=4 PROTO_VER=2][tag=03 size=0]` at offset 0 of the canonical stream. No subsequent emission re-emits it.

Reference bytes: `07 04 00 00 00 02 00 00 00 03 00 00 00 00`

### 2. Tier flag is monotonic per session

Flag bytes within a session are a single ascending counter shared across all dashboards. They never reset, never realign to round numbers, never reuse.

| Capture | Flag span |
|---------|-----------|
| 112940  | 0x00..0x10 |
| 113616  | 0x00..0x40+ |
| 115840  | 0x00..0x40+ |
| 113353  | 0x11..0x1f (mid-session entry) |

Flag advances by exactly the count of TIER records in the previous emission. A 3-tier emission advances by 3.

### 3. Section structure: header-position ENABLEs then TIERs then END

A *section* begins with zero or more `[tag=00 size=1 ENABLE_PREV_TIER=N]` records, followed by one or more `[tag=01 size=size TIER]` records, terminated by exactly one `[tag=06 size=4 END_MARKER val=N]`.

The first section of a session has zero ENABLEs (no prior tiers exist). Subsequent sections ENABLE every flag from the immediately-prior section.

### 4. ENABLEs reference *prior-section* flags only, never the current section's

In every capture, ENABLE records before a section's TIER records carry exactly the flag bytes of the previous section. There are no trailing per-tier ENABLEs after the END_MARKER for newly-declared flags. The act of declaring a TIER is itself the "enable" for that flag.

### 5. Channel records are 16 bytes: idx + comp + bw + reserved

`[idx:u32LE] [comp:u32LE] [bw:u32LE] [reserved:u32LE = 0]`

The `tag=01 TIER` value is `[flag:u8] [N × 16-byte channel records]`. Size field is `1 + 16N`.

### 6. No URL records anywhere in V2 traffic

Across all four captures, zero `tag=04` records observed. The wheel resolves URL→idx from a separate `b2h` catalog stream (out of scope for this document — see `2026-04-28-wheel-catalog-read.md`). The V2 builder emits numeric indices only; never URLs.

### 7. END_MARKER value is per-section, NOT global

End-marker values change between sections within a single emission and within a single session. Observed values from `bridge-20260503-112940.jsonl`:

- Section 1 (flags 0,1, 6 channels): END = 0
- Section 2+ (flags 2-16, 16 channels referenced): END = 16

From `bridge-20260503-113616.jsonl`:

- Section 1 (V4 1-channel warmup, flags 0,1,2): END = 0
- Section 2+ (V4 steady-state 6/2/1 channels, flags 3..0x29): END = 9
- Post-switch sections (V3 8/11/12 channels): END = 18, then 52
- Post-switch later (different dash, 5/1 channels): END = 53, then 78
- After more switches: END = 79, 88, 108

**Operational rule from captures**: END value tracks the maximum channel index referenced in *any* tier-def emitted in the session up to and including this section. When a new section introduces a channel index that exceeds the prior watermark, END jumps to the new max.

This is the **key operational rule** the new builder must implement. The negotiator must track a session-global `_maxChannelIdxEverSeen` and emit it as END for each section.

### 8. Sections may chain in a single message

A single tier-def emission can contain multiple sections back-to-back: `[ENABLEs][TIERs][END][ENABLEs][TIERs][END]...`. The 426B initial emission in 112940 contains 2 sections. The 89B+278B pattern in 113616 is one section then a follow-up emission with the next cumulative section.

### 9. PitHouse blind-retransmits each chunk multiple times

Retransmit audit from `bridge-20260503-112940.jsonl`: every seq from 14 to 56 appears 3-5 times with byte-identical content. The wheel never FC-acks session 0x01 (per `2026-05-02-tier-def-retransmission.md`). PitHouse uses a fixed retransmit count.

The new builder/sessions layer must implement `BlindRetransmit(rounds=10, intervalMs=200)` for session 0x01 (or whichever session carries tier-def in the final layout — note the plan calls for inverting to session 0x02; the retransmit policy travels with the tier-def stream regardless of session number).

## Patterns by dashboard kind

These per-dashboard channel-set patterns appear across captures. The builder will need to reproduce them given mzdash inputs.

### Rally V3 (112940, 113616 post-switch)

3 sub-tiers per broadcast, repeating every 3 flags:

| Sub | N | Channel idx set | Compression / bitwidth |
|----:|--:|:----------------|:------------------------|
| 0   | 6 | 1, 3, 5, 6, 7, 8 | bool/float32/uint5/uint16/int16/bool |
| 1   | 2 | 2, 4 | uint8 / float32 |
| 2   | 8 | 9-16 | tyre_temp_12 × 4, tyre_pressure_14 × 4 |

Initial-section warmup (only at session start): N=4 then N=2 (subset of the steady set) with END=0.

### Rally V4 (113616 init)

3 sub-tiers per broadcast:

| Sub | N | Channel idx set | Compression / bitwidth |
|----:|--:|:----------------|:------------------------|
| 0   | 6 | 1, 4, 6, 7, 8, 9 | bool/float32/uint5/uint16/int16/bool |
| 1   | 2 | 3, 5 | uint8 / float32 |
| 2   | 1 | 2 | float32 |

Initial-section warmup: 3 × N=1 with END=0.

### V3-style with extended catalog (115840 advanced phases)

Some emissions show N=11 and N=12 tiers carrying the full extended channel set including indices up to 20+. These appear when the wheel has expanded its catalog with new URL→idx mappings and the negotiator catches up.

### Smaller dashboards (Mono, Pulse — 113616 later phases)

5/1, 4/1, 4 channels per tier. Compression codes mix `0xe` (uint10) and the standard `0xd/0xf/0x4` set.

## Operational rules for the new TierDefBuilder

These are derived from the byte-level invariants and are sufficient to implement V2/Type02 emission byte-exactly:

1. **First emission of a session**:
   - Emit `[tag=07 PROTO_VER=2][tag=03 size=0]` once.
   - Emit section 1: TIER records for the dashboard's first sub-tier set, END_MARKER val = 0.
   - Section 1 has no ENABLE records.
   - Increment `_nextFlagBase` by `tier_count_in_section_1`.

2. **Every subsequent emission**:
   - No preamble.
   - For each section in the emission:
     - Emit ENABLE records for every flag declared in the immediately-prior section.
     - Emit TIER records for the new tiers, using `_nextFlagBase + i` as flag bytes.
     - Compute `_maxChannelIdx = max(_maxChannelIdx, max(channel.idx for channel in this_section))`.
     - Emit END_MARKER val = `_maxChannelIdx`.
   - Increment `_nextFlagBase` by total tier count emitted.

3. **Dashboard switch is identical to "next emission"**:
   - No preamble.
   - First section's ENABLEs reference the last section of the previous dashboard's emissions.
   - TIER records use the new dashboard's channel sets.
   - END_MARKER val = `_maxChannelIdx` (cumulative across all dashboards in this session).

4. **State carried across emissions**:
   - `_nextFlagBase: byte` — monotonic, starts 0, never reset until session-close.
   - `_lastSectionFlags: List<byte>` — flag bytes of the just-completed section, for ENABLE generation in the next section.
   - `_maxChannelIdx: int` — running max of all channel indices ever emitted.
   - `_preambleSent: bool` — gated to one-shot at session start.

5. **Channel records**: `[idx:u32LE] [comp:u32LE] [bw:u32LE] [00 00 00 00]`. Reserved is always 4 zero bytes.

This is sufficient for the V2/Type02 shape. V0 URL-subscription is a separate body shape (see `2026-04-21-pithouse-deviations.md`); V0 builder selection happens at the entry point based on `FirmwareEra`.

## What this resolves vs the old open questions

The earlier 2026-05-03 reference flagged END_MARKER semantics as "open" with a partial fit. The bridge captures here resolve it: END = max channel index referenced across the entire session up to this section. This explains every observation in the 2026-05-03 doc's table that earlier hypotheses (max-idx-in-section, channels-in-section) failed to fit.

The "what triggers section-2 emission" open question is still partially open — the ~3.1s gap between section 1 and section 2 in 113616 likely waits for the wheel to send a catalog ack on a different stream, but the new builder doesn't need to reproduce the exact timing as long as section 2 follows section 1 within the retransmit window. The negotiator will emit cumulative content per tick; live testing will confirm whether the wheel cares about timing.

The "cumulative redeclaration on retransmit" open question doesn't matter for the new builder — chunks are content-identical retransmits via the blind-retransmit policy, and the canonical-stream view we use for byte-diff testing already deduplicates them.

## Test-fixture rows for byte-diff verification

Each row below identifies a byte-diff reference scenario. The "expected bytes" column references the byte range in the canonical stream of the named capture file.

| # | Capture | Emission | Bytes (offset..end) | Scenario |
|--:|---------|---------:|:--------------------:|:---------|
| 1 | 112940 | E0 | 0..131 | Cold-start, Rally V3 init: preamble + 2-tier warmup, END=0 |
| 2 | 112940 | E1 | 131..426 | Steady V3 broadcast 1: ENABLE 0,1 + 3 tiers (6/2/8), END=16 |
| 3 | 112940 | E2 | 426..727 | Steady V3 broadcast 2: ENABLE 2,3,4 + 3 tiers (6/2/8), END=16 |
| 4 | 113616 | E0 | 0..89 | Cold-start, Rally V4 init: preamble + 3-tier warmup (1/1/1), END=0 |
| 5 | 113616 | E1 | 89..278 | V4 steady broadcast 1: ENABLE 0,1,2 + 3 tiers (6/2/1), END=9 |
| 6 | 113616 | E28 | 695..964 | Mid-session V4→V3 switch: ENABLE 5,6,7 + new V3 tiers (4/2/8), END=16 |
| 7 | 113616 | E29 | 964..1307 | First post-switch full V3 broadcast: ENABLE 8,9,10 + V3 8/11 tiers, END=18 |
| 8 | 115840 | E0 | 0..N | Cold-start (capture 4) preamble + first dash init |
| 9 | 115840 | E_switch1 | varies | First dashboard switch in 6-dash cycle |
| 10 | 115840 | E_switch2 | varies | Second dashboard switch (back-to-back) |

Phase-6 tests will: (a) parse the byte range from the per-capture markdown reference, (b) reconstruct the input state (firmware era, dashboard profile, prior subscription, max channel idx so far), (c) call the new builder, (d) byte-diff against the expected.

## Open items deferred to later phases

- **Compression-code coverage**: the captures show codes `0x00, 0x02, 0x04, 0x07, 0x0d, 0x0e, 0x0f, 0x11, 0x13, 0x16` in tier-def channel records. The new `CompressionTable` must support all of these. Bit widths observed: 1, 5, 8, 10, 12, 14, 16, 32. A few codes (`0x10` tyre_pressure_1, `0x11` tyre_temp_1) are flagged in `HANDOVER-DASHSWITCH.md` as "broken on Type02 — use float (0x07) instead"; the captures use `0x16` (12-bit) and `0x11` (14-bit) for tyre channels, which contradicts the handover note. This needs a separate decode against value frames in Phase 4 to reconcile.
- **Channel idx → URL mapping**: tier-def emission requires the wheel's `b2h` catalog to map mzdash channel URLs to numeric indices. The catalog parser already exists (`Telemetry/Dashboard/DashboardProfileStore.cs:54`); it must be tested against fresh `b2h` data from these captures.
- **Section-2 emission trigger timing**: confirmed not byte-relevant; defer until live testing in Phase 7.
- **`tag=04` URL records**: not present in any V2 capture, only V0. V0 path captures need their own byte-exact reference doc when the V0 builder is implemented (Phase 3 also covers V0).
