# MOZA motion platform (HMA150 actuators + Motion Controller)

MOZA's 4-corner motion platform: four **HMA150** linear actuators driven by a separate USB
box, the **MOZA Motion Controller**, configured and fed by **MOZA Motion Manager** (observed
version `1.0.3.10 - release`, a separate application — not Pit House). Everything here is read
off USBPcap captures of Motion Manager talking to the controller (inventory below); nothing is
taken from vendor documentation.

| Page | Scope |
|------|-------|
| [`identity-and-ports.md`](identity-and-ports.md) | Enumeration of the controller and of each actuator port, identity strings, presence poll, port → axis-role table, re-seating behaviour |
| [`modes-and-control.md`](modes-and-control.md) | Mode state machine (`0x1E/03` + `0x1F/03`), run-state and identify-port (`0x20/02`), status records (`0x21`), the observed start-up / power-up / game / shutdown sequences, error codes |
| [`effect-parameters.md`](effect-parameters.md) | Motion-effect intensity/smoothness slots (`0x1E/05`), road detail (`0x1E/06`), global sliders (`0x1E/04`), rig dimensions (`0x1F/08`), lighting table (`0x1E/0B`) |
| [`game-stream-0x22.md`](game-stream-0x22.md) | The game-mode streams: raw vehicle telemetry frame (`0x22/00`), haptic oscillators (`0x22/09`), ABS (`0x22/07`) and gear-shift (`0x22/08`) events; the manual-test setpoint (`0x1E/07`) |
| [`firmware-log-0x0E.md`](firmware-log-0x0E.md) | Firmware log lines and register sweeps on group `0x0E` — the controller narrates every mode change, port map and fault in plain ASCII |

Wire framing is the standard MOZA `7E len group dev payload chk` frame with `0x7E` stuffing and
the usual response transform (group `| 0x80`, device nibbles swapped) — see [`../wire/`](../wire/).
Nothing on this bus uses the session layer (`0x43`), tier definitions or dashboards.

## Topology

```
host (Motion Manager)
  │  USB CDC, VID 0x346E PID 0x2000 "MOZA Motion Controller"
  ▼
Motion Controller  = bus master, dev 0x12 (replies 0x21)
  ├─ port 1  dev 0x14 (reply 0x41)   ─┐
  ├─ port 2  dev 0x15 (reply 0x51)    │  8 actuator ports; only the populated
  ├─ port 3  dev 0x16 (reply 0x61)    │  ones answer. Captured rig: 4 × HMA150
  ├─ port 4  dev 0x17 (reply 0x71)    │  ("G01 AU") on ports 3,4,5,6 (10-06) or
  ├─ port 5  dev 0x18 (reply 0x81)    │  2,4,5,6 (10-07 after a re-seat).
  ├─ port 6  dev 0x19 (reply 0x91)    │
  ├─ port 7  dev 0x1A (reply 0xA1)    │
  └─ port 8  dev 0x1B (reply 0xB1)   ─┘
```

- **Port *p* (1-based, 1..8) is slave device id `0x13 + p`.** The firmware log names the same
  unit two ways: `Slave(6)` (the id's low nibble) and `port[2]` (0-based index). The controller
  reports `dev_support_num:4, dev_connected_num:8` (`0x1F/00` → `04 08`): four actuators are
  supported, eight ports exist.
- Slave device ids `0x15`/`0x17`/`0x18`/`0x19`/`0x1A`/`0x1B` collide with the wheel / ES-wheel /
  pedal / shifter / handbrake ids of the wheelbase bus **only by number** — this is a separate USB
  device with its own bus; nothing routes between the two.
- The controller does the motion cueing itself (kinematic model with the rig's length/width,
  per-effect intensity/smoothness). The host streams raw vehicle telemetry plus host-rendered
  haptic oscillators. See [`game-stream-0x22.md`](game-stream-0x22.md).

## USB identification

| Field | Value |
|-------|-------|
| Vendor ID | `0x346E` |
| Product ID | **`0x2000`** |
| Manufacturer / product string | `Gudsen` / `MOZA Motion Controller` |
| Serial string | the group-`0x06` UID, e.g. `32363333350B662443313E48` |
| bcdDevice | `0x0100` |
| Interfaces | MI_00 CDC ACM control (ep `0x81` INT, 8 B, bInterval 16) · MI_01 CDC data (ep `0x02` OUT / `0x82` IN, **bulk 512 B** → USB high-speed) · MI_02 HID (ep `0x03` OUT / `0x83` IN, 64 B, bInterval 1) |
| HID traffic | **none** in any capture — the HID interface is enumerated but idle |
| Line coding | Motion Manager sets `SET_LINE_CODING` 9600 8N1 on connect (irrelevant on a virtual CDC port, recorded for byte-exactness) |

Not in the plugin's PID inventory yet — see [`../devices/usb-ids.md`](../devices/usb-ids.md) for
what that means today. Interface map in [`../transport/usb-topology.md`](../transport/usb-topology.md).

## Traffic at a glance

| Stream | Dir | Group / cmd | Rate | When | Page |
|--------|-----|-------------|------|------|------|
| Presence / UID poll of all 8 ports | h2b | `0x06` (no payload) to `0x14`..`0x1B` | 2 Hz × 8 | always after connect | identity |
| Mode poll | h2b | `0x1F/03` → `0x9F/03 [target][current] 00 [flags]` | 2 Hz | always | modes |
| Controller status | h2b | `0x21/01` → 13-byte record | 2 Hz | always | modes |
| Port status | h2b | `0x21/02 [port]` → 13-byte record, populated ports only | 2 Hz per port | always | modes |
| Register sweep, controller | h2b | `0x0E/00 00 [idx 1..7]` | ~1 Hz | always | firmware-log |
| Register sweep, actuators | h2b | `0x0E/00 00 [idx 1..9]` to each port | ~1 Hz | always | firmware-log |
| Firmware log | b2h | `0x0E/05 <ascii>` from controller and actuators | bursty | always | firmware-log |
| **Telemetry frame** | h2b | `0x22/00` 53 B | **~33 Hz** (p50 27 ms) | mode 1 | game-stream |
| Haptic oscillators | h2b | `0x22/09` 51 B | ~30 Hz | mode 1 | game-stream |
| ABS oscillator | h2b | `0x22/07` 14 B | ~31 Hz while ABS active | mode 1 | game-stream |
| Gear-shift pulse | h2b | `0x22/08` 13 B | per shift | mode 1 | game-stream |
| Manual setpoint | h2b | `0x1E/07 [axis][f32]` | per slider move | mode 2 | game-stream |

No frame on this bus carries a session header, and `0x22` frames are **not** answered. Every
`0x1E`/`0x1F`/`0x20`/`0x21`/`0x06`/`0x0E` request gets its mirrored reply within ~2 ms.

## Captures

Source folder `~/Downloads/hma150/` (not in git — `*.pcapng` is ignored). Work copies, JSONL and
decoder output live under the git-ignored `usb-capture/hma150/` (`pcap/`, `jsonl/`, `inventory/`,
`decode/`); regenerate with the tools listed below. All captures were taken with USBPcap on the
Motion Manager PC; timestamps are UTC epoch and line up with the SimHub replays' `StartDate`.

| Capture | UTC | Length | Ctl USB addr | What it exercises | Paired |
|---------|-----|--------|--------------|-------------------|--------|
| `Startup-Shutdown/Startup-Shutdown.pcapng` | 2026-10-06 18:34 | 64 s | 3 | Motion Manager start → enrol → config push → self-check → calibration → Close → standby | video `20261006_193416.mp4` |
| `Pitch/Pitch+-5.pcapng` | 10-06 18:44 | 30 s | 3 | Manual Motion Test, pitch +0.05 / −0.05 / 0 | video |
| `roll+-5.pcapng` | 10-06 18:50 | 21 s | 3 | roll ±0.05 | — |
| `Heave+-5.pcapng` | 10-06 18:52 | 19 s | 3 | heave ±0.05 | — |
| `Pitch-Roll-Heave+-100/….pcapng` | 10-06 18:54 | 73 s | 3 | pitch, roll, heave each +1.0 / −1.0 / 0 | video |
| `nurburgring-iracing-002.pcapng` (16 GB → slim 10 MB) | 10-07 15:46 | 704 s | 5 | iRacing Nürburgring; preset "iRacing caterham" loaded at +20 s, game mode from +46 s | `Settings.png`, `Settings2.png`, video |
| `spa-003.pcapng` (3.3 GB → slim 3 MB) | 10-07 18:23 | 185 s | 5 | iRacing Spa, garage → one lap | SimHub replay `20261007_192315`, video |
| `100_STROKE/100_STROKE.pcapng` | 10-07 19:42 | 68 s | 5 | iRacing, short full-throttle run, game start/stop | SimHub replay `20261007_204238`, video |
| `hardware-powerup-software-running.pcapng` | 10-07 21:34 | 20 s | 5 | controller power-cycled while Motion Manager runs | — |
| `Self test configuration/Selftest.pcapng` | 10-07 21:45 | 27 s | 5 | port → corner assignment, *Configure to Self-Test*, config push | video |
| `unplugging port and reseating…/dev2-3.pcapng` | 10-07 21:57 | 35 s | 5 | actuator cable moved from port 3 to port 2 | video |
| `Telling software what port is what/ports and self test.pcapng` | 10-07 22:15 | 41 s | 5 | identify-port clicks, assignment, self-test | video |
| `Coldstart-software.pcapng` | 10-07 22:28 | 20 s | 5 | Motion Manager cold start against a powered controller (fault dump) | — |

(Times are UTC from the pcapng epoch timestamps; the capture PC's clock runs UTC+1, which is
what the videos show.)

The big race captures are dominated by two ASIX USB-ethernet adapters on the same host
controller; `tools/pcap-slim --device 5` reduces them to the controller's own traffic.

The note that shipped with the captures (`hma150-actuator-protocol.md`) described a *single*
actuator at `0x12` and only saw the manual-test setpoint; it is superseded by these pages
(its correct parts — the `0x1E/07` scale table and the `0x1E/05`/`0x06`/`0x0B` sightings — are
folded into [`game-stream-0x22.md`](game-stream-0x22.md) and [`effect-parameters.md`](effect-parameters.md)).

## Tools

| Tool | Purpose |
|------|---------|
| `tools/pcap-inventory <pcapng> --device auto --timeline --jsonl out.jsonl` | per-(dir, group, dev, cmd) inventory of one USB device; `auto` picks the VID `0x346E` device with the most frames |
| `tools/pcap-slim <in> <out> --device N` | strip a multi-GB capture down to one USB address |
| `tools/motion-decode log|streams|control|identity|registers <jsonl>` | the decoders behind every table on these pages |
| `tools/motion-replay-correlate <jsonl> <replay-stem>` | field-by-field fit of the `0x22` stream against a SimHub replay (how the telemetry frame was identified) |
| `tools/simhub_replay.py` | reader for SimHub session recordings (format in the module docstring) |

## Status / open items

Verified byte-exact against the UI or the game replay: identity, port map, mode numbers for
0/1/2/7/8/13, effect-parameter slots, global sliders, rig dimensions, telemetry-frame fields
f0–f5 and f8–f11, tail bytes, oscillator formula, ABS and gear-shift events.

Open (also listed in [`../open-questions.md`](../open-questions.md)): telemetry field `f7`;
exact semantics of modes 3/4/5 and of the `0x20/02` 02↔03 toggles; status-record byte 1 and
flag bit `0x04`; the `0x0E/03` notification codes; the per-port status word that appears in
game mode; the actuator `0x27` calibration block; whether mode 2 accepts a continuous
`0x1E/07` stream.
