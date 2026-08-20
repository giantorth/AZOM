# Session 0x02 FF-record kinds — protocol reference

> **Canonical reference:** [`../sessions/session-0x02-ff-init.md`](../sessions/session-0x02-ff-init.md).
> This file is investigation-era body-decoding detail used to derive the
> canonical protocol doc; consult the canonical doc first for the
> handshake structure, the kind=2/7/8/11 plugin status, and the
> verified-broken shortcut of replaying captured kind=8/11 bytes
> (locked a W17 wheel 2026-05-13).

Date: 2026-05-07. Source: bridge captures from the user's own wheel hardware
captured against a Windows PitHouse instance, located in `sim/logs/`, primarily
`bridge-20260429-163951.jsonl` (Rally V4 + Nebula, ~344 K frames, includes
init handshake + 10 dashboard switches). Tools used:
`tools/bridge-decode-ff-init`, `tools/trace-sess02-decode`.

## Summary

All meaningful host↔wheel FF-tagged property records ride on session 0x02
(NOT session 0x01 as an earlier comment in `Telemetry/TelemetrySender.cs`
suggested). The wheel will not echo dashboard-switch FF kind=4 records
nor bind post-switch tier-defs to display widgets until it has received
the host-side init handshake. The init handshake is four FF records on
sess=0x02, with the wheel responding ~3.5 s later via two FF records.

The earlier file `2026-04-29-session-01-property-push.md` describes the
inner FF-record envelope in detail; this doc complements it with the
specific kind values, their bodies, and the protocol timing.

## Wire-frame envelope

Verified against `Telemetry/Sessions/SessionPropertyPushBuilder.WrapFfRecord`:

```
[0xFF]                    — sentinel (1 byte)
[size:u32 LE]             — kindAndValue length (4 bytes), INCLUDES the kind prefix
[crc:u32 LE]              — zlib.crc32 of the entire kindAndValue payload (4 bytes)
[kindAndValue]            — exactly `size` bytes:
    [kind:u32 LE]         — discriminator (4 bytes)
    [payload]             — `size - 4` bytes, kind-specific
```

Total wire bytes per FF record: `9 + size`.

These records are wrapped in standard SerialStream session-data chunks
(`7c 00 02 01 <seq:u16> ...`) on session 0x02 and carry their own per-chunk
CRC32 trailer per [`../sessions/chunk-format.md`](../sessions/chunk-format.md).
Multi-chunk records (kind=8, kind=11) span multiple chunks; each chunk has
its own seq + CRC, the wheel reassembles before parsing the inner FF record.

## FF kind inventory (host ↔ wheel, sess=0x02)

| Kind | Direction | Name (working hypothesis)        | Typical wire size | Body shape                                      |
|-----:|-----------|----------------------------------|------------------:|-------------------------------------------------|
|    2 | h2b       | `init_nonce`                     | 16 B              | timestamp + magic                               |
|    4 | h2b → b2h | `DASH_SWITCH` (and its echo)     | 12 B              | slot index                                      |
|    5 | h2b       | `display_rotation` (VGS)         | 5 B               | 1-byte mode: 0=off, 1=smooth, 2=immediate       |
|    7 | h2b       | `init_enum`                      | 12 B              | small flags / version                           |
|    8 | h2b       | `init_payload_a` (channel cat.)  | 1.7 – 2.1 KB      | length-prefixed UTF-16-BE name list, zlib       |
|    9 | both      | `periodic` (heartbeat-shaped)    | 19 – 29 B         | small struct, fires every ~1.5–2 s              |
|   10 | b2h       | `wheel_state_a`                  | 12 B              | status code (sent ~3.5 s after init handshake)  |
|   11 | h2b       | `init_payload_b` (FFB props)     | 2.5 KB            | length-prefixed UTF-16-BE name list, zlib       |
|   14 | both      | `device_log_request` / `device_log_payload` | 8 B h2b, 2.7–3.2 KB b2h | h2b: `maxLines u32 LE` (=100). b2h: `[4 B reserved][zlib]` |
|   15 | h2b       | `device_log_receipt`             | 8 B               | `linesRead u32 LE` — device drops that many     |
|   16 | b2h       | `wheel_state_b`                  | 20 B              | status (sent right after kind=10)               |

## Init handshake timing (PitHouse first 10 s on sess=0x02)

```
t=0.050  h2b OPEN sess=02
t=0.101  b2h FC ack of OPEN seq=2
t=0.101  b2h TLV stream: tag=0x07 size=1 (proto-ver), tag=0x01 size=97
         (TIER record — wheel's own state), tag=0x04 size=9 (CHANNEL_INFO),
         tag=0x06 size=N (END marker)
t=2.105  b2h re-pushes the same TLV stream (host hasn't FC-acked yet)
t=2.917  h2b FC acks seq=6 and seq=10  (drains wheel's pending state)
t=2.970  h2b FF kind=2  (init_nonce)
t=2.970  h2b FF kind=7  (init_enum)
t=2.970  h2b FF kind=8  (init_payload_a)   ← the big channel catalog
t=3.079  h2b FF kind=11 (init_payload_b)   ← the big FFB property catalog
t=5.418  b2h FF kind=10 (wheel_state_a)    ← wheel ack of init handshake
t=5.468  b2h FF kind=16 (wheel_state_b)    ← wheel ack continuation
```

The ~2.4 s gap between the host's init burst (t=2.97) and the wheel's
first ack (t=5.42) is consistent with the wheel decompressing and
processing the kind=8 (~10 KB decompressed) and kind=11 (~10 KB
decompressed) catalogs. Without these uploads, the wheel never emits
kind=10 / kind=16, and (per the symptoms documented in
`2026-05-07-sess02-init-protocol-and-stale-catalog.md`) it does not
echo subsequent FF kind=4 dashboard-switch records.

## Body decode — kind=2 (`init_nonce`)

16-byte body, comparison across captures (decoded with
`tools/bridge-decode-ff-init <cap> --until-switch -k 2`):

```
bridge-20260428-155714.jsonl   4e3bf169 00000000 909dffff 01d7b784
bridge-20260428-155843.jsonl   a63bf169 00000000 909dffff 7d87268c
bridge-20260428-163735.jsonl   c144f169 00000000 909dffff 20a516f3
bridge-20260429-140855.jsonl   6873f269 00000000 909dffff 0f7b7cbb
```

Field layout (verified by cross-capture diff):

| Offset | Size | Field            | Value                                                |
|-------:|-----:|------------------|------------------------------------------------------|
|   0..3 |    4 | timestamp_u32_LE | Unix epoch seconds at session start; varies per cap. |
|   4..7 |    4 | reserved         | always `00 00 00 00`.                                |
|  8..11 |    4 | magic            | always `90 9d ff ff` (LE 0xFFFF9D90).                |
| 12..15 |    4 | (uncertain)      | varies per capture; possibly inner-CRC or salt.      |

**Note**: bytes 12..15 vary across captures and are NOT constant. They may be
a checksum of the first 12 bytes, or a session-specific salt the wheel
verifies. Plugin's current `Telemetry/Sessions/SessionPropertyPushBuilder.BuildSessionInitField2Body`
emits the current Unix time at offsets 0..3, zeros at 4..11, and bytes
`90 9d ff ff` at 12..15 — but this puts the magic in the wrong slot
(should be at 8..11) and omits any CRC/salt at 12..15. **TODO**: re-derive
bytes 12..15 from a fresh capture and update the builder; consider whether
to compute a CRC32 or copy them as captured.

## Body decode — kind=7 (`init_enum`)

8-byte value, IDENTICAL across all 4 captures (and all 34 records in
`bridge-20260731-064830.jsonl`):

```
03 00 00 00   00 00 00 00
```

> The third group (`83 18 92 0e` in the original dump) is **not part of this
> record** — it is the next FF record's sentinel+size, pulled in by
> `tools/bridge-decode-ff-init` reading `size` bytes from offset 13 instead of
> `size - 4`. Use `tools/ff-record-decode` instead. Confirmed 2026-08-07: the
> plugin's builder reproduces the full PitHouse record byte-for-byte, inner CRC
> included, only when the value is `[3][0]`.

Field layout:

| Offset | Size | Field            | Value                                                |
|-------:|-----:|------------------|------------------------------------------------------|
|   0..3 |    4 | enum_u32_LE      | always `0x00000003`. Probably a protocol-version /   |
|        |      |                  | capability indicator.                                |
|   4..7 |    4 | reserved         | always zero.                                         |
|  8..11 |    4 | magic / checksum | always `83 18 92 0e` (LE 0x0E921883). Static across  |
|        |      |                  | captures, so this is NOT a session-derived hash.     |

`Telemetry/Sessions/SessionPropertyPushBuilder.BuildSessionInitField7Body(slotIndex)`
puts an arbitrary slot index at offset 4..7 and zero at 8..11 — diverging
from PitHouse's static body. **TODO**: pin this body to the static bytes
above and verify against another fresh capture before committing.

## Body decode — kind=4 (`DASH_SWITCH`)

12-byte body, well-understood (see
[`2026-04-30-dashboard-switch-3f27.md`](2026-04-30-dashboard-switch-3f27.md)).
Bridge captures show the host sends the FF kind=4 record on sess=0x02 and
the wheel **echoes back the byte-identical record** on sess=0x02 within
~77 ms. The plugin currently sends the host side correctly but **never
receives the echo from the user's wheel** — symptom that the init
handshake (kinds 2/7/8/11) hasn't engaged the wheel.

```
04 00 00 00   <slot:u32 LE>   00 00 00 00
```

## Body decode — kind=8 (`init_payload_a`, channel catalog)

Wire-format outer wrapper:

```
[kind=8:u32 LE][uncompressed_size:u32 BE][zlib_stream]
```

Decompressed payload is a uniform sequence of records. Each record has
the **same header** and **same trailer envelope**; the trailer carries
a TLV-style value whose layout is determined by the `type` field:

```
record = [id:u16 BE] [name_len:u32 BE] [UTF-16BE name (name_len bytes)] [TLV trailer]

TLV trailer = [type:u32 BE] [reserved:u8] [value: type-determined layout]
```

The decoding rule for each `type`:

| `type` | Value size | Value semantics |
|--------|------------|------------------|
| **0**  | 0 bytes | End-marker only (reserved byte is always `0x01`). Used by the "channel slot" records (RpmAbsolute1..10, RpmPercent1..10). Total trailer = 5 bytes. |
| **2**  | 4 bytes | `u32 BE` integer — enum / index / bool state. E.g. `buttonGroupStandbyModeV2 = 1`, `paddleStatusMode = 0`. Total trailer = 9 bytes. |
| **4**  | 8 bytes | `u64 BE` integer — countable / millisecond / 0–100 setting. E.g. `activePaddleNum = 4`, `buttonGroupBrightness = 80`, `buttonGroupStandbyBreathInterval = 3000` (ms). Total trailer = 13 bytes. |
| **6**  | 8 bytes | `u64 BE` reinterpretable as **`double BE`** (IEEE 754) — float-valued setting. E.g. `equalizerGain1 = 90.0`, `naturalInertia = 400.0`, `naturalFriction = 15.0`, `naturalDamper = 42.0`, `naturalInertiaEnabled = 1.0`. Total trailer = 13 bytes. |
| **10** | 4 + N bytes: `[strlen:u32 BE] [UTF-16BE string]` | Locale / textual value. Only observed so far on `__location → "en_US"`. Total trailer = 9 + strlen bytes. |
| **9**  | Nested preset block (variable) | Complex sub-record (introduces UTF-16LE-encoded inner channel/preset names with their own header). Outer envelope is `type=9 + 4-byte zero + …`; **inner block layout not fully decoded** — see [`2026-05-15-sess02-kind8-tlv-and-preset-block.md`](2026-05-15-sess02-kind8-tlv-and-preset-block.md) for the partial decode and the verification gap. |

The "sub-format A" / "sub-format B" labels in earlier revisions of this
document referred to records using `type=0` (sub-format A) vs records
using `type ∈ {2,4,6,9,10}` (sub-format B). Both share the same
header + trailer envelope; only the trailer's `type` byte differs.

### Worked examples

`buttonGroupBrightness` (id=22, value=80):

```
00 16                                — id u16 BE = 22
00 00 00 2a                          — name_len u32 BE = 42 (= 21 chars × 2)
[42 bytes UTF-16BE "buttonGroupBrightness"]
00 00 00 04                          — trailer type u32 BE = 4
00                                   — reserved
00 00 00 00 00 00 00 50              — value u64 BE = 80
```

`equalizerGain1` (id=112, value=90.0):

```
00 70                                — id u16 BE = 112
00 00 00 1c                          — name_len u32 BE = 28 (= 14 chars × 2)
[28 bytes UTF-16BE "equalizerGain1"]
00 00 00 06                          — trailer type u32 BE = 6
00                                   — reserved
40 56 80 00 00 00 00 00              — value as double BE = 90.0
```

`__location` (id=0, value="en_US"):

```
00 00                                — id u16 BE = 0
00 00 00 14                          — name_len u32 BE = 20 (= 10 chars × 2)
[20 bytes UTF-16BE "__location"]
00 00 00 0a                          — trailer type u32 BE = 10
00                                   — reserved
00 00 00 0a                          — strlen u32 BE = 10
00 65 00 6e 00 5f 00 55 00 53        — UTF-16BE "en_US"
```

### Channel-slot records (`type=0`, first 20 records)

The records with `type=0` trailers are the **dashboard data slots** —
`RpmAbsolute1`, `RpmAbsolute10`, `RpmAbsolute2`, …, `RpmPercent9` (20
total, ids 2–21). Names sort alphabetically (hence the `1`, `10`, `2`
ordering). These appear to be the slots the wheel binds RPM-related
widgets to.

### Property records (`type ∈ {2, 4, 6, 10}`)

After the channel slots, records describe wheel-internal configuration
property names: `activePaddleNum`, `aidedSpringControl`, `baseMotorType`,
`bondingPiont` (firmware-side typo for "Point"), `buttonGroupBrightness`,
`buttonGroupStandbyBreathInterval`, …, `temperatureControlStrategy`,
`steeringWheelInertiaRatio`. These are wheel-config / settings property
names, not telemetry channels. The trailer carries the current setting
value (integer or float).

### Preset records (`type=9`)

A subset of property names like `preset/__keyboardInfo` use `type=9`
trailers that introduce nested preset/config blocks. The nested block
includes embedded **UTF-16LE** (note: little-endian, unlike everything
else in kind=8) channel/preset names with their own length prefixes.
The outer `type=9` envelope is known; the inner layout has not been
fully decoded — see [`2026-05-15-sess02-kind8-tlv-and-preset-block.md`](2026-05-15-sess02-kind8-tlv-and-preset-block.md).

### Important: NOT a copy of `Data/Telemetry.json`

Across all 235 names in kind=8 (mix of sub-formats A and B), there is
**zero overlap** with the channel names in `Data/Telemetry.json`
(454 entries: `Rpm`, `MaxRpm`, `Gear`, `ABS`, `SpeedKmh`, …).
`Data/Telemetry.json` is the *game telemetry channel* catalog that the
host is subscribing TO; kind=8 is the *wheel-internal property* catalog
the host is uploading INTO the wheel.

### Size grows over a session

In `bridge-20260429-163951.jsonl` PitHouse emits kind=8 multiple times
with sizes `1740, 1769, 1780, 1807, 1832, 1865, …, 2050` bytes — each
larger than the last. This suggests PitHouse adds new entries
incrementally as the user touches different settings or widgets, and the
wheel reconciles each upload with its internal master table.

## Body decode — kind=11 (`init_payload_b`, FFB property catalog)

Wire-format outer wrapper: same as kind=8 (4-byte uncompressed-size BE
prefix + zlib stream).

Decompressed payload is a list of records with a SIMPLER format than
kind=8:

```
[id:u32 BE]            sequential 0, 1, 2, ...
[name_len:u32 BE]      length in bytes of the UTF-16-BE name (no NUL)
[name:UTF-16-BE]       FFB-property name
(no separator)
```

Example names (all observed are FFB tuning parameters):
`decrementEqualizerGain1` ... `decrementEqualizerGain6`,
`decrementGameForceFeedbackFilter`,
`decrementGameForceFeedbackStrength`,
`decrementInitialSpeedDependentDamping`,
`decrementMaximumGameSteeringAngle`,
`decrementMaximumSteeringAngle`,
`decrementMaximumTorque`,
`decrementMechanicalDamper`,
`decrementMechanicalFriction`,
`decrementMechanicalSpringStrength`,
`decrementNaturalDamper`,
`decrementNaturalFriction`,
`decrementNaturalInertia`,
`decrementSoftLimitStiffness`,
`decrementSpeed*`, …

This is presumably the `decrement*` half; the equivalent `increment*` and
setter records likely follow.

## Body decode — kinds 9, 10, 14, 15, 16 (heartbeats / state)

Brief observations; each warrants its own note when used:

- **kind=9** — fires every ~1.5–2 s in BOTH directions. Body 19–29 B,
  varies per record. Likely a ticker / sync ping.
- **kind=10** — wheel-only, 12 B. Sent ~3.5 s after the host completes
  init kinds 2/7/8/11. Almost certainly the "init complete" ack.
- **kind=14 / kind=15** — **SOLVED 2026-08-07: the device log pull.** h2b
  kind=14 requests up to N lines (always 100) from the display's MOZADash
  logger; b2h kind=14 answers with `[4 B reserved][zlib([count u32 BE] +
  count × ([byteLen u32 BE][UTF-16BE])) ]`; h2b kind=15 acknowledges how many
  lines the host consumed, and the device drops that many. The guesses above
  ("per-event payload channel", "small u32 setting write") are wrong. Canonical
  decode: [`../sessions/session-0x02-ff-init.md`](../sessions/session-0x02-ff-init.md)
  § Device log pull.
- **kind=16** — wheel-only, 20 B. Sent immediately after kind=10. Pairs
  with kind=10 to fully ack init.

## Reusable tools

- `tools/bridge-decode-ff-init <capture>` — full inventory + decode of
  all FF kinds in a bridge capture, with cross-chunk reassembly and
  zlib-decompression for the big ones.
- `tools/trace-sess02-decode <wire-trace>` — same shape against our
  own wire-trace JSONL. Reports the gap: `h2b sess=0x02: no chunks` is
  the smoking gun that the plugin never engaged the protocol.
- `tools/tierdef-decode <trace>` — already extended to flag any
  TIER channel record with `chIndex=0` (catalog lookup miss).

## Open questions

1. **Are kind=8 / kind=11 byte content firmware-version-dependent?** The
   property names look firmware-specific. Replaying captured bytes from
   one firmware against a different firmware may fail validation. We
   need a fresh PitHouse-on-current-firmware capture to test.

2. **Which subset of the four init kinds does the wheel actually
   require?** The wheel's kind=10/16 ack arrives 2.4 s after the full
   handshake — we don't yet know if any kind alone is sufficient.

3. **What are the bytes 12..15 of kind=2 (`init_nonce`)?** Vary per
   capture, semantics unclear.

4. **What is the structure of kind=8 sub-format B?** Strict parser halts;
   need a careful walk to fully decode the wheel-config records.

5. ~~**Does the wheel echo kind=4 if we send the captured kind=2/7/8/11
   bytes verbatim?**~~ — ANSWERED 2026-05-13. **Partial yes for the
   first emission, then the wheel locks.** Plugin briefly shipped the
   captured kind=8 + kind=11 bytes via `SendSessionInitHandshake` on a
   W17 / CS Pro wheel. The first emission engaged kind=4 echoing and
   the user successfully switched dashboards through three slot
   selections (`slot=1 → slot=2 → slot=0`) over ~5 minutes. On the
   third Stop+Start cycle the wheel locked into a state where it
   stopped responding to any command and required a physical
   power-cycle. Diag bundle:
   `~/CS-Pro-moza-diagnostics-bundle-20260513-122621.zip`. The .bin
   files therefore carry session-bound state we have to regenerate
   per-cold-start rather than replay. See
   [`../sessions/session-0x02-ff-init.md`](../sessions/session-0x02-ff-init.md)
   for the required-work list before re-attempting emission.
