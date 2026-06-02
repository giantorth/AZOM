### Known wheel model names

Model name is returned by group `0x07` cmd `01` (16-byte null-padded
ASCII string). Some wheels split a longer name across `0x07/01` and
`0x07/02`. See [`wheel-probe-sequence.md`](wheel-probe-sequence.md) for
the full identity probe order.

### Confirmed (USB capture or live serial probe)

| Model name | Wheel | LED layout | Capability flags `0x05` | Source |
|------------|-------|------------|-------------------------|--------|
| `VGS` | Vision GS | 8 button LEDs, no flag LEDs | `01 02 1F 01` | `cs-to-vgs-wheel.ndjson` |
| `CS V2.1` | CS V2 | (no integrated screen) | `01 02 26 00` | `vgs-to-cs-wheel.ndjson` |
| `W17` | CS Pro | 16 RPM LEDs (single series), 8 button LEDs, no flag LEDs, 4 knobs | n/a | physical hardware inspection |
| `FSR` | FSR **V1** display wheel (box "FSR1") | integrated display; 10 RPM LEDs, 10 light-up buttons (group `0x3F` live telemetry observed) | not captured | `usb-capture/fsr1/FSR1 with game.pcapng` |
| `Display` | Display sub-device (inside VGS-class wheels) | n/a | `01 02 00 00` | wrapped 0x43 probe; see [`display-sub-device.md`](display-sub-device.md) |

Full identity reply for the `FSR` wheel (USB capture `usb-capture/fsr1/FSR1 with game.pcapng`,
wheel `0x17` → `0x71`): model-name (`0x07/01`) `FSR`, hw-version (`0x08/01`)
`RS21-D03-HW FW-C`, hw-revision (`0x08/02`) `U-V04`, sw-version (`0x0F/01`)
`RS21-D03-MC FW`, serial present (non-zero). The attached hub (`0x12` → `0x21`)
reports model `S03 HUB`, hw `RS21-S03-HW HUB-…`, sw `RS21-S03-MC HUB`, rev `CU-V04`.

This is the **FSR V1** wheel — a distinct, older product from the **FSR V2**, which is
the `W13` entry in the assumed table below. They are different wheels with different
firmware and different `0x07/01` replies: FSR V1 reports model-name **`FSR`** (hw
`RS21-D03`); FSR V2 reports **`W13`**. Do **not** treat the `FSR` reply as the same
device as `W13`. The FSR V1 also uses a wholly different telemetry transport (group
`0x42` display push — see [`../devices/wheel-0x17.md`](../devices/wheel-0x17.md) §
Group 0x42), whereas the W13/FSR V2 uses the standard tier-definition path.

### Assumed from device naming (unverified)

These prefixes appear in the rs21_parameter.db and `WheelModelInfo` table
but no live capture has confirmed the exact 0x07/01 reply byte-for-byte.
LED counts come from the `WheelModelInfo` defaults:

| Prefix | Wheel | RPM LEDs | Button LEDs | Flag LEDs |
|--------|-------|----------|-------------|-----------|
| `GS V2P` | GS V2P | (10 default) | 10 (5 per side) | none |
| `W18` | KS Pro | 18 | varies | none |
| `KS` | KS | 10 default | 10 | none |
| `W13` | FSR V2 | 16 | 10 | none |
| `TSW` | TSW | (10 default) | 14 | none |

Confirm byte-exact strings before assuming any of the above match the
firmware reply — `WheelModelInfo` may be using a heuristic match
(longest-prefix) rather than the literal model name.

### Identity probe shape

Request:

```
7E 02 07 17 01 [chk]
```

Response (16 bytes ASCII, null-padded):

```
7E 11 87 71 01 56 47 53 00 00 00 00 00 00 00 00 00 00 00 00 [chk]
            │  └ "VGS\0\0\0…" (null-padded to 16)
            └── echoes request cmd byte
```

`0x87` = `0x07 | 0x80`; `0x71` = nibble-swap of `0x17`. Length byte `0x11` =
17 (1 cmd echo + 16 string bytes).

### ES wheel identity caveat

ES (old-protocol) wheels share device ID `0x13` with the wheelbase. Probes
sent to `0x13` return **base** identity, not wheel identity. Example: ES
wheel mounted on R5 base returns:

```
0x07/01 → "R5 Black # MOT-1"
```

— that's the base's name. **There is no known way to query an ES wheel's
own model name via the serial protocol.** Plugin and sim treat ES wheels
as "wheel address = 0x13" with the wheel's identity coming from the
base; downstream code must check whether the protocol device is ES
before assigning model-specific behavior (LED count, button count).

### Cross-references

- [`wheel-probe-sequence.md`](wheel-probe-sequence.md) — full identity
  probe cascade
- [`dev-type-table.md`](dev-type-table.md) — per-wheel `0x04` response
  payload (type, sub-type bytes)
- [`display-sub-device.md`](display-sub-device.md) — display sub-device
  identity wrapping inside VGS-class wheels
- [`Devices/WheelModelInfo.cs`](../../../Devices/WheelModelInfo.cs) —
  plugin-side per-wheel config including LED counts
