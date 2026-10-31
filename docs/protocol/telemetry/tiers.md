# Telemetry tiers (`package_level`)

Live telemetry is delivered as **multiple concurrent streams**, each
running at its own update cadence. The grouping is called a *tier*; the
tier a channel belongs to is set per-channel in `Data/Telemetry.json` via
the `package_level` field.

This page is the canonical concept reference. Per-page detail:

| Topic | Page |
|-------|------|
| Live frame wire format & flag byte | [`live-stream.md`](live-stream.md) |
| Compression codes & channel ordering | [`channels.md`](channels.md) |
| Tier-definition negotiation (host → wheel) | [`../tier-definition/`](../tier-definition/) |
| Plugin tier-impl details (`FlagByteMode`) | [`../plugin/tier-impl.md`](../plugin/tier-impl.md) |

## Why tiers exist

The wheel firmware needs different update rates for different signals.
Steering response data (RPM, throttle, brake, gear, speed) must arrive at
~30 Hz to drive the LED bar smoothly; lap-time and tyre-wear updates
matter once per second; cumulative race stats only need to refresh
slowly.

Sending everything at 30 Hz wastes USB bandwidth and bit-packing budget;
sending everything slowly makes the LED bar laggy. Tiers split the
channel set into rate buckets so each frame carries only the data due at
that moment.

## The four `package_level` values

`package_level` is an **interval in milliseconds**. Telemetry.json (410
channels) ships these levels:

| Level | Cadence | Channels in `Telemetry.json` | Example channels |
|-------|---------|------------------------------|------------------|
| `30` | ~33 Hz | 180 | `Rpm`, `Brake`, `Throttle`, `Gear`, `SpeedKmh`, `GAP`, `CurrentLapTime` |
| `500` | ~2 Hz | 104 | `FuelRemainder`, `CurrentLap`, `CurrentPos`, tyre pressures |
| `1000` | ~1 Hz | 1 | `TimeAbsolute` (only level-1000 channel — wall-clock seconds, no point updating faster than the value changes) |
| `2000` | ~0.5 Hz | 125 | `BestLapTime`, `LastLapTime`, tyre wear, race info, track maps |

The `glossary` entry says "30/500/2000 → 30 Hz, 2 Hz, 0.5 Hz" — that's
this same mapping (`1 / (level/1000)` Hz).

## Channel-to-tier assignment

Each entry in `Data/Telemetry.json` carries its `package_level`:

```json
{
  "name": "Brake",
  "url": "v1/gameData/Brake",
  "package_level": 30,
  "compression": "float_001",
  "simhub_property": "DataCorePlugin.GameData.Brake"
}
```

`DashboardProfileStore.LoadTelemetryJson()` reads this. When a dashboard's
mzdash file is parsed (or a built-in profile is built from a URL list),
`BuildMultiStreamProfile()` groups the channels by `package_level`,
sorts each group alphabetically by URL, and emits a `MultiStreamProfile`
whose `Tiers` list is sorted by `package_level` ascending.

```
Tiers[0]    package_level = 30    (fastest, flag offset 0)
Tiers[1]    package_level = 500   (flag offset 1)
Tiers[2]    package_level = 2000  (flag offset 2)
```

## Wire flag byte

Each tier's frames are sent with a different **flag byte** (header byte
10 of the live frame, see [`live-stream.md`](live-stream.md) § Frame
structure). Flag offset = tier index in the sorted list:

```
flag = flag_base + tier_index
```

`flag_base` selection is the firmware-quirk part — see
[`../plugin/tier-impl.md`](../plugin/tier-impl.md) for `FlagByteMode`
modes 0/1/2. PitHouse 2026-04+ uses `flag_base = 0x00` always; older
firmware sometimes used `flag_base = telemetry_session_byte`. Plugin
default is mode 0 (zero-based).

| FlagByteMode | flag_base | Resulting flags (3-tier dashboard) |
|--------------|-----------|------------------------------------|
| 0 (default) | `0x00` | `0x00`, `0x01`, `0x02` |
| 1 (session-port) | `FlagByte` (typically `0x02`) | `0x02`, `0x03`, `0x04` |
| 2 (two-batch) | mixes probe + real batches | varies |

The wheel accepts telemetry on **whatever flag bytes the tier definition
declared** — agreement matters, not the literal value.

## Frame cadence (TelemetrySender)

`TelemetrySender` uses a single timer ticking at the **fastest tier's
cadence** and dispatches per-tier based on a `TickInterval` divisor:

```csharp
_baseTickMs = profile.Tiers[0].PackageLevel;          // e.g. 30 ms

for each tier:
    tickInterval = max(1, tier.PackageLevel / _baseTickMs);
    // 30→1, 500→16 (or 17 with rounding), 2000→66
```

Every timer tick:

```csharp
for (int i = 0; i < tiers.Length; i++) {
    if (_tickCounter % tiers[i].TickInterval == 0)
        send tier i frame with flag = (byte)i;
}
_tickCounter++;
```

So tier 0 fires every tick, tier 1 every 16th tick, tier 2 every 66th
tick. Send order within a tick is fast → slow. Tiers 0–7 each ride a
latest-wins `StreamKind.TierDash0..7` slot; tiers ≥ 8 (radar/track-map
dashboards split into more sub-tiers) ride the connection's per-pipeline
tier-overflow slots (`SendTierOverflow`), with the same latest-wins
coalescing.

The intervals are computed against `_baseTickMs`, which a profile
assignment can move after the timer was armed (e.g. a catalog-synthesised
profile replacing the preamble one). The tick re-syncs the timer period to
the current `_baseTickMs` (`SyncTickTimerToBaseTick`) so the tier rates hold.

(Source:
[`Telemetry/TelemetrySender.cs:231`](../../../Telemetry/TelemetrySender.cs)
profile setter,
[`Telemetry/TelemetrySender.cs:1684`](../../../Telemetry/TelemetrySender.cs)
dispatch loop.)

## Empty tier stub

If a dashboard's profile has zero channels in a given tier (some
dashboards subscribe only to fast-tier data and skip slow stats), the
plugin **still sends a frame for that tier on schedule** — but with no
data section. The frame is just the 12-byte header + 1-byte checksum:

```
7E 08 43 17 7D 23 32 00 23 32 [flag] 20 [chk]
   │
   └ N = 8 (cmd 2 + 6-byte header constants — no data bytes)
```

Wire length: 13 bytes. The flag byte still cycles through the tier
indices so the wheel sees every tier's expected flag at its expected
cadence — receivers that key off "received flag X within timeout"
don't time out on subscribed-but-empty tiers.

## Wire frame structure refresher

Each tier frame uses the standard live-telemetry format
([`live-stream.md`](live-stream.md)):

```
7E [N] 43 17 7D 23 [6-byte header: 32 00 23 32 flag 20] [data] [chk]
                    └─ flag selects which tier this frame carries
```

Data length depends on the tier's bit-packed channels:

```
data_bytes = ceil(sum_of_channel_bit_widths / 8)
```

Bits packed **LSB-first within each byte**, channels in **alphabetical
URL order across all tiers** (1-based channel indices assigned globally,
not per-tier).

## End-to-end example: F1 dashboard `Gear` channel

Trace one channel from `Telemetry.json` to a wire bit position.

### Step 1 — Telemetry.json declares the channel

```json
{
  "name": "Gear",
  "url": "v1/gameData/Gear",
  "package_level": 30,
  "compression": "int30",
  "simhub_property": "DataCorePlugin.GameData.Gear"
}
```

### Step 2 — Compression code from channels.md

`int30` → 5 bits per sample, raw value `0..31`, decoded `-1=R, 0=N, 1..30
= forward gears`. Lookup table in
[`channels.md`](channels.md).

### Step 3 — `BuildMultiStreamProfile` puts `Gear` in tier 0

F1 dashboard's level-30 channel set (from active mzdash):
`Brake`, `CurrentLapTime`, `DrsState`, `ErsState`, `GAP`, `Gear`, `Rpm`,
`SpeedKmh`, `Throttle`. Sorted alphabetically by URL, `Gear` lands at
tier-0 index 5 (0-based within tier).

### Step 4 — Bit position computed cumulatively

| Channel | Bits | Bits 0..n-1 |
|---------|------|-------------|
| Brake | 10 | 0–9 |
| CurrentLapTime | 32 | 10–41 |
| DrsState | 1 | 42 |
| ErsState | 4 | 43–46 |
| GAP | 32 | 47–78 |
| **Gear** | **5** | **79–83** |
| Rpm | 16 | 84–99 |
| SpeedKmh | 16 | 100–115 |
| Throttle | 10 | 116–125 |
| (padding) | 2 | 126–127 |

Total tier-0 frame data: 128 bits = 16 bytes.

### Step 5 — Tier definition declared

`TierDefinitionBuilder.BuildTierDefinitionMessage()` writes Gear's entry
into the v2 tier-def:

```
01                           — tier-def tag
[size_LE]                    — total size of this tier block
00                           — flag = 0 (tier 0)
…
[ch_idx_LE u32]              — global channel index (1-based, alphabetical
                               across all tiers — 'Gear' sorts at position 8 in
                               the F1 master list)
0D 00 00 00                  — comp = 0x0D (int30)
05 00 00 00                  — bits = 5
00 00 00 00                  — reserved
…
```

### Step 6 — Live frame on wire

When the timer fires for tier 0 (every base tick), Gear's value is
encoded by `TelemetryEncoder.Encode("int30", gear)` → packed at bit
positions 79..83 in the data section, LSB-first. For a 6th-gear sample:

```
gear=6 → raw=6 → bits: 00110 (LSB first)
At bit 79: byte 9 bit 7 = 0
At bit 80: byte 10 bit 0 = 0
At bit 81: byte 10 bit 1 = 1
At bit 82: byte 10 bit 2 = 1
At bit 83: byte 10 bit 3 = 0
```

Resulting wire bytes (hex, byte 9 high bit + byte 10 low nibble shown):

```
7E 16 43 17 7D 23 32 00 23 32 00 20 [bytes 0-7] [byte 8] [byte 9] [byte 10] … [chk]
                              │  │
                              │  └ 0x20 constant
                              └─── flag = 0 (tier 0)
```

### Step 7 — Wheel decodes

Wheel firmware reads:

1. Frame flag byte → look up tier definition for flag = 0.
2. Walk tier-def's channel list in order; for each channel, read `bits`
   from the bit stream starting at the running position.
3. For Gear (`comp = 0x0D`, `bits = 5`): read 5 bits at bit 79..83 → raw
   = 6.
4. Decode `int30`: `gear = raw if raw != 31 else -1` → 6.
5. Render gear digit on the integrated display.

## Operational notes

### Catalog filtering

Under the `V2Type02` encoding, before sending a tier definition
`TierDefinitionEmitter.RefilterLiveTiers` filters each tier against the
wheel's live channel catalog (session 0x02 tag 0x04 URLs — see
[`../tier-definition/session-02-channel-catalog.md`](../tier-definition/session-02-channel-catalog.md)).
Channels whose URL has no idx in the live catalog are dropped from their
tier and the rest are sorted by catalog idx. Tiers are never dropped.
This avoids sending a tier-def the wheel can't decode.

### Empty-but-still-declared tiers

The empty-frame stub (previous section) only fires for tiers that are
*declared* but happen to have zero channels for some reason (rare —
usually means a profile/catalog mismatch on a single-tier-style mzdash).

### `BuildTierDefinitionMessage` enable-count quirk

PitHouse always sends **at least 2 enable entries** even for 1-tier
dashboards. Plugin replicates this:

```csharp
int enableCount = Math.Max(2, profile.Tiers.Count);
```

So a 1-tier dashboard still emits enable entries for flag offsets 0 AND
1; the second references a tier that doesn't exist in the tier-def
section. Wheel firmware appears to treat the orphan enable as a no-op.

### Timing precision

`tickInterval = max(1, tier.PackageLevel / _baseTickMs)`, with the timer
period kept equal to `_baseTickMs` across mid-session profile changes. For the
default 30/500/2000 mix, `_baseTickMs = 30` and intervals are 1/16/66.
500 ms isn't an exact multiple of 30, so tier 1 fires at 16×30 = 480 ms
intervals (slightly faster than declared); tier 2 at 66×30 = 1980 ms
(slightly faster than 2000). Matches Pithouse's observed jitter — both
implementations under-shoot the slow-tier nominal slightly.

### Adding a new tier

A new `package_level` value in `Telemetry.json` (e.g. `100`) automatically
becomes a new tier when a dashboard subscribes to a channel at that
level. Plugin / firmware require no changes — just rebuild and the new
tier appears in the profile, gets a flag offset, and ticks at its
declared cadence. The `package_level: 1000` `TimeAbsolute` channel
demonstrates this — a dashboard that subscribes to it gets a third
middle tier inserted between 500 and 2000, ticking at 1 Hz.

## Cross-references

- [`live-stream.md`](live-stream.md) — wire frame format, flag-byte
  observed values across captures, full F1 base+slow tier byte tables
- [`channels.md`](channels.md) — compression code → bit-width table,
  channel ordering rules, namespace distribution
- [`../tier-definition/version-2-compact-vgs.md`](../tier-definition/version-2-compact-vgs.md) —
  v2 tier-def TLV encoding (used to declare tiers to VGS / KS Pro)
- [`../tier-definition/version-0-url-csp.md`](../tier-definition/version-0-url-csp.md) —
  v0 URL-only encoding (CSP — wheel resolves package level internally)
- [`../tier-definition/handshake.md`](../tier-definition/handshake.md) —
  when tier defs flow during connect
- [`../plugin/tier-impl.md`](../plugin/tier-impl.md) — `FlagByteMode`
  selection, profile filtering, double-send behavior
- [`../GLOSSARY.md`](../GLOSSARY.md) — `tier`, `flag byte`, `tier def`
