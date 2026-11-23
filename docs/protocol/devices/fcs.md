## FCS — MOZA Farming Control System (PID `0x0030`)

A farming-sim control panel: throttle lever with buttons, a base plate with
buttons (six of them RGB-lit), knobs and toggles, and a right-hand joystick ("rod") with a twist
axis and a thumbstick. It is its own USB device (VID `0x346E` PID `0x0030`), not
a sub-device behind a wheelbase. Pit House calls it **S14** internally
(`Domain::S14Program`, `qrc:/images/S14/…`); the product name is
`Farming Control System`.

**Sources.**

| Capture (`usb-capture/fcs/pcap/`) | Content |
|---|---|
| `FCS Capture 2.pcapng` | Device power-on → Pit House 1.4.2.7 beta connect → every settings control stepped once (calibrate all four, the three axis-mode toggles + thresholds, light mode, brightness, six colours, standby effects 1–5 + intervals) |
| `fcs attempt 2.pcapng` | Earlier session (power-on count 13 vs 14): light mode, throttle / Z / XY calibration, colours, a mid-capture device reset and reconnect, a cancelled thumbstick calibration |
| `possible FCS capture.pcapng` | **Not the FCS** — two USB hubs and a Ploopy Knob, no MOZA traffic |
| `../fcs.webp` | Pit House FCS page screenshot (button numbering 1–56, axis widgets) |

Pit House names and enums come from the 1.4.2.7 beta binary (`MOZA Pit House.exe`
embedded QML, extracted with `tools/qt-rcc-extract.py`). Decoder:
[`tools/fcs-decode`](../../../tools/fcs-decode) (identity, named `0x4E` timeline,
reassembled firmware log, HID axis/button activity).

No game ran in either hardware capture. The telemetry light mode, the right-rod
types and the effect list were recorded later from Pit House against the
simulator (`usb-capture/fcs/sim/pithouse-gadget-20261118-061305.jsonl`).

### USB

Device descriptor: USB 2.0, class `0xEF`/`0x02`/`0x01` (IAD composite), EP0 64,
bcdDevice `0x0100`, self-powered, 100 mA.

| String | Value |
|---|---|
| iManufacturer | `Gudsen` |
| iProduct | `MOZA Farming Control System` |
| iSerialNumber | 24 upper-case hex chars = the 12-byte MCU UID that group `0x06` returns |

| Interface | Class | Endpoints |
|---|---|---|
| MI_00 | CDC ACM (comm) | `0x81` interrupt IN, 8 B, 16 ms |
| MI_01 | CDC data | `0x02` bulk OUT 64, `0x82` bulk IN 64 — the MOZA serial bus |
| MI_02 | HID (no boot subclass) | `0x03` interrupt OUT 64 / 1 ms, `0x83` interrupt IN 64 / 1 ms |

Pit House sets line coding (9600 8N1 in `FCS Capture 2`) and clears DTR/RTS;
the device answers regardless.

#### HID report

The report descriptor (52 bytes, read on enumeration in `fcs attempt 2`):

```
05 01 09 04 a1 01 85 01 05 01 09 30 09 31 09 33 09 34 09 35 09 32
15 00 27 ff ff 00 00 75 10 95 06 81 02 05 09 19 01 29 80 15 00 25 01
95 80 75 01 81 02 c0
```

Joystick, report ID 1, six 16-bit axes (logical 0–65535) in the order
**X, Y, Rx, Ry, Rz, Z**, then 128 one-bit buttons → a **29-byte** input report:

| Bytes | Field | Control (moved during the matching calibration in both captures) | Rest |
|---|---|---|---|
| 0 | report ID `0x01` | | |
| 1–2 | X | joystick X | 32767 |
| 3–4 | Y | joystick Y | 32767 |
| 5–6 | Rx | thumbstick X | 32767 |
| 7–8 | Ry | thumbstick Y | 32767 |
| 9–10 | Rz | throttle lever | 65535 |
| 11–12 | Z | joystick twist (Z) | 32767 |
| 13–28 | buttons 1–128 | bit *n−1* = button *n* | 0 |

The device streams the report at 1 kHz. Buttons (including external rods and
axis-as-button output) are plain HID buttons that games bind directly, so the
host needs no button map.

### Bus identity

The FCS answers on dev `0x12` (replies from `0x21`). Pit House runs the standard
identity cascade ([`../identity/wheel-probe-sequence.md`](../identity/wheel-probe-sequence.md))
**twice back to back** on connect:

| Group | Request | Reply | Meaning |
|---|---|---|---|
| `0x09` | — | `00 0b` | Pit House `networkId` = 11 |
| `0x04` | `00 00 00 00` | `01 02 0a 00` | `appFirmwareVersion` 1.2.0.10 (matches the firmware log `FCS:APP version: 1.2.0.10`) |
| `0x06` | — | 12 bytes | `mcuUid` (same bytes as the USB serial string) |
| `0x02` | `00` | `02` | Pit House `deviceId` = 2 |
| `0x05` | `00 00 00 00` | `01 02 00 04` | `bootFirmwareVersion` 1.2.4.0 (same byte order as `0x04`) |
| `0x07` | `01` | `FCS # S14` | model name |
| `0x0F` | `01` | `RS21-S14-MC FCS` | MCU part string |
| `0x11` | `04` | `04 01` | |
| `0x08` | `01` / `02` | `RS21-S14-HW TP-C` / `U-V10` | hardware version |
| `0x10` | `00` / `01` | two 16-char halves | serial |

Pit House's `ALL_FmwHwCompatTab.json` lists the product code
`[RS21-S14-HW_TP-CU-V10][FCS][11][2]` (category `Farming Control System`): the
hardware string, the model, then the group `0x09` value (11) and the group
`0x02` value (2).

Heartbeat: group `0x00` (empty) → `0x80` (empty). Pit House sends it every
500 ms. There is no session layer, no tier definition and no `0x43` traffic.

### Connect sequence and keep-alive

What Pit House does on connect (a host can do the same):

1. Identity cascade (Pit House runs it twice, 0.1–0.7 s apart).
2. Read every `0x4E` setting plus `status rightRodType` (21 frames in one burst).
3. Write the settings it wants. In `FCS Capture 2` it wrote every setting; after
   the device reset in `fcs attempt 2` it wrote only the three LED colours that
   differed from its stored values.
4. Re-read all settings every 2.0 s.

The device does not need the 2 s settings poll. Against the simulator, stretches
of several minutes with only the 500 ms heartbeat kept the connection up. On
real hardware the poll ran throughout both captures, so a heartbeat-only
connection hasn't been checked there.

The device sometimes drops a reply (11 of 2466 requests in `FCS Capture 2`);
Pit House resends after ~300 ms.

### Group `0x4E` — settings, calibration, status

All FCS configuration is on group `0x4E` (reply `0xCE`). Payload:

```
[op] [param] [index]? [value…]
```

| op | Meaning | Reply |
|---|---|---|
| `01` | write | echo of the request |
| `02` | read — value bytes sent as zeros, same width as a write | same layout with the stored value |
| `03` | calibration control `03 <cal> <action>` | echo |
| `04` | status read `04 <what> [<index>] 00` | value in the last byte |

#### op `01` / `02` parameters

| param | index | value | Pit House parameter | Values (power-on value in capture 2) | Firmware param (Table 9) |
|---|---|---|---|---|---|
| `01` | 0 / 1 / 2 | 1 B | `thumbstickXYMode` / `joystickXYMode` / `joystickZMode` | 0 = Axis, 1 = Button (0) | 56–57 / 58–59 / 60 (`axis_map_button_enable[0..4]`) |
| `02` | 0 / 1 / 2 | u16 BE | `…ButtonModeThreshold` (same index order) | trigger point (`0xCCCC` = 52428 = 80 %) | 51–52 / 53–54 / 55 (`axis_button_bound[0..4]`) |
| `03` | — | 1 B | `buttonGroupState` (UI "Button Light Mode") | 0 = Off, 1 = Non-Telemetry, 2 = Telemetry (2) | 7 |
| `04` | — | 1 B | `buttonGroupBrightness` | 0–100 (80) | 6 |
| `05` | 0–5 | R G B | colour of LED 1–6 = the six square buttons on the base plate (UI 21–26). Pit House's "LED Sequence" overlay numbers them in a U: 1–3 down the left column, 4–6 up the right | (`56 F7 FC`) | 14–31 (3 per LED) |
| `06` | — | 1 B | `buttonGroupStandbyMode` (UI "Button Light Standby Effect") | 0 Off, 1 Constant, 2 Breathing, 3 Color cycle, 4 Rainbow, 5 Quicksand, 6 Pulse (2). Pulse needs app firmware ≥ 1.2.7.0; on 1.2.0.10 Pit House offers only 0–5 | 8 |
| `07` | 2–6 | u16 BE, ms | standby interval of effect *index*: `Breath`, `Colored`, `Rainbow`, `Quicksand`, `Pulse` `…Interval` | (3000 for all five) | 9–13 |

Notes:

- **Axis-mode index → stick.** Index 1 = joystick XY (Pit House's "Joystick XY
  Mode → Button" writes `01 01 01 01`). Index 2 is the single-axis firmware
  group (joystick Z), which leaves index 0 = thumbstick. The same order applies
  to the thresholds (param `02`).
- **Threshold** is u16 BE over the full axis range (`0xCCCC` = 80 %). It only
  matters in Button mode.
- **Colours.** The device stores one 6-slot colour table, used by the Constant
  and Breathing effects. A host that offers separate "button" and "standby"
  palettes has to write the right one when it changes the effect (Pit House
  writes its Button Light palette with Constant and its Standby palette with
  Breathing). The other effects are firmware animations.
- **Intervals.** Writing an interval also re-applies the running effect
  (`set led frequency effect:N`). Pit House's UI defaults are 3000 / 1200 /
  1000 / 1000 / 3000 ms (Breath … Pulse); the device stored 3000 for all five.

#### Telemetry light mode (params `08` / `09`)

Write-only frames that drive the LEDs live while the light mode is Telemetry
(`03 = 2`). Recorded 2026-11-18 from Pit House against the simulator, with
DiRT Rally 2.0 running and one RPM effect
(`usb-capture/fcs/sim/pithouse-gadget-20261118-061305.jsonl`).

| Frame | Payload | Meaning |
|---|---|---|
| mask | `01 09 <lit> <owned>` | `lit`: LEDs on; `owned`: LEDs under telemetry control. Bit 0 = LED 1 … bit 5 = LED 6 |
| colours | `01 08 [<idx> R G B]…` | colour per LED, index 0–5. LEDs 1–5 go in one frame (22-byte payload), LED 6 in a second frame |

Behaviour seen:

- From game detection until telemetry data arrives: `lit=0x3F owned=0x00`,
  once per second. With data: `owned=0x3F`, `lit` follows the effect.
- Frames go out on change, `09` first and then `08` if colours changed. An
  unchanged mask is repeated every 1.0 s. While revving, updates came
  20–60 ms apart.
- Unlit LEDs keep the standby colour (`#56F7FC`) in the colour frames.
- The RPM effect lit LEDs 1–2 green, 3–4 red, 5 magenta. At the limit it
  switched all six to red (one colour frame) and blinked them by toggling
  `lit` between `0x3F` and `0x00` every 125 ms.
- The sender doesn't wait for a reply: the frame rate was the same with
  and without the simulator echoing.
- When the game exits the frames just stop. The last one was the routine 1 Hz
  `01 09 00 3f` (none lit, all owned); no release frame and no settings write
  follow.

#### op `03` — calibration

`03 <cal> <action>`, action 1 = start, 0 = stop (finish), 2 = cancel.

| cal | Pit House control | Firmware axes | HID axes moved |
|---|---|---|---|
| `01` | `thumbstick_calib` | 4, 5 (`max`/`mid`/`min`) | Rx, Ry |
| `02` | `joystickXy_calib` | 1, 2 (circle: needs clockwise **and** counter-clockwise sweeps) | X, Y |
| `03` | `throttle_calib` | 0 (`max`/`min`) | Rz |
| `04` | `joystickZ_calib` | 3 (`max`/`mid`/`min`) | Z |

After **stop**, read the per-axis result (`04 04 <axis> 00`) for each axis of
that control; `1` = pass. Pit House reads ~190 ms after stop, in the order
below. While a calibration runs the user moves the control through its full
range. The only live position is the HID report; nothing about position travels
over the serial link.

| `<axis>` | Pit House control |
|---|---|
| `00` | `throttle_checkCalib` |
| `01`, `02` | `joystickX_checkCalib`, `joystickY_checkCalib` |
| `03` | `joystickZ_checkCalib` |
| `05`, `04` | `thumbstickX_checkCalib`, `thumbstickY_checkCalib` (X is index **5**, read first) |

The firmware stores each axis as three float32s (max, mid, min) in Table 9
params 32–49 (`32 + 3·axis`), logged as raw IEEE-754 bits.

In both XY calibrations the firmware logged a failure
(`xy calib fail dir=0 bins=1` / `xy calib fail samples cw=0 ccw=1441`, then
`axis xy calib invalid`), yet `checkCalib` for axes 1 and 2 returned `1`. A
cancelled calibration (`03 01 02`, thumbstick) logged `axis_4/5 calib cancelled`
and was not followed by a check. A failed calibration only means the axis has
no travel until it is calibrated again — nothing on the wire beyond the
`checkCalib` value.

#### op `04` — status

| `<what>` | Read | Values |
|---|---|---|
| `03` | `04 03 00` → `04 03 <v>`, polled every 2 s | `rightRodType`: 0 not connected, 1 built-in joystick (default), 2 MHG, 3 MA3X, 4 MH16 |
| `04` | `04 04 <axis> 00` → `04 04 <axis> <ok>` | calibration result, see above |

`rightRodType` read `0` in the sweep right after the device reset in `fcs attempt 2`
and `1` at the next poll, after the firmware logged `right_rod is connected: fighter`.
Types 2–4 are external rods (MHG, MA3X, MH16) plugged in place of the built-in
joystick. The rod type changes nothing else on the wire: with the simulator
switching it live (1 → 2 → 3 → 4 → 1), Pit House sent no extra reads, writes or
calibration commands for any type. It's an informational value; which HID
buttons an external rod produces is still open.

### Firmware debug log (group `0x0E`)

Unsolicited, dev `0x21`, payload `05` + up to 63 ASCII bytes; one log line can
span several frames, and several short lines can share a frame. Same
`param_manage.c` format as other MOZA MCUs ([`../heartbeat.md`](../heartbeat.md)).
Content seen:

- Power-on banner: `farm_router.c:154 Usb Port`, `right_rod is connected: fighter`,
  `FCS power_on_num: N`, `FCS:APP version: 1.2.0.10`, stored `axis_2..5_max/_min`,
  `axis_button_bound[0..4]` / `axis_map_button_enable[0..4]`, then
  `Table 9, Param 61 Written: N` (power-on counter) and the reset causes
  (`system reset: ext_pin_reset`, `power_reset`).
- Every 60 s: `Throttle Heart Beat Log`, `FCS run_time: Ns`,
  `left_rod connect_state:1, connect_type: 1`, `right_rod: connect_state:1, connect_type: 1`.
- Every setting write: the effect (`set brightness:80`, `set mode:2`,
  `set effect:2`, `led_3 set color: r:86, g:247, b:252`,
  `AxisMappingButton group:1 enable:1`) and the EEPROM write
  (`Table 9, Param N Written: V`).
- Calibration: `axis_N calib start`, `axis N calib complete`,
  `axis N max: …, mid: …` / `max: …, min: …`, `calib cancelled`, XY failures.

Firmware Table 9 layout (from these lines): 6 brightness, 7 light mode,
8 standby effect, 9–13 intervals of effects 2–6, 14–31 LED RGB, 32–49 axis
calibration, 51–55 `axis_button_bound[0..4]`, 56–60
`axis_map_button_enable[0..4]`, 61 power-on count.

### Plugin status

Not yet verified against real hardware or the simulator.

- **Lane.** `MozaDeviceCategory.Fcs`, claimed by the standalone peripheral lane
  (`StandalonePeripheralDescriptor.Fcs`). It sends the group `0x00` heartbeat
  every 500 ms, reads identity (`0x04`, `0x06`) and every `0x4E` setting once
  the heartbeat is answered, re-reads `rightRodType` every 2 s, and keeps the
  `0x0E` log in the diagnostics firmware log under source `fcs`. There is no
  identity cascade and no 2 s full settings poll.
- **Commands.** `fcs-*` in `MozaCommandDatabase`; each op-`01`/`02` parameter is
  a `-set`/`-get` pair, because read and write differ by the op byte.
- **Settings.** LED settings (light mode, brightness, the six colours, standby
  effect, intervals) are per game, in `MozaFcsDeviceProfile` plus the SimHub
  device's extension settings. On connect a setting is written only after its
  readback arrives, and only if it differs (shared `ReadbackReconcileCache`).
  Axis modes and thresholds are device-stored and written on user action only.
  The threshold is shown as 1–100 % (Pit House's scale); the interval slider is
  125–5000 ms (Pit House's bounds). Pulse is offered only on app firmware
  ≥ 1.2.7.0.
- **Calibration.** Start / Done (stop) / Cancel per control. 190 ms after stop
  it reads `checkCalib` for that control's axes and shows the result (pass only
  when every axis returns `1`).
- **Device definition.** "MOZA Farming Control System", six button LEDs,
  deployed on first detection. The SimHub device page has Inputs and LEDs tabs.
- **Telemetry LEDs.** SimHub's buttons channel, while light mode is Telemetry
  (`2`). The frames are `01 08` colours (5 + 1 per change, changed LEDs only)
  and `01 09 <lit> 3F`. The mask repeats at 1 Hz. When SimHub stops feeding
  colours, one `01 09 00 00` releases the LEDs. Unlike Pit House, colours go
  out before the mask.
- **HID.** `MozaHidClass.Fcs`: axes and buttons go to `MozaData.Fcs`, never the
  steering, paddle, pedal or wheel button fields.

### Simulator

`moza-simulator` profile `fcs` (`sim/profiles/standalone/fcs.py`, engine
`sim/engines/fcs.py`) answers the identity cascade, heartbeat and every `0x4E`
op above with stored state, emits the firmware log lines for writes and
calibration, and drives the 29-byte HID report when the gadget has the HID
function. Verified 2026-11-18 against Pit House 1.4.2.7 under the
`linux-simracing-utils` Wine: Pit House connects, shows the FCS page and runs
the same connect sequence and 2 s poll as on real hardware. Use a local gadget —
that Wine only reports a VID/PID for real USB serial devices, so a tty0tty port
registered with `pithouse_wine.py` shows up as `vid:0 pid:0` and is ignored:

```
sudo bash sim/setup_rig.sh --local fcs          # host: /dev/ttyACMn + /dev/hidrawN
python3 sim/gadget_manager.py run fcs /dev/ttyGS0
# restart the Wine session (wineserver -k), then launch Pit House
```

For a remote Windows Pit House, drop `--local` and attach over usbip.
