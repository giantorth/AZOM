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
| f0 / f1 | −`PitchDegrees` / −`RollDegrees` → rad. SimHub builds pitch from −GameData pitch and roll from +GameData roll, so this keeps the game-telemetry signs |
| f2, f3, f7, pedals, flags, engine vibration | as in the table above while a game runs; else 0 and flag `01` |
| f4, f5 | 0, so the controller's Acceleration Pitch / Roll effects add nothing |
| f6 | +`HeaveMm` × 17/80 m/s² per mm — SimHub's default heave range (17 m/s² → 80 mm) run backwards. f6 is the input the controller's Heave effect follows (below); +f6 raises the platform, as SimHub's +mm does. *Crest strength* takes its push off it (below), and the sum is held within ±30. The scale is fixed: the profile's Heave row is where strength is tuned, and a row set up for less travel gives proportionally less f6 |
| f8–f11 | rest, so heave reaches the base through f6 alone. (They carried rest − `HeaveMm` before f6 did: felt as a kick when it steps 10 mm, but SimHub's few mm over a bump moved nothing on the rig, and a held offset washes out in ~1 s) |

Heave inputs in game mode, seat-judged bench run of 10 Nov (`tools/hma150-characterize.ps1
-Experiment HeaveFeel`: Heave 1.0 / 0, Road Detail 0, f7 40 m/s; pulses of 0.3 s, a 2 s step
and 3 Hz sines): f6 ±8 m/s² and all four shocks ±10 mm were each felt as sharp movement, f6
−8 the strongest. The one snapshot inside the f6 +8 pulses read −5.42 mm on all four
actuators 0.26 s after a pulse ended; the snapshot inside the shock sine (8.4 mm off rest,
Road Detail 1.0) read 0.00.

Direction and size of the Heave effect's answer to f6 (`-Experiment HeaveGain`, 10 Nov, same
settings: an f6 step started a set time before each 60 s `Output Displacement` line, all four
actuators equal in every line; positive displacement is up, as the mode 2 heave setpoint
showed from the seat):

| f6 step | Into the step | Displacement |
|---------|---------------|--------------|
| +8 m/s² | 142 ms | +0.55 mm |
| +8 | 488 ms | +22.75 mm |
| +8 | 1985 ms | +0.47 mm |
| +16 | 512 ms | +28.09 mm |
| +24 | 456 ms | +29.39 mm |
| −8 | 485 ms | −21.98 mm |

So +f6 raises the platform, about 2.8 mm per m/s² half a second in, levelling off near
29–30 mm from about 16 m/s². It is a washout: a held step is back at rest within 2 s, and
the platform dips the other way when the input lets go, which is what the −5.42 mm above
(and a −12 mm line 0.3 s after a +18 push had ramped out on track) caught. A reading taken
just after a pulse therefore shows the opposite sign to the pulse.

**Crest strength** (0–100, default 0 = off, pose source only) takes strength/100 · p off f6, where
p is built from the game's own `AccelerationHeave`, not from the profile:

| Step | Rule |
|------|------|
| Input | `AccelerationHeave` clipped to ±12 m/s², low-passed with τ = 0.2 s → a |
| Target | min(30, max(0, −a − 2) · 15): lightness only, 2 m/s² dead band, ceiling 30 |
| Attack | p rises to the target with τ = 0.05 s |
| Release | p falls with τ = 0.8 s, shortening to 0.3 s as a turns positive (full at +3 m/s²: the car landing) |
| Impact | \|surge\| or \|sway\| > 80 m/s² holds a at 0 for 2 s and releases p with τ = 0.15 s |

Why each part is there (iRacing, Bathurst, 911 Cup, 10 Nov, USB captures with matching replays):
a crest is long and small (217 km/h: 1.2 s at −4 m/s² mean, which a road-tuned profile turned
into an f6 of about 0.35, the size of its road noise) and a bump short and large (~7 m/s² for 30–50 ms),
so only duration separates them, hence the low-pass and dead band. A first two-sided version
pushed back as hard on the landing after the crest (a 47 m/s² swing within a second at strength
80), and the controller left game mode with error 261 seconds later (meaning unknown, cleared
by a power cycle), hence lightness only. Over a long crest the lightness wobbles around the
dead band at 2–3 Hz, which an instant release chopped into separate pushes, hence the slow
release; a release tied to the landing lets a soft landing down gently and a firm one firmly.
Wall hits got through the low-pass and held the push at its ceiling, hence the clip and the
impact hold. The impact level is 80 m/s² so that no car's braking or cornering trips it (the
fastest downforce cars reach 5–6 g, about 60 m/s²; the wall hit read 90–350 along the car);
a lighter hit is not caught, and the input clip and the ceiling still bound what it can ask
for. Tested on that one track and car only.

Two limits to keep in mind. The shape above was tuned from the seat while the push still went
out with the wrong sign (it lifted the platform, and the drop that felt right was the dip on
its release); with the sign corrected the drop comes as the car goes light, driven once and
judged good, not yet retuned. And because the Heave effect is a washout that levels off near
30 mm, the platform answers the start and the end of a push, not its length, and a push of
30 is already at the top of what f6 can ask for.

### AI mode (controller mode 11): the same frame followed as positions

With *AI mode* ticked (pose source only, off by default) the lane enters mode 11 instead of
mode 1 and sends the same `0x22/00` frame, which the controller then follows as a pose rather
than cueing from it. Bench, fw 2.35, 12–13 Nov 2026, slots pitch 1.0 / 0.2, roll 1.0 / 0.2,
heave 1.0 / 0, f7 40 m/s, displacement read from the 60 s `Output Displacement` line with a step
timed against it (±30 ms from the frame interval):

| Input held | Time into the step | Displacement |
|------------|--------------------|--------------|
| f6 +3 | 0.15 s / 0.48 s / 5.0 s | 1.09 / 2.986 / 2.986 mm, all four actuators |
| f6 +6 | 0.13 s | 1.87 mm |
| f6 +10 | 0.15 s / 0.23 s / 0.40 s | 4.58 / 8.97 / 9.51 mm |
| f6 −10 | 0.38 s | −9.51 mm |
| f6 ±20 (ramped, held 3 s) | 7 s | ±16.72 mm |
| f8–f11 all +5 mm | 0.48 s / 5.0 s | 0.00 mm |
| f0 +2° | 5.0 s | 17.63 / −17.31 / −17.31 / 17.63 mm (1029 mm long rig: 17.96 mm is 2°) |
| f1 +2° | 5.0 s | −11.90 / −11.90 / 12.23 / 12.23 mm (700 mm wide rig: 12.2 mm is 2°) |

- f6 is a **held heave position**: mm = 25.5 · tanh(f6 / 25.5), so 1 mm per unit near centre and
  a soft limit near ±25 mm. It does not wash out.
- The step response is a smoothing filter, not a speed limit: the same share of the step at
  0.14 s for 3, 6 and 10 mm (about half), done by ~0.3 s. A 30–50 ms bump is rounded off.
- f0 / f1 are the platform's angles 1:1 at intensity 1.0 while small (game mode gives about
  three quarters of the angle), and hold; larger angles meet the soft limit below. The shock
  fields are ignored.
- `0x22/09` engine vibration is rendered as in game mode (felt at strength 50 / tactile 40).
- Untested in this mode: f4 / f5, road detail, `0x22/07` / `0x22/08`, and how the slots'
  intensity and smoothness scale it.

**Travel envelope of modes 1, 2 and 11** (bench 13 Nov 2026, fw 2.35, 1029 × 700 mm rig, Max
Available Stroke 1.0 unless noted; every input ramped over 5–8 s and held 4 s; the furthest
actuator up / down from the `Output Displacement` line):

| Input | Mode 1 (game, Pitch / Roll 1.0 / 0.2) | Mode 2 (manual) | Mode 11 (AI) |
|-------|----------------------------------------|-----------------|--------------|
| pitch | 10°: 43.1 / −40.8; 20°: 49.5 / −46.4 | 0.5: 30.4 / −29.3; 1.0: 50.4 / −47.1 | 3.6°: 30.1 / −29.2; 7.2°: 49.5 / −47.0 |
| roll | 10°: 36.5 / −33.4; 20°: 48.0 / −42.1 | 0.5: 30.7 / −28.6; 1.0: 51.4 / −44.4 | 5.3°: 30.7 / −28.7; 10.6°: 51.0 / −45.7 |
| pitch + roll | 10° each: 58.1 / −54.2; 20° each: 62.5 / −56.7 | 0.5 each: 50.1 / −47.5; 1.0 each: 63.2 / −56.7 | 3.6° + 5.3°: 57.7 / −54.9 |
| pitch + roll + heave | — | 1.0 each: 64.7 / −21.8 | not measured; the plugin asks for 62 mm at most |
| heave | washes out (above) | 1.0: 45.6 | f6 ±40: ±23.41 |
| 1° pitch step | 1.76 mm at 0.16 s, 6.71 mm at 0.39 s | speed-limited (below) | 1.91 mm at 0.13 s, 8.95 mm at 0.39 s |
| stroke 0.5 | — | — | pitch 3.6°: 24.4 (30.1 at 1.0); f6 10: 8.68 (9.51 at 1.0) |

- The controller soft-limits tilt in all three modes at the same place: about 50 mm on one axis
  and about 60 mm with pitch and roll together. Mode 11 was asked for 64.5 mm (7.2° of pitch)
  and gave 49.5 mm, the figure mode 1 gives at 20° and mode 2 at 1.0.
- Manual mode with pitch, roll and heave at 1.0 reaches as far as anything measured in modes 1
  and 11 (64.7 mm against 62.5 mm and 57.7 mm), so SimHub's Manual control in the slow manual
  mode, sliders at their ends, shows the clearance the rig needs in every mode.
- Max Available Stroke scales the limit in mode 11 too.
- Mode 11 reaches the limit at about a third of the angle: game mode turns about 0.75 of an
  angle into tilt (6.7 mm per degree of pitch here, where 1° is 8.98 mm), mode 11 all of it.
- The two game modes respond to a step at about the same pace.

**Why AI mode is limited unless *max performance* is ticked** (iRacing, 13 Nov 2026, USB
captures). With the pose followed 1:1 and a profile at full gains, two laps asked one actuator
for up to 153 mm (pitch 65 + roll 65 + heave 23; the controller's readings stayed at 52–55 mm)
and for an 84 mm swing on one axis within 0.3 s, a full pitch reversal. Nothing faulted, but
the rig hopped on the floor at least once. Game mode laps on the same track never asked for a
swing above about 23 mm in 0.3 s, and an earlier AI mode lap with a 53 mm swing did not hop.
Replayed through `AiPoseLimiter` at 210 mm/s and 1080 mm/s², with the 0.9 tilt and the 62 mm
travel, the largest 0.3 s swing of those laps is 56 mm and of a gentler lap 19 mm (none of
its frames more than 3 mm behind the pose). The caps come from those replays; that the rig
no longer hops with them rests on a later drive, not on a measurement of the hop itself.

Mode 12 (flight-sim game) takes `0x22/01` — `7E 2D 22 12 01`, ten floats (pitch, roll,
heading, angular velocity X/Y/Z, ACCX, ACCY, ACCZ, VelocityX), brake, throttle, `00`, in-game
flag, per Motion Manager's `SuTelemetry_GameTelemtry2` — and washes heave out like mode 1
(ACCZ +3: 10.75 mm at 0.49 s, 0.0003 mm at 5 s; pitch +2° holds at 13.2 mm).

What the plugin sends in AI mode (`MotionGameFeed.PoseFrameAsPosition`, `MozaMotionController.StreamTick`):

| Part | Rule |
|------|------|
| f0 / f1 | the pose angle × 0.9 (game mode turns about 0.75 of an angle into tilt; 0.75 felt tamer than the game mode build from the seat); with *AI mode max performance* the true angle, each held only to the angle that moves an actuator 65 mm |
| Travel | `AiPoseLimiter`, on the travel the platform is meant to end up with: pitch and roll are each eased off towards Max Available Stroke × 50 mm (where the controller puts one axis in game mode), then \|heave\| + pitch travel + roll travel, the furthest any actuator moves, is held to stroke × 62 mm (game mode's own maximum, above) by scaling the whole pose back. The angles sent are 70 · atanh(travel / 70), which undoes the controller's soft limit on a mode 11 angle, so up to about 72 mm can be asked of one actuator for 62 mm meant. Not with max performance, which leaves it all to the controller |
| Speed, acceleration | each axis, as the actuator travel it causes, moves at no more than 210 mm/s and changes speed by no more than 1080 mm/s². A frame that arrives late counts as 0.05 s at most, so a stall is not caught up in one step (10.5 mm at most per frame). Not with max performance |
| f6 | 25.5 · atanh(mm / 25.5), mm held within ±0.9 · 25.5, which undoes the soft limit: the platform sits where the profile's heave asks up to about 23 mm |
| Crest strength | taken off as a drop in mm (strength 100 = up to 20 mm), none below 5 m/s and full from 15 m/s: a car being placed on track reads as "light" and held the drop for 10 s |
| Source changes | f0, f1 and f6 ease over 2.5 s whenever the stream flips between a live pose and neutral frames (game start, pause, exit, SimHub idle, stale data) |
| Frame shape | a posted frame carries its shape (`MotionTelemetryFrame.IsPosition`: f6 in mm for mode 11, f6 as acceleration for mode 1). The lane streams neutral frames while a frame's shape is not the mode it entered, so around a mode or source change one mode's f6 never goes out in the other |
| Switching while running | ticking AI mode on or off while streaming goes hold → mode 3 → wait for the actuators → the normal entry, the path Manual control takes out of game mode. Whether the controller levels by itself between modes 1 and 11 is not known |
| Engine tick-over | strength 0–100 = a heave pulse of up to 3 mm added to f6 at rpm / 230 Hz (4–11 Hz), each pulse with its own height, length and timing, +40 % with throttle, up to 2.5× for 2.5 s after the engine fires, never more than 4 mm; gone between 40 % and 70 % of the redline. It is added after the limiter, as a step of its own height, and the limiter keeps that much of the 62 mm travel free at the current revs, so pose plus pulse stays within the travel. A pulse rendered by the profile's own engine effect is sampled at the 33 Hz frame rate and arrives uneven (0.15 mm at idle, 0.7 mm at 6000 rpm in a capture), hence this one |
| `0x22/09` | with a tick-over strength set it fades in over 30–55 % of the redline; *Engine vibration pitch* multiplies f₀ by 2^((value − 50) / 25) (0.25× at 0 to 4× at 100; f₀ kept within 20–250 Hz) and divides the gains by that factor^1.5. The engine counts as running from 500 rpm (iRacing reports a few hundred for a car that is not started) |

The pose is `FinalMotionState` (corner haptics folded in) while *Include SimHub corner haptics*
is on (default), else `MotionState`; haptics that write heave directly (engine, touchdown) are
in both. They run at 7–50 Hz against a ~33 Hz frame rate, so the fast ones alias. The
controller's Pitch / Roll / Heave intensity still scales what arrives.

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

The captures contain only discrete slider moves (9 frames in 73 s). The mode poll reads
`03 02 02 00 00` throughout the manual-test captures; the controller logs nothing per setpoint.

Motion Manager's sequence around the test (`2026-11-11 18-03-53`): Start = `set mode 7`, `set mode 0`,
`0x20/02 02 00`, `set mode 3`; toggle on = `set mode 2` (the actuators had reached mode 3 ~3.3 s
after the controller); toggle off = `set mode 3`; Stop = `0x20/02 01 00`, `set mode 13`. The
controller ramps to each setpoint at its own slow rate, also for a −1 → +1 jump.

The plugin uses mode 2 as a slow follow target while SimHub's Manual control or Hop-in position
is the motion source (`MozaMotionController.ManualSetpointTick`): each axis is re-sent when it
moves by 0.5 %, at most every 100 ms. The controller (fw 2.35) accepts that stream and keeps
ramping towards the latest target. Rates above 10 Hz per axis are untested. The ramp is a speed
limit with a soft start and stop, about 25 mm/s of heave whatever the step (bench 12 Nov 2026:
a +0.2 and a +0.5 step both read 7.1–7.2 mm after 0.38 s; +0.5 read 21.7 mm at 0.98 s and
26.6 mm at 2.5 s; full scale ±1 is about ±56 mm), so mode 2 cannot carry a game's heave. Unlike game mode, a
held heave setpoint stays put (game mode washes a steady shock offset out).
