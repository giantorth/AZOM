# Devices — index and command tables

Per-device pages with device IDs, group breakdowns, and full command tables. For frame format / checksum / wire-level rules see [`../wire/`](../wire/). For functional cross-cuts (telemetry, sessions, dashboards, LEDs) see the sibling folders under [`../`](../).

For the host-side USB Vendor/Product ID inventory and how the plugin routes each PID to the right connection class, see [`usb-ids.md`](usb-ids.md).

## Protocol constants

| Name | Dec | Hex | Notes |
|------|-----|-----|-------|
| Frame start byte | 126 | `0x7E` | First byte of every frame |
| Checksum magic | 13 | `0x0D` | Added to the running byte sum before mod 256 |

## Device IDs

All communication goes over a single COM serial interface. Device IDs are addresses on the internal bus — the wheelbase hub routes each frame to the correct peripheral.

| Device | Dec | Hex | File | Notes |
|--------|-----|-----|------|-------|
| main / hub | 18 | `0x12` | [`main-hub-0x12.md`](main-hub-0x12.md) | Base USB address; hub enumeration shares this ID. Identity bytes are byte-identical to dev `0x13` (see [`../identity/hub-base-cascade.md`](../identity/hub-base-cascade.md)) |
| base | 19 | `0x13` | [`wheelbase-0x13.md`](wheelbase-0x13.md) | Wheelbase motor controller |
| dash | 20 | `0x14` | [`dash-0x14.md`](dash-0x14.md) | Standalone Moza MDD display peripheral |
| wheel | 21 | `0x15` | — | Secondary wheel address — observed in group `0x43` broadcasts, purpose unclear |
| wheel | 23 | `0x17` | [`wheel-0x17.md`](wheel-0x17.md) | Primary steering wheel address used by all standard (separate-MCU) models. Identity probes in [`../identity/`](../identity/). **Silent on ES wheels** — those answer at `0x18` |
| ES wheel | 24 | `0x18` | — | ES (entry) wheel — a *module of the wheelbase MCU*, not a separate device. `0x17` is silent on ES; its real identity (model `ES`, hw `…SM-C`) is read at `0x18` and shares the base MCU UID + sw-version. See [`../identity/known-wheel-models.md`](../identity/known-wheel-models.md) § ES wheel identity |
| pedals | 25 | `0x19` | [`pedals-0x19.md`](pedals-0x19.md) | Pedal set. On a base that integrates pedals (e.g. R5 + SR-P Lite) this is another base-MCU module (hw `…PM-C`, shared UID). Identity quirks in [`../identity/pedal-0x19.md`](../identity/pedal-0x19.md) |
| hpattern / sequential | 26 | `0x1A` | [`shifter-0x1A.md`](shifter-0x1A.md) | H-pattern and sequential shifter share this device ID; distinguish via `shifter-type` setting |
| handbrake | 27 | `0x1B` | [`handbrake-0x1B.md`](handbrake-0x1B.md) | |
| estop | 28 | `0x1C` | [`estop-0x1C.md`](estop-0x1C.md) | Emergency stop button |
| AB9 active shifter | 18 | `0x12` | [`ab9-shifter.md`](ab9-shifter.md) | Separate USB composite (VID `0x346E` PID `0x1000`) with its own dev `0x12`. Writes on `Group 0x1F`, reads on `Group 0x1E` (1-byte cmd payload, 2-byte BE responses on `0x9E`), engine-vibration multi-stream on `Group 0x20` |

Response device IDs have their nibbles swapped: base `0x13` → response `0x31`, wheel `0x17` → `0x71`, ES wheel `0x18` → `0x81`, etc. Response group IDs have `0x80` added. See [`../wire/frame-format.md`](../wire/frame-format.md) for full response encoding rules.

**Base-integrated modules.** On bases that integrate the wheel and/or pedals into the base MCU (e.g. R5 + ES + SR-P Lite), several device ids share one MCU UID + sw-version and are distinguished only by model-name (group `0x07`) and a hardware **module code** in the hw-version (group `0x08`) suffix:

| Code | Module | Example dev id |
|------|--------|----------------|
| `BM` | Base Module | `0x12` / `0x13` |
| `SM` | Steering Module (ES wheel) | `0x18` |
| `PM` | Pedal Module (integrated pedals) | `0x19` |
| `HB` | Handbrake | `0x1B` (own MCU UID + sw-version) |

A handbrake (`0x1B`) is separate silicon — its UID and sw-version differ from the base. Verified live (R5 base + ES wheel + SR-P Lite + handbrake, 2026-06-12).

## Cross-cutting commands (all devices)

- EEPROM direct access (group `0x0A`) — applicable to any device, see [`../settings/eeprom-0x0A.md`](../settings/eeprom-0x0A.md)

## Command table format

Each device section is organized by group. Within a group, the **ID** column shows the command ID bytes (1–4 bytes, big-endian hex). **Bytes** is the payload size (value bytes only, not the ID). **Dir** is `R` (read-only), `W` (write-only), or `RW` (both, using the same group).

When a command is truly read/write and uses the same ID in both directions, the group is shown as `read / write`. When read and write use the same group number, `Dir` disambiguates.

## Authoritative source

`Pit House/bin/rs21_parameter.db` — SQLite DB with 919 commands across 23 groups. Device-side tables here are derived from that DB and from USB capture analysis. See [`../telemetry/service-parameter-transforms.md`](../telemetry/service-parameter-transforms.md) for value-encoding rules.
