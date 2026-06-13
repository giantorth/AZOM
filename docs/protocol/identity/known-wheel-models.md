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

### ES wheel identity (device 0x18)

> **Corrected 2026-06-12 (live R5 base + ES wheel).** Earlier notes claimed
> the ES wheel shared `0x13` with the base and that its model name could not
> be queried. That was wrong: `0x13` returns the **base/motor** identity, but
> the ES wheel answers its own identity probes at **device `0x18`**.

The ES (entry) wheel is a **module of the wheelbase MCU**, not a separate
device. It does **not** answer at the standard wheel id `0x17` (silent); its
identity lives at `0x18`:

```
0x18  0x07/01 → "ES"                  (model name)
0x18  0x08/01 → "RS21-D05-HW SM-C"    (hw — SM = Steering Module)
0x18  0x0F/01 → "RS21-D05-MC WB"      (sw — shared with the base MCU)
0x18  0x06    → <12-byte UID>         (same UID as the base — shared silicon)
0x18  0x04    → 01 02 10 09           (dev-type — identical to the base; see dev-type-table.md)
```

For contrast, `0x13` (base) on the same unit returns `"R5 Black # MOT-1"`,
hw `"…BM-C"` (Base Module). So the wheel and base are distinguished by
**model-name (0x07) and hw module code (0x08)**, not by MCU UID, sw-version,
or dev-type (which are shared across the base MCU's modules `0x12/0x13/0x18/0x19`).

**Device-id map on this unit (R5 + ES + SR-P Lite + handbrake):**

| dev | model (0x07) | hw (0x08) | MCU UID / sw | what |
|-----|--------------|-----------|--------------|------|
| `0x12` / `0x13` | `R5 Black # MOT-1` | `…BM-C` | shared `…WB` | base / motor |
| `0x18` | `ES` | `…SM-C` | shared `…WB` | **ES wheel** |
| `0x19` | `SR-P Lite` | `…PM-C` | shared `…WB` | integrated pedals |
| `0x1B` | `HB # S01` | `…HB-C` | **own UID / `…HB`** | handbrake (separate MCU) |

The base does **not** echo-answer arbitrary ids: `0x14/0x15/0x16/0x17/0x1A/0x1C`
and out-of-range ids (`0x22/0x44/0x55/0x99/0xAA`) are all silent. Only the
real modules above respond.

**Plugin handling.** The plugin probes `0x18` via the dedicated `es-wheel-*`
identity commands (device type `es-wheel`, parser hint maps the swapped id
`0x81` → `es-wheel`), resolving the wheel to the `"ES"`
[`WheelModelInfo`](../../../Devices/WheelModelInfo.cs) entry (RPM-only,
`HasDisplay=false`). See the detection flow in
[`wheel-probe-sequence.md`](wheel-probe-sequence.md).

### Cross-references

- [`wheel-probe-sequence.md`](wheel-probe-sequence.md) — full identity
  probe cascade
- [`dev-type-table.md`](dev-type-table.md) — per-wheel `0x04` response
  payload (type, sub-type bytes)
- [`display-sub-device.md`](display-sub-device.md) — display sub-device
  identity wrapping inside VGS-class wheels
- [`Devices/WheelModelInfo.cs`](../../../Devices/WheelModelInfo.cs) —
  plugin-side per-wheel config including LED counts
