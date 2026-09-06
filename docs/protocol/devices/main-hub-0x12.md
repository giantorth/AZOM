## Main (Device `0x12` / 18)

> Device id `0x12` is shared across several physical devices on the bus — it is the wheelbase main controller, the universal hub, the AB9 active shifter, the mBooster pedals, and the CM2 standalone dashboard's bridge/main. Disambiguation happens at the USB level (each device exposes its own VID/PID composite) and via group context (group `0x32` writes addressed to `0x12` belong to the CM2 standalone dashboard, not the wheelbase main).

### CM2 bridge/main routing

A standalone-USB CM2 (PID `0x0025`) addresses its stored meter/LED config at
dev=`0x12` under group `0x32` — distinct from legacy MDD-style dashboards which
use dev=`0x14` under group `0x32`/`0x33`. (A CM2 behind a wheelbase addresses
the meter at dev=`0x14` instead — `0x12` there is the base main.)

| Command intent | Sub-cmd | Payload | Effect |
|---|---|---|---|
| Indicator brightness | `17 00 FF` | int | ✓ brightness changes |
| Normal mode | `18 00` | int (0=off, 1=telemetry, 2=forced-on) | mode 1/2 visually similar in lab |
| Standby mode | `19 00` | int | |
| RPM group mode | `11 00` | int (1 = SimHub mode) | |
| Flag group mode | `11 02` | int (1 = SimHub mode) | |
| RPM regulation mode | `0D` | int (0=percent, 1=absolute — TBV) | |
| RPM percent thresholds | `05` | 10 bytes | |
| RPM absolute thresholds | `0E <index>` | u32 RPM per rung | |
| Per-LED stored color | `1B 00 FF <index>` | byte[3] RGB | ✓ persists across replug |

CM2 has 16 physical RPM LEDs (no buttons, no separate flag strip per user-confirmed hardware layout). Physical order: logical 1–3 left side bottom-to-top, 4–13 top row left-to-right, 14–16 right side top-to-bottom.

The group-`0x32` table above is the **one-time config** (stored colours +
thresholds + modes), pushed once at connect — it persists across replug. The
**live** RPM/flag LED state is the `0x41 FD DE` on/off bitmask (the
`dash-send-telemetry` command), sent per telemetry frame; the firmware lights
each set bit in its stored colour. It targets the meter: dev `0x14` for a
base-bridged CM2 (confirmed — PitHouse drives the bus CM2 this way per frame in
`cm2.pcapng` 2026-06-08), dev `0x12` for a standalone-USB CM2. (An earlier plugin
build streamed per-LED colour on group `0x32` sub `0x0B` to dev `0x12` instead of
the bitmask; the firmware ignored it, so CM2 LEDs never lit.)

Screen telemetry is the standard group-`0x43` tier-def / value-frame pipeline,
targeted at dev=`0x12` for a standalone-USB CM2, dev=`0x14` for a base-bridged
CM2, and dev=`0x17` for a wheel-resident display. See
[`../telemetry/live-stream.md`](../telemetry/live-stream.md) § Target device id
and [`dash-0x14.md`](dash-0x14.md).

Plugin implementation: `MozaPlugin.ShouldUseStandaloneDashboardTarget()` returns true when the open USB port has a Dashboard-category PID and no wheel is detected; the dashboard binding coordinator then pins `TelemetrySender.TargetDeviceId` to the meter — `0x14` for a base-bridged CM2, `0x12` for a standalone-USB CM2 (`MozaPlugin.PreferredStandaloneDashboardTargetDeviceId`).

### Group `0x01` (1) — Lifecycle (soft reboot)

| Command | ID | Dir | Bytes | Type | Notes |
|---------|----|-----|-------|------|-------|
| soft-reboot | `02` | W | 0 | — | Reboots the wheelbase main firmware. No payload. |

Wire: `7E 01 01 12 02 <chk>` (request_group `0x01`, dev `0x12`, cmd `0x02`, zero
payload — the length byte `0x01` counts the command byte only). Observed once in
`usb-capture/soft-restart.calibrate-paddles.interpolation-0-5-10-0.deadzone-0-5-10-0-10-0.pcapng`
(PitHouse h2b): after the single frame the base stops answering for ~5 s, then
re-enumerates and PitHouse re-opens sessions `0x01`/`0x02`/`0x03` from scratch —
i.e. a full firmware restart, not a settings reset.

Note the SDK CoAP `…/SoftReboot` POST carries a 4-byte LE int (`1` observed), but
the serial frame to the base has **no payload**; the value is not forwarded.
`Sdk/Resources/Lifecycle/SoftRebootResource.cs` already routes this through
`HardwareApplier.WriteIfBaseConnected("wheel-soft-reboot", 1)`, which is a silent
no-op until a matching `MozaCommandDatabase` entry exists (writeGroup `1`, cmd
`{2}`, 0-byte payload).

### Group `0x1E` (30) — Output (read-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| output | `39` | 7 | int | |

### Group `0x1F` (31) — Settings

Note: get and set commands in this group use **different** command IDs (unlike most other devices where read/write share the same ID).

| Command | ID | Dir | Bytes | Type | Notes |
|---------|----|-----|-------|------|-------|
| set-compat-mode | `13` | W | 1 | int | `1` = **Forza Horizon compatibility** (PitHouse's toggle of that name), `0` = off. Persistent base-side state — survives as `get-compat-mode`, and it is a single global mode, not per-game or per-profile. |
| get-compat-mode | `17` | R | 1 | int | Readback of the above. PitHouse polls it at ~0.5 Hz whenever the panel is open. |
| get-ble-mode | `46` | R | 1 | int | 0 = off, 0x55 = on |
| set-ble-mode | `47` | W | 1 | int | |
| get-led-status | `08` | R | 1 | int | |
| set-led-status | `09` | W | 1 | int | |
| get-work-mode | `34` | R | 1 | int | |
| set-work-mode | `33` | W | 1 | int | |
| get-default-ffb-status | `36` | R | 1 | int | |
| set-default-ffb-status | `35` | W | 1 | int | |
| get-interpolation | `4D` | R | 1 | int | |
| set-interpolation | `4C` | W | 1 | int | |
| get-spring-gain | `4F 08` | R | 1 | int | |
| set-spring-gain | `4E 08` | W | 1 | int | |
| get-damper-gain | `4F 09` | R | 1 | int | |
| set-damper-gain | `4E 09` | W | 1 | int | |
| get-inertia-gain | `4F 0A` | R | 1 | int | |
| set-inertia-gain | `4E 0A` | W | 1 | int | |
| get-friction-gain | `4F 0B` | R | 1 | int | |
| set-friction-gain | `4E 0B` | W | 1 | int | |

**`compat-mode` verification** — real R16 Ultra, PitHouse bridge capture
`bridge-pithouse-features-20260828-061341.jsonl`. Clicking PitHouse's
"Forza Horizon compatibility" emitted exactly one write in a 925 s session:

```
h2b  7E 02 1F 12 13 01 [chk]     ← set-compat-mode = 1
b2h  7E 02 9F 21 13 01 [chk]     ← echo/ack
```

`get-compat-mode` was polled at ~0.5 Hz across the whole session and returned
`00` for 218 replies, then `01` for every reply after t+844.08 — 687 ms after
the write, bounded by the poll interval rather than the apply time.

The plugin registers `main-set-compat-mode` / `main-get-compat-mode`
(`Protocol/MozaCommandDatabase.cs`) but has no caller for either, so a base
left in compat-mode `1` by PitHouse is invisible to it.

### Group `0x20` / `0x22` (32 / 34) — Base Ambient LEDs

Controls 2 LED strips (9 LEDs each) on the wheelbase body. Group 32 = write, group 34 = read. Found in rs21_parameter.db but not observed in USB captures. See [`../leds/base-ambient-0x20-0x22.md`](../leds/base-ambient-0x20-0x22.md).

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| indicator-state | `1C` | 1 | int | On/off |
| standby-mode | `1D` | 1 | int | 0=constant, 2=breath, 3=cycle, 4=rainbow, 5=flow |
| standby-interval | `1E [mode]` | 2 | int | Interval for given mode |
| brightness | `1F 02` | 1 | int | |
| led-color | `20 [strip] [mode] [led]` | 3 | array | RGB. strip=0/1, mode=1(constant)/2(breath), led=0–8 |
| sleep-mode | `21` | 1 | int | |
| sleep-timeout | `22` | 2 | int | |
| sleep-breath-interval | `23 01` | 2 | int | |
| sleep-brightness | `24` | 1 | int | |
| sleep-led-color | `25 [strip] 01 [led]` | 3 | array | Sleep breathing per-LED RGB |
| startup-color | `26` | 3 | array | RGB |
| shutdown-color | `27` | 3 | array | RGB |

## Hub (Device `0x12` / 18)

### Group `0x64` (100) — Connected Device Status (read-only)

Two probe forms observed:

**Form A — single-byte cmd ID** (SimHub plugin, also `sim/wheel_sim.py` `(0x64, 0x12, 03)` "hub-port1-power probe"):

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| base | `02` | 2 | int | |
| port1 | `03` | 2 | int | |
| port2 | `04` | 2 | int | |
| port3 | `05 01` | 1 | int | |
| pedals1 | `06` | 2 | int | |
| pedals2 | `07` | 2 | int | |
| pedals3 | `08` | 2 | int | |

**Form B — `01 NN 00` PitHouse probe** (observed in `usb-capture/ksp/gfdsgfd.pcapng` @ f54501, 5-frame burst):

| Request | Response (`0xE4/0x21`) | Notes |
|---------|------------------------|-------|
| `7E 03 64 12 01 01 00` | `7E 03 E4 21 01 01 00` | Slot 1 — value `00` = empty/none |
| `7E 03 64 12 01 02 00` | `7E 03 E4 21 01 02 07` | Slot 2 — `07` |
| `7E 03 64 12 01 03 00` | `7E 03 E4 21 01 03 00` | Slot 3 |
| `7E 03 64 12 01 04 00` | `7E 03 E4 21 01 04 00` | Slot 4 |
| `7E 03 64 12 01 05 00` | `7E 03 E4 21 01 05 02` | Slot 5 — `02` |

Sub-cmd `01 NN` enumerates 5 slots; response last byte = device-type/status code. Distinct from Form A — PitHouse uses Form B in startup probe. Per-slot semantics (which physical port maps to which NN) undecoded.
