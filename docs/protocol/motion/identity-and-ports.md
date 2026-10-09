# Motion platform — identity, enumeration and actuator ports

Captures: `Startup-Shutdown` (full enrol), `Coldstart-software`, `hardware-powerup-software-running`,
`dev2-3` (re-seat), `ports and self test`. Decoder: `tools/motion-decode identity <jsonl>`.

## Enrolment sequence

Motion Manager enrols the controller first, then every populated port, with the standard MOZA
identity probes (same groups as [`../identity/wheel-probe-sequence.md`](../identity/wheel-probe-sequence.md)).
Observed order on the controller (`dev 0x12`, t = 23.37 s of `Startup-Shutdown`):

```
0x06 (n=0) · 0x04 00 00 00 00 · 0x05 00 00 00 00 · 0x02 00 · 0x09 (n=0)
0x07 01 · 0x08 01 · 0x0F 01 · 0x10 00 · 0x10 01 · 0x11 00 · 0x11 01 · 0x11 04 · 0x08 02
```

then `0x1F` reads (`00`, `03`, `04 00`, `04 01`, `09`, `06`, `08`) and the first `0x21/01`.
Ports are enrolled ~2.7 s later with the same probe set plus `0x0F 02`, `0x0F 03`, `0x07 02`
and `0x27 00`; the four ports are probed back-to-back (one frame each, interleaved).

## Identity strings

All strings are 16-byte zero-padded ASCII as on every other MOZA device.

| Group / cmd | Controller (`0x12` → `0x21`) | Actuator (ports → `0x61`/`0x71`/`0x81`/`0x91`) | Meaning |
|-------------|-------------------------------|-------------------------------------------------|---------|
| `0x06` (n=0) | `32 36 33 33 35 0B 66 24 43 31 3E 48` | `34 31 30 35 36 0F 70 7C 43 31 5F D0` (one per unit) | 12-byte UID; the controller's is also its USB serial string |
| `0x02` | `02` | `02` | protocol version byte (as on wheels) |
| `0x04 01` | `02 23 00` | `02 1C 00` | firmware version tuple (2.35 / 2.28) |
| `0x05 01` | `02 18 00` | `02 16 00` | hardware version tuple (2.24 / 2.22) |
| `0x09` (n=0) | `01 FF` | `02 00` | presence/ready reply (controller `01 FF`, actuator `02 00`) |
| `0x07 01` | `"G01 SU"` | `"G01 AU # MOT-1-V"` | model name — **SU** = supervisor/controller unit, **AU** = actuator unit; `0x07 02` continues `"01"` → `MOT-1-V01` |
| `0x08 01` | `"MP24-G01-HW SU-C"` | `"MP24-G01-HW AU-C"` | hardware id |
| `0x08 02` | `"U-V13"` | `"U-V14"` | hardware revision |
| `0x0F 01/02/03` | `" "` (blank) | `"[MP24-G01-HW_AU-" "CU-V10][G01][512" "][4]"` → `[MP24-G01-HW_AU-CU-V10][G01][512][4]` | firmware descriptor (actuators only) |
| `0x10 00/01` | `HKqqgDnnTQqeKk19` / `C4xWnCVk90lkIHOK` | per unit | 32-char token (serial), two halves |
| `0x11 00/01` | `cKehS3ZajoS6mcue` / `sXG4n2o7yLeeQNi9` | per unit | second 32-char token |
| `0x11 04` | `01` | `01` | — |
| `0x27 00` (`00 00`) | — | 33 bytes: 8 × BE float | per-actuator calibration block, read once at enrol (below) |

`0x27` values on the four captured actuators (`998.01 4.7344 2.1898 9.5e-06 -2.0011 -0.28584
0.11918 0`, `1004.8 27.061 -5.2567 -9.5e-06 5.2143 -0.27873 1.7317 0`, `1004.4 11.02 -2.3103 0
4.4402 -0.07441 0.62437 0`, `1012.5 2.9578 -1.3275 9.5e-06 12.491 0.060006 0.5869 0`): the first
value is ~1000 on every unit, the rest differ per unit — a factory/motor calibration record.
Field meanings are unknown; Motion Manager reads it and never writes it.

## Counts and rig geometry (`0x1F`, controller)

| Read | Reply | Meaning |
|------|-------|---------|
| `1F 12 00 00 00` | `9F 21 00 04 08` | supported actuators = 4, ports = 8 (firmware log: `dev_support_num:4, dev_connected_num:8`) |
| `1F 12 08 00…` | `9F 21 08 43FA0000 43FA0000` | rig dimensions L1 = 500.0 mm (front↔rear actuator centres), L2 = 500.0 mm (left↔right) — the "Actuators Installation Distance" fields; the firmware logs them as `Calib Length : 500.00000,Width : 500.00000` |
| `1F 12 09 00` | `9F 21 09 00` | unknown, always 0 |

## Presence poll — group `0x06` at 2 Hz to all eight ports

After enrolment the host sends an empty group-`0x06` frame to **every** port id `0x14`..`0x1B`
(and, during enrol only, to `0x12`) about every 500 ms:

```
h2b  7E 00 06 16 [chk]            ← port 3
b2h  7E 0C 86 61 34 31 30 35 36 0F 70 7C 43 31 5F D0 [chk]   ← UID, cmd byte 0x34
```

Populated ports answer with their UID (first payload byte `0x34` on actuators, `0x32` on the
controller); empty ports stay silent. This is the host's live presence/keepalive for the
actuators. The same facts reach the host through the controller's own port status
(`0x21/02`, [`modes-and-control.md`](modes-and-control.md)) and the firmware log
(`port[2] is_connected: 1, dev_id: 6`).

## Port → axis-role table

The user tells Motion Manager which corner is plugged into which port ("Controller Port
Connection"). The assignment is stored in the controller.

| Dir | Frame | Meaning |
|-----|-------|---------|
| write | `1E 12 02 [port] 01 [role] 00` | assign role to port; echoed on `9E 21` |
| read | `1F 12 02 [port] 00 00 00` → `9F 21 02 [port] 00 [role] 00` | current role of port; the host reads all 8 at connect |

Observed stored map (both rigs): ports 1,2(or 3),7,8 → role 0 (unassigned); the four actuators
carry roles **5, 7, 1, 3**. Firmware echoes each write as `serial_reply_app.c:333 port:N axis:R`.

| Role code | Corner (inferred) | Evidence |
|-----------|-------------------|----------|
| `0` | none / unassigned | empty ports read 0 |
| `1` | Front-Right | `Selftest`: port 5 ← 1 while the UI shows the third populated port as "Front-Right Actuator" |
| `3` | Front-Left | port 6 ← 3; the fourth dropdown (hidden by a tooltip in both videos) is the remaining corner |
| `5` | Rear-Left | first populated port ← 5, UI "Rear-Left Actuator" |
| `7` | Rear-Right | second populated port ← 7, UI "Rear-Right Actuator" |

The dropdown lists the corners as Front-Left, Front-Right, Rear-Left, Rear-Right. The code ↔
corner pairing above assumes the UI's four "Device N" rows are in ascending port order (their
labels — `Device1/3/7/8` and `Device1/2/7/8` on the two rigs — are **not** the wire port numbers).
Treat 1↔FR / 3↔FL as *inferred*; 5↔RL / 7↔RR are consistent in both captures.

The Motion Manager "Device N" dropdown labels and the firmware's port numbering differ; the
wire always uses the 1-based port number (`0x13 + port` = slave id).

## Re-seating a cable (`dev2-3`)

Moving an actuator from port 3 to port 2 while connected:

1. `0x21/02 03` turns `present=0` and `0x21/02 02` turns `present=1 slave=05`; the group-`0x06`
   poll of `0x16` goes unanswered and `0x15` starts answering.
2. The mode poll reports `03 00 05 00 04` — current mode 5 (armed) with flag `0x04` set — while
   the firmware re-enumerates (`access_dev_num`, `axis_connected_num`).
3. The new port has role 0 until the user re-assigns it; Motion Manager shows "Port Not
   Configured" and the status pill goes "Unavailable" until *Configure to Self-Test* is run.

The actuator keeps its UID, `0x27` block and identity; only its bus address changes.
