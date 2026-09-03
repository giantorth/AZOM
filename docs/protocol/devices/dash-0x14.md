## Dash / meter (Device `0x14` / 20)

Device `0x14` is the dashboard/meter address on the internal serial bus. Several
physical peripherals use it: the legacy Moza MDD display, the CM2 Racing Dash when
attached behind a wheelbase (tier-def, group `0x43`), and the **CM1 dash** when
bridged behind a wheelbase (a fixed-schema keyed push on group `0x35` — it does NOT
speak tier-def). Steering wheels with integrated display screens are a separate
device at `0x17`.

### CM2 Racing Dash

The CM2 (USB PID `0x0025`) connects in one of two topologies:

| Topology | Transport | Screen-telemetry target dev |
|----------|-----------|-----------------------------|
| Standalone USB | Own USB CDC device on its own COM port | `0x12` (CM2 bridge/main) |
| Behind the wheelbase | Bridged on the wheelbase serial bus as the meter at `0x14` | `0x14` (`0x12` there is the base main) |

- **Detection is by USB PID enumeration** (registry / `usbser`); no COM-port
  scanning. A standalone CM2 is claimed on a dedicated dashboard connection
  filtered to PID `0x0025`. A base-bridged CM2 has no own USB port and is the
  dash sub-device at `0x14` on the wheelbase connection.
- **Identity:** the CM2 answers the group-`0x43` identity probe with display
  model `S09 Display` (HW/SW `RS21-W08`, device type `01-02-08-06`).
- **The CM2 is driven by the group-`0x43` telemetry session pipeline** — tier
  definitions on session `0x01`, value frames on session `0x02`, routed by
  FlagByte. It acks host session opens with `fc:00` on sessions `0x01`/`0x02`.
  For a base-bridged CM2 the whole pipeline targets dev `0x14`; PitHouse runs the
  session + value stream there and `0x14` answers with b2h session chunks
  (`cm2.pcapng` 2026-06-08).
- **CM2 RPM/flag LEDs depend on the meter FIRMWARE ERA** (see § "CM2 LED firmware
  eras" below). Two distinct live-LED paths exist; the plugin auto-detects which
  and picks the matching emitter.

#### CM2 LED firmware eras

The CM2 meter firmware was reworked around 2026-06, replacing the LED subsystem.
The eras are distinguishable from the meter's own `0x0E` firmware-debug heartbeat
and drive LEDs by completely different wire commands. The heartbeat's `src` byte
follows the topology: a base-bridged CM2 logs as the dash sub-device (`src=0x41`
on the wheelbase pipe), while a standalone-USB CM2 logs as its own main MCU
(`src=0x21` on the dashboard pipe) — same `meter_diag.c` vocabulary on both
(bundle `CGZC7E3A` 2026-08-01: `[INFO]meter_diag.c:88 Release Heartbeat Log:` /
`IndicatorMode:` / `StandbyMode:` inbound as `0e 21 05 …` on the CM2's own port,
~48 s after connect, ~1/min).

| | **Legacy RPM-ramp** (≤ 2026-06) | **Indicator** (2026-06+) |
|---|---|---|
| Heartbeat vocabulary | `RpmMode`, `RpmNumber[0~9]`, `RpmPercent[0~9]` | `IndicatorMode`, `StandbyMode`, `SleepMode`, `SleepSwitches` |
| `meter_diag.c` line | `:89` | `:88` |
| Config params (`Table 7`) | ramp thresholds at 60–69 / 75–84, modes 90/92/94 | packed RGB at 21–36, mode 6, brightness 8 |
| Live LED driver | `0x41 FD DE <u32 bitmask>` to `0x14`; firmware lights each set bit in its stored/ramp colour | group-`0x32` live path (below) |
| Reference capture | `cm2.pcapng` (2026-06-08) | `cm2(1).pcapng` |

On the **legacy** firmware the autonomous RPM ramp (driven by the one-time
group-`0x32` config push — `1B 00 FF <idx>` stored colours, `05`/`0E` thresholds,
`18`/`11`/`0D` modes; see [`main-hub-0x12.md`](main-hub-0x12.md) § "CM2 bridge/main
routing") lights the bar from the RPM value alone; the `41 FD DE` bitmask is the
per-frame on/off overlay. PitHouse drives it per frame (`cm2.pcapng`: `7E .. 41 14
FD DE <u32>`).

On the **indicator** firmware the autonomous ramp and the `41 FD DE` / `0B` live
registers are **gone** (the bitmask is still sent by PitHouse but always `00000000`
— vestigial parity). LEDs are driven live, wheel-style, on group `0x32`
(`cm2(1).pcapng`, PitHouse on an updated CM2; frames reproduce byte-exact under
`MozaProtocol.CalculateWireChecksum`):

```
colours  7E <6N> 32 14 13 00 [ <idx> <R> <G> <B> ] * N   <csum>   (~14.5 Hz driving)
bitmask  7E 0A   32 14 14 00 <active u32 LE> <window u32 LE> <csum> (~2.4 Hz)
```

- **Colours** (`13 00`): the full 16-LED strip in physical order
  `[flag 1-3][RPM 1-10][flag 4-6]`, 5 LEDs per 20-byte chunk, 4 chunks
  (0-4, 5-9, 10-14, 15 — last chunk short). `idx` is the absolute 0-based LED.
- **Bitmask** (`14 00`): `window` is fixed at `0x00001FF8` (LEDs 3..12 = the 10
  RPM positions); `active` sets the lit RPM bits within it. Flags (0-2, 13-15) are
  outside the window — driven by colour presence alone (no flag-lit capture on this
  firmware yet, so flag on/off semantics are unconfirmed).

Plugin handling: `MozaPlugin.Cm2NewLedFirmware` (persisted) is set by
`DetectCm2LedFirmwareEra` from the heartbeat vocabulary (bidirectional, so a
downgrade recovers). `MozaDashLedDeviceManager.DisplayNewEra` emits the group-`0x32`
path via `cm2-live-colors` / `cm2-live-bitmask`; the legacy path is unchanged.
Earlier note (still true for legacy fw): a build that streamed group-`0x32` sub
`0x0B` to dev `0x12` did nothing — that firmware ignores it.

### CM1 Racing Dash — group `0x35` keyed value stream

The CM1 is a base-bridged dash that does **not** speak tier-def. Like the FSR1 wheel
(group `0x42`, see [`wheel-0x17.md`](wheel-0x17.md)) it is driven by a fixed-schema
display push, but the layout differs: where the FSR1 uses positional records, the CM1
uses a **flat keyed value stream** on group `0x35` to dev `0x14`.

Decoded from `FSR1_CM1.pcapng` (PitHouse driving an FSR1 wheel + CM1 dash) via
`tools/cm1-0x35-decode`.

**Frame layout**

```
7E <6N> 35 14  [ <keyHi><keyLo> <float32 BE> ] * N   <csum>
```

- Each record is **6 bytes**: a 16-bit field key (wire order `keyHi keyLo`) + a
  **big-endian IEEE-754 float32** value. The high key byte clusters `0xDA`/`0xF5`/`0xD9`
  but is **part of the key, not a type tag** — every value is a BE float32.
- The full flat field set (67 keys, e.g. `0xf54d`, `0xdaa1`) streams **round-robin,
  10 records per frame**, cycling continuously (~80 Hz frame rate; ~14-20 Hz per field).
  The same set streams regardless of which dashboard is selected — a switch only changes
  what the dash *displays*.
- **Two lanes, split by volatility.** Group `0x35` is the **fast lane** — 53 keys at ~90
  Hz (speeds, RPM, gear, pedals, tyre temps/pressures, brake temps, current lap time, DRS).
  Group `0x36` is the **slow lane** — 14 keys at ~3.3 Hz, identical record format, carrying
  the session / slow-changing fields: track & air temp, fuel (remaining / range) + oil
  pressure, current lap, position, car count, last & best lap time, fuel %. The two key sets
  are **disjoint** (a key is on exactly one lane). The plugin mirrors this split (`Cm1FieldDef.Slow`).
- Checksum is the standard wire checksum (`MozaProtocol.CalculateWireChecksum`). Encoding
  proven byte-exact: 37319/37319 captured `0x35` frames reproduce under BE-float32 + that
  checksum.

**Why big-endian float32:** in the capture's driving window (car moving, ~t260s+) the
values resolve to clean physical quantities only under BE-float — per-wheel quads at
~25 (pressure), ~50/~120 (temps), pedals at 0..1. Little-endian int/float yield garbage.
With the car parked (start of the capture) almost everything sits at 0, which is why a
**driving** capture is required to confirm encoding and to map fields.

**Handshake (host-initiated)**

| Frame | Bytes | Dash reply |
|-------|-------|-----------|
| Presence probe | `7E 00 00 14` | group `0x80` (`7E 00 80 41 …`) |
| Session ping (~1 Hz) | `7E 01 43 14 00` | group `0xC3` (`7E 00 C3 41 …`) |
| Param read (init sweep) | `7E 03 0E 14 00 <reg_hi> <reg_lo>` | group `0x8E` (`7E 07 8E 41 <reg:3 echoed> <BE u32>`) |

There is **no tier-def catalog** — the dash never advertises channels on group `0x43`
(only the 1-byte ping). This absence is how the plugin distinguishes a CM1 from a real
tier-def CM2 (see below).

**Param-manager register reads — group `0x0E` / `0x8E`**

At connect PitHouse sweeps a register-read interface on the dash (the dash's own
firmware-debug lines reference `param_manage.c`). Each read echoes the 16-bit
register address and returns a 32-bit big-endian value:

```
host →  7E 03 0E 14  00 <reg_hi> <reg_lo>              (read register, 16-bit addr)
dash →  7E 07 8E 41  <reg_hi'> <reg_lo'> 00 <BE u32>   (addr echoed + value)
```

Checksum is the standard wire checksum. Decode with `tools/cm1-0e-register-decode`
(checksum-validated, so the dense group-`0x35` stream and the co-bus FSR1 wheel don't
produce false matches).

The captured sweep (`FSR1_CM1.pcapng`, PitHouse → FSR1 wheel + CM1 dash) reads **49
registers** across four banks. `0xFFFF8000` (int32 −32768) is an "unset/NA" sentinel.

| Bank | Registers | Non-sentinel values (reg → dec) |
|------|-----------|---------------------------------|
| `0x0001–0x0014` | 20 | 1→27, 2→22, 3→26, 4→35, 5→24, 7→12, 9→23, 10→32, 13→825 (rest sentinel) |
| `0x012C–0x0140` | 21 | 300→875, 301→51, 302→9318, 303→0, 304→0, 305→223, 307→70, 309→164, 310→91, 313→843 (rest sentinel) |
| `0x0190–0x0191` | 2 | 400→839, 401→878 |
| `0x0BB8–0x0BBD` | 6 | 3000→100, 3001→0, 3002→0, 3003→0, 3004→1, 3005→62 |

**Interpretation is unconfirmed.** The values are plain numeric params, not an ASCII
identity/model string, so this is *not* a "CM1 vs CM2" name probe by itself. The
round-address high banks (`0x0190+`, `0x0BB8+` = decimal 400 / 3000) are plausibly
**firmware version / build / device-info** registers (e.g. `0x0BBC`=1, `0x0BBD`=62 could
be a major/build pair), but nothing here is anchored to a known FW version, and there is
no CM2 dump of the same registers to diff against. To turn any of these into a fast,
positive CM1↔CM2 discriminator (replacing the ~25 s catalog-absence timeout), capture a
**CM2** answering the same `0x0E` reads and diff the two snapshots — a register whose
value differs by device type is the discriminator.

**Dashboard switching**

Identical command family to the FSR1, addressed to dev `0x14`:

```
host →  7E 05 32 14 81 00 00 00 <BE32 index>     (group 0x32 cmd 0x81; index 1-based, 1..13 observed)
dash →  7E 05 B2 41 81 00 00 00 <BE32 index>     (group 0xB2 ack)
```

The dash also reports its current page via the **same** firmware-debug log the FSR1 emits,
on dev `0x41`:

```
[INFO]param_manage.c:344 Table 7, Param 6 Written: N
```

so the plugin follows dash-initiated (button) switches with the FSR1 regex.

**Field semantics**

Each 16-bit key identifies a MOZA telemetry channel (a `v1/gameData/<path>`): the low 15
bits are the channel's index in MOZA's channel catalog and the **top bit is set on the
wire**. Every key thus maps to its SimHub property via `Data/Telemetry.json` (MOZA's own
channel catalog); the mapping is confirmed for all 67 observed keys and every one is
user-overridable via the dash page's channel mapper.

Values are raw physical quantities as BE-float32 — no bit-packing / bias artifacts (the
FSR1's `+300` / `×1000` do **not** apply here). Groups:

- **Per-wheel quads** — tyre temps (base FL/FR/RL/RR plus inner/middle/outer variants),
  tyre pressures, and brake temps. Each temperature also appears as a **°F unit-variant
  key** (`&unit=F`), for which the plugin sends °C→°F (`Scale 1.8, Bias 32`).
- **Scalars** — speeds (mph / km/h / m/s), RPM + max RPM, gear, pedals (throttle / brake /
  clutch, 0..1), current lap / position / car-count / lap-count, lap times
  (current / last / best / estimated), fuel (% / range / remaining), ERS / ABS / TC levels,
  L/R blinkers, engine-enabled, oil pressure, track / air temp, odometers, front ARB.
- Two keys have no generic SimHub channel (a track-name string, a kPa-unit pressure) and
  ship unmapped for the user to assign.

Earlier drafts shipped this as a flat catalog of range-guessed labels with blank default
mappings; that guesswork was wrong on several fields (e.g. the brake-temp quad had been
read as "speed", and the constant-`32.0` fields are real odometer / temperature-aggregate
channels that simply read 0 in a parked capture).

**Plugin handling**

- `Telemetry/Display/Cm1DashboardCatalog.cs` — flat field set (keys + labels + SimHub channel +
  per-field Scale/Bias for °F unit-variants).
- `Telemetry/Display/Cm1DisplayEmitter.cs` — frame/handshake/switch builders (BE-float32).
- `Telemetry/Display/Cm1DisplayDriver.cs` — standalone ~50 ms driver on the wheelbase connection,
  dash-lane stream slots 18-28 (disjoint from the wheel lane 0-8), so it runs concurrently
  with an FSR1/tier-def wheel screen.
- **Discriminator** (`MozaPlugin.TickCm1Discriminator`): a bus-bridged dash first gets the
  tier-def `_cm2Sender` with its engagement watchdog **suppressed**
  (`TelemetrySender.SuppressDisplayWatchdog`); if no catalog arrives within ~25 s it is
  latched as a CM1 (`DashIsCm1`, persisted per dash GUID), the tier-def sender is torn down,
  and the CM1 driver takes over. A real CM2 (catalog arrives) is unaffected. Known-CM1 boots
  start the CM1 driver immediately and never run the tier-def probe.

### Group `0x32` / `0x33` (50 / 51) — Settings (legacy MDD / wheel-dash)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| rpm-timings | `05` | 10 | array | |
| rpm-display-mode | `07` | 1 | int | |
| flag-colors | `08 00` | 18 | array | Write-only |
| rpm-blink-color1 | `09 00` | 3 | array | RGB; write-only |
| rpm-blink-color2 | `09 01` | 3 | array | |
| rpm-blink-color3 | `09 02` | 3 | array | |
| rpm-blink-color4 | `09 03` | 3 | array | |
| rpm-blink-color5 | `09 04` | 3 | array | |
| rpm-blink-color6 | `09 05` | 3 | array | |
| rpm-blink-color7 | `09 06` | 3 | array | |
| rpm-blink-color8 | `09 07` | 3 | array | |
| rpm-blink-color9 | `09 08` | 3 | array | |
| rpm-blink-color10 | `09 09` | 3 | array | |
| rpm-brightness | `0A 00` | 1 | int | |
| flags-brightness | `0A 02` | 1 | int | |
| rpm-color1 | `0B 00 00` | 3 | array | RGB |
| rpm-color2 | `0B 00 01` | 3 | array | |
| rpm-color3 | `0B 00 02` | 3 | array | |
| rpm-color4 | `0B 00 03` | 3 | array | |
| rpm-color5 | `0B 00 04` | 3 | array | |
| rpm-color6 | `0B 00 05` | 3 | array | |
| rpm-color7 | `0B 00 06` | 3 | array | |
| rpm-color8 | `0B 00 07` | 3 | array | |
| rpm-color9 | `0B 00 08` | 3 | array | |
| rpm-color10 | `0B 00 09` | 3 | array | |
| flag-color1 | `0B 02 00` | 3 | array | |
| flag-color2 | `0B 02 01` | 3 | array | |
| flag-color3 | `0B 02 02` | 3 | array | |
| flag-color4 | `0B 02 03` | 3 | array | |
| flag-color5 | `0B 02 04` | 3 | array | |
| flag-color6 | `0B 02 05` | 3 | array | |
| rpm-mode | `0D` | 1 | int | |
| rpm-value1 | `0E 00` | 4 | int | RPM threshold for LED 1 |
| rpm-value2 | `0E 01` | 4 | int | |
| rpm-value3 | `0E 02` | 4 | int | |
| rpm-value4 | `0E 03` | 4 | int | |
| rpm-value5 | `0E 04` | 4 | int | |
| rpm-value6 | `0E 05` | 4 | int | |
| rpm-value7 | `0E 06` | 4 | int | |
| rpm-value8 | `0E 07` | 4 | int | |
| rpm-value9 | `0E 08` | 4 | int | |
| rpm-value10 | `0E 09` | 4 | int | |
| rpm-indicator-mode | `11 00` | 1 | int | 0=Off, 1=RPM, 2=On |
| rpm-interval | `0C` | 4 | int | |
| flags-indicator-mode | `11 02` | 1 | int | 0=Off, 1=Flags, 2=On |
