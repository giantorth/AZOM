### Dev 0x19 is pedal (newer firmware)

> **KS Pro / 2026-04+ firmware only.** Capture: `usb-capture/ksp/putOnWheelAndOpenPitHouse.pcapng`. See [`../FIRMWARE.md`](../FIRMWARE.md) for the firmware-era matrix.

`23:19:*` and `25:19:*` probe families (observed in KS Pro capture) target a pedal sub-device. Real KSP capture shows 70+ identity responses from dev 0x19 with `RS21-D01-MC PB` / `RS21-D01-HW PM-C` strings and UTF-8 debug log stream (param writes, calibration status). Sim's wheel-only personality drops these probes silently; PitHouse keeps re-probing. Covered in `sim/replay/kspro_pedal_19.json` (92 entries) for KS Pro profile only.

### Pedal device 0x19 identity (KS Pro capture)

Extracted byte-exact 2026-04-23 from `usb-capture/ksp/putOnWheelAndOpenPitHouse.pcapng`:

| Field | Pedal (dev 0x19) |
|-------|------------------|
| name | `SRP` (null-padded) |
| hw_version | `RS21-D01-HW PM-C` |
| hw_sub | `U-V11` |
| sw_version | `RS21-D01-MC PB` |
| dev_type | `01 02 02 05` |
| caps | `01 02 18 00` |
| hw_id | `20 00 28 00 04 57 48 41 32 34 33 20` (`" .(..WHA243 "`) |
| identity_09 | `00 04` (hub/base return `00 01`) |

Sim's `pedal_identity` in `WHEEL_MODELS['kspro']` covers these. PitHouse probes pedal on KS Pro captures; sim answers via procedural per-device identity dispatch.

### SR-P Lite on R5 base (live probe 2026-06-12)

On a base that integrates pedals into the base MCU, `0x19` is a **base-MCU module**, not separate silicon — it shares the base's MCU UID and sw-version and is distinguished by model-name + hw module code (`PM` = Pedal Module):

| Field | Pedal (dev 0x19) |
|-------|------------------|
| name | `SR-P Lite` |
| hw_version | `RS21-D05-HW PM-C` |
| sw_version | `RS21-D05-MC WB` (shared with base `0x12/0x13` and ES wheel `0x18`) |
| mcu_uid | shared with base / ES wheel |
| dev_type | `01 02 10 09` (same as base — not distinguishing) |

Contrast the KS Pro pedal above (`RS21-D01-*`, own `hw_id`), which is a separate device. See [`known-wheel-models.md`](known-wheel-models.md) § ES wheel identity for the full device-id map.

### Parser routing — the identity groups are shared

A relayed pedal set answers the *same* identity groups as the wheel (`0x04`
device-type, `0x07` model-name, `0x09` presence, `0x10` serial), distinguished
only by the device byte. `MozaResponseParser` therefore hints dev `0x19` →
`pedals` (and `0x1B` → `handbrake`), exactly as it does for `0x12`/`0x13`/`0x1A`.

Without that hint the reply fell through to the first entry in the group bucket —
the `wheel-*` command — so a pedal set answering the routed-mBooster identity
probe wrote **its** name and serial into the wheel's identity fields. On bundle
`65HZBQJT` (R12 + KS + SRP) `87 91 01 "SRP"` parsed as `wheel-model-name`, which
`DeviceProber` read as a wheel model change `KS` → `SRP` and treated as a
hot-swap: full detection reset plus a `PendingResponseTracker.Clear()` that
dropped the base identity reads still in flight ~240 ms after they were sent —
including `base-fw-version`, which is re-issued only once per base detect and
gates the wheelbase LFE effects. The diagnostics dump also reported the pedals'
serial as the wheel's.
