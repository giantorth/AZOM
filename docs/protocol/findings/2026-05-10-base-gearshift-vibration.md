# Base "Gearshift vibration intensity" — cmd 0x2E

PitHouse exposes a "Gearshift vibration intensity" slider in the wheelbase
settings. The wire command and encoding were verified live on 2026-05-10
(`sim/logs/bridge-20260510-115644.jsonl`).

## Wire format

```
2E [BE u16 intensity]    h2b grp=0x29 dev=0x13     write
```

Targets the wheelbase device (`dev=0x13`) on the standard base write group
(`0x29`, read group `0x28`). Same shape as every other `base-*` setting —
1-byte cmdid + 2-byte big-endian int payload.

| Capture body | PitHouse value |
|--------------|----------------|
| `2E 00 01`   | 1 (slider min) |
| `2E 00 05`   | 5 (slider max) |

Slider range observed: 1..5 inclusive. The wire encoding is u16, so the
firmware almost certainly accepts the full 0..0xFFFF range; the 1..5 is a
PitHouse-side clamp. (The "0" value hasn't been captured — likely either
"off" or simply the bottom of the slider, which PitHouse may render as 1.)

## Plugin alignment

`Protocol/MozaCommandDatabase.cs` (updated 2026-05-10) registers:

```csharp
AddCommand("base-gearshift-vibration", "base", 40, 41, new byte[] { 46 }, 2, "int");
```

`WriteSetting("base-gearshift-vibration", N)` will produce the correct wire
bytes — `BuildWriteInt`'s big-endian 2-byte encoding matches PitHouse.

The wheelbase settings UI (`UI/SettingsControl.xaml(.cs)`) does not yet
expose this slider — adding it would mirror the existing FFB / damper /
inertia / etc. sliders alongside the other `base-*` writers.
