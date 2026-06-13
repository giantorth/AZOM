## Handbrake (Device `0x1B` / 27)

### Identity (live probe 2026-06-12, R5 base)

Unlike the ES wheel (`0x18`) and integrated pedals (`0x19`) — which are modules
of the base MCU and share its UID/sw-version — the handbrake at `0x1B` is
**separate silicon** with its own MCU UID and sw-version:

| Field | Handbrake (dev 0x1B) |
|-------|----------------------|
| name (`0x07`) | `HB # S01` |
| hw_version (`0x08`) | `RS21-S01-HW HB-C` (`HB` = Handbrake module) |
| sw_version (`0x0F`) | `RS21-S01-MC HB` (distinct from the base `RS21-D05-MC WB`) |
| mcu_uid (`0x06`) | own UID (`…WHH096`), **not** shared with base/wheel/pedals |
| dev_type (`0x04`) | `01 02 03 01` |

See [`../identity/known-wheel-models.md`](../identity/known-wheel-models.md)
§ ES wheel identity for the full base device-id map.

### Group `0x5B` / `0x5C` (91 / 92) — Settings

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| direction | `01` | 2 | int | |
| min | `02` | 2 | int | |
| max | `03` | 2 | int | |
| hid-mode | `04` | 2 | int | |
| y1 | `05` | 4 | float | Curve point |
| y2 | `06` | 4 | float | |
| y3 | `07` | 4 | float | |
| y4 | `08` | 4 | float | |
| y5 | `09` | 4 | float | |
| button-threshold | `0A` | 2 | int | |
| mode | `0B` | 2 | int | |

### Group `0x5D` (93) — Output (read-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| output | `01` | 2 | int | |

### Group `0x5E` (94) — Calibration (write-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| calibration-start | `03` | 2 | int | |
| calibration-stop | `04` | 2 | int | |
