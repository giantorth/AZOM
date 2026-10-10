## USB topology

Moza wheelbase enumerates as a single USB composite device exposing two
functional interfaces: a CDC ACM serial pipe carrying the Moza protocol
(every frame and session described in the rest of this folder), and a
standard HID interface providing wheel axes and button events for OS
input subsystems.

### Device descriptor

| Field | Value | Notes |
|-------|-------|-------|
| Vendor ID | `0x346E` | Moza |
| Product ID | `0x0006` / `0x0002` / `0x0012` | Wheelbase composite (R5 / R9 / R12, etc.) |
| Product ID | `0x0025` | CM2 Racing Dash — separate USB device when connected by its own cable (own CDC pipe) |
| Product ID | `0x1000` | AB9 active shifter — separate USB device, parallel composite (see [`../devices/ab9-shifter.md`](../devices/ab9-shifter.md)) |
| Product ID | `0x2000` | MOZA Motion Controller — separate USB device heading its own actuator bus (see below and [`../motion/`](../motion/)) |
| Class / SubClass / Protocol | composite (`0xEF / 0x02 / 0x01`) | Multi-interface device |

A standalone-USB CM2 is its own USB device with its own CDC serial pipe (a
separate COM port), opened on a dedicated connection — not multiplexed through a
wheelbase. A CM2 attached behind a wheelbase instead rides the wheelbase pipe as
the dash sub-device at `0x14` (see [`../devices/dash-0x14.md`](../devices/dash-0x14.md)).

### Interface map

| Interface | Class | Endpoints | Purpose |
|-----------|-------|-----------|---------|
| MI_00 | CDC ACM (`0x02 / 0x02`) | `0x02` BULK OUT / `0x82` BULK IN | **Moza protocol bus** — every serial frame in this docs tree rides here |
| MI_01 | CDC Data | data endpoints for MI_00 | Companion data interface for the CDC ACM control interface |
| MI_02 | HID (`0x03`) | `0x03` INTERRUPT OUT / `0x83` INTERRUPT IN | Wheel axes, buttons, paddles — consumed by SimHub via standard HID, not by this protocol |

### Endpoint usage

| Endpoint | Direction | Use |
|----------|-----------|-----|
| `0x02 OUT` | host → device | All Moza-protocol writes — heartbeats, LED frames, telemetry stream, session opens, dashboard upload |
| `0x82 IN` | device → host | All Moza-protocol reads — identity replies, fc:00 ACKs, configJson state, RPC replies, debug logs |
| `0x03 OUT` | host → device | HID output reports (FFB on basic HID profile; AB9 capture shows endpoint exists but unused) |
| `0x83 IN` | device → host | HID input reports — wheel position (1 kHz), buttons, axes |

### Internal bus addressing

The wheelbase exposes one USB serial pipe but routes frames internally to
multiple peripherals. Device IDs in protocol frames (`0x12`, `0x13`,
`0x14`, `0x17`, `0x19`, `0x1A`, `0x1B`, `0x1C`, `0x1E`) are addresses on
this internal serial bus — **not separate USB devices**. The wheelbase
hub multiplexes; the host only sees one CDC pipe.

| Device ID | Hex | Role | Notes |
|-----------|-----|------|-------|
| 18 | `0x12` | Main / hub | Hub controller, USB-side endpoint of the bus |
| 19 | `0x13` | Base | Wheelbase motor controller |
| 20 | `0x14` | Dash / meter | Legacy Moza MDD display, or a base-bridged CM2 Racing Dash |
| 21 | `0x15` | Wheel (secondary) | Observed in `0x43` broadcasts; purpose undecoded |
| 23 | `0x17` | Wheel (primary) | All known steering wheel models target this address |
| 25 | `0x19` | Pedals | KS Pro firmware exposes pedal sub-device at this ID |
| 26 | `0x1A` | Shifter | H-pattern / sequential — `shifter-type` setting disambiguates |
| 27 | `0x1B` | Handbrake | |
| 28 | `0x1C` | E-stop | Emergency stop button |
| 29 | `0x1D` | (reserved) | Heartbeat receiver only |
| 30 | `0x1E` | (reserved) | Heartbeat receiver only |

See [`../devices/`](../devices/) for the full per-device command catalog
and [`internal-bus.md`](internal-bus.md) for the topology tree (`monitor.json`).

### Telemetry routing

Group-`0x43` screen telemetry is addressed to the device that owns the display:

| Display | Target dev |
|---------|-----------|
| Wheel-integrated screen | `0x17` (wheel) |
| Standalone-USB CM2 | `0x12` (the CM2's own bridge/main, on its own CDC pipe) |
| Base-bridged CM2 | `0x14` (the meter on the wheelbase bus; `0x12` there is the base main) |

The CM2 uses the same group-`0x43` tier-def / value-frame format as a
wheel-integrated display, just at a different target dev. See
[`../devices/dash-0x14.md`](../devices/dash-0x14.md).

### Motion Controller (PID `0x2000`) — a second bus

The motion platform is not a peripheral of the wheelbase. Its controller enumerates as its own
composite (`Gudsen` / `MOZA Motion Controller`, bcdDevice `0x0100`, serial string = its
group-`0x06` UID) with the same three-interface shape, and heads a bus of up to eight HMA150
actuators that reuse the `0x14`–`0x1B` id range:

| Interface | Class | Endpoints | Purpose |
|-----------|-------|-----------|---------|
| MI_00 | CDC ACM control (`0x02 / 0x02`) | `0x81` INTERRUPT IN (8 B, bInterval 16) | CDC notifications; `SET_LINE_CODING` 9600 8N1 at connect |
| MI_01 | CDC data (`0x0A`) | `0x02` BULK OUT / `0x82` BULK IN (**512 B** — high-speed) | Every Moza frame of the motion protocol |
| MI_02 | HID (`0x03`) | `0x03` INTERRUPT OUT / `0x83` INTERRUPT IN (64 B, bInterval 1) | Enumerated, **idle in every capture** |

| Device ID | Hex | Role |
|-----------|-----|------|
| 18 | `0x12` | Motion Controller (bus master; replies as `0x21`) |
| 20–27 | `0x14`–`0x1B` | Actuator ports 1–8 (port *p* = `0x13 + p`); four HMA150s populated on the captured rig |

Frames never cross between this bus and a wheelbase; the shared id values are coincidence.
Full description: [`../motion/README.md`](../motion/README.md).

### Re-enumeration on disconnect

USB disconnect / reconnect (or `sim_reload` from the dev workflow) is the
fastest way to recover PitHouse from a stuck-state — Windows treats it as
a fresh device and PitHouse drops its cached "upload in progress" / "state
syncing" flags, allowing a clean re-handshake. Protocol-level session
close frames alone are sometimes insufficient.
