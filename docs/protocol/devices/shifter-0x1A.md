# H-Pattern (HGP) / Sequential (SGP) Shifter — Device `0x1A` / 26

Both passive shifters share internal-bus device id `0x1A` (26). USB PIDs (VID `0x346E`):
**HGP = `0x001E`**, **SGP = `0x0023`** — see [`usb-ids.md`](usb-ids.md). On their own USB-CDC
pipe they answer as the root `main` device `0x12` (nibble-swap `0x21`), exactly like standalone
pedals/handbrake; when attached behind a base/hub they are addressed at bus id `0x1A`.

Command IDs/groups below are verified against `usb-capture/rs21_parameter.db`
(`ShifterGetCfg_*` / `ShifterSetCfg_*`) and cross-checked with the boxflat/foxblat
`serial.yml` table. Settings: read group `0x51` (81) / write group `0x52` (82); output read
group `0x53` (83); calibration write group `0x54` (84). All payloads 2 bytes, big-endian.

The plugin implements these as the `shifter-*` command family
([`Protocol/MozaCommandDatabase.cs`](../../../Protocol/MozaCommandDatabase.cs)); config surface
via the standalone-peripheral lane + a "Shifter" settings tab.

**Write policy.** Every group `0x52` write is an EEPROM commit on the shifter (its group `0x0E`
debug stream echoes `param_manage.c:346 Table 9, Param N Written` per setting), so the plugin
never pushes the profile blind at detect. Detect arms a reconcile, the per-model `0x51` reads go
out, and each readback writes that one setting only if the profile's value differs
(`HardwareApplier.PrimeShifterCfgFromDevice`). Later profile applies and tab writes go through the
same per-model cache, which is static so a plugin reload cannot re-write what the shifter already
holds. Background: bundle `T3AAXZRX` (v1.6.0, 2026-09-17) reconnected its standalone SGP every
35 s and re-applied five settings on each connect — six Table-9 commits per reconnect, about
1,170 in a two-hour session.

**A shifter that answers nothing is never written by that path.** A missing individual reply is
treated as a dropped frame and released after a 15 s grace, but only once the device has answered
at least one `0x51` read. A shifter that has answered none stays held indefinitely, because a
write is an EEPROM commit aimed at values the host cannot see. Bundle `W3H1TH2S` (v1.6.1,
2026-09-21) is the case: a standalone HGP on its own port ACKed all 74 presence probes and
returned a **zero-length payload** to all 44 settings reads, and the earlier timeout pushed
direction, paddle-sync and hid-mode into it anyway. The Shifter tab still writes on user action,
so such a unit stays configurable by hand. A zero-length `d1`/`d2` reply is itself the signature
of a shifter that is not healthy — every other bundle, standalone and relayed, answers with data.

## Telling HGP from SGP

On its own USB port the two are told apart by **PID** (`0x001E` vs `0x0023`) and nothing else is
needed. Behind a base/hub there is no PID, both answer at bus id `0x1A`, and the only measured
discriminator is the **model-name string** — `0x1A` self-describes.

### The `0x51` settings block does NOT discriminate — measured on a relayed HGP

Bundle `32ZD7KHW` (2026-08-21, ES wheel on an R5 base, HGP on the base's RJ11 shifter port,
no standalone shifter present) has a base-relayed **HGP** answering **every** group `0x51`
read, including the two the tables above list as SGP-only:

| cmd | name | HGP reply (`d1 a1 …`) |
|-----|------|------------------------|
| `01` | hid-mode | `00 00` |
| `02` | shifter-type / apply-mode | `00 00` (= H-pattern, matching the confirmed polarity) |
| `03` | brightness | `00 00` |
| `04` | colors | `00 00` |
| `05` | direction | `00 00` |
| `06` | paddle-sync | `00 01` |

Writes on `0x52` are acked the same way — `52 1a 03` (brightness) and `52 1a 04` (colors) both
returned `d2 a1 …` acks on this HGP, and the shifter's own group `0x0E` debug stream (device
byte `a1`) echoed `param_manage.c:346 Table 9, Param … Written`, so those writes reached
**EEPROM table 9** rather than being dropped. The HGP therefore stores the LED params it has no
LEDs for; whether that is literally the same firmware image as the SGP is not established, but
the settings surface is indistinguishable over the wire.

**Consequence:** a brightness read is *not* an SGP identification. `DeviceProber` used to latch
SGP on this reply, which is why this HGP was reported as an SGP; the model comes from the name
below, and a brightness answer counts only as "a shifter on this pipe is answering settings
reads".

### Group `0x07` model name — the discriminator

Request `7e 01 07 1a 01` → reply `87 a1 01 <ASCII, NUL-terminated>`. Both models name themselves,
and the leading token is the model. Group `0x08` (`7e 01 08 1a 01` → `88 a1 01 …`) carries a
hardware version that splits the same way:

| Device | model name (`0x07`) | hw version (`0x08`) | Bundles |
|---|---|---|---|
| HGP behind a base | `H Shifter # S00` | `RS21-S00-HW SH-C` | `TWV94SPY`, `WS761M92`, `NWS6EY7X` |
| SGP behind a base | `S Shifter # S04` | `RS21-S04-HW SH-C` | `EK7MM2FM`, `ARE6993X`, `MSXCPB40` |

Match the **leading token only**. The `# S0N` suffix begins with `S` on both, as does the
hw-version's `RS21` prefix, so a substring search for "S" matches an HGP.
`DeviceProber.ResolveRelayedShifterModelFromName` latches on the prefix; an unrecognised string
leaves the pipe undecided and the probe running, rather than guessing.

### Group `0x04` device-type — does NOT discriminate

Request `7e 00 04 1a`, reply `84 a1 01 02 08 01` — **the same on both models**:

| Device | dev-type (`01 02 [DT_2] [DT_3]`) | Source |
|---|---|---|
| HGP behind an R5 base | `01 02 08 01` | bundle `32ZD7KHW`, 2026-08-21 |
| SGP behind an R5 base | `01 02 08 01` | bundle `EK7MM2FM`, 2026-09-20 |

Format and per-device caveats: [`../identity/dev-type-table.md`](../identity/dev-type-table.md).
Until 2026-09-20 only the HGP value had been measured, so the plugin latched HGP on a match and
SGP on anything else; since every relayed shifter returns the match, **every** relayed SGP was
reported as an HGP in sequential mode — `EK7MM2FM` is that report, and the plugin then pushed the
HGP profile block at the user's SGP. The reply is now logged as evidence and decides nothing. A
relayed shifter also answers a presence probe (`7e 00 00 1a` → `80 a1`), which says a shifter is
attached but not which one.

**Do not use `shifter-type` / apply-mode as a discriminator** even though the measured HGPs read 0
and the measured SGPs read 1. It is a user-writable setting, and v1.5.1 flipped some HGPs to 1 —
which is exactly the population that most needs correct identification.

A relayed pedal set at `0x19` answers the same identity groups as the wheel (see
[`../identity/pedal-0x19.md`](../identity/pedal-0x19.md) § Parser routing), so `ProbeRelayedShifter`
now also reads model-name (`0x07 01`) and hw-version (`0x08 01`) at `0x1A` and logs whatever comes
back — `7e 01 07 1a 01 ae` / `7e 01 08 1a 01 af`, untracked, first two probe rounds only. Whether
`0x1A` answers them is **unmeasured**; a self-describing name string would retire the dev-type
magic value entirely.

Note that MOZA's own model has **one** shifter product: `rs21_parameter.db` `ServiceParameter`
rows all sit under `pithouse://classify.moza-racing.com/Product/Shifter`, LED params included —
there is no separate HGP/SGP category. The HGP/SGP split is plugin-side.

## H-Pattern Shifter (Device `0x1A` / 26)

### Group `0x51` / `0x52` (81 / 82) — Settings

| Command | ID | Bytes | Type | Range | Notes |
|---------|----|-------|------|-------|-------|
| hid-mode | `01` | 2 | int | {0,1} | game-compat mode |
| shifter-type / apply-mode | `02` | 2 | int | {0,1} | DB name `ShifterApplyMode` ("game apply mode") |
| direction | `05` | 2 | int | {0,1} | reverse shift output direction |
| paddle-sync | `06` | 2 | int | {1,2} | wheel-paddle sync (default 1) |

### Group `0x53` (83) — Output (read-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| output-x | `01` | 2 | int | |
| output-y | `02` | 2 | int | |

### Group `0x54` (84) — Calibration (write-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| calibration-start | `03` | 2 | int | |
| calibration-stop | `04` | 2 | int | |

---

## Sequential Shifter (Device `0x1A` / 26)

Shares device ID `0x1A` and group numbers with the H-pattern shifter. Distinguish by command IDs or the `shifter-type` setting.

### Group `0x51` / `0x52` (81 / 82) — Settings

| Command | ID | Bytes | Type | Range | Notes |
|---------|----|-------|------|-------|-------|
| hid-mode | `01` | 2 | int | {0,1} | |
| shifter-type / apply-mode | `02` | 2 | int | {0,1} | `ShifterApplyMode` |
| brightness | `03` | 2 | int | [0,10] | LED brightness (default 10) |
| colors | `04` | 2 | array | — | **the 2 LEDs** — see below |
| direction | `05` | 2 | int | {0,1} | |
| paddle-sync | `06` | 2 | int | {1,2} | default 1 |

#### The 2 LEDs (`colors`, cmd `0x04`)

The SGP has **2 RGB LEDs (S1, S2)** set via a single command whose 2-byte payload is
`[S1, S2]` — **each byte is a palette INDEX 0–7, not an RGB triplet** (DB params
`ShifterLedRgbColor_1` / `ShifterLedRgbColor_2`, both int8 `[0,7]`, default 0). They are a
stored/static setting (read back on group `0x51`), *not* a live telemetry stream. Fixed
8-colour palette (index → approximate swatch, matching PitHouse / foxblat `data/style.css`):

| Index | Colour | Hex |
|-------|--------|-----|
| 0 | Red | `#cf2727` |
| 1 | Orange | `#dfa500` |
| 2 | Yellow | `#dfdf3a` |
| 3 | Green | `#3a903a` |
| 4 | Cyan | `#00d0d0` |
| 5 | Blue | `#3a3aff` |
| 6 | Purple | `#802080` |
| 7 | White | `#dddddd` |

### Group `0x53` (83) — Output (read-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| output-x (theta) | `01` | 2 | int | raw axis; DB `ShifterGetState_ShifterTheta` |
| output-y | `02` | 2 | int | (boxflat table; not in the local DB) |

## Automatic downshift throttle-blip (HGP) — host-side, NO wire command

The HGP "auto-blip" (blip the throttle on a downshift for rev-matching) has **no MOZA wire
command** — a search of all 919 commands in `rs21_parameter.db` finds nothing blip/handing-shifter
related, and the SDK exposes it only as host-side free functions
(`get/setHandingShifterAutoBlipOutput` 0–100, `…AutoBlipDuration` 0–1000 ms, `…AutoBlipSwitch` 0/1;
see [`../../sdk/api-inventory.md`](../../sdk/api-inventory.md) §3.9, all marked `[G]`/gap). It is
implemented in host software: foxblat detects a single-gear downshift from HID and injects a
synthetic throttle-axis value via Linux evdev for the configured duration. On Windows a SimHub
plugin has no throttle-output path, so this feature is **not implemented** and would require a
virtual-controller (ViGEm/vJoy) approach.
