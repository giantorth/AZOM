# Motion platform — game-mode streams (group `0x22`) and the manual-test setpoint

In mode 1 the host streams **raw vehicle telemetry** and **host-rendered haptic oscillators** to
the controller on group `0x22`, dev `0x12`. None of these frames is answered. The controller
turns the telemetry into actuator motion with its own model (rig geometry + the effect slots of
[`effect-parameters.md`](effect-parameters.md)).

Field identification: `tools/motion-replay-correlate` fits every float of the frame against
every numeric channel of the same-session SimHub replay (iRacing, 10 Hz) — captures `100_STROKE`
(straight-line, R² quoted below) and `spa-003`. Decoder: `tools/motion-decode streams`.

## `0x22/00` — telemetry frame, 53-byte payload, ~33 Hz

```
7E 35 22 12 00 [f0][f1][f2][f3][f4][f5][f6][f7][f8][f9][f10][f11] [brake][throttle][b2][flags] [chk]
                └──────────── 12 × IEEE-754 big-endian float ────────────┘
```

| Field | iRacing channel | Fit (`100_STROKE`) | Unit / notes |
|-------|-----------------|--------------------|--------------|
| f0 | **−Pitch** | a = −0.995, R² 0.995 | rad, sign flipped |
| f1 | **−Roll** | a = −0.999, R² 1.000 | rad, sign flipped |
| f2 | **Yaw** | a = +0.9995, R² 1.000 (exact: −1.008427 at rest) | rad, −π..π as iRacing reports it (wraps) |
| f3 | **YawRate** | a = +0.998, R² 0.983 (Spa 0.995) | rad/s |
| f4 | **−LongAccel** | a = −0.99, R² 0.978 | m/s², sign flipped (positive = braking) |
| f5 | **−LatAccel** | a = −0.97, R² 0.972 (Spa 0.84) | m/s², sign flipped |
| f6 | ≈ −(VertAccel − g) | a ≈ −0.63…−0.79, R² 0.43–0.64 | m/s², gravity removed and sign flipped; the weak fit against the 10 Hz replay suggests host-side filtering |
| f7 | *unidentified* | tracks 0.35 × Speed in the straight-line run (R² 0.88) but fits nothing at Spa (−50…+56, negative while coasting) | not a plain irsdk channel; see open questions |
| f8 | **LFshockDefl × 1000** | a = 980, R² 0.952 | mm |
| f9 | **LRshockDefl × 1000** | a = 986, R² 0.983 | mm |
| f10 | **RRshockDefl × 1000** | a = 991, R² 0.984 | mm |
| f11 | **RFshockDefl × 1000** | a = 961, R² 0.947 | mm |
| brake | Brake × 100 | a = 99.6, R² 0.994 | u8 percent |
| throttle | Throttle × 100 | a = 100.1, R² 0.999 | u8 percent |
| b2 | always `00` | — | — |
| flags | `01` normally; `03` on the frame(s) around a gear change (66 frames = 66 gear-shift pulses at Spa); `00` before the car is on track (`IsOnTrack = 0`, all floats 0) | — | bit0 = data valid / on track, bit1 = gear-shift event |

Example (standing in the pits, brake 100 %):

```
7E 35 22 12 00 3D2D1B1E 3D352404 BF811283 B7E0750D BEDA6035 3ED10055 3CADD91F 392A9BAF
               420AD24A 4219BCBE 421AC890 42044781 64 00 00 01 [chk]
      f0=0.0423 f1=0.0442 f2=-1.0084 f3≈0 f4=-0.43 f5=0.41 f6=0.02 f7≈0  LF=34.7 LR=38.4 RR=38.7 RF=33.1 mm
```

Cadence: ~33 Hz (inter-frame p50 27 ms, p90 29 ms, occasional 190 ms stalls), in all three game
captures. The frame keeps streaming while the car sits in the garage (all floats zero, flags
`00`) and across the `0x20/02` 02↔03 flips. The corner order **LF, LR, RR, RF** is the one the
fits give; the Spa fit of f8–f11 is poor only because the replay samples at 10 Hz.

The `100_STROKE` and Spa fits put the stream at zero lag against the replay (±50 ms search), so
Motion Manager forwards the game's values without visible buffering.

## Controller output and heave

Game mode has **no heave position field**; the Heave slot (`0x1E/05 03`) and Road Detail
(`0x1E/06`) scale what the controller derives from the frame itself. The only readback of
what it does is the firmware log's `Output Displacement` (mm per actuator) every 60 s
([`firmware-log-0x0E.md`](firmware-log-0x0E.md)), logged with the pitch/roll it applied.

- The four displacements always satisfy `d0 + d2 = d1 + d3` (warp exactly 0): the platform is
  driven as a plane — heave = mean, plus a pitch and a roll term.
- The logged `pitch` / `roll` equal f0/f1 of the last frame, so attitude passes straight
  through. A plugin session (Pitch/Roll 1.0, smoothness 0, every other slot 0) logged pitch
  −0.032 / roll −0.020 rad → `-2.45 9.65 2.69 -9.41` mm, ≈ 6 mm per degree of pitch; Motion
  Manager's Nürburgring session gave ≈ 200 mm/rad at Pitch 0.60.
- Heave source, from the 12 Motion Manager game snapshots (`nurburgring-002`, `spa-003`;
  Heave 0.30 / 0.56, Road Detail 0.70): the mean displacement fits the mean of f8–f11 minus its
  1 s average (r = −0.80, −0.58 mm per mm) better than f6 averaged over 0.3 s (r = +0.65); raw
  shocks, f4, f5, f7 do not fit. So heave comes mainly from common-mode suspension deflection,
  washed out over ~1 s; too few samples to rule out an f6 term.
- Sign is unresolved. Manual Input heave −1.0 (mode 2) logged `13.35935` on all four
  actuators, but standby logs `-80` on all four, so if standby is fully down a positive
  displacement is up and the slider's + is down. Same capture: Max Available Stroke 0.30.
- A held common-mode shock offset does not hold heave: plugin bundle `P12GYH3K` (heave slot
  1.0, road detail 1.0) sent +4.5 mm on all four shocks for 97 s and the 60 s snapshot read
  0.00003 mm; a moving offset of ~5 mm read −0.12 mm.
- Pitch holds (bench run 13 Oct, `tools/hma150-characterize.ps1 -Experiment Gain`, slots
  other than pitch at 0, smoothness 0): 0.82 mm per actuator per degree at intensity 0.25
  (1° and −3°, 5 s into the hold) and 1.46 at 0.5 (+5°, 8.6 s in) — linear in angle, slightly
  under linear in intensity or a little washout; d0/d3 move opposite d1/d2. No sample at 1.0.

## Plugin mapping

`Devices/Motion/MotionGameFeed` fills the frame from SimHub `GameData` the way the handover
bridge (v30) does — tuned against Motion Manager's stream and the rig — and leaves strength to
the controller's Motion Effects sliders, as Motion Manager does:

| Field | Source |
|-------|--------|
| f0 | `OrientationPitch` → rad |
| f1 | −`OrientationRoll` → rad |
| f2 | −`OrientationYaw` → rad, wrapped to ±π |
| f3 | `OrientationYawVelocity` → rad/s |
| f4 / f5 / f6 | `AccelerationSurge` / −`AccelerationSway` / `AccelerationHeave`, m/s² (surge positive when braking). SimHub's sway is opposite to Motion Manager's lateral field — an iRacing slow-turns Motion Manager capture (tester, 16 Oct, bridge v31); the synthetic shocks use the flipped value too |
| f7 | `SpeedKmh` / 3.6 |
| f8–f11 | iRacing: `GameRawData.Telemetry.{LF,LR,RR,RF}shockDefl` × 1000 (what Motion Manager sends); other games: `base + a·surge + b·sway + c·heave + d·speed²` per corner, fitted to the Spa stream (R² 0.78–0.90; coefficients in the source) |
| brake / throttle | `Brake` / `Throttle`, 0–100 |
| flags | `01`; bit1 for 120 ms after each gear change |

No frame while the game is not running or paused: the lane then sends the neutral frame
(shocks at rest, flag `01`), and SimHub idle sends it with flag `00`.

**SimHub pose source** (output setting *Motion source* = SimHub Motion profile,
`MotionGameFeed.PoseFrame`): the frame carries SimHub Motion's pose instead, so its effects
profile (washout, limits, per-game tuning) does the cueing:

| Field | Source |
|-------|--------|
| f0 / f1 | −pose `PitchDegrees` / −pose `RollDegrees` → rad. SimHub builds pitch from −GameData pitch and roll from +GameData roll, so this keeps the game-telemetry signs |
| f2, f3, f7, pedals, flags, engine vibration | as in the table above while a game runs; else 0 and flag `01` |
| f4–f6 | 0, so the controller's Acceleration Pitch / Roll effects add nothing |
| f8–f11 | rest − pose `HeaveMm`, the common-mode shock change the controller turns into heave (above). Sign and gain not yet checked on a rig, and the controller washes held offsets out in ~1 s |

The controller's Pitch / Roll / Heave intensity still scales what arrives.

Each live frame is followed by a `0x22/09` engine vibration frame (below) built from SimHub's
`Rpms` and `Throttle`, scaled by the profile's Engine vibration and Tactile feedback values (else
50 % and the controller's haptic intensity, i.e. the gt3 level). Unlike Motion Manager it sends
no tone while the engine is off (`Rpms` ≤ 100, the bridge's cut-off): testers found Motion
Manager's 25 Hz buzz before ignition and after a stall unwanted. When live frames stop, the
engine stops, and on disengage, one zero-gain `0x22/09` follows.

## `0x22/09` — engine vibration, 51-byte payload, ~30 Hz

```
7E 33 22 12 09 00 FF  [id][gain f32][freq f32] × 5  00 00 64 [chk]
```

| Record | id byte | gain | frequency | Reading |
|--------|---------|------|-----------|---------|
| 0 | `00` | 0.00018 | 265.0 Hz constant | fixed carrier (unknown effect) |
| 1 | `59` | 0.0090 | **f₀** | engine vibration fundamental |
| 2 | `0F` | 0.0045 | 2 f₀ | 2nd harmonic |
| 3 | `00` | 0.0045 | 3 f₀ | 3rd harmonic |
| 4 | `0F` | 0.0009 + 0.00135 × throttle | 4 f₀ | 4th harmonic, louder with throttle |

Fit of record 1 against the replay's `RPM`: **f₀ = RPM / 200 + 20 Hz** (`100_STROKE`: 0.00527·RPM
+ 17.9, R² 0.979; Spa: 0.00500·RPM + 20.2, R² 0.975), **floored at 25.00 Hz** (the minimum in
all three game captures), so idle (1000 rpm) sits at 25 Hz and 8000 rpm at 60 Hz; harmonics
exactly ×2/×3/×4. Record 4's gain follows the `0x22/00` throttle byte (all frames within its
1 % rounding; the tester found the same, 16 Oct). Trailer `00 00 64` is constant.

**Gain level = Engine Vibration strength × tactile intensity**, in float32 at every step:
record 0 = 0.0009·S·T, record 1 = 0.045·S·T, records 2–3 = record 1 × 0.5, record 4 =
record 1 × (0.1 + 0.15 × throttle), S = the preset's `EngineVibration.Strength`, T =
`platform.tactileFeedbackValue` (also written to the controller as Haptic Feedback Intensity,
`0x1E/04 01`). `100_STROKE` and Spa ran "iRacing gt3" (S 50, T 40 → 0.009, stored as `3C1374BD`,
one ULP above the nearest float32); `nurburgring` ran "iRacing caterham" (S 60, T 50 → 0.0135,
all five gains 1.5×, the haptic intensity read 0.40 then written 0.50 before the drive). Both
reproduce bit for bit; it rests on these two combinations, and whether the controller applies
its own Haptic Feedback Intensity again on top is unknown. The preset's
`EngineVibration.Frequency` (100 / 132 / 200) does not show in f₀: it fits RPM / 200 + 20 in the
gt3 sessions, whose preset says 132 (no RPM replay exists for the caterham session).

Motion Manager streams it while the game runs, even with the engine off, stops with the game,
and sends one frame with every gain zero (frequencies kept) as it leaves game mode
(`nurburgring` 693.2 s, with `Set next mode:3`). `tools/motion-frame-check` rebuilds all 6 566
captured frames at the base level byte-exact.

## `0x22/07` — ABS oscillator, 14-byte payload, ~31 Hz while active

```
7E 0E 22 12 07 02 FF 60 [amp f32] [freq f32] 00 00 05 [chk]
   amp = 0.021 (occasionally 0.0 within a burst), freq = 50.0 Hz
```

Sent only in bursts; 122 of 124 frames (`100_STROKE`) and 165 of 195 (Spa) coincide with the
replay's `BrakeABSactive = true`. 50 Hz is the "ABS Trigger — Frequency" slider value
(`Settings2.png`); the 0.021 amplitude did not change (slider 40 %).

## `0x22/08` — gear-shift pulse, 13-byte payload, one per shift

```
7E 0D 22 12 08 [seq] FF 90 [amp f32] 03E8 00 01 14 [chk]
```

`seq` increments per pulse (wraps at 11 → 0 in the Spa capture). Every pulse lands within 0.3 s
of a `Gear` change in the replay (8/8 and 66/66). Up-shifts send a 0.44 pulse, sometimes
preceded 30 ms earlier by a 0.121 pulse; down-shifts send 0.38–0.42 followed by 0.062. `03E8`
(1000) and the `00 01 14` trailer are constant. The "Gear Shift" slider showed intensity 40 %,
duration 100 ms.

## Manual Motion Test — `0x1E/07 [axis] [f32 setpoint]` (mode 2)

The "Manual Motion Test" sliders write one frame per release:

```
h2b  7E 06 1E 12 07 00 3F800000 [chk]      pitch = +1.0
b2h  7E 06 9E 21 07 00 3F800000 [chk]      echo
```

| axis | DOF | Captures |
|------|-----|----------|
| `00` | pitch (+ = pitch up slider) | `Pitch±5`, `±100` |
| `01` | roll | `roll±5`, `±100` |
| `03` | heave | `Heave±5`, `±100` |
| `02` | not observed (gap — presumably another DOF of other platform types) | — |

| Slider | Float | Hex |
|--------|-------|-----|
| +5 % | +0.05 | `3D4CCCCD` |
| −5 % | −0.05 | `BD4CCCCD` |
| +100 % | +1.0 | `3F800000` |
| −100 % | −1.0 | `BF800000` |
| Zero | 0.0 | `00000000` |

The captures contain only discrete slider moves (9 frames in 73 s); whether the controller
accepts a continuous stream of `0x1E/07` setpoints at game rates, and how it filters them, is
untested (open question). The mode poll reads `03 02 02 00 00` throughout the manual-test
captures; the controller logs nothing per setpoint.
