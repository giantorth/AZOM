## Pedals (Device `0x19` / 25)

### Group `0x23` / `0x24` (35 / 36) — Settings

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| throttle-dir | `01` | 2 | int | |
| throttle-min | `02` | 2 | int | |
| throttle-max | `03` | 2 | int | |
| brake-dir | `04` | 2 | int | |
| brake-min | `05` | 2 | int | |
| brake-max | `06` | 2 | int | |
| clutch-dir | `07` | 2 | int | |
| clutch-min | `08` | 2 | int | |
| clutch-max | `09` | 2 | int | |
| compat-mode | `0D` | 2 | int | |
| throttle-y1 | `0E` | 4 | float | Curve points — spline knots for pedal response shaping |
| throttle-y2 | `0F` | 4 | float | |
| throttle-y3 | `10` | 4 | float | |
| throttle-y4 | `11` | 4 | float | |
| throttle-y5 | `1B` | 4 | float | |
| brake-y1 | `12` | 4 | float | |
| brake-y2 | `13` | 4 | float | |
| brake-y3 | `14` | 4 | float | |
| brake-y4 | `15` | 4 | float | |
| brake-y5 | `1C` | 4 | float | |
| clutch-y1 | `16` | 4 | float | |
| clutch-y2 | `17` | 4 | float | |
| clutch-y3 | `18` | 4 | float | |
| clutch-y4 | `19` | 4 | float | |
| clutch-y5 | `1D` | 4 | float | |
| brake-angle-ratio | `1A` | 4 | float | |
| throttle-hid-source | `1E` | 2 | int | |
| throttle-hid-cmd | `1F` | 2 | int | |

### Group `0x25` (37) — Output (read-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| throttle-output | `01` | 2 | int | |
| brake-output | `02` | 2 | int | |
| clutch-output | `03` | 2 | int | |

### Group `0x26` (38) — Calibration (write-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| throttle-calibration-start | `0C` | 2 | int | |
| brake-calibration-start | `0D` | 2 | int | |
| clutch-calibration-start | `0E` | 2 | int | |
| throttle-calibration-stop | `10` | 2 | int | |
| brake-calibration-stop | `11` | 2 | int | |
| clutch-calibration-stop | `12` | 2 | int | |

### On its own USB port the pedal set is `0x12`, not `0x19`

`0x19` is the **bus** sub-device id, used when a wheelbase or Universal Hub
relays the pedals. A set plugged straight into the PC gets its own CDC port
(PID `0x0001` CRP/CRP2, `0x0011` CRP2 variant, `0x0003` SRP) and is the root
device on that pipe, so every read/write is addressed to `main` (`0x12`) and
answers as `0x21`. `StandalonePeripheralController` overrides its
`MozaDeviceManager` to `DeviceMain` for exactly this reason, and passes
`"pedals"` as the parser's `busHint` so group `0x23`/`0x24` replies bind to
`pedals-*` rather than the `mbooster-*` commands that share those groups.

Confirmed on a CRP2 (bundle `20260812-205845`): `7e 03 24 12 03 00 63`
(`pedals-throttle-max` = 99) is acked `a4 21 03 00 63`, and the firmware echoes
`param_manage.c:340 Table 6, Param 11 Written: 99` on group `0x0E`. The same
unit emits an unsolicited `pedal_diagnostic.c` block on `0x0E` roughly every
60 s (`PD Linked:[T 1 B 1 C 0]`, per-pedal min/max/angle, `Sensor Dir`,
`P-Sens raw`), which is wheel-alive evidence for a pedals-only rig.

Settings are read on connect on both topologies: a relayed set from
`DeviceProber.MarkPedalsDetected`'s `issueReads` path, a standalone one from
`StandalonePeripheralDescriptor.Pedals.SettingsReadCommands`. Until the reads
answer, the Pedals tab shows `MozaData`'s placeholder defaults (throttle
max 100, brake-angle-ratio 50, curve 20/40/60/80/100) rather than the device's
stored calibration — `PedalsSettingsRead` (in the diagnostics bundle's
"Standalone peripherals" panel as `settingsRead=`) is what distinguishes the
two.
