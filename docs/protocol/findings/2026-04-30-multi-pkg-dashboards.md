# 2026-04-30 — Multi-pkg-level dashboards on Type02 (R5+W17)

Live verification on R5 base + W17 wheel of three dashboards spanning 1, 3, and 2 distinct `package_level` values: Nebula (1), Rally V4 (3), Grids (2). Plugin and PitHouse render all three. Bridge logs: `sim/logs/bridge-20260430-052712.jsonl` (Grids PitHouse baseline).

## Findings

### Broadcast count formula

PitHouse fills wheel tier slots up to `broadcasts × subCount`. Widgets bound to slots beyond that stay un-subscribed and never animate. Observed pattern:

| Dashboard | Sub-tiers | PitHouse broadcasts | Total slots |
|-----------|-----------|---------------------|-------------|
| Nebula | 1 (pkg=30) | 3 | 3 |
| Grids | 3 (pkg=30 / 500 / 2000) | 4 | 12 |
| Rally V4 | 3 (pkg=30 / 500 / 2000) | 4 | 12 |

Plugin formula in `TelemetrySender.Profile` setter:

```csharp
int broadcasts = subCount == 1 ? 3 : Math.Max(4, subCount + 1);
```

Earlier `subCount + 1; max 3` gave broadcasts=3 for `subCount=2`, which left wheel slots 6+ un-subscribed and TestMode widgets bound to those slots stayed at 0.

### Per-dashboard sub-tier split is custom, not from Telemetry.json

PitHouse's Grids tier-def has channels grouped into:

- pkg=30 sub-tier (5 ch): idx 1, 4, 6, 7, 8 — codes bool, float, int30, uint16, float_6000_1
- pkg=500 sub-tier (2 ch): idx 3, 5 — codes int8, float
- pkg=2000 sub-tier (1 ch): idx 2 — code float

Plugin's `Data/Telemetry.json` marks ALL 8 of those URLs as pkg=30, with codes bool/uint30/float/int30/float_6000_1. PitHouse must source the per-dashboard split (and per-dashboard compression overrides) from somewhere outside `Telemetry.json`. Plugin uses `Telemetry.json` pkg-level grouping → 2 sub-tiers (8 + 12 channels) instead of PitHouse's 3 (5+2+1). Wheel renders both because tier-def is internally consistent and widget binding uses channel idx (1-based catalog position) not flag/sub-tier position.

### Tyre compression codes 0x10/0x11 broken on Type02

Plugin's `TierDefinitionBuilder.CompressionCodes` has these inferred entries:

```csharp
["tyre_pressure_1"] = 0x10,  // inferred
["tyre_temp_1"]     = 0x11,  // inferred
```

With these codes in the tier-def, R5+W17 wheel decodes the tyre channels' bit slots but **never updates tyre widgets** — they stay at 0 (tested in TestMode, sweep wave applied to all channels). Switching `compression` to `float` in `Telemetry.json` (which routes to code `0x07`, 32-bit IEEE) immediately fixed tyre widget animation. So either:

- Type02 firmware doesn't recognise `0x10`/`0x11` codes at all, or
- Type02 firmware decodes them but the bit-packing format the plugin emits (12-bit pressure × 10, 14-bit temp × 10 + 5000) doesn't match the firmware's expectation.

Either way, `float` is the safe choice on Type02 until a live capture proves a working alternative. `percent_1` (`0x0E`, 10 bits) is similarly inferred but used widely (Throttle, Brake, FuelPercent, ErsPercent, TyreWear); not yet confirmed broken — keep an eye on it.

Other inferred codes still untested on Type02: `0x12` (`track_temp_1`), `0x13` (`oil_pressure_1`), `0x15` (`float_600_2`), `0x16` (`brake_temp_1`).

### Plugin Telemetry.json missing SimHub mappings

Out of 454 channel sectors in `Data/Telemetry.json`, only ~17 had non-empty `simhub_property` before 2026-04-30. Added 12 mappings on 2026-04-30 for ABS/TC/Tyre channels:

| URL | SimHub property |
|-----|-----------------|
| `v1/gameData/ABSActive` | `DataCorePlugin.GameData.ABSActive` |
| `v1/gameData/ABSLevel` | `DataCorePlugin.GameData.ABSLevel` |
| `v1/gameData/TCActive` | `DataCorePlugin.GameData.TCActive` |
| `v1/gameData/TCLevel` | `DataCorePlugin.GameData.TCLevel` |
| `v1/gameData/TyrePressure{FrontLeft,FrontRight,RearLeft,RearRight}` | `DataCorePlugin.GameData.TyrePressure{...}` |
| `v1/gameData/TyreTemp{FrontLeft,FrontRight,RearLeft,RearRight}` | `DataCorePlugin.GameData.TyreTemperature{...}` |

Note SimHub property name uses **`TyreTemperature`** (full spelling) while the URL suffix is `TyreTemp`. ~437 sectors still unmapped — backfill is mostly mechanical (URL suffix → `DataCorePlugin.GameData.<suffix>`) but each needs verification per dashboard.

### TestMode phase scaling

`TelemetryFrameBuilder.BuildTestFrame` advances `_testPhase` by 1 per call. For a pkg=2000 tier firing every ~2s, period=100 means a full triangle sweep takes ~200s — visually static. Fix: scale phase increment by `_profile.PackageLevel / 30` so all tiers complete a sweep in similar wall-clock time (~3s) regardless of update rate.

```csharp
int phaseStep = Math.Max(1, _profile.PackageLevel / 30);
_testPhase = (_testPhase + phaseStep) % period;
```

This is a TestMode-only ergonomic fix; live game data sweeps at the natural per-tier cadence.

## Files changed

- `Telemetry/TelemetrySender.cs` — broadcast formula
- `Telemetry/Frames/TelemetryFrameBuilder.cs` — TestMode phase scaling
- `Data/Telemetry.json` — 12 SimHub mappings + tyre compression flip to `float`
- `docs/protocol/README.md` — 2026-04-30 status banner
- `docs/protocol/tier-definition/version-2-compact-vgs.md` — multi-pkg broadcast pattern + Type02 compression-code support note
- `docs/protocol/FIRMWARE.md` — Type02 / 2026-04-30 era row + W17/R5 hardware row
- `docs/protocol/open-questions.md` — broadcast/sub-tier resolved + new entries

## Cross-references

- [`tier-definition/version-2-compact-vgs.md`](../tier-definition/version-2-compact-vgs.md) — full multi-tier broadcast section
- [`telemetry/live-stream.md`](../telemetry/live-stream.md) — `7d:23` value frame layout (legacy N=14 on Type02)
- [`open-questions.md`](../open-questions.md) — outstanding inferred-code verifications
