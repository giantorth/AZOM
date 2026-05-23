## Wheelbase (Device `0x13` / 19)

### Group `0x28` / `0x29` (40 / 41) — Settings

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| limit | `01` | 2 | int | Steering angle limit |
| ffb-strength | `02` | 2 | int | |
| inertia | `04` | 2 | int | |
| damper | `07` | 2 | int | |
| friction | `08` | 2 | int | |
| spring | `09` | 2 | int | |
| speed | `0A` | 2 | int | |
| road-sensitivity | `0C` | 2 | int | |
| protection | `0D` | 2 | int | Hands-off protection strength |
| protection-mode | `2D` | 2 | int | |
| gearshift-vibration | `2E` | 2 | int | "Gearshift vibration intensity" PitHouse slider. Range 0..5 (0 = effect disabled, 5 = max). Sets the strength of the rumble fired by `gearshift-event` (cmd 0x76, see Group `0x2D` below). Verified 2026-05-10 (`bridge-20260510-115644.jsonl` t=41600.748 `2E 00 01` = 1, t=41700.520 `2E 00 05` = 5). |
| equalizer1 | `0E` | 2 | int | |
| equalizer2 | `0F` | 2 | int | |
| equalizer3 | `10` | 2 | int | |
| equalizer4 | `11` | 2 | int | |
| equalizer5 | `14` | 2 | int | |
| equalizer6 | `2C` | 2 | int | |
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

### Group `0x2A` (42) — Calibration / Music (startup chime)

Group 42 is used for both writes (calibration, music set) and reads (music get). `Dir` column applies.

> **Capture-verified** (2026-05-05) from R25 base. See `usb-capture/startupchime/`.

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
| mcu-temp | `04` | 2 | int | |
| mosfet-temp | `05` | 2 | int | |
| motor-temp | `06` | 2 | int | |

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

Notes:
- Trigger and intensity are independent commands. `gearshift-event` (group
  `0x2D`) is the per-shift trigger; `gearshift-vibration` (group `0x29` cmd
  0x2E) is the persisted strength setting. The wheel reads the setting
  from EEPROM and reacts to the trigger from RAM.
- The on-wheel display gear digit comes from the dashboard telemetry
  (session 0x02 channel 15) — that's a separate render path and does NOT
  drive the rumble.
