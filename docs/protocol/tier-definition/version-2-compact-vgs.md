### Session `0x02` — host response: version 2 compact tier definitions (VGS / KS Pro)

For VGS and KS Pro wheels, Pithouse sends a binary-encoded tier
definition that explicitly declares **flag byte**, **channel index**,
**compression code**, and **bit width** for each entry. The wheel's
firmware doesn't need URL metadata — it decodes the bit stream using the
host-provided schema.

> See [`../telemetry/tiers.md`](../telemetry/tiers.md) for the
> tier-concept reference: how `package_level` becomes a tier, how flag
> offsets map to tiers, and an end-to-end channel example.

> **Used by:** VGS, KS Pro (W17/W18). CSP uses v0 URL subscription (see
> [`version-0-url-csp.md`](version-0-url-csp.md)).

### Stream structure

> **Corrected 2026-04-29** after byte-perfect PitHouse capture replay
> (R5 base + W17 wheel, Nebula in-game). Earlier description had three
> errors: end-marker value was wrong, tier-enable entries weren't
> interleaved, and the same channels need to be re-broadcast at multiple
> flags.

The session 0x02 stream contains:

```
[0x07] [04 00 00 00] [02 00 00 00]            — version preamble
[0x03] [00 00 00 00]                            — config separator (size=0, no body)
[tier_def 0]    [end_marker 0]                  — tier 0
[enable 0]      [tier_def 1]    [end_marker 4]  — tier 1, preceded by enable for tier 0
[enable 1]      [tier_def 2]    [end_marker 4]  — tier 2, preceded by enable for tier 1
```

i.e. **enables are interleaved** and **the end-marker value alternates**
(0 for the first tier, 4 for all subsequent tiers). PitHouse emits the
final tier with NO trailing enable.

#### Session preamble

| Tag | Field | Notes |
|-----|-------|-------|
| `0x07` | version | 4-byte length + 4-byte LE u32 = `2` (selects v2 format) |
| `0x03` | config | 4-byte length = 0 — empty body |

#### Tier definition TLV

```
[0x01] [size: u32 LE] [flag_byte]                  — tier definition header
  [ch_index: u32 LE] [comp: u32 LE]                — 16-byte channel entry (repeated)
  [bits: u32 LE]     [reserved: u32 LE]
```

| Field | Size | Meaning |
|-------|------|---------|
| Tier header | 5 bytes | `0x01` tag, 4-byte LE size = `1 + N×16`, 1-byte flag selecting which telemetry tier this defines |
| Channel entry | 16 bytes | Repeated for each channel in the tier |
| `ch_index` | u32 LE | 1-based channel index in the wheel's advertised catalog (NOT host alphabetic order on Type02 firmware) |
| `comp` | u32 LE | Compression code from `Telemetry.json` |
| `bits` | u32 LE | Bit width of this channel in the live frame |
| `reserved` | u32 LE | Always `0` in observed captures |

#### Per-tier end-marker

```
[0x06] [04 00 00 00] [marker_value: u32 LE]
```

`marker_value` = `0` for the **first** tier, `4` for **all subsequent**
tiers. **NOT** total_channels — earlier doc claim was wrong. Semantics
of the value still TBD (likely a status/flush flag).

#### Per-tier enable

```
[0x00] [01 00 00 00] [flag_byte]
```

`flag_byte` identifies the tier this enable activates. PitHouse pattern:
emit enable BEFORE the NEXT tier's `0x01` block, naming the **previous**
tier (i.e. enable confirms tier-N is fully defined, then next tier-N+1
follows). The first tier has no preceding enable; the last tier has no
trailing enable.

#### Multi-tier broadcast pattern

> **Updated 2026-04-30** after multi-pkg-level dashboards (Grids, Rally V4)
> verified rendering on Type02. Single-pkg case (Nebula) below documents
> the simpler historical pattern; multi-pkg subsection generalises it.

##### Single-pkg-level dashboard (Nebula)

PitHouse capture (Nebula in-game) emits **3 broadcasts carrying the
SAME single channel set** at flag bytes 0/1/2:

```
tier 0 (flag=0): idx 1,2,3,4 with types 0e/0d/04/0f, bits 10/5/16/16
tier 1 (flag=1): same 4 channels, identical encoding
tier 2 (flag=2): same 4 channels, identical encoding
```

Plugin's `TelemetrySender.Profile` setter expands a single-pkg_level
profile to 3 broadcasts.

> If only one broadcast is sent, the wheel registers the channels but
> **does not bind widgets** — verified live 2026-04-29 with Nebula
> display non-rendering until 3-broadcast pattern was added.

##### Multi-pkg-level dashboard (Rally V4, Grids)

For dashboards with 2+ distinct `package_level` values (faster + slower
update rates), PitHouse emits **4 broadcasts × N sub-tiers per
broadcast**. Within a broadcast, sub-tiers are ordered by `package_level`
ASCENDING (fastest first). End-marker is emitted **per broadcast** (not
per sub-tier); enables are interleaved between broadcasts. Flag bytes
increment monotonically across all sub-tiers, so the wheel sees flag
slots `0..(broadcasts × subCount - 1)`.

```
broadcast 0:  tier(flag=0) tier(flag=1) ... tier(flag=N-1) end-marker(0)
              enable(flag=0) ... enable(flag=N-1)
broadcast 1:  tier(flag=N) ... tier(flag=2N-1) end-marker(channelsPerBroadcast)
              enable(flag=N) ... enable(flag=2N-1)
... (4 broadcasts total)
broadcast 3:  tier(flag=3N) ... tier(flag=4N-1) end-marker(channelsPerBroadcast)
              (no trailing enable on last broadcast)
```

Rally V4 (3 sub-tiers, 5+2+1 channels per broadcast):
12 total flag slots = 4 broadcasts × 3 sub-tiers.

Grids (PitHouse splits into 5+2+1, plugin splits into 8+12 per its
`Telemetry.json` pkg-level grouping):
- PitHouse: 4 × 3 = 12 flag slots, 8 unique channels
- Plugin:   4 × 2 = 8 flag slots, 20 unique channels
  Both render successfully — wheel binds widgets via channel idx, not
  flag position, so the count of broadcasts × sub-tiers only needs to
  fill enough wheel slots that no widget is left un-subscribed.

##### Broadcast count formula

| Sub-tier count | Broadcasts | Total flag slots |
|---------------|-----------|------------------|
| 1 (single pkg) | 3 | 3 |
| 2 (e.g. plugin's Grids) | 4 | 8 |
| 3 (e.g. PitHouse's Grids, Rally V4) | 4 | 12 |

Plugin formula in `TelemetrySender.Profile` setter:
`broadcasts = (subCount == 1) ? 3 : max(4, subCount + 1)`.

##### First-broadcast channel-count anomaly

PitHouse's first broadcast for multi-pkg dashboards sometimes lists
**fewer channels in the fastest sub-tier** than subsequent broadcasts
(e.g. Rally V4: 5 channels first broadcast, 6 channels broadcasts 1-3,
extra channel = `TCActive` idx=9). This appears to be a staged-catalog
artefact — PitHouse re-emits the subscription with the latest
wheel-advertised catalog after the wheel's first catalog burst. Plugin
emits the same channel set in every broadcast (no staging) and the
wheel still renders correctly, so this is informational rather than a
required match.

### Channel indexing

Indices are **1-based**. Source of truth differs by firmware:

- Pre-Type02 firmware: **alphabetic by URL across all tiers**
- Type02 firmware (CSP / R5+W17 2026-04+): **wheel-catalog order** —
  channel index = position in the catalog the wheel pushed back on
  session 0x02 (1-based). Plugin must wait for catalog parse before
  emitting tier-def. Verified byte-identical to PitHouse 2026-04-29.

### Compression codes

The `comp: u32 LE` field is one of the codes from
[`../telemetry/channels.md`](../telemetry/channels.md) (e.g. `0x07` =
`float`, `0x0E` = `percent_1`, `0x14` = `uint3`, `0x17` = `float_001`).
Wheel firmware uses this to decode the corresponding `bits` from the
live bit stream.

> **Type02 firmware compression-code support (2026-04-30)**: `0x10`
> (`tyre_pressure_1`) and `0x11` (`tyre_temp_1`) are inferred-only —
> live R5+W17 wheel does NOT decode them. Tyre widgets stayed at 0
> until plugin switched these channels to `float` (`0x07`, width 32).
> Other inferred codes that have NOT been confirmed against live
> Type02 firmware: `0x12` (`track_temp_1`), `0x13` (`oil_pressure_1`),
> `0x15` (`float_600_2`), `0x16` (`brake_temp_1`). Use `float` until
> a PitHouse capture proves otherwise. Codes confirmed working live:
> `0x00` (`bool`), `0x01`/`0x02` (`uint8`/`int8`), `0x04` (`uint16_t`),
> `0x07` (`float`), `0x0D` (`int30`/`uint30`), `0x0F` (`float_6000_1`),
> `0x0E` (`percent_1`).

### Worked example: F1 dashboard tier 30 entry for `Gear`

Channel `Gear` is `int30`, 5 bits, alphabetic index 8:

```
01                                — tier definition tag
30 00 00 00                       — size = 48 (header + 3 channel entries × 16 bytes if there were more)
00                                — flag_byte = 0 (tier 30)
08 00 00 00                       — ch_index = 8
0D 00 00 00                       — comp = 0x0D (int30)
05 00 00 00                       — bits = 5
00 00 00 00                       — reserved
```

(Real F1 base tier has 9 channels packed into 128 bits — see
[`../telemetry/live-stream.md`](../telemetry/live-stream.md) for the
full layout.)

### Plugin builder

[`TierDefinitionBuilder.BuildTierDefinitionMessage`](../../../Telemetry/Frames/TierDefinitionBuilder.cs)
constructs the v2 stream. Flag-byte assignment is controlled by
`FlagByteMode`:

| Mode | Behavior |
|------|----------|
| 0 (zero-based, default) | First tier = `0x00`, second = `0x01`, etc. — matches Pithouse 2026-04+ |
| 1 (session-port-based) | First tier = telemetry session byte (typically `0x02`), increments — older firmware quirk |
| 2 (two-batch) | Probe + real batches as described above |

### Cross-references

- [`session-02-channel-catalog.md`](session-02-channel-catalog.md) — wheel
  side of the negotiation
- [`version-0-url-csp.md`](version-0-url-csp.md) — alternative v0 format
  for CSP wheels
- [`../telemetry/live-stream.md`](../telemetry/live-stream.md) — how the
  resulting bit stream is laid out at runtime
- [`../telemetry/channels.md`](../telemetry/channels.md) — full
  compression code → bit-width / encoding table
