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

**One SimHub device per motor port** — `MOZA S12 Throttle`, `MOZA S12 Brake`,
`MOZA S12 Clutch`. Each carries its own ShakeIt profile, effect defaults and
channel list. Grouping all three into one device made the list unreadable and
forced unrelated pedals to share one set of defaults.

### Channels are routing destinations, not slots

Each device exposes 16 generic channels plus Road Texture. That is deliberately
more than the eight usable oscillators: a user can build as many ShakeIt effects
as they like, so channels are places to route an effect to, and the worker maps
whichever are live onto real slots.

They are numbered rather than named after the firmware slot labels. Hardware
testing confirmed **every slot produces the same vibration** — the ABS / Lockup /
Gear Shift names describe nothing the firmware actually does — so naming channels
after them promised behaviour that does not exist.

### Allocation

A channel takes a slot when it goes active and keeps it until it goes silent, so
a sustained effect never jumps oscillator mid-note. Slots come from a rotating
cursor, so sharing spreads out instead of piling onto slot 0:

| Active channels | Result |
|---|---|
| 1-8 | one slot each, nothing shared |
| 9+ | latecomers share a slot; gains sum (clamped), frequency is their gain-weighted mean |

Sharing cannot be faithful — the hardware takes one tone per slot — but summing
keeps both effects audible rather than silently dropping the quieter one.

This is only sound because the slots are interchangeable. If a future firmware
gives them distinct behaviour, the allocator has to go.

### Emission budget

In practice few channels are live at once and each is serviced every 20 ms tick.
The budget below exists so the pathological case degrades instead of breaking:
refreshing all 27 hardware slots every tick would be 1350 frames/s, roughly
23 kB/s — more than a 115200 link carries, and far more than a routed unit's
share of a pipe already streaming telemetry.

Slots are serviced round-robin under a per-tick budget of 8 frames: up to eight
behave as if there were no budget, and all 27 still refresh every 80 ms —
comfortably inside the 500 ms streamed duration. Releases are never budgeted, so
a motor is never left running because the budget ran out.

Frames go on the paced one-shot FIFO rather than latest-wins stream lanes: every
frame addresses a different pedal+slot, so coalescing by lane would drop one
slot's update in favour of another's.

### Road Texture

Kept as its own named channel, outside the pool, because it cannot stand in for
a tone: its frequency field is a suspension position and the firmware makes
texture from that position *changing*. ShakeIt's gain is the part that varies
with an effect, so it drives the position; the reported frequency would sit still
and produce nothing.

A steady effect on this channel therefore produces little or nothing. That is the
hardware's design, not a bug — but see the duration note above: a build that
sent duration 0 here (as the written spec says) had users report this slot as the
only one that did nothing, so the plugin now sends a non-zero duration like Pit
House does.

### Keepalive is mandatory on a dedicated lane

The module never speaks unprompted, and once detected the plugin has nothing to
say to it while no effect is running. On its own USB port that means the lane
goes completely silent — and `MozaSerialConnection`'s half-open watchdog closes
any port with no inbound for 30 s. Every other MOZA device is chatty enough that
this never fires; this one is the exception.

Left alone the failure is quiet and confusing: the port closes 30 s after
connecting, detection stays latched so nothing reopens it, and effects appear to
work only if you trigger them inside that first 30 s window. Reported as
`TWV94SPY` — "clicking Test makes it work, but once I exit the test and go back
in, it stops functioning".

So the registry sends a presence query on every connected lane each 5 s poll
tick. It costs one 16-byte frame, sits well inside the 30 s window, and doubles
as the liveness check. Readiness is gated on a lane being detected **and**
connected, so a dropped lane reports disconnected rather than silently
swallowing frames.

### Detection

Detection uses a query rather than a bus heartbeat: any well-formed answer proves
the device speaks the vibration protocol, where a heartbeat only proves something
is there. A USB unit is found by PID; a routed one has no port of its own, so a
lane is registered speculatively on each connected pipe and its query decides —
nothing reaches the wire until the unit answers.

Three definitions sharing one PID is fine — the wheel definition already binds
the base's PID the same way. Each pedal device resolves which motor port it
drives from its own DeviceTypeID.

## Note on group `0x4D`

[`open-questions.md`](../open-questions.md) records a lone `0x4D` frame in an AB9
capture as Razer noise on a multi-vendor bus. That remains true of that frame, but
`0x4D` is a real group: it is this command.
