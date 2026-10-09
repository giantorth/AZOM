# Motion platform — effect parameters, global sliders, rig geometry, lighting

Everything the Motion Manager settings pages write to the controller. All floats are IEEE-754
**big-endian**, 0.0–1.0 for percentages. Writes go on group `0x1E`, readbacks on `0x1F`, both
to dev `0x12`; every write is echoed verbatim (`0x9E 0x21`).

## Motion effects — `0x1E/05 [slot] [f32 intensity] [f32 smoothness]`

```
h2b  7E 0A 1E 12 05 00 3F19999A 3E4CCCCD [chk]     slot 0 = pitch: 0.60 / 0.20
b2h  7E 0A 9E 21 05 00 3F19999A 3E4CCCCD [chk]     echo
```

| Slot | Effect (Motion Manager "Motion Effects") | Verified values (preset "iRacing caterham", `nurburgring` 19.9 s) |
|------|-------------------------------------------|---------------------------------------------------------------------|
| `00` | Pitch | intensity 0.60, smoothness 0.20 — `Settings.png` Pitch 60 % / 20 % |
| `01` | Roll | 0.45 / 0.20 — Roll 45 % / 20 % |
| `03` | Heave | 0.30 / 0.56 — Heave 30 %, smoothness 56 % (`Settings2.png` top) |
| `07` | Acceleration Pitch | 0.20 / 0.30 — 20 % / 30 % |
| `08` | Acceleration Roll | 0.20 / 0.35 — 20 % / 35 % |

Slots 2, 4, 5, 6 are never written on this rig (presumably surge / sway / yaw / traction-loss
effects of other platform types). The "Gear Shift Isolation" toggle under Acceleration Pitch has
no observed write.

**Write pattern.** The host re-sends the whole pair on every slider change, so a preset load is
10 frames in 80 ms (two per slot). At connect it is more verbose: each slot is written three
times — `00000000 00000000`, then `intensity 00000000`, then `intensity smoothness` — i.e. the
UI applies the two fields one after the other starting from zero. After a power-cycle under a
running Motion Manager the five slots are re-sent twice more with the final pair.

The host pushes whatever its active preset holds. Two sets were seen: pitch 0.28/0.90, roll
0.72/0.84, heave 0.54/0.80, accel-pitch 0.53/1.00, accel-roll 0.39/1.00 (`Startup-Shutdown`,
`nurburgring` at connect and again after +46 s, `100_STROKE`), and 1.0/0.5, 1.0/0.5, 1.0/0.5,
0.5/0.5, 0.5/0.5 with road detail 0.0/0.5 (every capture from `hardware-powerup` 21:34 UTC
onwards — a different preset was active, not a device reset: the values come from the host).

## Road detail — `0x1E/06 [f32 intensity] [f32 smoothness]`

The one haptic effect the **controller** renders (it has the suspension data in the telemetry
frame). Verified: `06 3F333333 3DCCCCCD` = 0.70 / 0.10 = `Settings.png` Road Detail 70 % / 10 %.
Readback `1F 12 06 00…` → `9F 21 06 [f32][f32]`. Same three-step write pattern at connect.

The other haptic effects (Engine Vibration, ABS Trigger, Gear Shift, Traction Control, Wheel
Slip) are rendered on the host and arrive as oscillators/events on group `0x22` — see
[`game-stream-0x22.md`](game-stream-0x22.md). Their sliders produce no `0x1E` write.

## Global sliders — `0x1E/04 [idx] [f32]` / `0x1F/04 [idx]`

| idx | Slider (home page) | Evidence |
|-----|--------------------|----------|
| `00` | Max Available Stroke | read 0.30 on 10-06 while the home page shows 30 %; 1.0 on the reconfigured 10-07 rig |
| `01` | Haptic Feedback Intensity | `nurburgring` writes `04 01 3F000000` (0.50) and `04 01 3ECCCCCD` (0.40); `Settings.png` shows 50 % |

Read at connect as `1F 12 04 00 00 00 00 00` / `1F 12 04 01 …` → `9F 21 04 [idx] [f32]`.

## Rig dimensions — `0x1F/08`

`1F 12 08 00×8` → `9F 21 08 43FA0000 43FA0000` = L1 500.0 mm, L2 500.0 mm ("Actuators
Installation Distance": front↔rear and left↔right actuator-centre distances). Read at connect
and again after a self-test. No write captured (the fields were never edited); the controller
logs them as `Calib Length : 500.00000,Width : 500.00000` when entering mode 3.

## Lighting table — `0x1E/0B [zone] [param] [value…]` ("Table 12")

28 writes at every connect and after every self-test, each echoed, each followed by a
`0x1F/0B [zone] [param]` readback and a firmware line `param_manage.c:340 Table 12, Param N
Written: V`. The controller also logs `LightManageConfig_SetCmd: Global: switch=1, brightness=NaN`.

| Frame | Param (firmware) | Value | Reading |
|-------|------------------|-------|---------|
| `0B 00 01 64` | 5 | 100 | global: switch / level 100 |
| `0B 01 01 02` | 6 | 2 | global: mode 2 |
| `0B 02 01 03` | 7 | 3 | global: mode 3 |
| `0B 03 01 50` | 8 | 80 | global: brightness 80 |
| `0B 04 01 000000` … `0B 04 08 000000` | 9–16 | per zone 4: params 1..8 | zone 4: `01`=0, `02`=0 or `F7C283`, `03`..`06`=`F7C283` (RGB), `07`/`08`=0 |
| `0B 05 01…08` | 17–24 | zone 5 | colour `1AC1C7` |
| `0B 06 01…08` | 25–32 | zone 6 | colour `FF0000` |

Zones 4–6 take 3-byte values (RGB `F7 C2 83` = warm white, `1A C1 C7` = teal, `FF 00 00` = red);
zones 0–3 take 1 byte. The Nürburgring capture wrote the identical table, so the user's lighting
page was never exercised — which parameter is "on/off", "mode", "colour A/B" etc. is only
partially inferable from the shape. Readback `9F 21 0B [zone][param][value]`.

## Everything else on `0x1F`

| Read | Reply | Page |
|------|-------|------|
| `00` | `00 04 08` | [`identity-and-ports.md`](identity-and-ports.md) |
| `02 [port]` | port → role | identity-and-ports |
| `03` | mode poll | [`modes-and-control.md`](modes-and-control.md) |
| `09` | `09 00` | unknown |
