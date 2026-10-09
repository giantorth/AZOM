# Motion platform — firmware log and register sweeps (group `0x0E`)

Group `0x0E` is the same parameter-manager / debug-console channel the wheelbase uses
([`../periodic/group-0x0E-param-reader.md`](../periodic/group-0x0E-param-reader.md)); the
motion controller is by far its heaviest user. Decoders: `tools/motion-decode log` (lines),
`tools/motion-decode registers` (sweeps).

## Log lines — `0E [dev] 05 <ascii>` (device → host, unsolicited)

Each frame carries up to 63 ASCII bytes after the `05`; a line ends with `\n` and longer lines
span frames (reassemble per device id). The controller logs as `0x21`, each actuator as its
reply id (`0x61`/`0x71`/`0x81`/`0x91`…).

### Controller (`dev 0x21`)

| Line | When |
|------|------|
| `curr_mode:N target_mode:M` | every 5 s, with `Controller ErrCode:E` before it and one `Slave(n) curr_mode:N  error_code:E` per actuator after it |
| `port[i] is_connected: 1, dev_id: D` (×4) then `access_dev_num: 4, axis_connected_num: 4` | every 15 s, 0-based port index, D = slave low nibble |
| `[INFO]serial_reply_app.c:324 dev_support_num:4, dev_connected_num:8` | on connect / re-enrol |
| `[INFO]serial_reply_app.c:333 port:N axis:R` (×8) | after the host reads or writes the port→role table |
| `[INFO]safety_control.c:422 Set next mode:N` | on every `0x1E/03` write |
| `[INFO]safety_control.c:472 Controller Set curr mode:N` | when the controller reaches a mode (5 precedes every target) |
| `[INFO]controller_model.c:183 ModelMode Set CtrlMode:C` + `[INFO]common_wrapper.c:57 controller_mode change to [C]` | model mode: 1 (standby / self-test), 2 (mode 3), 5 (game), 6 (self-check) |
| `[ERRO]diag_svr_event.c:86 error_code 259 occurs` / `[ERRO]err_diag_svr.c:69 Travel Not Check` | entering mode 7 |
| `[INFO]common_wrapper.c:57 pitch_offset:[0.00854], roll_offset:[0.00014]` / `selfCheckSuccess` / `error_code 259 clear` | mode 7 done |
| `[INFO]controller_model.c:57 Device Type : 1, eeprom data: 1` / `:61 Output Displacement : a b c d` / `:67 Calib Length : 500.00000,Width : 500.00000` | entering mode 3, then every 60 s in game mode (displacements in mm, four actuators — e.g. `8.48902 -3.39236 -7.96388 3.91752`) |
| `[ERRO]controller_cmd.c:502 Set device type to : 1` | each `0x1E/0A 01` |
| `[INFO]param_manage.c:340 Table 12, Param N Written: V 0.00000` (N = 5..36) and `Table 2, Param 26 Written: 1` | lighting-table writes (`0x1E/0B`) and device type |
| `LightManageConfig_SetCmd: Global: switch=1, brightness=NaN` | first `0x0B` write |
| `[INFO]diag_svr_event.c:108 error_code 269 clear` | Motion Manager connects after a boot with error 269 |
| `Controller ErrCode:8` / `ErrCode:4` | boot fault pending / actuators missing |
| `=========== Fault Pre-Data (100 samples) ===========`, `[1] I_total=… P0=… P1=… P2=… P3=…` … `[100]`, `=========== Fault Post-Data (100 samples) ===========`, `[101]` … `[200]` | once, at a cold start with a stored fault |

### Actuators (`dev 0x61` …)

| Line | When |
|------|------|
| `[INFO]motor_mode.c:28 Set MotorMode to:12` / `[INFO]motor_wrapper.c:58 MotorMode From 0 to 12` | motors enabled (leaving standby); `to:0` / `From 12 to 0` on entering standby |
| `[INFO]safety_control.c:298 Actuator Set curr mode:N` | follows the controller's mode changes (5, then 7 / 3 / 1 / 13 …) |
| `V=48.99370, I=0.04431, id=-0.20666, iq=-0.49649, mcu_t=28.63667, mos_t=33.85234, motor_t=21.20425` | every 60 s: bus voltage (48 V supply), phase current, d/q currents, MCU / MOSFET / motor temperatures in °C |

## Register sweeps — `0E [dev] 00 00 [idx]` → `8E [dev'] 00 00 [idx] [u32 BE value]`

The host walks a short index range on the controller (~0.9 Hz per index) and on each actuator
(~1 Hz), exactly like Pit House's wheelbase parameter reader.

| Device | Indices | Values seen | Reading |
|--------|---------|-------------|---------|
| controller | 1, 2, 3, 4, 5, 6, 19 | `1E 02 1C 26 19 1C` / idx 19 = `19` | idx 1 = 30, 2 = 2, 3 = 28, 4 = 38, 5 = 25, 6 = 28 — constant across captures, so probably configuration/limits rather than live temperatures |
| actuators | 1, 2, 3, 4, 5, 6, 7, 9, 10 | `1E 02 1C 24 [19..1D] 1A 0B/0C 15/19 1C/1D` | idx 5 (25–29) and idx 9/10 (21–29) vary per unit and per capture; idx 7 = 11 or 12 |

The varying actuator indices are in the range of the logged temperatures (mcu_t 26–33, mos_t
31–39, motor_t 17–25 °C) but no index matches a logged value exactly at the same instant, so the
mapping is left open.

## Notification handshake — `0x0E/03` ↔ `0x0E/04`

`0E 21 03 01 [code] 00 01` from the controller, `0E 12 04 01 [code] 01` from the host, `8E 21 04
01 [code] 00 01` echo. See [`modes-and-control.md`](modes-and-control.md) § notification.
