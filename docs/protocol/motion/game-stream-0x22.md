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

## `0x22/09` — haptic oscillators, 51-byte payload, ~30 Hz

```
7E 33 22 12 09 00 FF  [id][gain f32][freq f32] × 5  00 00 64 [chk]
```

| Record | id byte | gain | frequency | Reading |
|--------|---------|------|-----------|---------|
| 0 | `00` | 0.00018 | 265.0 Hz constant | fixed carrier (unknown effect) |
| 1 | `59` | 0.0090 | **f₀** | engine vibration fundamental |
| 2 | `0F` | 0.0045 | 2 f₀ | 2nd harmonic |
| 3 | `00` | 0.0045 | 3 f₀ | 3rd harmonic |
| 4 | `0F` | 0.0009 or 0.00225 | 4 f₀ | 4th harmonic (gain toggles between the two values) |

Fit of record 1 against the replay's `RPM`: **f₀ = RPM / 200 + 20 Hz** (`100_STROKE`: 0.00527·RPM
+ 17.9, R² 0.979; Spa: 0.00500·RPM + 20.2, R² 0.975), so idle (1000 rpm) sits at 25 Hz and 8000
rpm at 60 Hz, harmonics exactly ×2/×3/×4. The gains never changed in the captures; the Engine
Vibration slider stood at 60 % throughout (`Settings.png`), so gain ↔ slider is not established.
Trailer `00 00 64` is constant.

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
