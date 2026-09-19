# S12 pedal vibration (group `0x4D`)

A three-port pedal vibration module. Each port drives one pedal's motor, and each
pedal owns **nine concurrent effect slots** — independent oscillators the host
feeds a duration, frequency and strength. The plugin decides when and how
strongly to vibrate; the module only produces it.

USB: VID `0x346E`, PID `0x002F`, CDC (a virtual serial port), 115200 8N1.

Plugin implementation: `Protocol/MozaPedalHapticsProtocol.cs`,
`Devices/PedalHaptics/`, `Integration/MozaPedalHapticsChannelsProvider.cs`,
`Devices/Haptics/MozaPedalHapticsBridge.cs`. Frame-level regression check:
`tools/pedal-haptics-frame-check` (no capture, hardware or build needed).

## Two envelopes

The same 11-byte vibration payload is carried two ways, and which one to use
depends on how the unit is attached:

| | USB CDC | Extended (routed) |
|---|---|---|
| Length byte | `0x0B` = 11 | `0x0C` = 12 |
| Command | `0x4D` / reply `0xCD` | `0x4D` / reply `0xCD` |
| Address | `0x12` / reply `0x21` | `0x1F` / reply `0xF1` |
| Extended id | none | `0x1E` (30), one byte before the payload |
| Frame size | 16 bytes | 17 bytes |

`0x1F` is not a bus device id — it is the extended-addressing form, meaning "host
1 is talking to an extended device", and the real destination is the `0x1E` that
follows. That extended id **counts toward both the length and the checksum**, so
every payload byte shifts one place right. Everything after it is byte-identical
between the two envelopes.

The same vibration, both ways:

```
USB   7e 0b 4d 12    01 01 02 00 01 01 f4 00 32 80 00 cd → (cks a1)
Ext   7e 0c 4d 1f 1e 01 01 02 00 01 01 f4 00 32 80 00 cd
```

## Payload

```
7e  LEN  4d  ADDR  [1e]   01  SC  PD  SL  EN   DH DL   FH FL   SH SL   CK
                          │   │   │   │   │    └dur┘   └freq┘  └─str┘
                          │   │   │   │   └ enable 1 / disable 0
                          │   │   │   └ effect slot 0..8
                          │   │   └ pedal 1 throttle, 2 brake, 3 clutch
                          │   └ 01 set / 02 query
                          └ vibration command
```

| Payload byte | Field | Notes |
|---|---|---|
| 0 | command | always `0x01` |
| 1 | sub-command | `0x01` set (also stops), `0x02` query |
| 2 | pedal | 1 throttle, 2 brake, 3 clutch |
| 3 | effect slot | 0..8, see below |
| 4 | enable | 1 on, 0 off |
| 5–6 | duration | u16 BE, milliseconds, 1..60000 |
| 7–8 | frequency | u16 BE, plain Hz, 10..100 (slot 8: suspension position 0..100) |
| 9–10 | strength | u16 BE, full scale 65535 |

Every 16-bit value is big-endian. Strength is a fraction of 65535 — `0x3333` =
13107 is exactly 20 %. Perceived vibration is not necessarily linear in it.

Checksum and `0x7E` stuffing are the standard wire rules
(`MozaProtocol.CalculateWireChecksum`); the extended id participates in both.

## Pedal numbering

**1 = throttle, 2 = brake, 3 = clutch.**

Worth stating loudly, because the control box's ports are silkscreened
**CLUTCH / BRAKE / THROTTLE** left to right — the reverse of the id order.
Counting ports left to right and using the position as the pedal id gets throttle
and clutch backwards. Brake is the middle port either way, which is the one a
brake-only install uses and the easiest to verify by ear.

## Effect slots

Nine per pedal, all concurrent. The names are the game events each slot is meant
to represent; the firmware synthesizes nothing from them, so functionally a slot
is just one more independent oscillator on that pedal's motor.

| Slot | Name | Parameters | Stops when |
|---|---|---|---|
| `0x00` | Traction control | duration 1–60000 ms, frequency 10–100 Hz | duration elapses from the last enable frame |
| `0x01` | ABS | as above | as above |
| `0x02` | Lockup | as above | as above |
| `0x03` | Brake threshold | as above | as above |
| `0x04` | Engine vibration | frequency 10–100 Hz; duration must still be valid but is **not** the timeout | ~1 s without an update, or explicit disable |
| `0x05` | Clutch bite point | duration + frequency | duration elapses |
| `0x06` | Gear shift | duration + frequency | duration elapses |
| `0x07` | Wheel slip | duration + frequency | duration elapses |
| `0x08` | Road texture | frequency field carries an integer **suspension position 0–100**, not Hz; duration 0 | ~1 s without an update, or explicit disable |

Slot `0x04` maps engine RPM onto 10–100 Hz — raw RPM in the thousands is not a
valid frequency. Slot `0x08` wants a stream of *changing* positions: the firmware
generates texture from the movement, so a constant position settles to near
silence.

## Streaming and stopping

An effect is held on by streaming: send a short duration faster than it expires
and each frame refreshes the timer. Roughly a 100 ms duration updated every 20 ms
is a reasonable cadence. The upside of this design is that a host crash silences
the motors about one duration later rather than leaving them running.

Stopping is the same command with enable `0` and every parameter zeroed, and it
still carries the full payload:

```
7e 0b 4d 12 01 01 02 00 00 00 00 00 00 00 00 f9      stop brake slot 0
```

Two rules that are easy to get wrong:

- **Stopping one slot does not stop the others.** Disable each slot the plugin
  enabled, individually.
- **Strength 0 is not the same as disabled.** The slot stays enabled.

Disable on pause and exit — but not before every update, which would fight the
stream.

## Query

Sub-command `0x02` reads a slot back. The request must be padded to a full
payload; a truncated `01 02 PD SL` is not accepted.

```
→  7e 0b 4d 12 01 02 02 00 00 00 00 00 00 00 00 fa
←  7e 0b cd 21 01 02 02 00 00 00 00 00 00 00 00 89     disabled, parameters cleared
```

The reply carries `[enabled][remaining ms][frequency][strength]` in the trailing
2 + 2 + 2 bytes. Remaining time is approximate for the timed slots, and
meaningless for `0x04` / `0x08`, which do not expire on duration.

What a reply does **not** prove: a set/stop echo does not mean the parameters
were valid or that the motor moved, and a query reports software effect state,
not measured vibration.

## Reading replies

Replies may arrive in chunks, or several at once. Buffer the bytes, undo the
escaping, use the length to extract complete frames and verify checksums — one
read is not necessarily one frame. The plugin gets this from the shared read path
rather than reimplementing it.

## What the capture shows

`moza.pcap` (2026-09-11) is a mid-session Pit House effect test with the unit
routed behind an R16/R21 wheelbase, so every frame in it uses the extended
envelope. Four bursts, each a run of retriggers ended by one stop frame:

| t (s) | pedal | slot | duration | frequency | strength | resend |
|---|---|---|---|---|---|---|
| 2.57–4.16 | brake | `0x02` Lockup | 50 ms | 50 Hz | 32767 (50 %) | 32 ms |
| 5.88–7.40 | brake | `0x08` Road texture | 100 ms | position 100 → 37 → 100 | 32767 (50 %) | 32 ms |
| 9.73–11.13 | throttle | `0x00` TC | 200 ms | 81 Hz | 40631 (62 %) | 104 ms |
| 12.60–14.08 | throttle | `0x07` Wheel slip | 400 ms | 40 Hz | 20315 (31 %) | 234 ms |

Two things to note. The road-texture burst's sweeping middle field is a
suspension position, not a frequency — which is why it moves smoothly while
strength holds still. And that burst carried a duration of 100 ms where the slot
takes 0; harmless, since the slot ignores duration as a timeout, but the plugin
follows the documented shape and sends 0.

The unit also narrates itself on the existing Param Reader / Debug Log channel
(group `0x0E`, cmd `1E 05`):

```
[INFO]pedal_vibration_ffb.c:602 FFB channel: 2 duration : 50
[INFO]daemon.c:56 Pedal vibration heartbeat log
device connected: pedal_uart
   MeanLossRate: 0.00002 %   MeanRecvGap: 4.83364 (ms)   MaxRecvGap: 83.00000 (ms)
```

`device connected: pedal_uart` is a hotplug announcement. The log repeats on a
throttled ring unrelated to command boundaries — it kept reporting `duration : 50`
through a burst whose duration field held 100 — so it is a rate-limited
last-message repeat, not a per-effect trace.

## How the plugin uses this

All 27 oscillators — three pedals × nine slots — are exposed as ShakeIt Motors
channels, named `Throttle: ABS`, `Brake: Lockup` and so on. The index order is
**pedal-major** (0–8 throttle, 9–17 brake, 18–26 clutch) and is a user-visible
contract: reordering it silently moves every effect a user has already assigned.

The slot names are the firmware's, and they are suggestions rather than
behaviour — nothing stops a user routing a gear-shift effect to `Brake: ABS`.
They exist so the obvious mapping is the easy one.

### Emission budget

In practice only a handful of channels are live at once, and those are serviced
every 20 ms tick with no throttling. The budget below exists so the pathological
case degrades instead of breaking: refreshing all 27 every tick would be 1350
frames/s, roughly 23 kB/s — more than a 115200 link carries, and far more than a
routed unit's share of a pipe already streaming telemetry.

Active channels are serviced round-robin under a per-tick frame budget of 8:

| Active channels | Worst-case refresh | Peak output |
|---|---|---|
| 1 | 20 ms | 0.85 kB/s |
| 8 | 20 ms | 6.8 kB/s |
| 27 | 80 ms | 6.8 kB/s |

So up to eight channels behave exactly as if there were no budget, and a
hypothetical full 27 still refreshes every 80 ms — comfortably inside the 500 ms
streamed duration, 6× over.

Activation edges bypass the budget entirely, so an effect's attack is never
delayed; only sustain is ever rate-limited. Deactivation sends an explicit
disable immediately, so the long duration never delays a stop — it only bounds
how long a motor runs if the host dies.

Frames go on the paced one-shot FIFO rather than latest-wins stream lanes:
every frame addresses a different pedal+slot, so coalescing by lane would drop
one slot's update in favour of another's. The budget is what keeps the queue
from growing.

### Road Texture through ShakeIt

Slot `0x08` is the one channel that does not map cleanly onto a tone. Its
frequency field is a suspension position, and the firmware generates texture
from that position *changing* — a constant value settles to silence. ShakeIt's
gain is the field that actually moves with an effect, so it drives the position;
its frequency would sit still for most effects and produce nothing. Strength
tracks gain as well, so the channel still fades in and out.

A steady effect on this channel therefore produces little or nothing. That is
the hardware's design, not a bug.

### Detection

Detection uses a query rather than a bus heartbeat: any well-formed answer proves
the device speaks the vibration protocol, where a heartbeat only proves something
is there. A USB unit is found by PID; a routed one has no port of its own, so a
lane is registered speculatively on each connected pipe and its query decides —
nothing reaches the wire until the unit answers.

### If 27 channels proves unwieldy

The alternative is three SimHub devices of nine channels each, one per pedal.
Several definitions sharing one PID is already an established pattern here (the
wheel definition binds the base's PID), so it is workable: it needs three GUIDs,
a pedal-filtered provider, and a device extension that reads its pedal from the
DeviceTypeID. The single-device form was chosen first because it is one
definition, one extension and one provider.

