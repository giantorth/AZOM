## Steering Wheel (Device `0x17` / 23)

This covers all Moza steering wheels, including models with integrated display screens (e.g. formula-style wheels that show speed, gear, lap time). Live game telemetry (group `0x43`) is sent here by Pithouse — confirmed by USB capture. Wheels with integrated displays use that data to drive the screen internally. See [`../telemetry/`](../telemetry/) for the full topology and telemetry analysis.

### Identity Queries (read-only)

Request payload is just the command ID byte with no value bytes. The device returns 16 null-padded ASCII bytes regardless of request size. See [`../identity/wheel-probe-sequence.md`](../identity/wheel-probe-sequence.md).

| Command | Read Group | ID | Notes |
|---------|------------|----|-------|
| model-name | `0x07` | `01` | e.g. `VGS`, `CS V2.1` (see [`../identity/known-wheel-models.md`](../identity/known-wheel-models.md)) |
| hw-version | `0x08` | `01` | e.g. `RS21-W08-HW SM-C` |
| hw-revision | `0x08` | `02` | e.g. `U-V12`, `U-V02` |
| sw-version | `0x0F` | `01` | Firmware version string |
| serial-a | `0x10` | `00` | Serial number first 16 chars |
| serial-b | `0x10` | `01` | Serial number second 16 chars |

Full serial number = serial-a + serial-b (32 ASCII chars total).

### Group `0x3F` / `0x40` (63 / 64) — Configuration

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| colors | `00` | 15 | hex | Write-only |
| brightness | `01` | 1 | int | |
| rpm-timings | `02` | 10 | array | |
| paddles-mode | `03` | 1 | int | 1=Buttons, 2=Combined, 3=Split (1-based) |
| stick-mode | `05` | 2 | int | 0=Buttons, 256=D-Pad |
| set-rpm-display-mode | `07` | 1 | int | Write-only |
| get-rpm-display-mode | `08` | 1 | int | Read-only |
| clutch-point | `09` | 1 | int | |
| knob-mode | `0A` | 1 | int | |
| paddle-adaptive-mode | `0B` | 1 | int | |
| device-info | `0C` | 12 | array | Read-only. 1-byte request `0C` returns 12-byte block (e.g. `03 19 03 ab 04 04 02 df 00 00 00 00`). PitHouse polls ~6×/session. Structure undecoded — possibly version/capability flags. Observed in `usb-capture/ksp/gfdsgfd.pcapng` |
| paddle-button-mode | `0D` | 1 | int | |
| flag-colors1 | `0E 00` | 21 | array | Write-only |
| flag-colors2 | `0E 01` | 9 | array | Write-only |
| rpm-blink-color1 | `0F 00` | 3 | array | RGB; write-only |
| rpm-blink-color2 | `0F 01` | 3 | array | |
| rpm-blink-color3 | `0F 02` | 3 | array | |
| rpm-blink-color4 | `0F 03` | 3 | array | |
| rpm-blink-color5 | `0F 04` | 3 | array | |
| rpm-blink-color6 | `0F 05` | 3 | array | |
| rpm-blink-color7 | `0F 06` | 3 | array | |
| rpm-blink-color8 | `0F 07` | 3 | array | |
| rpm-blink-color9 | `0F 08` | 3 | array | |
| rpm-blink-color10 | `0F 09` | 3 | array | |
| key-combination | `13` | 4 | array | RW. Read form: 1-byte request `13` returns 4-byte current value (`FF FF FF FF` = unset) |
| telemetry-mode | `1C 00` | 1 | int | |
| telemetry-idle-effect | `1D 00` | 1 | int | |
| buttons-idle-effect | `1D 01` | 1 | int | |
| telemetry-idle-interval | `1E 00` | 3 | int | Write-only |
| buttons-idle-interval | `1E 01` | 3 | int | Write-only |
| idle-mode | `20` | 1 | int | Sleep-light mode selector. Verified value `0x01` = Breathing on 2026-05-10 (`bridge-20260510-115644.jsonl` t=41008.106). Plugin: `wheel-idle-mode` |
| idle-timeout | `21` | 2 | int | BE u16 in **minutes** (verified 2026-05-10: `21 00 01` = 1 min, `21 00 0a` = 10 min). Plugin: `wheel-idle-timeout` |
| idle-speed | `22 [mode] [ms_msb] [ms_lsb]` | 3 | array | Per-mode sleep-light animation speed. Wire payload is `[mode, BE u16 ms]` — each sleep mode stores its own speed. Verified 2026-05-10: `22 01 0c d7` = mode 1 (Breathing), 3287 ms. Plugin: `wheel-idle-speed` (3-byte array). Earlier docs documented this as `22 00` (cmdid with mode hardcoded to 0) + 2-byte int payload — incorrect; the mode byte must be the actual target mode |
| idle-color | `24 FF 01 FF` | 3 | array | Sleep-light color RGB. Verified 2026-05-10: `24 FF 01 FF FF 00 00` = red. Plugin: `wheel-idle-color` |
| rpm-interval | `16` | 4 | int | |
| rpm-mode | `17` | 1 | int | |
| rpm-value1 | `18 00` | 2 | int | RPM threshold for LED 1 |
| rpm-value2 | `18 01` | 2 | int | |
| rpm-value3 | `18 02` | 2 | int | |
| rpm-value4 | `18 03` | 2 | int | |
| rpm-value5 | `18 04` | 2 | int | |
| rpm-value6 | `18 05` | 2 | int | |
| rpm-value7 | `18 06` | 2 | int | |
| rpm-value8 | `18 07` | 2 | int | |
| rpm-value9 | `18 08` | 2 | int | |
| rpm-value10 | `18 09` | 2 | int | |
| rpm-color1 | `1F 00 FF 00` | 3 | array | RGB |
| rpm-color2 | `1F 00 FF 01` | 3 | array | |
| rpm-color3 | `1F 00 FF 02` | 3 | array | |
| rpm-color4 | `1F 00 FF 03` | 3 | array | |
| rpm-color5 | `1F 00 FF 04` | 3 | array | |
| rpm-color6 | `1F 00 FF 05` | 3 | array | |
| rpm-color7 | `1F 00 FF 06` | 3 | array | |
| rpm-color8 | `1F 00 FF 07` | 3 | array | |
| rpm-color9 | `1F 00 FF 08` | 3 | array | |
| rpm-color10 | `1F 00 FF 09` | 3 | array | |
| button-color1 | `1F 01 FF 00` | 3 | array | |
| button-color2 | `1F 01 FF 01` | 3 | array | |
| button-color3 | `1F 01 FF 02` | 3 | array | |
| button-color4 | `1F 01 FF 03` | 3 | array | |
| button-color5 | `1F 01 FF 04` | 3 | array | |
| button-color6 | `1F 01 FF 05` | 3 | array | |
| button-color7 | `1F 01 FF 06` | 3 | array | |
| button-color8 | `1F 01 FF 07` | 3 | array | |
| button-color9 | `1F 01 FF 08` | 3 | array | |
| button-color10 | `1F 01 FF 09` | 3 | array | |
| button-color11 | `1F 01 FF 0A` | 3 | array | |
| button-color12 | `1F 01 FF 0B` | 3 | array | |
| button-color13 | `1F 01 FF 0C` | 3 | array | |
| button-color14 | `1F 01 FF 0D` | 3 | array | |
| flag-color1 | `15 02 00` | 3 | array | |
| flag-color2 | `15 02 01` | 3 | array | |
| flag-color3 | `15 02 02` | 3 | array | |
| flag-color4 | `15 02 03` | 3 | array | |
| flag-color5 | `15 02 04` | 3 | array | |
| flag-color6 | `15 02 05` | 3 | array | |
| rpm-brightness | `1B 00 FF` | 1 | int | |
| buttons-brightness | `1B 01 FF` | 1 | int | |
| flags-brightness | `1B 02 FF` | 1 | int | |
| paddles-calibration | `08` | 1 | int | Write-only |

### Group `0x3F` (63) — Live Telemetry (write-only)

These use the same write group as configuration above. They send real-time data to the wheel's LED bar and button LEDs.

See [`../leds/color-commands.md`](../leds/color-commands.md) for the LED encoding (index, R, G, B per LED, 5 per 20-byte chunk). Use index `0xFF` for unused padding slots to prevent firmware from overwriting LED 0.

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| send-rpm-telemetry | `1A 00` | 2..8 | array | Current RPM position on the LED bar; see [`../telemetry/control-signals.md` § RPM LED telemetry](../telemetry/control-signals.md) |
| send-buttons-telemetry | `1A 01` | 2..8 | array | |
| send-knob-telemetry | `1A 03` | 8 | array | Knob indicator bitmask (8-byte active+window form). 4 bits on CSP, 5 on KSP. See [`../leds/color-commands.md`](../leds/color-commands.md) |
| telemetry-rpm-colors | `19 00` | 20 | array | 5 LEDs per chunk; 2 chunks needed for 10 RPM LEDs |
| telemetry-button-colors | `19 01` | 20 | array | 3 chunks for 14 button LEDs; pad unused entries with index `0xFF` |
| telemetry-knob-colors | `19 03` | 20 | array | 1 chunk for 4–5 knob LEDs; pad unused entries with index `0xFF` |

### Group `0x41` (65) — Telemetry Enable (write-only)

Confirmed in USB capture: sent to device `0x17` at ~100×/sec with payload always `00 00 00 00`. Likely a mode/enable flag. See [`../telemetry/control-signals.md` § Dash telemetry enable](../telemetry/control-signals.md).

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| send-telemetry | `FD DE` | 4 | int | Wheels with integrated display; always `00 00 00 00` in captures |
| old-send-telemetry | `FD DE` | 4 | int | Old wheel firmware without integrated display |

### Group `0x43` (67) — Live Telemetry Stream (write-only)

Main game telemetry sent at ~17–20×/sec. See [`../telemetry/live-stream.md`](../telemetry/live-stream.md) for full packet analysis and bit-packing format.

Payload = 2-byte cmd ID + 6-byte header + variable-length bit-packed channel data. Header bytes 0–3 are constant (`32 00 23 32`), byte 4 is a flag/stream selector, byte 5 is constant (`0x20`). Three concurrent streams use consecutive flag values for `package_level` tiers 30/500/2000. Channel data is bit-packed alphabetically by URL suffix per the active dashboard; payload size = `ceil(total_channel_bits / 8)`. Empty tiers send a 2-byte stub.

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| send-live-telemetry | `7D 23` | varies | array | 6-byte header + bit-packed channel data; size depends on dashboard |
| send-telemetry-state | `FC 00` | 3 | array | Session acknowledgment (`session + ack_seq`) ~1×/sec |
| dashboard-transfer | `7C 00` | varies | array | Session-based chunked file transfer / RPC; see [`../dashboard-upload/`](../dashboard-upload/) |
| display-config | `7C 27` | 4–8 | array | Periodic display config push (~1/s), page-cycled alongside `7C 23` |
| dashboard-activate | `7C 23` | 8 | array | Periodic dashboard activate (~1/s), interleaved per page with `7C 27`; declares active pages |
| display-settings | `7C 1E` | 8 | array | Periodic display settings push (~1/s) — brightness/timeout/orientation; sent to all wheel models |

### Old-Protocol Commands (Groups `0x3F` / `0x40`)

Used by older wheel firmware revisions. Observed in protocol captures and retained for backwards compatibility.

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| rpm-indicator-mode | `04` | 1 | int | 1=RPM, 2=Off, 3=On (1-based) |
| old-rpm-color1 | `15 00 00` | 3 | array | |
| old-rpm-color2 | `15 00 01` | 3 | array | |
| old-rpm-color3 | `15 00 02` | 3 | array | |
| old-rpm-color4 | `15 00 03` | 3 | array | |
| old-rpm-color5 | `15 00 04` | 3 | array | |
| old-rpm-color6 | `15 00 05` | 3 | array | |
| old-rpm-color7 | `15 00 06` | 3 | array | |
| old-rpm-color8 | `15 00 07` | 3 | array | |
| old-rpm-color9 | `15 00 08` | 3 | array | |
| old-rpm-color10 | `15 00 09` | 3 | array | |
| old-rpm-brightness | `14 00` | 1 | int | |

### Extended LED Group Architecture (Groups `0x3F` / `0x40`)

Newer wheels organize LEDs into 5 independently controlled groups, extending beyond the RPM (Shift) and Button groups above. Found in rs21_parameter.db. See [`../leds/wheel-groups-0x3F-0x40.md`](../leds/wheel-groups-0x3F-0x40.md) for the high-level group breakdown.

| Group ID | Name | Max LEDs | Purpose |
|----------|------|----------|---------|
| 0 | Shift | 25 | RPM indicator bar |
| 1 | Button | 16 | Button backlights |
| 2 | Single | 28 | Single-purpose status indicators |
| 3 | Rotary | 56 | Rotary encoder ring LEDs |
| 4 | Ambient | 12 | Ambient / underglow lighting |

Per-group commands (G = group ID 0–4, N = LED index):

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| group-brightness | `1B [G] FF` | 1 | int | Plugin command `wheel-group{G}-brightness` (G=2..4). Firmware answers even when hardware absent — cannot be used as a presence check |
| group-normal-mode | `1C [G]` | 1 | int | Telemetry-active mode. Plugin command `wheel-group{G}-mode` |
| group-standby-mode | `1D [G]` | 1 | int | Idle mode. Not yet exposed by plugin |
| group-standby-interval | `1E [G] [2..6]` | 2 | int | 2=breath, 3=circular, 4=rainbow, 5=drift sand, 6=breath color. Not yet exposed by plugin |
| group-led-color | `1F [G] FF [N]` | 3 | array | LED N static RGB. Plugin commands `wheel-rpm-color{1..25}` (G=0), `wheel-button-color{1..16}` (G=1), `wheel-group{G}-color{1..Nmax}` (G=2..4) |
| group-live-colors | `19 [G]` | 20 | array | Bulk live telemetry frame (packed `[idx, R, G, B]` entries, 0xFF padding). **Groups 0/1/3 confirmed**; 2/4 may or may not support. Plugin `wheel-telemetry-rpm-colors`, `wheel-telemetry-button-colors`, `wheel-telemetry-knob-colors` |
| group-live-bitmask | `1A [G]` | 2..8 | int | Per-frame active-LED bitmask (LE). **Groups 0/1/3 confirmed**. Plugin `wheel-send-rpm-telemetry`, `wheel-send-buttons-telemetry`, `wheel-send-knob-telemetry` |

**Static vs live paths**: groups 0/1/3 have two rendering pipelines. Static (`1F`) writes persist in EEPROM and render only when firmware is in idle/constant mode (`wheel-telemetry-mode=2`, `wheel-buttons-idle-effect=1`). Live (`19` + `1A`) writes a volatile frame buffer used while telemetry is active. Group 3 (Rotary/knob) live path confirmed via `knob-rpm-effect.pcapng` (2026-05-03, CS Pro). Groups 2/4 have only the static path in documented commands.

Additional newer wheel commands:

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| meter-auto-rotation | `10` | 1 | int | |
| sleep-breath-brightness | `23 [0/1]` | 1 | int | min (0) / max (1) |
| startup-color | `25` | 3 | array | RGB |
| paddle-thresholds | `26` | 24 | array | 12× 2-byte thresholds |
| knob-active-color | `27 [knob] [role]` | 3 | array | Per-knob "Active position" LED RGB. `knob=0..4` (knob 1..5; CS Pro 0..3, KS Pro 0..4). Role-byte semantics verified live 2026-05-10 against PitHouse: `role=0` is the only writable form — sets the persisted Active LED colour and is what PitHouse's "Active" swatch fires; `role=1` is read-only and returns the live ring-LED colour at the knob's current rotation position. Plugin commands: `wheel-knob{1..5}-active-color` (write/read role 0) and `wheel-knob{1..5}-live-color` (read-only role 1). Earlier docs labelled role 0 as "background/idle" and role 1 as "primary/active" — that mapping was wrong; corrected here and in [`../telemetry/control-signals.md` § Per-knob Active LED colour](../telemetry/control-signals.md). |
| multi-function-switch | `28 [0..2]` | 1 | int | Enable, count, left/right assignment |
| rotary-signal-mode | `2A [N]` | 1 | int | Encoder N (0–4) signal mode |
