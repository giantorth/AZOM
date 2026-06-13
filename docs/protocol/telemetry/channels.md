## Telemetry channel encoding

Master reference for all compression types.

| Type | Bits | TierDef code | Encode (game → raw) | Decode (raw → game) | Range / note | Count in Telemetry.json |
|------|------|--------------|---------------------|---------------------|--------------|-------------------------|
| `bool` | 1 | 0x00 ✓ | `raw = value` | `value = raw` | 0 or 1 | 51 |
| `uint3` | 4 | 0x14 ✓ | `raw = min(value, 15)` | raw, 15=N/A | 0–14 | 1 |
| `uint8` | 4 | inferred | `raw = min(value, 15)` | raw, 15=N/A | 0–14 | 5 |
| `uint15` | 4 | 0x03 inferred | `raw = min(value, 15)` | raw, 15=N/A | 0–14 | 1 |
| `int30` | 5 | 0x0D ✓ | `raw = min(value, 31)` | raw, -1=R as 31 | Gear (-1=R, 0=N, 1–12) | 1 |
| `uint30` | 5 | inferred | `raw = min(value, 31)` | raw | 0–31 | 2 |
| `uint31` | 5 | inferred | `raw = min(value, 31)` | raw | 0–31 | 1 |
| `int8_t` | 8 | 0x02 inferred | `raw = value` | raw | signed byte | — |
| `uint8_t` | 8 | 0x01 inferred | `raw = value` | raw | 0–255 | 12 |
| `percent_1` | 10 | 0x0E ✓ | `clamp(game% × 10, 0, 1000)` | `game% = raw / 10` | 0–100%, 1023=N/A | 19 |
| `float_001` | 10 | 0x17 ✓ | `clamp(game × 1000, 0, 1000)` | `game = raw / 1000` | 0.0–1.0, 1023=N/A | 3 |
| `tyre_pressure_1` | 12 | 0x10 inferred | `clamp(kPa × 10, 0, 4095)` | `kPa = raw × 0.1` | 0–409.5 kPa | 12 |
| `tyre_temp_1` | 14 | 0x11 inferred | `°C × 10 + 5000` | `°C = (raw − 5000) × 0.1` | −500–1138.3°C | 43 |
| `track_temp_1` | 14 | 0x12 inferred | `°C × 10 + 5000` | `°C = (raw − 5000) × 0.1` | −500–1138.3°C | 5 |
| `oil_pressure_1` | 14 | 0x13 inferred | `°C × 10 + 5000` | `°C = (raw − 5000) × 0.1` | −500–1138.3°C | 1 |
| `int16_t` | 16 | 0x05 inferred | `raw = value` | raw | signed 16 | — |
| `uint16_t` | 16 | 0x04 ✓ | `raw = value` | raw | 0–65535 | 2 |
| `float_6000_1` | 16 | 0x0F ✓ | `clamp(game × 10, 0, 65535)` | `game = raw / 10` | 0–6553.5 | 4 |
| `float_600_2` | 16 | 0x15 inferred | `clamp(game × 100, 0, 65535)` | `game = raw / 100` | 0–655.35 | 12 |
| `brake_temp_1` | 16 | 0x16 inferred | `clamp(°C × 10 + 5000, 0, 65535)` | `°C = (raw − 5000) / 10` | −500–6053.5°C | 14 |
| `uint24_t` | 24 | — | `raw = value` | raw | 0–16777215 | — |
| `float` | 32 | 0x07 ✓ | IEEE 754 single bits | IEEE 754 reinterpret | full float | 73 |
| `int32_t` | 32 | 0x08 inferred | `raw = value` | raw | signed 32 | 3 |
| `uint32_t` | 32 | 0x09 inferred | `raw = value` | raw | 0–2³²-1 | 65 |
| `double` | 64 | 0x0A inferred | IEEE 754 double bits | IEEE 754 reinterpret | full double | — |
| `location_t` | 64 | 0x0B inferred | IEEE 754 double bits | IEEE 754 reinterpret | track coords | 65 |
| `int64_t` / `uint64_t` | 64 | — | raw | raw | 64-bit | — |
| `string` | var | — | — | — | names | 15 |

✓ = confirmed from F1 dashboard USB capture. Inferred codes assigned sequentially by factory ID order from Telemetry.json. Code 0x06 unassigned (gap between int16_t and float).

**Notes:**
- `DoubleInterface` flag byte at object offset +4: flag=1 returns 32-bit (`float`), flag=0 returns 64-bit (`double`).
- Factory ID 20 (`uint3`, `uint8`, `uint15`) maps through abstract `IsUnsignedInterface` → `Int15Interface` (4 bits). Type name's number does NOT determine bit width.
- `UFloatInterface` reads per-instance exponent from `this+8`. Scale = `10^exponent`. Type name encodes `float_{max}_{decimal_places}`: `float_6000_1` = max ~6000, 1 decimal.
- CSP uses tier-def version 0 (URL-based) which doesn't need compression codes — wheel firmware resolves by URL.

### Key constants

| Value | Usage |
|-------|-------|
| 10.0 | Scale for percent, UFloat, temps, pressures (×10) |
| 100.0 | Normalized → percent (×100 then ×10) |
| 1000.0 | Max raw for 10-bit percent/normalized |
| 5000.0 | Temperature offset (raw = temp×10 + 5000) |
| 65535.0 | Max raw for 16-bit UFloat/BrakeTemp |

### SimHub property → encoder input

Each `Telemetry.json` channel may declare a SimHub property as its value source plus an optional pre-encoder scale:

| JSON field | Type | Effect |
|------------|------|--------|
| `simhub_property` | string | Full SimHub property path (e.g. `DataCorePlugin.GameData.SpeedMph`). Resolved live via the SimHub `IDataPlugin` snapshot. Empty/absent → falls back to `simhub_field` (a hardcoded `SimHubField` enum entry in `Telemetry/Dashboard/DashboardProfile.cs`, covering the ~17 always-on channels). |
| `simhub_scale` | number | Multiplier applied to the SimHub value before it enters the encoder. Default `1.0`. Read at `Telemetry/Dashboard/DashboardProfileStore.cs` (`sector["simhub_scale"]`). |
| `simhub_field` | enum name | Fallback when no `simhub_property` is set. Case-insensitive match against `SimHubField`. `Zero` if unset — channel sends a constant 0 in game mode. |

**The full pipeline:**

```
simhub_value × simhub_scale  →  encoder(compression)  →  raw bits
```

**Field-name pitfall:** the JSON key is `simhub_scale`, NOT `simhub_property_scale`. The C# property is named `SimHubPropertyScale`, which suggests the latter — and editors using auto-completion may produce a silently-ignored field. The deserialiser only reads `simhub_scale`.

#### Required scale by (SimHub-side range, target compression)

The scale must bridge the SimHub property's range to the encoder's expected input range. Get this wrong and the bar fills 1% of its travel; the wheel renders without complaint, the bug is invisible without a side-by-side comparison.

| SimHub property returns | Target compression | Encoder expects input | Required `simhub_scale` |
|-------------------------|--------------------|-----------------------|-------------------------|
| 0–1 fraction (`Throttle`, `Brake`, `Clutch`, `TrackPositionPercent`) | `float_001` | 0–1 | `1.0` (or absent) |
| 0–1 fraction | `percent_1` | 0–100 | `100` |
| 0–100 percent (`FuelPercent`, `TyreWearFrontLeft`, `ERSPercent`) | `percent_1` | 0–100 | `1.0` (or absent) |
| 0–100 percent | `float_001` | 0–1 | `0.01` |

SimHub-side ranges should be verified per-property against `libs/SimHub/GameReaderCommon.dll` (the `<PropertyName>k__BackingField` strings list every exposed property). The convention is consistent across games for the well-known properties listed above, though game-specific properties on `PluginManager.GameData.NewData` may use their own ranges.

#### Test-mode override range

`Telemetry/TestMode/TestSignalOverrides.cs` hardcodes test sweeps per channel. The sweep range must match the **encoder's expected input** range (column 3 above), not the SimHub-side range — `simhub_scale` is **not** applied in the test-frame path (`TelemetryFrameBuilder.BuildTestFrame`). A `percent_1` channel needs `TestSignal.Sweep(0, 100, …)`; a `float_001` channel needs `Sweep(0, 1, …)`.

### Channel ordering

Channels first grouped by `package_level` (30 → base frame, 500 → base+1, 2000 → base+2). See [`tiers.md`](tiers.md) for the full tier-concept reference (cadence, flag-byte mapping, profile build flow). Within each frame packed **alphabetically by URL suffix** (part after `v1/gameData/`). Iterated sorted by URL, packed sequentially into bit stream starting at bit 0.

Bits packed **LSB-first within each byte** (bit 0 = LSB of byte 0, bit 8 = LSB of byte 1). Multi-bit fields span byte boundaries when needed.

### Namespace distribution (Telemetry.json, 410 total channels)

| Namespace | Count | Notes |
|-----------|-------|-------|
| `v1/gameData/` | 275 | Standard game telemetry |
| `v1/gameData/patch/` | 133 | Extended: 64 track map coords, 64 race info slots, display names |
| `v1/preset/` | 2 | `CurrentTorque`, `SteeringWheelAngle` (both `float_6000_1`, 16 bits) — wheelbase state, NOT game telemetry |

### `patch/*` channel value sources

The MOZA-specific `patch/*` channels feed wheel-dashboard widgets (track name, game
name, car count, radar). Their `simhub_property` mappings in `Telemetry.json` need
care — several shipped pointing at SimHub properties that **do not exist**, which the
resolver returns as empty (string) or 0 (numeric), so the widget renders blank with no
error. Verified against the FSR2 "1 LD - Marco" dashboard, which reads exactly these:

| Channel | Widget reads | Correct SimHub source |
|---------|--------------|-----------------------|
| `patch/DisplayTrackName` | track name (`value!=null ? value : "Track Name"`) | `DataCorePlugin.GameData.TrackName` — there is **no** `GameData.DisplayTrackName` property |
| `patch/TrackName` | (internal, `is_visible:false`) | `DataCorePlugin.GameData.TrackName` |
| `patch/GameName` | layout switch (`== "ACC"`, `== "AC"`, …) | computed `@internal/GameName` (see below) — there is **no** `GameData.GameName` property |
| `patch/OpponentCount` | car count (`value + 1`) | `DataCorePlugin.GameData.OpponentsCount` |
| `v1/gameData/CurrentCarCount` | car-count color threshold only | `DataCorePlugin.GameData.OpponentsCount` |

**`@internal/GameName` (computed string).** The wheel dashboards branch on MOZA's
short game strings (`AC`, `ACC`, `Automobilista 2`, `rFactor 2`), not SimHub's game
code (`AssettoCorsa`, `AssettoCorsaCompetizione`, `Automobilista2`, `rFactor2`).
`patch/GameName` is mapped to the sentinel `@internal/GameName`, resolved by
`SimHubPropertyResolver.ResolveInternalStringChannel` → `Dashboard.GameNameMap`, which
translates the four games confirmed against real dashboards and passes any other
`PluginManager.GameName` value through unchanged (no fabricated short-codes). This is
the **string** counterpart to the numeric `@internal/*` channels in
`ResolveInternalChannel` (e.g. `@internal/SteeringWheelAngle`);
`ResolveAsString` tries the string resolver first, then falls back to the numeric one.

**`v1/preset/*` is wheel-internal — not host-emitted (corrected 2026-06-12).**
The `preset/` namespace (`TimeStamp`, `CurrentTorque`, `SteeringWheelAngle`) is
filled by the wheel firmware from its own state, not sent by the host. PitHouse
subscribes to none of them and sends no value frames for them (wire-verified —
see [`../tier-definition/session-02-channel-catalog.md`](../tier-definition/session-02-channel-catalog.md)).
`DashboardProfileStore.IsWheelInternalPresetChannel` drops the whole namespace
from every subscription, so a `preset/*` channel never enters a tier-def or
value frame. The old `@internal/TimeStamp` resolver case was removed (it was
unreachable once the namespace is excluded); the `Telemetry.json`
`v1/preset/TimeStamp → @internal/TimeStamp` mapping remains but is never
resolved. Host-sending `preset/TimeStamp` previously overrode the wheel's own
clock with a large positive ms count and garbled dashboards that render it.

**`OpponentCount` / `PlayerIndex` are not radar-gated.** `DashboardProfileStore.IsRadarTrackMapChannel`
gates only the *bulky* track-map / radar geometry (`patch/Location`, `patch/Location_N`,
`patch/riN`) behind the `EnableRadarTrackMapChannels` setting (default off). `OpponentCount`
and `PlayerIndex` are 1-byte scalar session fields ordinary dashboards read for
car-count / grid-position display, so they emit regardless of the radar setting.
