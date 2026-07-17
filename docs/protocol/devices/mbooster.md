# Moza mBooster Pedals

Vibration-motor pedal product on USB-CDC PID `0x0008`. The mBooster is a
single analog pedal with a built-in vibration motor — the user picks
whether the pedal serves as throttle, brake, or clutch in the plugin
UI, and the motor is driven by host-rendered effects (ABS, Lockup,
Threshold, Engine) per the documented protocol.

**Multi-device:** the plugin supports more than one mBooster on the
same host concurrently (one each for throttle / brake / clutch is the
canonical layout). Each unit gets its own [`MozaSerialConnection`](../../../Protocol/MozaSerialConnection.cs)
under [`MBoosterDeviceController`](../../../Devices/MBoosterDeviceController.cs);
all controllers are owned by [`MozaMBoosterRegistry`](../../../Devices/MozaMBoosterRegistry.cs).

## Reference protocol

The user-supplied protocol note in
[`../../MozamBooster — Protocol Note.md`](../../MozamBooster%20—%20Protocol%20Note.md)
is the authoritative wire-format reference. It includes verified
known-good frames against real hardware captures + the host-side
synthesizer formulas the plugin reproduces verbatim (see
[`MBoosterEffectSynthesizer.cs`](../../../Devices/MBoosterEffectSynthesizer.cs)).

The plugin-side implementation diverges from the protocol note in only
two ways:

1. **No firmware-version handshake** — the note says there is none.
2. **No probe fallback** — the registry-driven discovery is the only
   path. The serial probe fallback under `MozaProbeTarget.MBooster`
   returns null because mBooster device id `0x12` collides with
   wheelbase Main + AB9 Main, so writing a discovery probe at every
   COM port is high-risk for non-mBooster peripherals.

## USB identification

| Field           | Value                                       |
|-----------------|---------------------------------------------|
| Vendor ID       | `0x346E` (Gudsen / Moza)                    |
| Product ID      | `0x0008`                                    |
| Category        | `MozaDeviceCategory.MBooster`               |
| HID match       | VID+PID (no name regex — see [`MozaHidReader.cs`](../../../Protocol/MozaHidReader.cs)) |
| Baud rate       | 115200                                      |
| Stable identity | USB device instance segment from the registry walk; fallback to the device instance ID surfaced by `HidDevice.DevicePath` — see `MBoosterDeviceController.Identity` |

## Frame shapes

mBooster uses the same Moza wire framing as the wheelbase
(`7E LEN GRP DEV PAYLOAD CHK` with `data_len` excluding group + device
and the wire-aware checksum that compensates for `0x7E` byte stuffing).
[`MozaProtocol`](../../../Protocol/MozaProtocol.cs)'s checksum +
stuffing routines handle all framing.

### Motor write — cmd `0xb1`

Built inline by [`MozaMBoosterProtocol.BuildMotorFrame`](../../../Protocol/MozaMBoosterProtocol.cs).
14 bytes pre-stuffing.

```
7e  09  24  12   b1  EF  EN  00   P1  FH  FL  AH  AL   CK
                 │   │   │   │    │   └─┴─freq u16 BE
                 │   │   │   │    └ param1 (1..255)
                 │   │   │   └ pad (0x00)
                 │   │   └ enable (0 = off, 1 = on)
                 │   └ effect type (1..4)
                 └ cmd id (0xb1)
```

**Effect IDs** (enum [`MBoosterEffectId`](../../../Protocol/MozaMBoosterProtocol.cs)):

| ID  | Name      | ParamK | Trigger condition (host-side, doc § 4) |
|-----|-----------|--------|----------------------------------------|
| `1` | ABS       | 2000   | `absActive > 0.1` from SimHub          |
| `2` | Lockup    | 2640   | Heavy brake (>0.8) + wheels < 30 % of vehicle speed (fallback: brake > 0.9 when wheel speed unavailable) |
| `3` | Threshold | 3080   | Rising edge on brake > 0.6; release at < 0.3 (hysteresis) |
| `4` | Engine    | 1000   | `rpm > 0.8 × idleRpm` — runs continuously |

**Known-good frames** (verified against the protocol note's hardware
captures — diff against these in a `SerialTrafficCapture` export to
confirm wire correctness):

```
ABS on, 22Hz, amp=0x08e8:  7e 09 24 12 b1 01 01 00 5a 1c 28 08 e8 0b
ABS off:                   7e 09 24 12 b1 01 00 00 00 00 00 00 00 7c
Lockup on, 55Hz, ramp 0:   7e 09 24 12 b1 02 01 00 30 46 66 00 00 5a
Lockup off:                7e 09 24 12 b1 02 00 00 00 00 00 00 00 7d
Engine on, 10Hz, amp=0x020c: 7e 09 24 12 b1 04 01 00 64 0c cc 02 0c ca
Engine off:                7e 09 24 12 b1 04 00 00 00 00 00 00 00 7f
```

### Keepalive

Degenerate 0-payload frame targeting device 0x12 — `7e 00 00 12 9d`.
Built by [`MozaMBoosterProtocol.BuildKeepalive`](../../../Protocol/MozaMBoosterProtocol.cs).
Emitted every ~500 ms from `MBoosterEffectWorker` regardless of
effect state. Stops being sent → motor eventually drops connection
state and may stop responding to writes.

**Detection note:** the keepalive and motor frames are write-only —
the device never replies to either. With all effects disabled (the
default for a freshly-detected device) the worker sends nothing else,
so nothing would ever elicit a parseable response for
`MBoosterDeviceController.MarkDetected` to latch onto. `TryConnect`
fires `RequestCalibrationReads()` immediately after the port opens for
exactly this reason — without it the UI sits at "Probing…"
indefinitely until something (previously, only the user manually
clicking "Read from device") prompts a response.

### Disable

Same opcode with `enable = 0` and all params zeroed.
[`MozaMBoosterProtocol.BuildDisableFrame(effect)`](../../../Protocol/MozaMBoosterProtocol.cs).
Sent at every effect deactivation edge AND for all four effect IDs on
controller dispose, otherwise the last-active waveform can latch on
the motor after the port closes.

## Stream lane

All motor frames go through a single per-connection lane:

```
StreamKind.MBoosterEffect = 17
```

Per protocol note § 4 the worker emits at most one motor frame per tick
(~50 Hz), so a single coalesced lane is sufficient — the latest-wins
behaviour on writer lag is the same property the doc relies on when it
says "the motor plays the instantaneous amplitude you send".

Keepalives go via the one-shot FIFO (`MozaSerialConnection.Send`) so
they aren't coalesced.

## Effect synthesis

[`MBoosterEffectSynthesizer.cs`](../../../Devices/MBoosterEffectSynthesizer.cs)
reproduces protocol note § 4 verbatim:

| Effect    | Waveform                                                    |
|-----------|-------------------------------------------------------------|
| ABS       | `wave = 0.9 + 0.1 * sin(phase); amp = wave * intensity`      |
| Lockup    | `ramp = clamp(elapsed / 0.5, 0, 1); amp = ramp * intensity`  |
| Threshold | 5 Hz envelope: 20 ms full + 120 ms 80 % + 60 ms gap          |
| Engine    | `wave = 0.5 + 0.5 * sin(phase); amp = wave * intensity`      |

Engine intensity is clamped to 10 % at apply time (doc § 4 default
`engineScale = 0.01`, clamped to `[0, 0.1]`) — engine runs
continuously and would dominate the other effects without this cap.

### Effects card UI

The Effects card was rebuilt one effect at a time — Engine first, then
ABS, Road Texture, Lockup, and finally Threshold. All five now have
their own expander with Enable + sustained Test toggles; the
fire-and-forget 1s `TestPulse` mechanism this replaced (originally used
by all four non-Engine effects) has been fully removed —
`MBoosterEffectWorker.FireTestPulse`/`TestPulse` and
`MBoosterDeviceController.FireEffectTest` no longer exist.

Above all five expanders, a **Pedal Trace (last 5s)** sparkline
(`MBoosterPedalTraceViz`, reusing `MozaControls.BandwidthSparkline`
single-series with `MaxValue=100` and `OutBrush=Transparent` — its
second series is unbound, and without that the control's tip-dot
Ellipse still renders at its (0,0) default) plots the currently
selected device's pedal position so the user has a visual reference for
when the effects below actually trigger. Fed from
`UpdateMBoosterCurveMarkers`, which already runs at 30Hz (same cadence
as the curve editors' live position dot) — 150 samples × 1/30s = 5
seconds, `_mboosterPedalTraceSamples` — and reset to a flat baseline on
device switch so it doesn't show a discontinuous mix of two different
pedals' history.

**Engine Vibration** was the first effect rebuilt: it originally had two
real sliders, Frequency (Hz) and Intensity, with Frequency replacing the
telemetry-derived mapping (`clamp(rpm / 20000 * 200, 10, 200)` per doc
§ 4) with a fixed, user-chosen value.

That fixed slider has since been reverted: Engine's frequency is once
again telemetry-derived, this time to match AB9's parametric
engine-vibration model exactly (see `Ab9EngineVibrationWorker.Tick` and
`docs/protocol/devices/ab9-shifter.md`) rather than the old doc § 4
formula — `frequency = EngineRedlineFreqHz × (rpm / redline)`, clamped
to `MBoosterUiConstants.EngineFreqMinHz`/`MaxHz` (60–200Hz), where
`EngineRedlineFreqHz` is the constant frequency reached exactly at
redline (`MBoosterEffectWorker.EngineRedlineFreqHz`, pinned to
`EngineFreqMaxHz`) and `redline` is the game's reported `MaxRpm`
(`MBoosterTelemetrySnapshot.MaxRpm`, threaded from `MozaPlugin
.DataUpdate`), falling back to `EngineDefaultRedlineRpm` (8000, same
convention as `Ab9EngineVibrationWorker.DefaultRedlineRpm` and
`HardwareApplier`'s CM2 RPM-ramp fallback) when a game doesn't report
one. There is no user-facing Frequency slider for Engine any more —
`MBoosterEffectSettings.FrequencyHz` is unused for Engine and kept only
so older saved profiles still deserialize; only Intensity remains
user-configurable. The `rpm > 0.8 × idleRpm` activation gate is
unchanged.

Engine's Test control is a **toggle**, not the "Test 1s" button Lockup/
Threshold still use — ABS used to fire the same kind of one-shot 1s
pulse (`MBoosterEffectWorker.FireTestPulse`, a `TestPulse` with a fixed
deadline) before its own rebuild below, but Engine has no brake
modulation to preview against a live pedal press, so a timed pulse
didn't fit it well. Instead, `Test` turns `_engineTestSustained` on/off
(`MBoosterEffectWorker.SetEngineTestSustained`, wired through
`MBoosterDeviceController.SetEngineTestActive`); while on, the effect
runs indefinitely at the fixed redline frequency (there's no guarantee
a game is running to supply RPM during a test) and live-reads Intensity
from settings every tick (not a snapshot) so slider drags are felt
immediately. This bypasses the `Enabled`/RPM-idle gates entirely, same
as the other effects' test pulses. Three places explicitly turn it back
off so a forgotten toggle can't leave the pedal buzzing: switching the
selected mBooster device in the dropdown, closing the settings panel
(`OnUnloadedStopTimers`), and — same as always — `SendAllDisableFrames`
on controller dispose sends the wire-level disable regardless of this
flag's state.

**ABS rebuild**: three sliders — **Frequency (Hz)** (5–30,
`MBoosterEffectSettings.FrequencyHz`, bounds in
`MBoosterUiConstants.AbsFreqMinHz`/`AbsFreqMaxHz`, default 22 — the
exact value from the "known-good" real Pit House capture above),
**Intensity** (0–100%, unchanged in role), and **Smoothness** (0–100%,
new — pulse modulation depth, `MBoosterEffectSettings.SmoothnessPct`).
Frequency replaces the old ABS-activation-depth mapping (doc § 4:
`18 + abs01*12`, 18–30Hz) — moot in practice since the plugin's
snapshot exposes `AbsActive` as a bool, not the `0..1` float the
pseudocode expects, which collapsed that formula to a constant 30Hz
anyway. Smoothness is a host-side extension to
`MBoosterEffectSynthesizer.SynthesizeAbs`, *not* from the protocol
note: the function now takes a `smoothness01` parameter that
generalizes the ripple depth of `wave = baseline + depth * sin(phase)`,
where `depth = 0.5 - 0.4 * smoothness01`. At `smoothness01 = 1` (100%,
the default — preserves behavior for profiles that predate this
slider) `depth = 0.1`, reducing to the *exact* original verified
formula (`0.9 + 0.1*sin`) that the file's header comment warns not to
modify without verification — untouched, just reachable at one specific
input now. At `smoothness01 = 0` (0%) `depth = 0.5`, matching
`SynthesizeEngine`'s full 0..1 swing for a sharper, choppier pulse.

ABS also gets Engine's sustained Test toggle pattern
(`_absTestSustained`/`SetAbsTestSustained`/`SetAbsTestActive`, replacing
the old 1s `FireTestPulse` path for this effect only) — since ABS has
no live "how hard is ABS engaging" signal to preview against outside a
real ABS event, the toggle substitutes live brake position for
`absActive`, just indefinite and live-tracking Frequency/Intensity/
Smoothness every tick instead of snapshotting them at toggle-on time.
Unlike the old 1s pulse (which fired on any nonzero press, `brakeT >
0.01`), the sustained test gates at 60% brake (`brakeT < 0.6` stays
silent) — the test should only fire once you're pressing hard enough
to plausibly trigger real ABS, not on a light tap. The same three
turn-it-back-off safety nets apply (switching devices, closing the
settings panel, and `SendAllDisableFrames` on dispose).

**Overlap bug found and fixed while rebuilding this card**: pairing a
second `OffOnToggle` (the new Test toggle) next to the existing Enable
toggle, both at the pre-existing `Width="120"`, wasn't enough room for
either toggle's own label text *and* its OFF/ON pill — verified with an
offscreen WPF render (`RenderTargetBitmap`) of the card in isolation,
which showed the pill visually overlapping its own label, and the two
toggles bleeding into each other. Fixed by dropping the fixed `Width`
entirely (letting each toggle size to its natural content — the
`OffOnToggle` base style already defaults to `HorizontalAlignment=
"Left"`, so nothing stretches or clips) and giving the Test toggle a
`24px` left margin instead of `6px`. Re-verified clean at both 860px
and 620px card widths, including after adding ABS's three-slider
expander and the pedal trace sparkline above Engine's. Note this
`Width="120"` pattern is used on `OffOnToggle` instances throughout the
rest of `SettingsControl.xaml` (Handbrake/Throttle/Brake/Clutch/etc.) —
those weren't touched (out of scope here, and each only has one toggle,
not two competing for the same box), but the same latent overlap could
in principle apply to any of them with a long enough label/translation.

A second, unrelated alignment bug turned up the same way while adding
the pedal trace label: `SliderLabel`'s `MaxWidth` combined with its
inherited `HorizontalAlignment="Stretch"` centers the text when the
style is used standalone in a vertical `StackPanel` (as opposed to its
normal home in a `Grid.Column="Auto"`, where the column already hugs
the content and masks the issue). Fixed by adding explicit
`HorizontalAlignment="Left"` to both standalone uses — the new Pedal
Trace label and the pre-existing Start/End of Travel (mm) label above
the `MozaRangeSlider` — while leaving the (unaffected) `Grid.Column`
uses alone.

### Road Texture (effect type 9) — a genuinely different wire shape

**Road Texture** is the third effect rebuilt, and the first entirely
new one (ABS/Engine already existed pre-rebuild; this one didn't).
Confirmed as a *real* Pit House effect via two USB captures (a first
pass isolating the effect generally, then a stepped 0/25/50/75/100%
pass per control) rather than invented — see the original request
context. Two sliders: **Intensity** and **Smoothness**, both 0–100%,
plus the same Enable + sustained Test toggle pattern as Engine/ABS.

Previously only effect types 1–4 were verified against real hardware
captures (the frame diagram literally said "effect type (1..4)"). The
capture confirmed **effect type 9** is real and accepted by the
firmware — sustained valid frames, not silently dropped.

**The wire payload shape is materially different from the other four**,
reverse-engineered from the stepped capture:

```
7e  09  24  12   b1  09  EN   SH   SL   NH  NL   IH   IL   CK
                 │   │   │    └─┴─smoothness u16 BE  └─┴─intensity u16 BE
                 │   │   └ enable (0 = off, 1 = on)
                 │   └ effect type (9 = Road Texture)
                 └ cmd id (0xb1)
```

- For ABS/Lockup/Threshold/Engine, the pad byte is always `0x00` and
  param1 is a per-cycle scaling factor derived from `ParamK`/freq. Road
  Texture repurposes those exact two byte positions (`pad`, `param1`)
  as the high/low bytes of a 16-bit **Smoothness** value instead.
- The "freq" slot (bytes 9–10, `EncodeFreq`'s home for every other
  effect) instead carries a **live noise sample** — confirmed by the
  first capture, where this field oscillated continuously the entire
  time the effect was on, cycling through roughly ±32700 with a
  ~0.7s period, regardless of what Intensity/Smoothness were set to.
- The "amp" slot (bytes 11–12, `EncodeAmp`'s home for every other
  effect) carries **Intensity**.

**Intensity and Smoothness share one encoding**, verified exactly
against all 8 stepped-capture data points (4 per parameter — 25/50/75/
100% each): `raw = round(pct / 100 * 65536) - 1`, clamped to 0 at
`pct <= 0`. This is a different formula shape from every other
reverse-engineered mbooster value in this doc (which use `* 65535` or
`* 65536 / fullscale`) — a "count-1" full-scale pattern instead. See
`MozaMBoosterProtocol.EncodeRoadTextureLevel`.

**Key architectural finding**: comparing the noise field's amplitude
range and oscillation rate across the 4 different Intensity values (and
separately across the 4 Smoothness values) in the stepped capture shows
neither changed the noise signal at all — same ~63500-64000 range, same
~1.3-1.6 peaks/sec regardless of setting. This means **the firmware
applies Intensity and Smoothness to the noise signal internally**;
Pit House just streams a constant-character reference noise waveform
alongside the two percentage values. Practically: this plugin doesn't
need to reverse-engineer Pit House's exact noise algorithm to work
correctly — any reasonable road-like noise generator satisfies the wire
contract, since the actual shaping happens firmware-side. See
`MBoosterEffectSynthesizer.SynthesizeRoadTextureNoise` (a deterministic
value-noise generator, smoothstep-interpolated between pseudo-random
keyframes every 0.35s to loosely match the observed oscillation rate —
explicitly *not* a decoded replica of Pit House's own algorithm, since
that wasn't necessary or knowable from this evidence).

Because the payload shape differs so much from the other four effects,
Road Texture doesn't go through the shared `ProcessEffect`/
`BuildMotorFrame`/`ComputeParam1`/`EncodeFreq`/`EncodeAmp` pipeline —
it has its own `MozaMBoosterProtocol.BuildRoadTextureFrame` and
`MBoosterEffectWorker.ProcessRoadTextureEffect`, mirroring only the
activation-edge/disable-frame handling from `ProcessEffect`.
`BuildDisableFrame` needed no changes — zeroing every field produces
byte-identical output under either payload shape, matching the real
capture's disable frame exactly.

**Update**: Intensity is no longer a constant level while driving — it's
now scaled live by a road-roughness proxy every tick. SimHub's
`StatusDataBase` has **no generic suspension telemetry at all** (no
`Suspension*`/`Damper*`/`RideHeight*` properties — confirmed by
reflecting on `GameReaderCommon.dll` and cross-checking a live catalogue
of ~7700 SimHub property names; zero matches). The only way to get
*real* suspension travel is per-game reflection into each title's own
raw telemetry struct via `StatusDataBase.GetRawDataObject()` (the same
escape hatch `Telemetry/Frames/GameDataSnapshot.cs`'s
`TryReadRawCarCoordinates` already uses for car coordinates) — accurate
but fragile, since it only works for games whose raw struct happens to
expose it and can silently break if a SimHub game-plugin update changes
that struct's shape.

Chose the generic option instead: `StatusDataBase.AccelerationHeave`
(nullable `double`, vertical chassis G-force) is a real, standard field
present across every SimHub-supported game, and bumps produce vertical
acceleration whether or not a game exposes true suspension data. Added
`MBoosterTelemetrySnapshot.SuspensionHeaveG` (sourced from
`nd?.AccelerationHeave ?? 0.0` in `MozaPlugin.cs`'s `DataUpdate`, same
fail-soft null-coalescing style as every other field on that snapshot)
and a `RoadTextureHeaveScaleMaxG = 1.0` constant (1g vertical accel
saturates roughness at 100%) in `MBoosterEffectWorker`. The activation
gate itself is unchanged (`Enabled && GameRunning && VehicleSpeedMs >
0.5`) — what changed is that the transmitted Intensity is now
`userIntensityPct * roughness01` every tick
(`EffectState.RoadTextureRoughness01`, computed in
`UpdateRoadTextureRequest`, applied in `ProcessRoadTextureEffect`)
instead of the raw user percentage. The effect deliberately stays
"active" (streaming frames) continuously while driving rather than
toggling enable/disable edges on every smooth patch — only the
amplitude drops to near-zero, not the frame stream itself, since
flickering the wire-level enable bit on every dip below some fixed
threshold would be indistinguishable from Threshold's already-solved
hysteresis-latch problem, just reintroduced here for no reason. The
sustained Test toggle previews at `RoadTextureRoughness01 = 1` (full
scale) — like Engine's and ABS's tests, there's no live signal to
preview against outside a real drive, so it just uses the raw
configured settings. A matching `AccelerationHeave` test-mode signal
(`Telemetry/TestMode/TestSignalOverrides.cs`, a fast ±0.6g 700ms
oscillation — deliberately much quicker than the other orientation
signals' multi-second sweeps, to actually look like bumps) lets this be
exercised without a live game.

Caveat worth remembering: this is chassis motion, not actual suspension
travel — a curb strike and a mid-corner weight-transfer G-spike look
similar to `AccelerationHeave`. Good enough for "does the road feel
bumpy right now", not a precise physics replica.

**Update**: reversed the "stay active continuously" call above —
feedback was that a continuously-streaming ambient effect whose
amplitude merely dips low on smooth track doesn't read as "the pedal
triggers when you hit a bump/kerb", it reads as background hum. Road
Texture is now a genuine bump/kerb *trigger*, same "silent unless
something is actually happening" contract Lockup/ABS/Threshold already
have. A single bump only spikes `AccelerationHeave` for one or two 20 ms
ticks — too brief to feel as a motor pulse on its own — so
`UpdateRoadTextureRequest` runs a peak-and-decay envelope instead of
using the instantaneous reading directly: fast attack once
`|SuspensionHeaveG|` clears `RoadTextureBumpTriggerG` (0.15g, a
heuristic like Lockup's brake/speed/wheel-slip thresholds — no
hardware capture backs this since it's a host-side telemetry gate, not
a wire value), then exponential release (`RoadTextureBumpDecayTau` =
0.15 s) back toward zero. `EffectState.IntensityRequest` (and so the
activation edge in `ProcessRoadTextureEffect`) now tracks that envelope
crossing ~0.01 instead of "is the car moving", so the effect goes fully
silent (disable frame sent) between bumps and only streams frames for
the duration of each decaying pulse. `RoadTextureRoughness01` is still
the transmitted-Intensity multiplier, just envelope-shaped now instead
of the raw `|heave| / 1g` ratio.

**Update**: added a directional attack transient so a bump/kerb strike
leads with a punchy "hit" rather than easing in from the ambient noise
baseline — a haptics technique (asymmetric onset transients bias
perceived direction more than steady-state amplitude does), not a
protocol-verified behavior. `MBoosterEffectSynthesizer
.SynthesizeRoadTextureNoise` now cross-fades from a fast-decaying
directional spike (`RoadTextureAttackSign`, exponential decay over
`RoadTextureAttackSec` = 80 ms) into the regular ambient noise for the
first 80 ms of `elapsedSec` (time since the activation edge — i.e. time
since *this* bump started, resetting each time the effect goes
silent-to-active, so a sustained kerb only gets one punch on first
contact, not one per ripple). **`RoadTextureAttackSign`'s polarity is an
unverified guess** at "pushes the pedal face toward the driver's foot"
— there's no capture evidence for which raw-sample sign the
firmware/motor treats as which physical direction (prior Road Texture
work only needed to match amplitude/oscillation character, never a
sign's physical meaning). If it feels backwards on real hardware,
negate that one constant.

### Lockup rebuild

Fourth effect rebuilt, and the most direct port of the Engine/ABS
pattern: two sliders, **Frequency (Hz)** (10–100,
`MBoosterEffectSettings.FrequencyHz`, bounds in
`MBoosterUiConstants.LockupFreqMinHz`/`LockupFreqMaxHz`, default 55 —
the exact value from the "known-good" real Pit House capture above,
"Lockup on, 55 Hz, start of ramp") and **Intensity** (0–100%, unchanged
in role), plus Enable + sustained Test toggle. Frequency replaces the
old brake-position mapping (doc § 4: `40 + brake*30`, 40–70Hz) with a
fixed user-set value — same transformation as Engine/ABS, no new wire
evidence needed since Lockup's wire command (effect type 2) was already
verified.

Unlike ABS/Engine/Road Texture, Lockup's *activation* gate is
untouched — it's the most sophisticated of the four (wheel-slip
detection: `brake > 0.8 && vehicleSpeed > 5 && avgWheelSpeed <
vehicleSpeed * 0.3`, with a fallback for games that don't expose
per-wheel speeds). Only the frequency computation changed; the
detection heuristic that decides *whether* to fire is exactly what it
was before. The sustained Test toggle bypasses that heuristic entirely
(same substitution the old 1s pulse used — live brake position stands
in for "is the wheel locking", since there's no live wheel-slip signal
to preview against outside a real drive), live-tracking Frequency/
Intensity every tick like the other three sustained toggles.

### Threshold rebuild

Fifth and last of the original four effects to be rebuilt (Road
Texture, added between Engine and Lockup, was the only genuinely new
one). Four sliders — more than any other effect, since Threshold
already had more moving parts than a simple frequency+intensity pair:

- **Trigger Input Level** (50–100%, new — `MBoosterEffectSettings
  .TriggerLevelPct`, bounds in `MBoosterUiConstants.ThresholdTriggerMinPct`
  /`MaxPct`, default 60) — the brake position at which the effect's
  rising-edge hysteresis latch fires. Replaces the original fixed
  `brake > 0.6` threshold (doc § 4). The release/falling threshold is
  *not* independently configurable — it stays a fixed 30 points below
  the trigger level (`Math.Max(0, triggerLevel - 0.3)`), preserving the
  original hysteresis gap rather than exposing a second slider for it.
  Default 60 exactly reproduces the original threshold. Bounded at 50%
  minimum since a threshold-braking effect firing on a barely-pressed
  pedal defeats the point.
- **Frequency (Hz)** (5–100, `FrequencyHz`, bounds in
  `MBoosterUiConstants.ThresholdFreqMinHz`/`ThresholdFreqMaxHz`,
  default 70 — the exact value from the `ComputeParam1` "known-good"
  reference table above, "Threshold @ 70 Hz -> 44"). Replaces the old
  brake-position mapping (`60 + brake*30`, 60–90Hz) — same
  transformation as the other three fixed-frequency rebuilds.
- **Intensity** (0–100%, unchanged in role).
- **Vibration Decay** (0–100%, new — `DecayPct`, default 20) — how much
  the pulse fades after its initial burst. Generalizes
  `MBoosterEffectSynthesizer.SynthesizeThreshold`'s fixed "20ms full +
  120ms @ 80% + 60ms gap" envelope (protocol-note-verified, same
  "do not modify" caveat `SynthesizeAbs` carries) into `sustain =
  intensity * (1 - decay/100)`. At the default 20, `1 - 0.2 = 0.8`
  exactly reproduces the original verified 80% sustain — same
  "reduces to the exact reference at its default" pattern used for
  ABS's Smoothness. 0 barely decays (near-full strength for the whole
  120ms); 100 drops to silence immediately after the burst, for a
  short, sharp tick instead of a sustained buzz.

The sustained Test toggle shares the *same* rising-edge hysteresis as
real gameplay — `_thresholdLatched` and the trigger/release thresholds
are computed once per tick and used by both the test and real paths,
whichever is active — rather than bypassing it like ABS's/Lockup's
tests bypass their own detection logic. The effect deliberately
doesn't fire on a light tap during testing: it only latches once brake
position crosses the configured Trigger Input Level, same as it would
in real gameplay, so the Test toggle actually verifies whether the
chosen threshold feels right instead of firing on anything. Frequency/
Intensity/Decay are still live-tracked from settings every tick (not
snapshotted); Trigger Input Level's *effect* on the live substituted
"live brake position" is real, not bypassed. This was also the last
effect using the old fire-and-forget 1s `TestPulse` mechanism — with
Threshold's rebuild, that whole mechanism (the `TestPulse` class,
`_thresholdPulse` field, `MBoosterEffectWorker.FireTestPulse`, and
`MBoosterDeviceController.FireEffectTest`) has been deleted entirely,
since nothing constructs one anymore.

### Brake Fade — real Travel End + Max Threshold calibration override, not a vibration effect

Sixth effect added. The motivating request was literal: "make the
pedal feel like it goes long (more travel needed for the same brake
force) when the brakes overheat, and softer/needing more pressure to
reach 100%" — i.e. simulate brake fade as an actual change in pedal
feel, not a buzz representing one. First attempt was a haptic
warning-cue effect (a sustained buzz on a new, uncaptured wire effect
type) — rejected on a second pass in favor of what was actually asked
for: dynamically rewriting TWO real hardware calibrations in lockstep.

- `mbooster-brake-travel-end` (cmdId `0x85`, the same wire command
  `TravelEndMm`'s own Pedal Feel slider writes) — more physical travel
  needed to reach 100%.
- `mbooster-brake-threshold` (cmdId `0xB3`, the same wire command
  `MaxThresholdKg`'s own Sim Input Mapping slider writes) — more
  load-cell force needed to reach 100%. This is the "softer to press"
  half, and it specifically has to be `MaxThresholdKg`, not the
  similarly-named `MaxForceKg` (Pedal Feel) — `MaxForceKg` is
  host-side only with no wire command at all (see "Pedal Feel" below),
  so ramping it would only change what this plugin's own dashboard
  reads, not what the game actually receives. `MaxThresholdKg` is the
  real, hardware-level equivalent, same category of command as
  `TravelEndMm`.

Both restore to the user's configured base value as brake temperature
cools. No new/unverified wire ID involved for either — both reuse
commands already confirmed real via Pit House USB captures.

**This trades a hardware-verification risk for a different one: write
frequency on a calibration channel.** Every other calibration write in
this app — `TravelStartMm`/`TravelEndMm`, `EndstopFrontStiffness`/
`EndstopEndStiffness`, `CurveY`, `MaxThresholdKg` — only fires when a
user drags a slider thumb, i.e. rarely, by design. There's no capture
evidence or protocol-note guidance on whether the device is fine being
written to repeatedly in real time (e.g. EEPROM wear if the firmware
persists every write to flash rather than holding it in RAM until some
explicit "commit"). `MBoosterEffectWorker.UpdateBrakeFadeTravelEnd`/
`UpdateBrakeFadeThreshold` each mitigate this with their own explicit
throttle rather than writing on every 20ms tick:

- `BrakeFadeWriteMinIntervalSec = 0.5` — at most one write every 500ms,
  per calibration (Travel End and Max Threshold throttle independently).
- `BrakeFadeWriteMinDeltaMm = 0.2` / `BrakeFadeWriteMinDeltaKg = 1.0` —
  ignore target changes smaller than this (brake temp telemetry can be
  noisy; not every fluctuation should become a wire write).
- **Exception**: restoring to the exact configured base value (brakes
  cooled below onset, or the effect gets disabled) always goes through
  immediately for both, bypassing both the interval and delta checks —
  this is a safety action, not a cosmetic ramp step, so it's never
  throttled away.

**Each calibration requires its own known-safe value to restore to, or
it individually stays fully inert.** `TravelEndMm`'s and
`MaxThresholdKg`'s shared `-1` sentinel means "not yet set from this
plugin" — the plugin doesn't know what the device's real current
calibration is in that state (see "Pedal Feel" below on the Max Force
fix that hit this same wall for `MaxThresholdKg` specifically). If
`TravelEndMm < 0`, the travel-extension half does nothing; if
`MaxThresholdKg < 0`, the force half does nothing — independently, so
a user who's only configured one of the two still gets that one
working. The user must configure both base values (drag the Pedal Feel
Travel range slider and the Sim Input Mapping Max Threshold slider,
each once) to get the full combined effect.

**Residual shutdown risk, explicitly accepted, not fully closed.** If
the app is force-quit or crashes while brake temp is above onset (an
override is live), the device can be left holding the extended
Travel End / raised Max Threshold indefinitely — there is no watchdog
outside this worker's own tick loop. `MBoosterEffectWorker.Stop()`
makes a best-effort restore attempt (`TryRestoreBrakeFadeOnStop`)
covering both calibrations on the common clean-disconnect/
plugin-shutdown path, but this cannot cover an abrupt process kill. If
the pedal ever feels permanently "long and soft" after an unclean exit,
dragging the respective slider once (which always writes) fixes it
immediately.

**Telemetry**: `MBoosterTelemetrySnapshot.BrakeTempC`, sourced from
`StatusDataBase.BrakesTemperatureMax` (peak across all 4 corners — any
one wheel overheating should trigger fade) in `MozaPlugin.cs`'s
`DataUpdate`, normalized to Celsius via a fail-soft substring check on
`TemperatureUnit` (contains "F" → treat as Fahrenheit and convert;
otherwise assume Celsius — the same "unit gotcha" style already used
for `VehicleSpeedMs`'s km/h→m/s conversion, since the real set of
`TemperatureUnit` string values SimHub's game plugins write isn't
documented anywhere). Confirmed to genuinely exist as a
`StatusDataBase` member (verified by reflecting `GameReaderCommon.dll`
directly) — but per-corner brake temp is less universally populated by
individual SimHub game plugins than basics like `Brake`/`SpeedKmh`, so
0 (unpopulated) is a real possibility for some titles, in which case
`ramp01` never exceeds 0 and the effect never fires.

**Design**, in `MBoosterEffectWorker.UpdateBrakeFade` — called every
tick, computes one shared `ramp01` fraction from brake temp (or 1.0
while the sustained Test toggle is on, or 0.0 while disabled) and
passes it to `UpdateBrakeFadeTravelEnd`/`UpdateBrakeFadeThreshold` so
both calibrations progress in lockstep. Neither touches the
motor-stream slot or vibration priority ladder at all (this is a
completely separate mechanism from the other five effects). One
slider:

- **Onset Temperature (°C)** (300–900,
  `MBoosterEffectSettings.BrakeFadeOnsetC`, bounds in
  `MBoosterUiConstants.BrakeFadeOnsetMinC`/`MaxC`, default 550) — the
  brake temperature above which both calibrations start ramping. Unlike
  Lockup's hardcoded wheel-slip heuristic, this is user-configurable
  because real fade onset varies hugely by pad compound and game (road
  pads ~300°C, race pads 600°C+).

Each calibration ramps linearly from its own base value at
`BrakeFadeOnsetC` to its own cap
(`MBoosterUiConstants.BrakeFadeMaxTravelEndMm` = 47.9mm, explicitly
below `TravelMaxMm`'s 49.7mm slider ceiling per direct instruction, not
derived from any spec; `BrakeFadeMaxThresholdKg` = 200kg, the
theoretical full-scale `MaxThresholdKg`'s own wire encoding uses) at
`BrakeFadeOnsetC + BrakeFadeSpanC` (`BrakeFadeSpanC = 200`, fixed, not
user-configurable — same "one configurable knob, one fixed span"
pattern Threshold's trigger/release hysteresis uses). If a base value
is already at or above its own cap, there's no room to extend and that
calibration is a no-op (never shrinks below the user's own configured
base).

The sustained Test toggle forces both caps for as long as it's on
(same always-allow-off semantics as the other effects' tests — see
`MBoosterDeviceController.SetEngineTestActive`), bypassing Enabled and
the temperature gate — there's no live brake-temperature signal to
preview against outside a real drive with genuinely hot brakes, and
unlike those tests, this one produces a real, physically verifiable
change: the pedal should visibly/physically require more travel and
more force while the test is on, and snap back the moment it's
switched off.

### Custom Effects — user-defined NCalc/SimHub-driven vibration (Experimental)

Seventh addition to the Effects card, and the first that isn't a fixed,
pre-built effect: a user-addable list of custom effects
(`MBoosterDeviceSettings.CustomEffects`, `List<MBoosterCustomEffect>`),
each rendered as its own Expander in the "Custom Effects (Experimental)"
card below the five built-in effects. Each entry has Name, Enable, a
Formula field, an optional Threshold gate, and Frequency/Intensity
sliders. A "+ Add Custom Effect" button creates a new blank entry; each
entry's own "Delete Effect" button removes it.

**Formula editing reuses SimHub's own property-binding UI verbatim** —
the same dual-mode pencil/ƒₓ editor `docs/ncalc-channel-mapping.md`
already built for the telemetry channel-mapper, applied to
`MBoosterCustomEffect.Formula` instead of a channel's `SimHubProperty`.
`MBoosterCustomEffectRow` (`UI/MBoosterCustomEffectRow.cs`) carries its
own copy of the sync/serialize logic
(`Expression`/`ApplyEditedFormula`/`MakeExpression`/
`ApplyStoredToExpression`/`SerializeExpression`) mirroring
`Devices/WheelUi/ChannelMappingRow.cs` line-for-line minus the FSR1/CM1
boundary-stepper baggage that doesn't apply here:

- **Pencil** → the simple inline editor: a filterable, virtualized list
  of every live SimHub property name (`MozaPlugin.GetAllSimHubPropertyNames()`,
  snapshotted once per tab repopulate so every row shares one backing
  list). Picking one commits a bare property path.
- **ƒₓ** → SimHub's own `BindingEditor` dialog (`SimHub.Plugins
  .OutputPlugins.EditorControls`), opened against the shared
  `NCalcEngineBase` (`MozaPlugin.ChannelFormulaEngine`) and a throwaway
  copy of the row's `ExpressionValue` so the dialog never mutates the
  live formula mid-edit — full NCalc `[prop]` expressions or a `js:`
  JavaScript escape, exactly as in the dashboard/channel-mapper formula
  dialog. `SettingsControl.MBoosterAdvancedEditFormula_Click` is a
  byte-for-byte port of `DashboardManagementControl
  .AdvancedEditMapping_Click`, retargeted at the custom-effects row
  collection.

Formula stays a single persisted string (`MBoosterCustomEffect.Formula`)
exactly like `ChannelDefinition.SimHubProperty` — no schema/persistence
change from either editor path, both just write back through the row's
`Formula` setter.

**Two modes**, mirroring the built-ins' pulse-vs-continuous split:

- **Threshold off** (default) — the formula's value (clamped 0..1) scales
  Intensity every tick, continuously, like Engine. The user's formula is
  responsible for producing a sensible 0..1 range.
- **Threshold on** — a pulse trigger: the effect vibrates at the
  configured fixed Intensity whenever the formula's value is `>=`
  Threshold, like Lockup/Threshold. No release hysteresis (unlike the
  built-in Threshold effect's 30-point gap) — this is a v1 simplification.

**Wire transport — no new protocol ID.** There is no verified wire effect
type for arbitrary user content (only 1/2/3/4/9 are confirmed real — see
"Effect IDs" above), so every custom effect is transmitted using the
already-verified **Engine (effect type 4)** frame shape, `ParamKEngine`,
and Engine's own plain-sine waveform
(`MBoosterEffectSynthesizer.SynthesizeEngine`) —
`MBoosterEffectWorker.ProcessCustomEffect`. This means a custom effect
shares Engine's exact wire slot: if a custom effect and the real Engine
effect (or another custom effect) are active in the same tick, only the
last one processed reaches the motor (same latest-wins masking rule as
every other pair in the priority ladder — see "Stream lane" above).
Custom effects are emitted right after Engine/Road Texture and before
Abs/Lockup/Threshold in `Tick()`, so a real safety-relevant braking cue
always overrides an experimental custom effect, but a custom effect can
override ambient Engine vibration.

Capped at `CustomEffectScaleMax = 0.10` (same ceiling as Engine) since a
continuous-mode custom effect can run indefinitely and would otherwise
dominate the other effects, same rationale as Engine's own cap.

**Per-effect state** lives in `MBoosterEffectWorker._customEffectStates`
(`Dictionary<string, EffectState>` keyed by `MBoosterCustomEffect.Id`, a
GUID stable across list edits/reorders). An effect deleted from the
settings list has its worker state pruned each tick
(`UpdateAndProcessCustomEffects`) — if it was still vibrating, a disable
frame is sent first so the last-active waveform can't latch, same rule
every other effect's deactivation edge follows.

**Formula evaluation** reuses `SimHubPropertyResolver.ResolveAsDouble`
(threaded into the worker via a `Func<string, double>` constructor
parameter — `MozaMBoosterRegistry` → `MBoosterDeviceController` →
`MBoosterEffectWorker`, mirroring the existing `settingsLookup`/
`isShuttingDown` injection pattern) rather than the fixed
`MBoosterTelemetrySnapshot` struct the built-in effects read — the whole
point of NCalc formulas is access to *any* SimHub property, not just the
9 fields the snapshot carries. Evaluated live every tick (not cached), so
editing the formula text is felt immediately; a bad/unresolvable formula
reads as `0` (fail-soft, matching every other NCalc consumer in this app)
rather than throwing.

Has the same sustained Test toggle pattern as the five built-ins
(`MBoosterEffectWorker.SetCustomEffectTestSustained`, keyed by effect id
in a `ConcurrentDictionary<string, bool>` rather than one bool field since
the count is unbounded): while on, the effect runs continuously at its
live Frequency/Intensity, bypassing Enabled/Formula/Threshold entirely —
there's no live signal to preview a user's arbitrary formula against
outside whatever it's actually wired to, same substitution Engine's own
test toggle uses. Never persisted (fresh row instances always start
unchecked); explicitly turned off when switching the selected mBooster
device or closing the settings panel
(`SettingsControl.StopAllCustomEffectTests`), same safety net the other
five effects' tests have.

## Calibration surface (experimental)

The protocol note marks the pedal-config command surface (group 35
read, group 36 write) as "likely but unverified" on mBooster firmware.
The plugin ships the full surface in
[`MozaCommandDatabase.cs`](../../../Protocol/MozaCommandDatabase.cs)
under the `mbooster-*` prefix anyway — the user opted in. The UI's
Calibration card (Direction / Min Raw / Max Raw / Read from device /
Apply) surfaces this as experimental with a yellow warning.

## Sim Input Mapping

## Pedal Feel (host-side only)

A card above Sim Input Mapping holds a second 5-point curve,
`InputCurveY` on `MBoosterDeviceSettings`. Unlike `CurveY`, this one has
**no wire command at all** — it's pure host-side shaping, applied in
`MozaMBoosterRegistry.OnHidAxisUpdate` to the raw HID axis position
before it becomes `c.LastHidPosition`, i.e. before it reaches
`MozaData.{Throttle,Brake,Clutch}Position` (game telemetry) *and*
before the effect worker's brake-position test-pulse fallback. `CurveY`
is completely unaffected — it still writes to the device's own
output-curve command exactly as before.

`MozaMBoosterRegistry.EvaluateInputCurve` reproduces
`MozaControls.MozaCurveEditor`'s Catmull-Rom rendering exactly (same
1/6-tangent formula, anchored at the origin), inverted via bisection to
solve X(t)=x for the requested input X — so the applied shaping always
matches what's drawn on screen. Verified: the Linear preset is an exact
identity function (not just at the 5 breakpoints), and the S-Curve
preset interpolates smoothly through all 5 breakpoints. `null` (the
default) means no shaping — existing profiles are unaffected until a
user opens this section.

The same card also has a **Start/End of Travel (mm)** control —
`TravelStartMm`/`TravelEndMm` on `MBoosterDeviceSettings`. Unlike every
other control in this section, this one is a **real hardware
calibration write, not host-side shaping** — it was originally built
host-side (see git history), but the user confirmed this exact control
exists in Pit House itself, and two real Pit House USB captures
(isolating a drag of just the Start thumb to 10/20/30mm, then just the
End thumb to 40/30mm) turned up two previously-undocumented wire
commands: `mbooster-brake-travel-start` (cmdId `0x84`) and
`mbooster-brake-travel-end` (cmdId `0x85`), group 35 read / 36 write,
2-byte ints — same shape as the raw Min/Max calibration commands.
Encoding mirrors `MaxThresholdKg`'s pattern on a fixed 0–53.5mm scale
(53.5 = `TravelMinMm` 3.8 + `TravelMaxMm` 49.7, i.e. the slider's own
bounds) over the 0–65535 range: `raw = round(mm * 65536 / 53.5)`. All 4
capture data points matched within 1 raw unit (~0.001mm), and the
shared 30mm target hit the identical raw value (`0x8f8d`) via both
cmdIds — as solid a cross-check as the `MaxThresholdKg` evidence had.
See `MozaMBoosterProtocol.EncodeTravelMm`/`DecodeTravelMm`.

This is a genuine dual-thumb range slider (`MozaControls.MozaRangeSlider`,
`UI/Controls/MozaRangeSlider.cs`) — no dual-thumb control existed
anywhere in this app before; every other "linked min/max" pair
(Handbrake, Throttle, Brake, Clutch, mBooster's own raw Min/Max
calibration) is two separate `Slider` controls with mutual clamping
via the shared `OnMinMaxSliderChanged` helper. The two thumbs
(`LowValue`/`HighValue`) are bounded to `[3.8mm, 49.7mm]`
(`MBoosterUiConstants.TravelMinMm`/`TravelMaxMm`) and clamped against
each other so their gap always stays within `[3.8mm, 32.1mm]`
(`TravelMinGapMm`/`TravelMaxGapMm`) — dragging one thumb simply can't
push the gap outside that range. `TravelStartMm`/`TravelEndMm` default
to `-1` (same "not yet set / no override" sentinel as
Direction/Min/Max/`MaxThresholdKg`) so a fresh profile never overwrites
whatever calibration is already on the device; the UI seeds the
slider's displayed position at `[3.8, 35.9]` (the widest allowed
window) when the sentinel is unset, without writing anything until the
user actually drags a thumb.

Right below it are two more real hardware writes: **End Stop Stiffness**
(`EndstopFrontStiffness`/`EndstopEndStiffness`, 1–10 each, labeled "Front
Limit Stiffness"/"End Limit Stiffness" — how hard the pedal feels when it
hits the start/end of its physical travel). Reverse-engineered from two
real Pit House USB captures, each sweeping one slider through all 10
values. Unlike every other mbooster command (one cmdId per field), these
two **share a single cmdId (`0xB2`)** with a fixed `0x00` byte and a
selector byte (`0x00` = front, `0x01` = end) ahead of the 2-byte value —
`mbooster-brake-endstop-front`/`-end` in the command database encode this
as a 3-byte `CommandId` (`{0xB2, 0x00, 0x00}`/`{0xB2, 0x00, 0x01}`), the
same "prefix bytes then payload" shape `main-set-spring-gain` already
uses elsewhere. Fixed 1–10 scale over 0–65535: `raw = round(value * 65535
/ 10)`. All 18 capture points (9 per slider, values 2–10) matched exactly
— including two points that landed on an exact `.5` tie and rounded up,
which is why `EncodeEndstopStiffness` explicitly uses
`MidpointRounding.AwayFromZero` instead of the C# default (round-to-even)
that every other `Encode*` helper here implicitly relies on. Same `-1`
sentinel convention as `TravelStartMm`/`TravelEndMm`.

The same card also has two force-based sliders, both host-side only and
both applied in `MozaMBoosterRegistry.ApplyDeadzoneAndMaxForce`, which
runs *before* `EvaluateInputCurve`:

- **Deadzone** (`DeadzoneKg`, 0–40kg, default 0 = off) — force below
  this clamps to 0.
- **Max Force** (`MaxForceKg`, 0–200kg, default 200 = off) — the force
  at which the *input curve's* X-axis reaches 100%. Lets a user who
  never presses past, say, 100kg use the curve's full 0–100% range
  instead of only ever reaching its midpoint.

**Update**: originally both were combined into a kg-space remap that
treated raw 0–100% travel as a fixed 0–200kg full scale. That's wrong
whenever the device's real calibration isn't 200kg — and real Pit
House captures already on file for `MaxThresholdKg` show ~100-125kg,
not 200kg. Raw 100% travel is only ever as many kg as
`MaxThresholdKg` (Sim Input Mapping, a genuine hardware calibration —
see above) currently says it is; past that point the device itself
has already pegged its own output at 100%, so there is no more
resolution left for software to detect additional force. Concretely,
the bug this caused: setting Max Force to 200kg (its slider max) was
silently a no-op whenever `MaxThresholdKg` was lower, because
`hiPercent` degenerated to the same 100% raw-travel point the axis
already saturates at — pressing anywhere near the device's real max
already read as 100% input, never requiring the full 200kg the slider
implied.

Fixed by threading the device's actual `MaxThresholdKg` through as
`ApplyDeadzoneAndMaxForce`'s `fullScaleKg` reference (falling back to
200kg only when `MaxThresholdKg` is still the -1 "not yet set /
no override" sentinel, since the plugin has no way to read the
device's real calibration back — `RequestCalibrationReads`'s read of
`mbooster-brake-threshold` is sent but its response was never wired up
to populate `MaxThresholdKg`, so this remains a best-effort guess until
that's implemented). Force below the deadzone clamps to 0, and
everything between the deadzone and Max Force rescales linearly to
0–100%, same as before — just against the real reference scale instead
of a hardcoded one. (This used to be spelled out in a UI hint,
`Hint_DeadzoneScaleAssumption`, but that string was never actually
filled in for English — only other locales had it translated — so the
English UI showed a blank line; the hint has since been removed
entirely rather than backfilled.) Practical implication for users: Max
Force can only ever *lower* the effort needed to reach 100% below the
device's real Max Threshold — it can't demand *more* force than the
device's own calibration already saturates at, so getting a genuine
"200kg to reach 100%" feel requires setting Max Threshold to 200kg
first (Sim Input Mapping), not just Max Force.

### Traction Control — new effect, no verified wire type

Sixth vibration effect (Brake Fade, added earlier, isn't one — see
above), added as a direct mirror of ABS: same oscillating-pulse
waveform (`MBoosterEffectSynthesizer.SynthesizeTractionControl`, an
exact copy of `SynthesizeAbs`'s formula in its own function), same
sustained Test toggle semantics, driven by SimHub's `TCActive`
telemetry (`MBoosterTelemetrySnapshot.TcActive`) the same way ABS is
driven by `AbsActive`. Two sliders (Frequency, Intensity) — no
Smoothness; a later pass (see "Frequency range + Smoothness removal"
below) widened Frequency to 10–100Hz and dropped the Smoothness slider
entirely (fixed internally at `smoothness01 = 1`), so it's no longer a
complete ABS mirror on the UI side either.

The one place it can't be a pure mirror: ABS has a real, capture-
verified wire effect type (1); Traction Control has never been seen in
a Pit House capture, so there's no confirmed protocol ID to send. It
reuses Engine's already-verified frame shape (effect type 4) instead —
the same reuse Custom Effects make (see `ProcessCustomEffect` above) —
via its own `ProcessTractionControlEffect`, rather than inventing an
unconfirmed ID and risking the firmware misinterpreting it. Practical
consequence: Traction Control competes with the real Engine effect and
any active Custom Effects for that one wire slot; it's placed last in
the `Tick()` priority ladder (same tier as ABS/Lockup/Threshold) so it
always wins over ambient vibration when active.

The sustained Test toggle substitutes live throttle position for
`tcActive` (gated at 80% throttle, mirroring ABS's 60%-brake gate) —
this needed a new `MBoosterTelemetrySnapshot.Throttle` field and a
`MBoosterEffectWorker.EffectiveThrottle` helper (mirroring
`EffectiveBrake`), since the snapshot previously only carried Brake.

### Wheel Spin — Traction Control's physics-heuristic sibling

Seventh vibration effect, added with the exact same slider config as
Traction Control (Frequency 10–100Hz, Intensity, no Smoothness) and the
same Engine-wire-slot reuse (`ProcessWheelSpinEffect`,
`MBoosterEffectSynthesizer.SynthesizeWheelSpin` — another exact copy of
`SynthesizeAbs`'s formula), but a deliberately different trigger:
rather than reading SimHub's `TCActive` flag (which reflects whether
the *game's own* TC system chose to intervene), Wheel Spin runs its own
raw wheel-slip physics heuristic in `UpdateWheelSpinRequest` — the
acceleration-side counterpart to Lockup's braking-side heuristic (see
"Lockup rebuild" above):

```
isSpinning = throttle > 0.8 && vehicleSpeed < 40 && avgWheelSpeed > vehicleSpeed * 1.3
```

gated to `vehicleSpeed < 40` m/s (~144 km/h) since wheelspin is a
low/mid-speed launch or corner-exit phenomenon, not something that
should fire from flooring the throttle at speed in a tall gear. Same
fallback rationale as Lockup: `AvgWheelSpeedMs` is currently always 0
in this plugin (`MozaPlugin.DataUpdate` hardcodes it — no per-wheel
speed telemetry is wired up yet), so the primary condition never
actually fires yet; a fallback (`avgWheelSpeed <= 0 && throttle > 0.9 &&
vehicleSpeed < 40`) carries the real behavior today, same as Lockup's
own fallback does. The sustained Test toggle substitutes live throttle
position (via the same `EffectiveThrottle` helper Traction Control
uses), gated at 80% throttle.

This makes ABS/Traction Control (simple game-flag effects) and
Lockup/Wheel Spin (raw physics-heuristic effects) a deliberate
symmetric pair — one braking-side, one acceleration-side, in each
category.

### Frequency range + Smoothness removal (Traction Control)

Shortly after Traction Control was added, its Frequency range was
widened from 5–30Hz (ABS's range) to 10–100Hz, and its Smoothness
slider was removed entirely — `UpdateTractionControlRequest` now fixes
`smoothness01 = 1` internally instead of reading a (now nonexistent)
`SmoothnessPct` slider value. Wheel Spin was built to this same,
already-updated slider config from the start (see above) rather than
the original ABS-mirrored one.

### Gear Shift — the first genuine one-shot pulse effect

Eighth vibration effect, and the first one in this pipeline that
doesn't fit the "level-triggered continuous, re-evaluated every tick"
model every other effect (built-in or Custom) uses. It's a pulse: fire
briefly on a detected gear change, then self-terminate, even though the
underlying telemetry signal that triggered it is itself only true for
one tick.

**Detection** mirrors the wheelbase's own gear-shift feature
(`MozaPlugin.CheckGearshiftEvent`, see "Effects card UI" above) almost
exactly: a string-latch edge detector on SimHub's `Gear` telemetry
(`string`, values `"R"`/`"N"`/`"1"`–`"N"`), with a warm-up guard (the
first observed value is just recorded, never fires) so plugin/session
startup doesn't produce a false shift event. Computed once, globally,
in `MozaPlugin.DataUpdate` with its own independent latch
(`_lastMBoosterGearString` — separate from the wheelbase's
`_lastGearString` and the AB9 shifter's `_lastAb9GearString`, so none
of the three interfere with each other), producing two new
`MBoosterTelemetrySnapshot` fields: `GearChanged` (true for exactly the
one tick the gear string differed from the previous tick) and
`GearIsNeutral` (whether the *new* gear is "N"/"0"). Unlike the
wheelbase's version, no debounce or neutral-suppression decision is
made at this global layer — those are per-mBooster-device settings
(`VibrateOnNeutral`, `DebounceMs`), applied independently by each
device's own `UpdateGearShiftRequest`, same as every other Gear Shift
setting.

**The pulse mechanic itself** (`MBoosterEffectWorker
.UpdateGearShiftRequest`/`GearShiftPulseDurationSec` = 150ms) is new
machinery, not a reuse of anything Lockup/Threshold already had —
investigation confirmed neither of those is a true one-shot despite
having "pulse" or "burst" in their envelope descriptions: both stay
`Active` and keep re-evaluating for as long as their gate condition
holds, only deactivating when the gate goes false again. Gear Shift
can't work that way since `GearChanged` reverts to false on the very
next tick regardless of anything. Instead, `UpdateGearShiftRequest`
reads back `EffectState.Active`/`ElapsedSec` (already tracked by
`ProcessGearShiftEffect`, mirroring `ProcessTractionControlEffect`) to
know "am I already mid-pulse, and for how long" — if so, it keeps
requesting nonzero intensity until `GearShiftPulseDurationSec` elapses,
independent of the raw edge; only once that latch clears does it look
for a *new* `GearChanged` edge to start another pulse. The Debounce
window is tracked separately, in a plain `_gearShiftDebounceRemainingSec`
field decremented each tick — it has to live outside `EffectState`
since it must survive across the pulse's own on/off cycle (`ElapsedSec`
resets to 0 every time the pulse (re)activates).

Same Engine-wire-slot reuse as Traction Control/Wheel Spin (no
verified wire effect type of its own), and a new waveform,
`MBoosterEffectSynthesizer.SynthesizeGearShift` — a short oscillating
burst (`0.7 + 0.3*sin(phase)`, so it never crosses zero mid-burst)
multiplied by a linear decay envelope over the pulse duration, rather
than the plain continuous wave every other effect in this file uses.

Slider config, at the user's explicit request, mirrors the wheelbase's
own gear-shift feature in full rather than staying minimal like
Traction Control/Wheel Spin: Enable, Test, Frequency (10-100Hz),
Intensity, **and** a Vibrate on Neutral toggle + Debounce (ms) slider
(0-1000ms, 50ms steps — same bounds/step as
`GearshiftDebounceSlider`). The Test toggle bypasses the pulse/debounce/
neutral machinery entirely, same substitution every other effect's
test makes — there's no live "gear just changed" signal to press
against outside a real shift.

### Live position indicator on the curves

Both the Pedal Feel input curve and the Sim Input Mapping output curve
show a live dot on the spline (plus a dashed guide line down to the
X axis) tracking the pedal as it's pressed, at 30Hz
(`SettingsControl.UpdateMBoosterCurveMarkers`, which sets
`MozaCurveEditor.LiveX` on both editors — this used to also drive a
standalone position bar in the Pedal Role card, since removed in favor
of these two curve markers).

`MozaCurveEditor.LiveX` is a data-space X (0–100, same domain as
`XAxisLabels`); `NaN` (default) hides the indicator. `Recompute()`
maps it to a pixel X via the same `XAxisLabels`/`XLabelFractions`
correspondence used for tick labels (linear interpolation between
whichever two labels bracket it), locates which cached Bezier segment
contains that pixel X, then inverts that segment's X(t) via bisection
— the same approach as `EvaluateInputCurve`, just in pixel space — to
read off the exact point ON the spline. The two editors get different
values so each shows what it actually receives:

- **Input Curve**: `LastRawPercentPreCurve` — post deadzone/max-force,
  pre-`InputCurveY` (what this curve's evaluator receives).
- **Output Curve**: `LastHidPosition * 100` (post-`InputCurveY`, i.e.
  what's sent onward to game telemetry) — an approximation of what the
  device's own firmware curve sees, since that runs on the device's
  own raw sensor reading, a separate signal path we don't otherwise
  observe.

A separate card (Pit House calls this class of setting "input
mapping") holds the Pit House-parity controls, all still under
`MBoosterDeviceSettings`:

- **Sensor Output Ratio** (`SensorOutputRatioPct`, 0–100%) — blend
  between the mBooster's angle sensor (0%) and its load cell (100%).
  Wired to `mbooster-brake-angle-ratio` (cmdId 26) — the mBooster-side
  twin of the wheelbase Brake tab's own "Sensor Ratio" slider
  (`pedals-brake-angle-ratio`). Live-pushes on every drag.
- **Max Threshold (kg)** (`MaxThresholdKg`) — Pit House's "load cell
  force at which output reaches 100%" setting. **Reverse-engineered
  from two real Pit House USB captures** (not in any protocol note —
  see below). Wire command `mbooster-brake-threshold`, cmdId `0xB3`,
  group 35 read / 36 write, 4 bytes — but unlike every other 4-byte
  mbooster command, this one is a **big-endian unsigned int, not an
  IEEE-754 float**. It encodes kg on a fixed 0–200kg scale over the
  same 0–65535 range used elsewhere, mirroring the exact
  `EncodeFreq`/`ComputeParam1` "value × 65536 / 200" pattern already
  used for the motor effects in this same file:
  `raw = round(kg × 65536 / 200)`. See
  `MozaMBoosterProtocol.EncodeThresholdKg`/`DecodeThresholdKg`.

  **Evidence**: a capture isolating a drag to exactly 4kg produced
  `7e 05 24 12 b3 00 00 05 1f 61` → raw `1311`, and
  `round(4 × 65536 / 200) = 1311` exactly. A second, earlier capture
  (target value not recorded) produced raw `41287`, which decodes to
  `125.9998kg` — matching an independently-reported real Pit House
  setting of ~125kg to within rounding error. Two independent
  confirmations is about as solid as unofficial reverse-engineering
  gets, but it's still unconfirmed by Moza — the in-UI warning says so.
- **Output curve** (`CurveY`, 5-point) — moved here from Calibration.
  `MozaCurveEditor`-driven, mirrors the wheelbase pedal Y curves.
  Like Direction/Min/Max, always writes through the
  `mbooster-throttle-y1..y5` slot regardless of the device's assigned
  role — the mBooster is a single physical axis, so role-specific
  slots are reserved for symmetry with the wheelbase's three-pedal
  command surface but unlikely to matter on real hardware (see
  `SetMBoosterCurveY` in `SettingsControl.xaml.cs`).

  Unlike every other curve in the app, this one's nodes are also
  **draggable horizontally** (`MozaCurveEditor.AllowHorizontalDrag`,
  set only on this instance), so a node can be moved to a lower X and
  "100% output" reached before "100% input" — the same idea as Pedal
  Feel's Max Force slider, applied to the output side instead. The
  wheelbase's own FFB curve has real `base-ffb-curve-x1..x4` write
  commands for this; **no equivalent exists for the mbooster y-curve**
  (nothing found in captures or the command table), so this is
  implemented purely host-side: `MBoosterDeviceSettings.CurveX` stores
  each node's dragged X (null = untouched, fixed 20/40/60/80/100), and
  `MozaMBoosterRegistry.ResampleCurveAtFixedBreakpoints` — via the new
  `EvaluateCurveArbitraryX`, the same Catmull-Rom/bisection approach as
  `EvaluateInputCurve` but generalized to non-fixed node X — resamples
  the whole (CurveX, CurveY) shape at the wire protocol's actual fixed
  breakpoints before every push (`PushResampledMBoosterCurve` in
  `SettingsControl.xaml.cs`, called on every X or Y change, plus
  `ApplyMBoosterToHardware` on detect). Beyond the last dragged node,
  the resample returns that node's Y (flat plateau) — verified
  numerically: with `CurveX` untouched, resampling is the exact
  identity; dragging the last node from X=100 to X=60 (Y unchanged)
  makes breakpoints 60/80/100 all resample to that node's Y.

| Command                       | Group (R/W) | CmdId | Bytes | Type  |
|-------------------------------|-------------|-------|-------|-------|
| `mbooster-throttle-dir/min/max` | 35 / 36   | 1/2/3 | 2     | int   |
| `mbooster-brake-dir/min/max`    | 35 / 36   | 4/5/6 | 2     | int   |
| `mbooster-clutch-dir/min/max`   | 35 / 36   | 7/8/9 | 2     | int   |
| `mbooster-{throttle,brake,clutch}-y1..y5` | 35 / 36 | 14-29 | 4 | float |
| `mbooster-{throttle,brake,clutch}-output` | 37 / —  | 1/2/3 | 2 | int   |
| `mbooster-brake-angle-ratio`    | 35 / 36   | 26    | 4     | float |

All targeted at device id `0x12` on the mBooster's own CDC port. The
plugin's [`MozaResponseParser`](../../../Protocol/MozaResponseParser.cs)
disambiguates from wheelbase Main / AB9 Main via the
`busHint: "mbooster"` argument set in `MBoosterDeviceController.OnConnectionMessage`.

## HID identity reconciliation

The registry walk in [`MozaPortDiscovery`](../../../Protocol/MozaPortDiscovery.cs)
surfaces an `InstanceId` per CDC composite (e.g. `a&399b951f&0&0000`).
HidSharp's `HidDevice.DevicePath` contains a similar parent-USB
instance segment between the second and third `#` separators (e.g.
`\\?\HID#VID_346E&PID_0008&MI_02#a&399b951f&0&0002#{4d1e55b2-...}`).
The plugin's `MozaHidReader.ExtractUsbParentInstance` extracts that
segment as the HID-side identity.

**Resolved by real-hardware logs** (a support bundle showing the
position bar stuck at 0 despite the device showing "Connected"): the
"shared prefix, differing only in trailing interface index" theory
above is **wrong**. A real capture showed:

```
HID: 9&1bd82a3a&0&0000
CDC: 8&1709245b&0&0000
```

No shared segment at all — Windows assigned the HID and CDC
interfaces of the same physical device completely unrelated instance
IDs (different hash, not just a different trailing index). An
exact-match lookup in `MozaMBoosterRegistry.OnHidAxisUpdate` never
pairs these, so the position bar never updates even though the CDC
side detects fine, and no amount of prefix-stripping can fix it for
this device.

`MozaMBoosterRegistry.OnHidAxisUpdate` tries three things in order:

1. **Exact match** — works if Windows ever does assign the same
   instance ID to both interfaces (kept for hardware/driver versions
   where it might).
2. **`FindByInstancePrefixLocked`** — strips the trailing `&NNNN`
   segment from both sides and matches on the remainder. Kept as a
   fallback for the case the original theory *did* describe, even
   though it's now known not to be the common case.
3. **Single-device fallback** — if neither match and exactly one
   mBooster is registered, pair the HID identity to it unconditionally;
   there's no ambiguity with only one device. This is what actually
   fixes the common single-mBooster case given the finding above.

Each path logs once per HID identity at Info level so a support-bundle
log confirms which one resolved the device (`"...via instance-prefix
fallback..."` / `"...via single-device fallback..."`). With two or
more mBoosters that never exact- or prefix-match, there is currently
no way to disambiguate which HID stream belongs to which CDC device —
`LogUnmatchedHidIdentityOnceLocked` logs a Warn (visible in SimHub's
regular log, not just the bundle) so that gap is at least visible
rather than silent.

### Pedal Trace — all-pedals overlay, not just the selected mBooster

The Effects card's **Pedal Trace** sparkline (`MBoosterPedalTraceViz`)
originally plotted only the currently selected device's own HID
position, single-series, reset to a flat baseline on device switch (see
above). It's now a fixed three-series overlay — Brake (red), Throttle
(green), Clutch (blue) — showing every connected pedal's live position
at once, independent of which device's tab is open, and no longer
cleared on device switch since the history is no longer tied to a
single device.

`MozaControls.BandwidthSparkline` gained a third series
(`ThirdSamples`/`ThirdBrush`/`ThirdFillBrush`, same shape as the
existing `In`/`Out` pair) to make this possible with two series
reserved for it already. `SettingsControl` feeds it from
`_data.BrakePosition`/`ThrottlePosition`/`ClutchPosition` — the same
merged 0-100 values the Inputs tab's pedal bars already use — rather
than from the mBooster registry, so pedals that aren't mBoosters at all
(e.g. a dedicated load-cell brake) still show up on the graph. The
curve editors' own live-position markers (`MBoosterInputCurveEditor`/
`MBoosterCurveEditor`) are unaffected — those stay tied to the
currently selected device, matching what its own curve sliders shape.

`RedBrush`/`McuFillBrush` and `GreenBrush`/`MotorFillBrush` reuse the
existing theme pairs (same red/green the temperature graph's MCU/Motor
series use). `BlueBrush`/`BwThirdFillBrush` are new — no prior accent
color in the theme was a true blue distinct from Cyan.

## Source-of-truth files in this repo

- Protocol primitives — [`Protocol/MozaMBoosterProtocol.cs`](../../../Protocol/MozaMBoosterProtocol.cs)
- Effect synthesis — [`Devices/MBoosterEffectSynthesizer.cs`](../../../Devices/MBoosterEffectSynthesizer.cs)
- Settings types — [`Devices/MBoosterTypes.cs`](../../../Devices/MBoosterTypes.cs)
- Per-device controller — [`Devices/MBoosterDeviceController.cs`](../../../Devices/MBoosterDeviceController.cs)
- 50 Hz effect worker — [`Devices/MBoosterEffectWorker.cs`](../../../Devices/MBoosterEffectWorker.cs)
- Multi-device registry — [`Devices/MozaMBoosterRegistry.cs`](../../../Devices/MozaMBoosterRegistry.cs)
- HID extension — [`Protocol/MozaHidReader.cs`](../../../Protocol/MozaHidReader.cs) (`MozaHidClass.MBooster` path)
- Profile storage — [`UI/MozaProfile.cs`](../../../UI/MozaProfile.cs) (`MBoosterSettings` dict)
- UI tab — [`UI/SettingsControl.xaml`](../../../UI/SettingsControl.xaml) (`MBoosterTab`) + handlers in `SettingsControl.xaml.cs` under "mBooster tab — multi-device"
