## Wheelbase (Device `0x13` / 19)

### Group `0x28` / `0x29` (40 / 41) — Settings

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| limit | `01` | 2 | int | Steering angle limit. Raw = degrees / 2. Firmware floors the stored value at 60° (raw `0x1E`) — lower writes read back as 60°. Verified live 2026-08-05 via write-then-readback; PitHouse's own UI stops at 90°. |
| ffb-strength | `02` | 2 | int | |
| inertia | `04` | 2 | int | |
| damper | `07` | 2 | int | |
| friction | `08` | 2 | int | |
| spring | `09` | 2 | int | |
| speed | `0A` | 2 | int | |
| road-sensitivity | `0C` | 2 | int | The 10-band EQ UI's "sensitivity" presets (fw ≥ 1.2.10.10) write this as `10 + 4×N` for UI preset N (0–10 → wire 10–50), together with a canned EQ curve. See EQ section below. |
| protection | `0D` | 2 | int | Hands-off protection strength |
| protection-mode | `2D` | 2 | int | |
| gearshift-vibration | `2E` | 2 | int | "Gearshift vibration intensity" PitHouse slider. Range 0..5 (0 = effect disabled, 5 = max). Sets the strength of the rumble fired by `gearshift-event` (cmd 0x76, see Group `0x2D` below). Verified 2026-05-10 (`bridge-20260510-115644.jsonl` t=41600.748 `2E 00 01` = 1, t=41700.520 `2E 00 05` = 5). |
| equalizer1 | `0E` | 2 | int | 5 Hz band in the 10-band EQ UI (fw ≥ 1.2.10.10); 0–500 (%). See EQ section below. |
| equalizer2 | `0F` | 2 | int | 15 Hz band; 0–500 (%) |
| equalizer3 | `10` | 2 | int | 25 Hz band; 0–500 (%) |
| equalizer4 | `11` | 2 | int | 40 Hz band; 0–500 (%) |
| equalizer5 | `14` | 2 | int | 60 Hz band; 0–500 (%) |
| equalizer6 | `2C` | 2 | int | 100 Hz band; 0–100 (%) — UI keeps the old cap on this band |
| equalizer7 | `32` | 2 | int | **10 Hz band** (fw ≥ 1.2.10.10) — new in the 10-band EQ; 0–500 (%) |
| equalizer8 | `33` | 2 | int | **30 Hz band** (fw ≥ 1.2.10.10); 0–500 (%) |
| equalizer9 | `34` | 2 | int | **50 Hz band** (fw ≥ 1.2.10.10); 0–500 (%) |
| equalizer10 | `35` | 2 | int | **80 Hz band** (fw ≥ 1.2.10.10); 0–500 (%) |
| torque | `12` | 2 | int | |
| natural-inertia | `13` | 2 | int | Hands-off protection |
| natural-inertia-enable | `16` | 2 | int | |
| max-angle | `17` | 2 | int | |
| ffb-reverse | `18` | 2 | int | |
| speed-damping | `19` | 2 | int | |
| speed-damping-point | `1A` | 2 | int | |
| soft-limit-strength | `1B` | 2 | int | |
| soft-limit-retain | `1C` | 2 | int | |
| soft-limit-stiffness | `1F` | 2 | int | |
| temp-strategy / performance-output | `1E` | 2 | int | "Performance output" in newer PitHouse builds. 0 = Reserved, 1 = Full. Verified live 2026-05-10 (`bridge-20260510-115644.jsonl` t=41902.486 `1E 00 01`, t=42166.594 `1E 00 00`). |
| ffb-curve-x1 | `22 01` | 1 | int | FFB linearization curve X point 1 |
| ffb-curve-x2 | `22 02` | 1 | int | |
| ffb-curve-x3 | `22 03` | 1 | int | |
| ffb-curve-x4 | `22 04` | 1 | int | |
| ffb-curve-y1 | `22 05` | 1 | int | |
| ffb-curve-y2 | `22 06` | 1 | int | |
| ffb-curve-y3 | `22 07` | 1 | int | |
| ffb-curve-y4 | `22 08` | 1 | int | |
| ffb-curve-y5 | `22 09` | 1 | int | |
| ffb-curve-y0 | `22 0A` | 1 | int | No read or write group (both -1) — not usable |
| ffb-disable | `FE` | 2 | int | |

PitHouse's wheelbase **FFB-curve / "deadzone"** slider drives these `22 0x` points
directly — it has no dedicated deadzone register. In
`soft-restart.calibrate-paddles.interpolation-0-5-10-0.deadzone-0-5-10-0-10-0.pcapng`
each slider position re-emitted the y1–y4 points (`22 05`…`22 08`) as a 4-write
burst (6 bursts for the 6 slider values). These map to the existing
`base-ffb-curve-*` command-DB entries; no new command is needed.

#### FFB effect equalizer — 10 bands (fw ≥ 1.2.10.10)

The 2026-07 PitHouse build expands the FFB Effect Equalizer from 6 to 10
bands: 5 / 10 / 15 / 25 / 30 / 40 / 50 / 60 / 80 / 100 Hz. The four new
bands (10 / 30 / 50 / 80 Hz → cmds `32 33 34 35`) are interleaved into the
original six registers, which carry the 5 / 15 / 25 / 40 / 60 / 100 Hz
labels in the new UI. Values are raw percent as BE u16 (500 % → `0x01F4`);
sliders run 0–500 % except the 100 Hz band, which stays 0–100 %.

PitHouse rewrites **all 10 registers** on any single slider change
(~50 ms burst, old IDs first, then `32..35`), the base ACKs each write on
group `0xA9`, and the page-open read sweep covers the new IDs on group
`0x28`/`0xA8`. Note that PitHouse applies its local profile over the
base's stored values rather than adopting read-backs.

The page also offers eleven **"sensitivity" presets** (UI 0–10). Each is
a one-shot macro: it writes `road-sensitivity` (`0C`) = `10 + 4×N` plus
a canned 10-band curve that opens from a low-pass shelf (preset 0:
5/10 Hz = 100, rest tapering to 0) to fully flat (preset 10: all bands
100, the only preset that raises the 100 Hz band). No dedicated
"sensitivity" register exists — read-backs return the individual values
written. The selector additionally shows a **"customized"** position
when the current `0C` + EQ tuple matches no preset row — derived
UI state only, no wire traffic.

Wire-verified per-band and per-preset 2026-07-31 on base fw 1.2.10.10 —
full transition + preset matrices, capture reference, and the band-1
relabel note (the old 6-band UI labeled `0E` as 10 Hz; the new UI moves
that label to `32` and calls `0E` 5 Hz) in
[`../findings/2026-07-31-eq-10-band.md`](../findings/2026-07-31-eq-10-band.md).

### Group `0x2A` (42) — Calibration / Music (startup chime)

Group 42 is used for both writes (calibration, music set) and reads (music get). `Dir` column applies.

> **Capture-verified** (2026-05-05) from R25 base. See `usb-capture/startupchime/`.
> Re-verified 2026-07-31 under the 2026-07 PitHouse build (base fw 1.2.10.10):
> enable, index-set, preview, and the ~2 s state polling are byte-identical;
> still 10 chimes.

| Command | ID | Dir | Bytes | Type | Notes |
|---------|----|-----|-------|------|-------|
| calibration | `01` | W | 2 | int | |
| music-preview | `43 00` | W | 1 | int | Plays chime at index once (immediate audible feedback) |
| music-index-set | `43 01` | W | 1 | int | Persists selected startup chime (1–10) |
| music-index-get | `43 02` | R | 1 | int | Returns active chime index |
| music-enabled-set | `43 03` | W | 1 | int | 0 = disable, 1 = enable startup chime |
| music-enabled-get | `43 04` | R | 1 | int | Returns enabled state |
| music-volume-set | `44 00` | W | 1 | int | 0x00 (mute) – 0xFF (max) |
| music-volume-get | `44 01` | R | 1 | int | Returns current volume |
| feedforward | `40` | W | 2 | int | **Partner-SDK / iRacing.** Wheel writes EEPROM (table not yet pinpointed). iRacing posts CoAP `/MOZARacing/ProductDevice/{id}/Feedforward` (4-byte LE int32, value 356 observed); PitHouse forwards as BE16 in the last two payload bytes — `7E 03 2A 13 40 [HI] [LO] [chk]`. Wheel acks `7E 03 AA 31 40 [HI] 00 [chk]`. One-shot per session (capability probe). Capture-verified 2026-05-23 (`iracing-pithouse-{udp,serial}.pcapng`, `tools/correlate_coap_serial.py`). |
| high-freq-torque | `41` | W | 2 | int | **Partner-SDK / iRacing.** Same wire shape as `feedforward` (different cmd byte). Wheel writes EEPROM Table 11 Params 13 + 14 — firmware `[INFO]param_manage.c:340` log echoes the pair on group `0x0E` (debug-log channel). One-shot per session. |

**Chime index range:** 1–10 (0x01–0x0A). 10 built-in chimes on R25 base.

**Default volume:** 0x17 (23 decimal, ~9%).

**PitHouse workflow:** read state → `music-index-set(N)` → `music-preview(N)` → periodic re-read.

**Frame example — set chime 5 and preview:**
```
7E 03 2A 13 43 01 05 [chk]   ← index-set = 5
7E 03 2A 13 43 00 05 [chk]   ← preview chime 5
```
Both produce ACK response: `7E 03 AA 31 43 0x 05 [chk]` (echo with group|0x80, device nibble-swapped).

### Group `0x2B` (43) — Status (read-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| state | `01` | 2 | int | |
| state-err | `02` | 2 | int | |
| mcu-temp | `04` | 2 | int | **BE16, raw ÷ 100 = °C.** Measured `0F 3C` = 3900 → 39.0 °C (bundle `W603C6RV`). |
| mosfet-temp | `05` | 2 | int | Same scaling. Measured `0E A0`…`0E AD` → 37.4–37.6 °C, dithering a few counts. |
| motor-temp | `06` | 2 | int | Same scaling. Measured `09 D8`…`09 E2` → 25.2–25.3 °C. |
| live-torque | `07` | 2 | int | **Live motor torque.** BE16 biased by +500, 0.1 Nm per count — `torque_Nm = (raw - 500) / 10.0`, sign carries direction. Read-only live measurement; nothing is written to enable it. PitHouse's **torque curve** panel is a pure reader: opening it starts an ~18 Hz poll of this register alone (no temps alongside) and closing it stops the poll, so the poll's presence *is* the feature's on/off state. |

**`live-torque` (`07`) verification** — real R16 Ultra, PitHouse bridge capture
`bridge-pithouse-features-20260828-061341.jsonl`:

```
h2b  7E 03 2B 13 07 00 00 [chk]     ← poll, ~18 Hz (50 ms nominal)
b2h  7E 03 AB 31 07 01 F4 [chk]     ← reply: reg echo + BE16 value (0x01F4 = 500 = 0.0 Nm)
```

| Wheel state | raw | Nm |
|---|---|---|
| idle, untouched (±1 count dither) | `499`–`500` | 0.0 |
| hand-wiggled, both directions | `467`…`538` | −3.3 … +3.8 |
| held static against a stop (PitHouse UI read 6 Nm) | `440` plateau, 437–445 over 40 s | −6.0 |

The static hold is what distinguishes torque from angular velocity: steady force
with no rotation holds a *sustained* offset, where a velocity channel would
return to the 500 bias.

The sign is only which way the wheelbase is pulling. PitHouse's torque graph
plots the **unsigned magnitude** — `Math.Abs(raw - 500) / 10.0` — because torque
is torque regardless of direction; that is why a raw `440` displays as `6 Nm`
and not `-6 Nm`. Consumers wanting a plottable value should do the same.

Full scale (the clip point) is not yet measured — nothing in this capture drove
the value beyond ±60 counts.

### Group `0x2C` (44) — Motor run-state / partner-API extension (write-only)

Partner-SDK extension surface — sole observed command is the iRacing run-state
toggle. Distinct write group from `0x2A` and `0x29` because the firmware
routes it to a different parameter table.

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| motor-run-state | `01` | 2 | int | **Partner-SDK / iRacing.** iRacing posts CoAP `/MOZARacing/ProductDevice/{id}/SetMotorRunState` (4-byte LE int32, value `1` observed). Host forwards as BE16 in the last two payload bytes — `7E 03 2C 13 01 [HI] [LO] [chk]`. Wheel acks **zero-length** — `7E 00 AC 31 [chk]` — and the firmware then writes EEPROM Table 5 Param 6, plus flips `input_appmode` and `motor_mode debug_mode` (the latter two surface as `[INFO]base_model.c:192` and `[INFO]motor_mode.c:44` log echoes on group `0x0E`). One-shot per session. |

**Frame example** (CoAP value `01 00 00 00` = 1):
```
7E 03 2C 13 01 00 01 CF                ← host→base (SetMotorRunState=1)
7E 00 AC 31 [chk]                       ← base→host (zero-length ACK)
[INFO]base_model.c:192 input_appmode:…  ← debug-log echo on group 0x0E
[INFO]motor_mode.c:44 debug_mode disabled
[INFO]param_manage.c:340 Table 5, Param 6 Written: 1087065796 (≈ 6.0 as float)
```

Capture-verified 2026-05-23 from paired UDP/CDC trace
(`iracing-pithouse-{udp,serial}.pcapng`,
`tools/correlate_coap_serial.py`).

### Group `0x2D` (45) — Sequence Counter / Discrete Events (write-only)

Group 45 carries two distinct types of host→base traffic:

1. The **sequence counter** (`F5 31 …`), sent at ~42 Hz during driving as a
   frame-sync counter.
2. **Discrete event** frames sent aperiodically, fire-and-forget — the wheel
   does not echo them on group `0xAD`.

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| sequence-counter | `F5 31` | 4 | int | Last byte monotonically increments each send; frame-sync counter at ~42 Hz |
| gearshift-event | `76` | 2 | int | Fixed payload `00 01`. Fired once per gear change detected by PitHouse. Wheel firmware uses the configured `gearshift-vibration` (cmd 0x2E intensity) to drive a brief motor pulse. Verified 2026-05-10 (`bridge-20260510-115644.jsonl`): 112 occurrences across the capture, all identical body `76 00 01`, no `0xAD` echoes. The same trigger fires regardless of shift direction or gear value — direction/magnitude is not encoded. |
| lfe-effect | `77` | 9 | array | **Low-frequency effects** (fw ≥ 1.2.10.10) — host-rendered stream for the complex gearshift / engine / ABS effects. Replaces `gearshift-event` (0x76) for gearshift on LFE firmware. See below. |

Notes:
- Trigger and intensity are independent commands. `gearshift-event` (group
  `0x2D`) is the per-shift trigger; `gearshift-vibration` (group `0x29` cmd
  0x2E) is the persisted strength setting. The wheel reads the setting
  from EEPROM and reacts to the trigger from RAM.
- The on-wheel display gear digit comes from the dashboard telemetry
  (session 0x02 channel 15) — that's a separate render path and does NOT
  drive the rumble.

#### Low-frequency effects — cmd `0x77` (fw ≥ 1.2.10.10)

Recent base firmware adds three **host-rendered** vibration effects (PitHouse
"LFE"): a complex gearshift vibration, a continuous engine vibration, and an ABS
effect. Each has an on/off toggle, a test button, and frequency + intensity
sliders. All three are streamed as cmd `0x77` on group `0x2D` (dev `0x13`),
write-only/fire-and-forget — the same group as the classic `gearshift-event`
(`0x76`), which it **replaces** for gearshift on LFE-capable firmware (a capture
with LFE active shows only `0x77`, never `0x76`).

Reverse-engineered byte-exact from `lfe-{gearshift,engine,abs}-*.pcapng` +
`lfe-in-game-test.pcapng` (base fw 1.2.10.10). Frame (15 bytes on the wire):

```
7E 0A 2D 13 77  p0 p1 p2 p3 p4 p5 p6 p7 p8  CK    (len 0x0A = cmd + 9 payload)
```

| byte | field | encoding |
|------|-------|----------|
| p0 | const | `0x00` |
| p1 | effect id | `0x00` gearshift · `0x01` engine · `0x02` abs |
| p2 | play flag | `0x01` playing · `0x00` staged/idle |
| p3:p4 | period BE16 | `floor(ParamK / freqHz)` (oscillation period, ms). ParamK: engine `1000`, abs `2000`. Gearshift + off/preview frames use a fixed placeholder `0x000F`. |
| p5:p6 | frequency BE16 | `round(freqHz / 200 × 65536)` — identical to the mBooster `EncodeFreq` |
| p7:p8 | intensity BE16 | `round(pct / 100 × 65535)` — identical to the mBooster `EncodeAmp` |

**Only effect ids 0/1/2 are live channels** — probing ids 3..15 with cmd `0x77`
(via `tools/lfe_probe.py`) produces no vibration, so the base runs exactly three
concurrent oscillators. The base **sums** whatever the channels are doing
(verified on hardware), so the three can be repurposed as summed harmonic partials
for a richer engine (the plugin's "Additive Engine" preset) — they run on
independent phases, so they beat against each other for free roughness.

An all-zero payload disables the effect. Verified values: 100 Hz→`0x8000`,
50 Hz→`0x4000`, 30 Hz→`0x2666`, 20 Hz→`0x199A`, 18 Hz→`0x170A`, 5 Hz→`0x0666`;
100 %→`0xFFFF`, 50 %→`0x8000`, 1 %→`0x028F`. Frequency is computed from the
**nominal slider Hz** (not the round-tripped `freq16`): the period is
`floor(K/nominalHz)` — byte-exact across all 1992 engine frames; `round` diverges.
(ABS period is +1 raw at 15/18 Hz — an imperceptible timing hint on a
host-modulated effect; only 4 ABS capture points exist.)

Per-effect behaviour (from `lfe-in-game-test.pcapng`):

- **Engine** (p1=`01`) — continuous ~30 Hz stream while driving. Frequency
  sweeps with RPM: the slider is the *redline* pitch and the audible frequency is
  `slider × clamp(rpm / maxRpm)` (observed 5→100 Hz as RPM rose); intensity held
  at the slider. Same model as the AB9 host engine vibration.
- **ABS** (p1=`02`) — streamed only while ABS is active. Frequency fixed at the
  slider; intensity modulated by a host pulse waveform (observed 11→50 % with the
  slider at 50 %).
- **Gearshift** (p1=`00`) — a short burst (~2 frames) on each gear change; fixed
  placeholder period `0x000F`, freq + intensity from the sliders.

**Firmware detection.** The numeric base firmware version is read on **group
`0x04`** addressed to device `0x12` (main). The request carries a 4-byte zero
payload: `7E 04 04 12 00 00 00 00 A5` (length `0x04`). Reply `84 21 <4 version
bytes>`, e.g. `84 21 01 02 0A 0A` = `1.2.10.10`. Same probe shape as the wheel's
`device-type` (group `0x04`), and **distinct** from `sw-version` (group `0x0F`),
which returns the hardware model string (`RS21-D05-MC WB`), not a numeric version.

> **Not universal.** An **R12** (`RS21-D07-MC WB`, PID `0x0006`) on LFE-capable
> firmware (owner-reported 1.2.10.13) never answers this request. Bug bundle
> `2026-08-14_KS_1.5.4_65HZBQJT`: the probe goes out once at `19:13:49.355` and
> no `84 21` frame appears in ~6 minutes of capture, while dev `0x12` is
> demonstrably live either side of it (`a2 21` at `.329`, `87 21`
> `"R12 Black # MOT-…"` at `.348`) and the *same* group-`0x04` request shape is
> answered within 60 ms by the wheel (`84 71`), the pedals (`84 91`) and the
> shifter (`84 a1`). The plugin therefore issues three probes at base detect —
> the canonical 4-zero-byte request at `0x12`, the same request in its
> zero-length form (`7E 00 04 12`, the shape `0x19`/`0x1A` answer), and the
> query at dev `0x13` (`base-fw-version-b`) — and takes the first answer, with
> `0x12` winning if more than one lands. Which of the three an R12 answers, if
> any, is not yet confirmed on hardware.
>
> The detect-time burst rides the `BaseAmbientProbed` latch and so fires exactly
> once; in that bundle it was also *never retried*, because the read was tracked
> against a `PendingResponseTracker` that `MozaPlugin.Instance` had not published
> yet (fixed by constructor injection in the same commit). Since a lost reply
> costs the whole session — no LFE, no 10-band EQ, no "Wheelbase LFE haptics"
> device — `PollStatusCore` now re-issues all three probes on its 5 s tick while
> `BaseFwVersion == 0`, up to 5 rounds (~25 s past detect), on the pipe that owns
> the base. The resolved state is reported in the diagnostics bundle's
> `=== Base identity ===` section (`FW (numeric)` + which probe answered), which
> is what distinguishes a **silent** base from a genuinely old one — both read
> `LFE support: no`.

The 4 version bytes are in **wire order `[major, minor, build, patch]`** — MOZA's
PitHouse UI displays the last two swapped: wire `01 02 18 09` is shown `1.2.9.24`,
wire `01 02 0A 0A` is `1.2.10.10`. The same mapping holds for the **wheel's**
version reply (dev `0x71`): wire `01 02 09 07` displays as 1.2.7.9
(confirmed after the 2026-07-31 wheel firmware update — see
[`../findings/2026-07-31-wheel-firmware-update-protocol.md`](../findings/2026-07-31-wheel-firmware-update-protocol.md)). The plugin packs them in display/semver order
(`major.minor.patch.build`, swapping the last two wire bytes) so a `>=` compare
orders correctly — packing in raw wire order would misgate (`0x01021809` >
`0x01020A0A`, i.e. the older 1.2.9.24 would wrongly out-rank the LFE 1.2.10.10).
Registered as `base-fw-version` (DeviceType `"main"`, so the dev-0x12 reply routes
correctly); the LFE UI/emission gate is version ≥ 1.2.10.10
(`MozaData.BaseSupportsLfe`).
