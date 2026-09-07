## Steering Wheel (Device `0x17` / 23)

This covers all Moza steering wheels, including models with integrated display screens (e.g. formula-style wheels that show speed, gear, lap time). Live game telemetry (group `0x43`) is sent here by Pithouse — confirmed by USB capture. Wheels with integrated displays use that data to drive the screen internally. See [`../telemetry/`](../telemetry/) for the full topology and telemetry analysis.

### Identity Queries (read-only)

Request payload is just the command ID byte with no value bytes. The device returns 16 null-padded ASCII bytes regardless of request size. See [`../identity/wheel-probe-sequence.md`](../identity/wheel-probe-sequence.md).

| Command | Read Group | ID | Notes |
|---------|------------|----|-------|
| model-name | `0x07` | `01` | e.g. `VGS`, `CS V2.1` (see [`../identity/known-wheel-models.md`](../identity/known-wheel-models.md)) |
| hw-version | `0x08` | `01` | e.g. `RS21-W08-HW SM-C` |
| hw-revision | `0x08` | `02` | e.g. `U-V12`, `U-V02` |
| sw-version | `0x0F` | `01` | Firmware version string |
| serial-a | `0x10` | `00` | Serial number first 16 chars |
| serial-b | `0x10` | `01` | Serial number second 16 chars |

Full serial number = serial-a + serial-b (32 ASCII chars total).

### Group `0x3F` / `0x40` (63 / 64) — Configuration

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| colors | `00` | 15 | hex | Write-only |
| brightness | `01` | 1 | int | |
| rpm-timings | `02` | 10 | array | |
| paddles-mode | `03` | 1 | int | 1=Buttons, 2=Combined, 3=Split (1-based) |
| stick-mode | `05` | 2 | int | 0=Buttons, 256=D-Pad |
| set-rpm-display-mode | `07` | 1 | int | Write-only |
| get-rpm-display-mode | `08` | 1 | int | Read-only |
| clutch-point | `09` | 1 | int | |
| knob-mode | `0A` | 1 | int | |
| paddle-adaptive-mode | `0B` | 1 | int | |
| device-info | `0C` | 12 | array | Read-only. 1-byte request `0C` returns 12-byte block (e.g. `03 19 03 ab 04 04 02 df 00 00 00 00`). PitHouse polls ~6×/session. Structure undecoded — possibly version/capability flags. Observed in `usb-capture/ksp/gfdsgfd.pcapng` |
| paddle-button-mode | `0D` | 1 | int | |
| flag-colors1 | `0E 00` | 21 | array | Write-only |
| flag-colors2 | `0E 01` | 9 | array | Write-only |
| rpm-blink-color1 | `0F 00` | 3 | array | RGB; write-only |
| rpm-blink-color2 | `0F 01` | 3 | array | |
| rpm-blink-color3 | `0F 02` | 3 | array | |
| rpm-blink-color4 | `0F 03` | 3 | array | |
| rpm-blink-color5 | `0F 04` | 3 | array | |
| rpm-blink-color6 | `0F 05` | 3 | array | |
| rpm-blink-color7 | `0F 06` | 3 | array | |
| rpm-blink-color8 | `0F 07` | 3 | array | |
| rpm-blink-color9 | `0F 08` | 3 | array | |
| rpm-blink-color10 | `0F 09` | 3 | array | |
| key-combination | `13` | 4 | array | RW. **Bitfield of per-shortcut enable flags** (1 = enabled; `FF FF FF FF` = factory default, all shortcuts on — earlier "unset" reading was wrong). RS21-W17-MC bit map (angle presets, mode change, dash switching) decoded per-bit 2026-07-31 — see [`../findings/2026-07-31-wheel-key-combo-bitfield.md`](../findings/2026-07-31-wheel-key-combo-bitfield.md). Persists in wheel EEPROM Table 2 Param 26. Read form: 1-byte request `13` returns the 4-byte value |
| telemetry-mode | `1C 00` | 1 | int | |
| telemetry-idle-effect | `1D 00` | 1 | int | |
| buttons-idle-effect | `1D 01` | 1 | int | |
| telemetry-idle-interval | `1E 00` | 3 | int | Write-only |
| buttons-idle-interval | `1E 01` | 3 | int | Write-only |
| idle-mode | `20` | 1 | int | Sleep-light mode selector. Verified value `0x01` = Breathing on 2026-05-10 (`bridge-20260510-115644.jsonl` t=41008.106). Plugin: `wheel-idle-mode` |
| idle-timeout | `21` | 2 | int | BE u16 in **minutes** (verified 2026-05-10: `21 00 01` = 1 min, `21 00 0a` = 10 min). PitHouse emits this **on user setting change only** — scattered, not periodic; multiple values observed across bridge captures (`21 00 00` = disabled in some, `21 00 01` in others) reflecting the current PitHouse UI value at the moment of write. **Do not** include in heartbeat / widget-poll cycles: periodic emission silently overrides whatever the user set elsewhere (plugin previously fired `21 00 00` every ~87 s from `SendOneWidgetPoll` slot 79; removed). Plugin: `wheel-idle-timeout` |
| idle-speed | `22 [mode] [ms_msb] [ms_lsb]` | 3 | array | Per-mode sleep-light animation speed. Wire payload is `[mode, BE u16 ms]` — each sleep mode stores its own speed. Verified 2026-05-10: `22 01 0c d7` = mode 1 (Breathing), 3287 ms. Plugin: `wheel-idle-speed` (3-byte array). Earlier docs documented this as `22 00` (cmdid with mode hardcoded to 0) + 2-byte int payload — incorrect; the mode byte must be the actual target mode |
| idle-color | `24 FF 01 FF` | 3 | array | Sleep-light color RGB. Verified 2026-05-10: `24 FF 01 FF FF 00 00` = red. Plugin: `wheel-idle-color` |
| rpm-interval | `16` | 4 | int | |
| rpm-mode | `17` | 1 | int | |
| rpm-value1 | `18 00` | 2 | int | RPM threshold for LED 1 |
| rpm-value2 | `18 01` | 2 | int | |
| rpm-value3 | `18 02` | 2 | int | |
| rpm-value4 | `18 03` | 2 | int | |
| rpm-value5 | `18 04` | 2 | int | |
| rpm-value6 | `18 05` | 2 | int | |
| rpm-value7 | `18 06` | 2 | int | |
| rpm-value8 | `18 07` | 2 | int | |
| rpm-value9 | `18 08` | 2 | int | |
| rpm-value10 | `18 09` | 2 | int | |
| rpm-color1 | `1F 00 FF 00` | 3 | array | RGB |
| rpm-color2 | `1F 00 FF 01` | 3 | array | |
| rpm-color3 | `1F 00 FF 02` | 3 | array | |
| rpm-color4 | `1F 00 FF 03` | 3 | array | |
| rpm-color5 | `1F 00 FF 04` | 3 | array | |
| rpm-color6 | `1F 00 FF 05` | 3 | array | |
| rpm-color7 | `1F 00 FF 06` | 3 | array | |
| rpm-color8 | `1F 00 FF 07` | 3 | array | |
| rpm-color9 | `1F 00 FF 08` | 3 | array | |
| rpm-color10 | `1F 00 FF 09` | 3 | array | |
| button-color1 | `1F 01 FF 00` | 3 | array | |
| button-color2 | `1F 01 FF 01` | 3 | array | |
| button-color3 | `1F 01 FF 02` | 3 | array | |
| button-color4 | `1F 01 FF 03` | 3 | array | |
| button-color5 | `1F 01 FF 04` | 3 | array | |
| button-color6 | `1F 01 FF 05` | 3 | array | |
| button-color7 | `1F 01 FF 06` | 3 | array | |
| button-color8 | `1F 01 FF 07` | 3 | array | |
| button-color9 | `1F 01 FF 08` | 3 | array | |
| button-color10 | `1F 01 FF 09` | 3 | array | |
| button-color11 | `1F 01 FF 0A` | 3 | array | |
| button-color12 | `1F 01 FF 0B` | 3 | array | |
| button-color13 | `1F 01 FF 0C` | 3 | array | |
| button-color14 | `1F 01 FF 0D` | 3 | array | |
| flag-color1 | `15 02 00` | 3 | array | |
| flag-color2 | `15 02 01` | 3 | array | |
| flag-color3 | `15 02 02` | 3 | array | |
| flag-color4 | `15 02 03` | 3 | array | |
| flag-color5 | `15 02 04` | 3 | array | |
| flag-color6 | `15 02 05` | 3 | array | |
| rpm-brightness | `1B 00 FF` | 1 | int | |
| buttons-brightness | `1B 01 FF` | 1 | int | |
| flags-brightness | `1B 02 FF` | 1 | int | |
| paddles-calibration | `08` | 1 | int | Write-only, two commands: `08 01` = start, `08 02` = save. (PitHouse, dev `0x17`, write group `0x3F`.) |

### Group `0x3F` (63) — Live Telemetry (write-only)

These use the same write group as configuration above. They send real-time data to the wheel's LED bar and button LEDs.

See [`../leds/color-commands.md`](../leds/color-commands.md) for the LED encoding (index, R, G, B per LED, 5 per 20-byte chunk). Use index `0xFF` for unused padding slots to prevent firmware from overwriting LED 0.

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| send-rpm-telemetry | `1A 00` | 2..8 | array | Current RPM position on the LED bar; see [`../telemetry/control-signals.md` § RPM LED telemetry](../telemetry/control-signals.md) |
| send-buttons-telemetry | `1A 01` | 2..8 | array | |
| send-knob-telemetry | `1A 03` | 8 | array | Knob indicator bitmask (8-byte active+window form). 4 bits on CSP, 5 on KSP. See [`../leds/color-commands.md`](../leds/color-commands.md) |
| telemetry-rpm-colors | `19 00` | 20 | array | 5 LEDs per chunk; 2 chunks needed for 10 RPM LEDs |
| telemetry-button-colors | `19 01` | 20 | array | 3 chunks for 14 button LEDs; pad unused entries with index `0xFF` |
| telemetry-knob-colors | `19 03` | 20 | array | 1 chunk for 4–5 knob LEDs; pad unused entries with index `0xFF` |

### Group `0x41` (65) — Telemetry Enable (write-only)

Confirmed in USB capture: sent to device `0x17` at ~100×/sec with payload always `00 00 00 00`. Likely a mode/enable flag. See [`../telemetry/control-signals.md` § Dash telemetry enable](../telemetry/control-signals.md).

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| send-telemetry | `FD DE` | 4 | int | Wheels with integrated display; always `00 00 00 00` in captures |
| old-send-telemetry | `FD DE` | 4 | int | Old wheel firmware without integrated display |

### Group `0x42` (66) — Live Display Data Push (firmware variant)

**Observed only on the `FSR` / `RS21-D03` display wheel** (model-name `FSR`, hw-version `RS21-D03-HW FW-C`, sw-version `RS21-D03-MC FW`, hw-rev `U-V04`; box/marketing name "FSR1"). This is the **FSR V1** — a distinct, older wheel from the **FSR V2** (`W13`), which uses the standard tier-definition telemetry path; see [`../identity/known-wheel-models.md`](../identity/known-wheel-models.md). Captured in `usb-capture/fsr1/` — see [`../../../usb-capture/CAPTURES.md`](../../../usb-capture/CAPTURES.md).

On this unit, group `0x42` **replaces** the usual display-telemetry path. The documented `0x41` `FD DE` enable, the `0x43`/`7D 23` bit-packed value stream, **and the entire session-`0x02` / tier-definition channel-catalog handshake are all absent**: the wheel never advertises a channel catalog (no v0 URL or v2 compact advertisement, no `7C 00` session opens, no wheel→host `0xC3` catalog frames; `0x43` carries only a 1-byte cmdid-`00` keepalive poll). Instead PitHouse pushes **pre-computed display field values** to the wheel (`0x17`) as fixed-layout records at ~28 Hz during gameplay. The **byte schema is firmware-baked and fixed per record type** (fixed length, fixed field positions/widths), but **which telemetry channel feeds each field is host-chosen per loaded dashboard** — so the slots are fixed and the channel→slot mapping is configurable (see "The model" below).

This is documented as **observed** on this firmware. The record framing and type schema are proven; per-field channel semantics are only partially decoded (see "candidate mapping" below).

**Frame format** (host→wheel `0x17`):

```
7E <len> 42 17 [type] [b1] [b2] <fixed-layout data> <csum>
```

- `type` (first payload byte) selects a record with a **fixed length**. At startup PitHouse enumerates each record type once with an all-zero payload (declaration); at runtime the type for the **currently-displayed** dashboard carries live data. **14 types are observed** — `01 02 03 04 05 06 08 09 0b 0c 0d 0e 11 12`; `07 0a 0f 10` never appear. The 2026-06-08 captures (`All dashboards`, `Dashboards on time trial`, `GT Style dashboards`) drove **all 14** live (earlier captures only exercised 5, hence the older "declared-only" labels).
- `b1`, `b2` — **per-dashboard-configuration descriptors, NOT stable per record-type.** The same type carries different `b1`/`b2` across dashboard sets, so they cannot be hardcoded per type. Examples (type → old single-dash captures → new multi-dash captures): `06`→`0c/00`→`00/08`; `09`→`01/80`→`00/08`; `0e`→`0d/80`→`0e/01`; `02`→`03/00`→`00/00`. In the new set `b2` looks like a region/feature **bitmask** (`0e`→`01`, `0c`→`02`, `0b`→`04`, `06`/`09`→`08`, `11`→`06`); `b1` is usually `00`. Treat as opaque per-dashboard — derive from the loaded dashboard or leave `0`; do **not** assume a fixed value. (The plugin's previously-hardcoded `0x80` for `09`/`0e` only matched the old captures.)

**Record types** (len = total payload bytes incl. `type`/`b1`/`b2`; all 14 carry live values when their dashboard is the displayed page). Payload **data starts at offset 5** — offsets 3 and 4 are always `00` (reserved/padding) in every type:

| type | len | data bytes (off 5..) | notes |
|------|-----|----------------------|-------|
| `01` | 25 | 5..24 (20) | |
| `02` | 18 | 5..17 (13) | main racing record — 4×u16-BE gauges + tail (layout below) |
| `03` | 19 | 5..18 (14) | |
| `04` | 23 | 5..22 (18) | |
| `05` | 25 | 5..24 (20) | |
| `06` | 25 | 5..24 (20) | dense multi-gauge dashboard |
| `08` | 23 | 5..22 (18) | |
| `09` | 24 | 5..23 (19) | |
| `0b` | 15 | 5..14 (10) | smallest record |
| `0c` | 18 | 5..17 (13) | |
| `0d` | 25 | 5..24 (20) | |
| `0e` | 24 | 5..23 (19) | |
| `11` | 25 | 5..24 (20) | **GT Style dashboard** (gt_style capture: 27 k frames) |
| `12` | 25 | 5..24 (20) | **GT Style dashboard** (gt_style capture: 24 k frames) |

**Type `0x02` layout** (proven across 8720/8720 frames; offsets are payload-relative):

```
off:  0    1    2    3  4   5  6   7  8   9 10  11 12  13 14  15  16  17
      02   b1   b2   00 00  [ G0 ] [ G1 ] [ G2 ] [ G3 ]  ?   ?   ?   ?
                            u16BE  u16BE  u16BE  u16BE
```

`G0..G3` = four **16-bit big-endian** gauges at `[5,6] [7,8] [9,10] [11,12]`. **Proven** by a carry test on `all_dashboards` (the aligned pairs show frame-to-frame median |Δ| of the BE value ≈ 2, while the straddle pairs between them jump by ≈ 512 = one hi-byte): the field boundaries fall on `5,7,9,11`. In the older captures all four were `00 XX` (values < 256, e.g. `00 5b`=91 = four stable tyre temps), which is why they previously read as four u8s at `6,8,10,12` — that was the **low byte only**. `[13]`/`[14]`/`[15]`/`[16]`/`[17]` are the tail (a small scalar, the `0x4B` engine flag, and a gear/index — exact split best-effort).

### The model (corrected)

- **Field POSITIONS and WIDTHS are fixed per record type** (a type always has the same byte length and the same field skeleton). The records are **arrays of u16-BE gauges** (most of the payload) followed by a few u8 fields (engine flag `0x4B`, gear/page index `0..9`, 0/100 percentages). Offsets 3–4 are always `00`.
- **CHANNEL ASSIGNMENT is per-dashboard and host-driven** — *which* telemetry channel feeds each gauge slot is chosen by the loaded dashboard (configurable in PitHouse), not fixed by the firmware. That is why the same type shows wildly different value ranges across `All dashboards` / `time trial` / `GT Style` (different channels mapped to the same slots). This is exactly the shape the plugin wants: **fixed slots the user maps SimHub channels onto.**
- Consequence: the plugin should expose every gauge slot of every type as a **u16-BE** mappable field (low-byte-only u8 mappings clip anything > 255), and **not** hardcode `b1`/`b2`.

**Per-type field skeletons** (offsets payload-relative; ranges are *observed across the three 2026-06-08 captures*, so they reflect whatever channels those dashboards happened to map — they bound the wire width, not the channel). `u16` = big-endian pair `[o,o+1]`. Generated via `tools/fsr1-field-decode` + carry-test pairing (`/tmp/fsr1new/*.py`). Gauge pairing in the dense middle of the larger records is **best-effort** — treat the whole `o5..` region as consecutive u16-BE slots when implementing.

| type | u16-BE gauge slots (offsets) | u8 tail / flags | best-guess anchors |
|------|------------------------------|-----------------|--------------------|
| `02` | 5, 7, 9, 11, 13 | 14, 15, 16, 17 | 4 gauges (tyre temps when stable=91); tail has RPM-ish scalar, `0x4B` engine flag, gear |
| `06` | 5, 7, 9, 11, 13, 18, 20 | 22, 23(`0x4B`), 24(gear `0..5`) | densest; `[19]`/`[21]` busy u8 |
| `09` | 5, 7, 9, 11, 13, 15, 17, 19 | 21, 22, 23(gear `1..9`) | `[20]`/`[21]` 0..100 pct |
| `0e` | 6, 9, 11, 13, 15, 18, 20 | 17, 22, 23(gear `1..9`) | gt_style: `[14,15]`≈RPM, `[18,19]` slow decreasing (fuel) |
| `0c` | 5, 7, 9, 11, 13, 15 | 16, 17 | |
| `11` | 5, 14, 18, 21 | 8..13, 19, 20, 23(pct), 24(pct) | gt_style GT dash: `[5,6]` slow 9→2, `[14,15]`≈RPM, `[18,19]` fuel↓, `[21]` lap↑ |
| `12` | 5, 7, 10, 13, 15, 17, 21 | 9, 20, 23, 24 | gt_style GT dash (paired with 11) |
| `01` | 5, 7, 9, 11, 20 | 15..19, 22, 23, 24 | |
| `03` | 6, 8, 12, 14 | 7, 9..11, 16, 17, 18 | mostly slow/static gauges |
| `04` | 5, 7, 9, 17 | 10..16, 18..22 | |
| `05` | 5, 7, 9, 22 | 8, 10..21, 24 | |
| `08` | 5, 8, 10, 12, 15 | 6, 7, 9, 14, 17..22 | |
| `0d` | 5, 7, 16 | 6, 8..15, 18..24 | two 5-byte groups `[5..9]`/`[10..14]` |
| `0b` | 5, 8, 12 | 7, 10, 14 | smallest; mostly static |

**Confirmed anchors across types:** the `0x4B` **engine-running flag** (a low-distinct byte carrying `0x4B` while the RPM-LED bar is lit) and a small **gear / page index** (`0..9`) in/near the last data byte. **Channel identity beyond these is a guess** — derived from time-aligning fields against the group-`0x3F` RPM-LED signal and from monotonic behaviour over a session (steadily-decreasing 16-bit ⇒ fuel, steadily-increasing small int ⇒ lap, fast oscillating 16-bit ⇒ RPM). To pin a slot to a specific Telemetry.json channel, drive **one known channel at a time** (the plugin's Test Pattern / a controlled sweep) and read which offset moves.

**Channel identification — status.** Field **positions and widths** are decoded for all 14 types (table above); **channel identity** is still only best-guess (engine flag, gear, and behavioural guesses for RPM/fuel/lap). Because the channel→slot mapping is host-chosen per dashboard, the robust plan is to expose the decoded slots as mappable u16-BE/u8 fields and let users assign Telemetry.json channels, rather than chase a single "correct" per-type mapping. Findings from PitHouse (`MOZA Pit House/bin`):

- The display channels are the standard `Telemetry.json` (`v1/gameData/…`) set, **but `0x42` uses a byte-aligned firmware encoding, not the tier-def bit-packing** — e.g. the type-`02` 4-wheel array is byte-sized, whereas `TyreTemp`/`TyrePressure` are `float` (32-bit) in Telemetry.json. So the channels match; the wire widths do not.
- The FSR V1 LED/indicator preset (`default_preset_library.rcc` → `FSR-Official.json`, device `FSR`) is **LED/flag config only** and references a single telemetry channel, `CarSettings_CurrentDisplayedRPMPercent` (the RPM-bar driver on group `0x3F`). It does **not** define the `0x42` screen field layout — that mapping is firmware-baked.
- Mapping the `0x42` fields therefore needs a controlled capture with known telemetry. Until then, treat the type-`02` candidates as unconfirmed. Each switch coincides with a **full group `0x40` config re-sweep** (~30 config writes re-pushed, repeating ~every 2 s while the user is in the wheel/menu). **No dedicated dashboard-switch opcode was observed** (no `7C 25`, no `kind=4`, no `B8` event), and the wheel sends **nothing** about channels or dashboards on the serial bus: every wheel→host frame is either a solicited reply to a host config read/write (`0x80` ack, `0xC0`/`0xBF` group-0x40/0x3F replies, `0x8E` group-0x0E replies, `0xB2`/`0xB3` poll replies, identity replies) or an unsolicited **diagnostic log string** (group `0x0E` cmd `05xx` — `NRFloss`, `RotaryMode`, `Calib…`, `[INFO]…`). There is no catalog advertisement and no structured input-event frame (0 real `B8` frames — the 690 b2h frames containing a `0xB8` byte are all value bytes like `0bb8`=3000 in config readbacks), and nothing on the `0x42` group from the wheel (`0xC2` count = 0). The wheel *does*, however, announce its **current dashboard/page index** over serial as a `0x0E` diagnostic-log line (`Table 7 Param 6 Written: <N>`, `N`=`0..18`) on each switch — see "Dashboard switching" below. For value computation **the host owns the channel/value decision**; for dashboard *selection* either side can drive it — the **host** via a dedicated group-`0x32` cmd-`0x81` index write, the **wheel** via an HID button combo — see "Dashboard switching" below.

**Dashboard switching — verified.** The wheel has **19 dashboard/page positions** (index `0..18`); the active index selects which `0x42` record type the screen renders. Either the host or the wheel can change the index, and the wheel always reports its current index on the serial bus.

- **Host-initiated** (PitHouse UI; the plugin's dropdown). The host sends a dedicated **select command — group `0x32`, cmd `0x81`** — with the target index as a big-endian u32:
  ```
  7E 05 32 17 81 00 00 00 <index>          (index 0..18; e.g. 0x11=17)
  reply: 7E 05 B2 71 81 00 00 00 <index>   (wheel echoes/acks)
  ```
  The wheel switches to that page and logs `Table 7, Param 6 Written: <index>`. **Verified 7/7** in `dashboard change through pithouse, connected to base` (no wheel HID activity in that capture; each `g32/81 <N>` write is followed ~20 ms later by the wheel adopting index `N`). PitHouse then re-pushes the `0x40` config sweep and streams the `0x42` record type that matches the new page. *(Earlier drafts of this page mislabeled group `0x32` as a read poll, and then guessed that emitting the `0x42` record type was the switch — both wrong. The `0x32/0x81` write is the selector; the `0x42` stream is just the per-page value data.)*
- **Wheel-initiated** (button **combo**, no host command). A held modifier — **HID byte 21 bit `0x08`** — plus a direction tap — **HID byte 18 bit `0x01`/`0x04`** — on the 42-byte EP `0x83` input report. The wheel changes its own page in firmware (`0x01` = +1 wrap `18→0`, `0x04` = −1 wrap `0→18`) and reports the new index via the `0x0E` log below. The host sends **no** `g32/81` here — it follows by streaming the matching type. The structured `B8` wheel-input event used by standard display wheels (§ Group 0x43) is **absent** on this gen.
- **Reading the active index (either path).** The wheel emits an *unsolicited* group-`0x0E` diagnostic log on every change: `[INFO]param_manage.c:340 Table 7, Param 6 Written: <N>`, `N` = absolute index `0..18`. (The `B2/81` ack carries the same value.) So a host can always recover the absolute active dashboard from the serial stream — no HID press-counting needed. **Wheel-initiated path verified** in `Manual dashboard change - Pithouse opened.pcapng`: 46 unsolicited `Table 7, Param 6 Written: N` lines (cmd `0x0E`, dev `0x71`) track a manual `0→18` cycle over 22 s with no host `g32/81`. The plugin follows these via `Fsr1Cm1MappingCoordinator.TryFollowFsr1DashboardLog` (`MozaPlugin.cs`, gated dev `0x71` + `IsFsr1DisplayWheel`).

`g32/81` is sent **only at switch time** — the single-dashboard gameplay run (`FSR1 with game`) contains zero `g32/81` frames; it just streams the active type. The 19 index positions map **many-to-one** onto the live record types (pages within a dashboard share a layout).

**Group `0x32` is a persistent-parameter write group, and writes hit wheel EEPROM.** Beyond the `0x81` select (→ Table 7 Param 6), **display brightness** is `cmd 0x00` (write) + `cmd 0x80` (commit), big-endian u32 percent `0..100` — the wheel echoes `Table 7, Param 5 Written: <N>` ("brightness changes" capture, 2026-08-06). The value survives power cycles, so hosts must NOT re-apply it on connect or push it periodically — every select/brightness frame is an EEPROM write. The plugin emits brightness only on the debounced slider commit and dedupes/rate-limits selects (skip when already on the target page; ≥300 ms between emits).

**FSR1 answers NO identity read (2026-08 crash bundles).** On this wheel every identity query — model-name `0x07/01`, hw-version `0x08/01`, hw-revision `0x08/02`, sw-version `0x0F/01`, serial `0x10/00`+`01`, plus groups `0x02`/`0x04`/`0x05`/`0x06`/`0x09`/`0x11` — goes **unanswered**, while group-`0x40` config reads answer normally (`1c`/`05`/`18` fully; `03`/`09` partially). Each unanswered identity read is a param-manager read failure the wheel logs, so a periodic identity re-poll is a slow-drip `Failed to Read Parameter` generator: measured **60 unanswered `07/01` reads per minute**, with the failure sweep advancing table-by-table (Table 2 param 20→21→22→23, then Table 7 param 40→41→42→43) in lockstep with the ~1 Hz re-poll. PitHouse asks for identity **once per connect** and never re-polls. Gating rule: after identity resolves on an FSR1, do not re-read it — liveness comes from the `0x00` presence ACK and the wheel's continuous unsolicited `0x0E` logs.

**Param-store wedge TRIGGER — a Table-2 flash write (2026-08-13, six-bundle correlation).** The wedge is not caused by host *reads*, and not by Table-7 writes. It follows a **wheel Table-2 (LED / idle / sleep-light) parameter actually being persisted**. The wheel's failure log is a round-robin *persist sweep* — Tables 2 → 3 → 7, four params at a time, walking upward from wherever its cursor sat (`T2:51, T3:80-83, T7:20-23, T2:52-55, …`) — i.e. the firmware flushing its store after it was marked dirty, failing on every slot it cannot read, and never completing.

| bundle | wheel Table-2 params actually written | failures | session |
|---|---|---|---|
| `cycle through dashboards` | **0** | **0** | **60 min** |
| `Display crash v2` | 22 | 1790 | 19 min |
| `display crash v3` | 0 *(continuation — started 4 s after v2 ended, wheel already wedged)* | 2435 | 19 min |
| `display crash v4` | 8 | 1836 | 16 min |
| `display crash v5` | 5 | 403 | 1 min |
| `another bundle` (v6) | 8 | 1321 | 16 min |

The healthy 60-minute session **sent the same eight `0x3F` idle-family writes** (`1c`, `1d`×2, `20`, `21`, `22`, `24`) — but the values matched what the wheel already held, so the **firmware deduped them**: no `Param … Written` line, no flash write, no sweep, no failures. It also took **18 Table-7 Param-6 writes** (dashboard selects) in that same hour with no ill effect, so the dashboard-index param is safe to write. The crash sessions differ only in that a Table-2 value genuinely *changed* (e.g. sleep timeout 15 → 3 min, sleep colour light-blue → amber), producing real flash writes ~3 minutes before the sweep began. PitHouse never writes this family at all (writes = 0 across four captures; it only reads it and writes on explicit user action), which is exactly why the wheel never wedges under PitHouse. Gating rule: never emit an idle/sleep-light write from a connect / profile-apply path on this wheel — see `HardwareApplier.ApplyWheelToHardware`.

**Param-store wedge (failure mode, 2026-08-06 crash bundles).** The wheel's parameter subsystem can wedge: every param read fails (`Table N: Failed to Read Parameter M` sweeps across Tables 2/3/7) and a pending Table 7 Param 6 write retries ~1 Hz forever (`Table Id 7, ParamAddr 6: Failed to Write`, often split across two `0x0E` frames). The comm MCU keeps answering (polls, logs, identity) but the **display goes dark and stays dark until the wheel is power-cycled** — host reconnects don't help. No host command sustains the loop (zero `g32` traffic during the wedge); the retry is firmware-internal. The plugin detects the signature from the `0x0E` log and surfaces a PARAM-STORE FAULT line in diagnostics.

**Read-only variant + the flash-write budget (2026-08-11 bundles, FSR1 on R12).** A second wedge instance refines the picture. Signature was **0 failed writes / ~1,750 failed reads** — no stuck `Table 7 Param 6` write at all, so the wedge can present read-only. Each of Tables 2/3/7 sweeps params **0..127** on its own cursor (588/588/584 failures ≈ 4.6 sweeps), all three failing from the same instant.

The whole session issued only **four** wheel flash writes, and the wedge began **33 s after the last one**:

| time | write | source |
|---|---|---|
| 23:06:59.842 | `Table 2, Param 24 = 2` | connect-time `ApplyWheelToHardware` burst (8 group-`0x3F` cmds `1c/1d/1d/1c/20/21/22/24` in 26 ms) |
| 23:07:00.735 | `Table 2, Param 47 = 16770560` | same burst — `0xFFE600` is the `wheel-idle-color` RGB `ff e6 00`, so **Param 47 = idle colour** |
| 23:07:39.298 | `Table 2, Param 43 = 300000` | user set sleep timeout 15→5 min, so **Param 43 = idle timeout (ms)** |
| 23:07:45.049 | `Table 2, Param 43 = 180000` | user set sleep timeout 5→3 min |
| 23:08:17.785 | `Table 2: Failed to Read Parameter 24` | wedge begins; cleared only by the 23:27:10 base power-cycle |

The firmware self-dedupes: 8 commands produced 2 flash writes (the other 6 already matched). The `0x42` display stream contributed **zero** param writes — its pending+coalesce+1 Hz gate works.

Two host-side causes were fixed off the back of this:

- **`PrimeWheelCfgFromDevice` is inert on the FSR1.** It primes the write cache from the wheel's readback, but `DeviceProber.BuildNewWheelLedReadCommands` returns an empty list for this rim (by design — its LED read burst is what storms the store). Across the whole 60 s startup capture the plugin **read 5 wheel params and wrote 8**; only `wheel-telemetry-mode` is both primable and read. So the connect-time apply now skips the flash-backed family entirely on an FSR1 (`HardwareApplier.SuppressApplyFlashCfgWrites`), matching PitHouse, which never writes this family on connect and only writes on a user edit.
- **UI handlers bypassed the change-cache.** `WriteIfWheelDetected` and friends went straight to the wire, so every dropdown/slider interaction was an unconditional flash write *and* a later apply re-wrote the same value because the cache had never seen it. They now share the cache and are coalesced behind a 400 ms quiet window (`QueueWheelCfgWrite`), so a slider drag costs one write instead of ~50 — and a drag that ends where it started costs none.

**Full index→type map — verified.** Built by correlating every `g32/81` select + `Param 6` log with the `0x42` record type(s) streamed until the next switch, across `All dashboards`, `Moza FSR1 dashboard change`, `FS1 multiple changes`, `GT Style`, and the manual-change captures (`tools/` ad-hoc windowed correlation):

| index | type | index | type | index | type |
|-------|------|-------|------|-------|------|
| 0 | `01` | 7 | `06` | 13 | `04` |
| 1 | `02` | 8 | `05` | 14 | `04` |
| 2 | `06` | 9 | `03` | 15 | `0c` |
| 3 | `06` | 10 | `08` | 16 | *(unused)* |
| 4 | `03` | 11 | `09` | 17 | `11` + `12` |
| 5 | `04` | 12 | `0e` | 18 | `0c` |
| 6 | `04` | | | | |

- **Index 0** is the **power-on default**: the wheel streams type `01` before any switch (784 type-`01` frames precede the first `g32/81` in `All dashboards`). This is why a freshly-connected FSR1 sits on the type-`01` dashboard.
- **Index 16** is **never enumerated by PitHouse** — its full sweep goes `…15, 17, 18`, skipping 16 — so there appear to be 18 selectable dashboards over indices `{0–15, 17, 18}`. Left unmapped (the plugin falls back to the full live set there).
- **Index 17 is the only dual-type page.** The GT-style screen is fed by records `11` and `12` **interleaved frame-by-frame** — a clean 13 s dwell on index 17 streams `11 12 11 12 …` (521× `11` / 417× `12`). Both must be streamed to fill the screen.
- Earlier drafts of this page guessed `7`→`09` and `15`→`06`; both were wrong (18 captures of streaming data give `7`→`06`, `15`→`0c`). Index `15` and `18` both render type `0c`.

Open item: whether the `0x40` config re-sweep is strictly required on a host switch or merely habitual.

**GT-style dashboard (index 17) field semantics — community-contributed.** A user hand-mapped the GT-style page by driving known SimHub channels and reading which on-screen box moved (the GT layout has labelled boxes: Speed, Gear, Fuel, Lap time, Tyre press, TC, Lights). Because the GT page streams two records, the screen's fields are split across both. Gauge offsets are the u16-BE pairs `[o, o+1]`; meanings below supersede the behavioural guesses in the per-type skeleton table for these two types. Slots not listed were left UNKNOWN by the contributor.

| record | gauge | meaning |
|--------|-------|---------|
| `11` (GT Style A) | @7  | estimated lap time |
| `11` | @9  | predicted lap time |
| `11` | @11 | gear |
| `11` | @15 | speed (km/h) |
| `11` | @17 | fuel — remaining laps |
| `11` | @19 | gear |
| `12` (GT Style B) | @5  | tyre pressure front-left |
| `12` | @7  | tyre pressure rear-left |
| `12` | @9  | fuel used (litres) |
| `12` | @11 | fuel per lap (litres) |
| `12` | @13 | fuel level |
| `12` | @15 | current lap time |
| `12` | @17 | lap time |
| `12` | @19 | TC level |
| `12` | @21 | light stage |

The contributor's exact channels were game/hub-specific (`ATSRHubMain.Telemetry.*`, `PersistantTrackerPlugin.*`, plus generic `DataCorePlugin.*`); the **meaning** of each slot is the durable finding, since channel→slot assignment is host-chosen and user-overridable. The catalog seeds each decoded slot's default with the canonical `simhub_property` from [`Data/Telemetry.json`](../../../Data/Telemetry.json) (MOZA's own channel catalog) — e.g. speed → `DataCorePlugin.GameData.SpeedKmh`, fuel remaining laps → `DataCorePlugin.GameData.FuelLaps`, fuel/lap → `DataCorePlugin.GameData.FuelConsumeLap`. The one exception is **light stage** (record `12` @21): Telemetry.json has only individual light bools (`HighBeamLight`, `RainLight`, …), no aggregate, so it ships with no default. The u8 after the TC/ECU nibble pair on records `10`/`12` drives the on-screen **TC-R** gauge (previously guessed "TC cut"; tester-identified). The **light stage** value is the low 2 bits of the flags byte (record `10` data[21], record `12` data[22]): 0 = off, 1 = low beam, 2 = high beam. Record `0f`'s tyre-**pressure** pack is normal **FL, FR, RL, RR** corner order (tester box-verified; the RR,RL,FR,FL capture-decode guess rendered diagonally crossed — its tyre-**temp** pack remains RR,RL,FR,FL per the capture and is unconfirmed on-wheel). The `0x42` path is byte-aligned `u16` (not tier-def bit-packing), so only the property *names* are taken from Telemetry.json, not its compression codes.

**Record layout is firmware-baked.** The `0x42` builder carries one fixed encoder per record type — **encoder N → record type `0x0N`**, payload = record `PayloadLen − 5`. Each encoder is a hand-ordered field list, and each field has a fixed *strategy* setting its byte size and internal bit encoding. The wire is **LSB-first within each byte**. Transforms are `round(value)` unless noted (all capture-verified against `Tyre Data with stamps.pcapng`, an F1 2020 replay + parallel wire capture):

| strategy | bytes | encoding | transform |
|---|---|---|---|
| TyreTemp / TyrePressure | 5 | 4 × 10-bit LSB pack | tyre temp = **°C + 300** (inner & outer groups) |
| BrakeTemp / Speed / Rpm / Int16 / FuelLaps | 2 | 16-bit **big-endian** | brake = raw °C; fuel-remain-laps = **laps × 100** |
| Time (lap / gap / session) | 3 | 24-bit BE, ms | **seconds × 1000**; gap = **24-bit sign-magnitude** delta to best — **bit 23 = sign**, low **23 bits** = \|ms\| (data[9] bits 0-6 carry the high magnitude bits, not just a pad). Range reaches **±1640 s+** in a PitHouse capture, so it is **not** limited to ±65 s. Sign convention verified against an ACC capture (96% round-trip) |
| Int8 / Gear / Temperature / Float8 | 1 | 8-bit | gear = **SimHub gear + 1** (0 = R, 1 = N, 2 = 1st); tyre wear = **100 − SimHub wear %** — the gauge shows REMAINING, and SimHub's `TyreWear*` is 0–100 % worn (tester-verified twice, most recently 2026-08-25; corroborated on the wire, where PitHouse sends `100,100,100,100` for a fresh car on the type-`03` pages and 80–90 once used) |
| GearDrsErs | 1 | gear[0:4] · **ERS mode**[4:6] (2-bit) · **DRS**[6] (1-bit) | ERS mode 0–3, DRS 0/1. The brake dash (`02`) DRS toggle stays dark under both PitHouse and the plugin — but **not** because the bit is unsent: PitHouse sets bit 6 on type-`02` in 328/8720 frames (`All dashboards`) and 562/2000 (`Dashboards on time trial`), values like `0x58`/`0x79`. So the toggle is not wired to this record's data on the `02` page (firmware); remapping the bit cannot fix it. Open test: the byte-stepper probe on dashboard 2 sweeps every data byte, which would find the driving byte if one exists |
| Compact<4,4> | 1 | two 4-bit LSB | |
| Compact<1×7> | 1 | light/flag bundle | bits [0:2] = **light stage** (one 2-bit field: 0 = off, 1 = low beam, 2 = high beam — tester box-verified, NOT two independent beam bits), bits 2+ = 1-bit flags |

Corner order within a tyre/pressure pack is **FL, FR, RL, RR**. The `+300` earlier chased as `+44` was the low byte of the 10-bit `+300` value (300 = 256 + 44). SimHub's generic `TyreTemperature*` is the **surface** temp only, so the firmware's **inner** group is seeded from the F1 raw array `GameRawData.PacketCarTelemetryData.m_carTelemetryData01.m_tyresInnerTemperature0X` (outer group ← `…SurfaceTemperature0X`); ERS mode/deployed/harvested ← `GameRawData.PlayerCarStatusData.m_ers*` (the player-extracted root — `m_carStatusData01` is array slot 0, not the player, and its ERS members don't resolve on some SimHub builds). All defaults are user-overridable. This supersedes the "unconfirmed candidates" caveat in the type-`02` skeleton above.

**`b1`/`b2` do NOT gate the gap field — negative result, 7 captures.** A long-running theory held that a particular sub-header made the gap render (`0d/00` on type-`06`) while `b2=08` suppressed it. It is wrong. Grouping every group-`0x42` frame by `(type, b1, b2)` and asking how often the gap u24 is populated (`tools/fsr1-page-field-map.py` for the per-page headers, plus a one-off gap-vs-header correlation) shows no relationship in either direction: type-`09` at `01/80` carries a live moving gap in `Dash 12 and 13 assetto corsa` (22.8 % of 11 681 frames, range −5.9 s…+6.7 s) yet **zero** gap at the same `01/80` in `FS1 multiple changes`; type-`09` at `00/00` carries one 79.4 % of the time in `GT Style`; type-`0c` at `00/00` carries one in 100 % of frames while `00/02` carries none. Whether the gap is populated tracks only whether PitHouse *had* a live delta. Header values also vary per session for the same page (type-`09`: `00/08`, `00/48`, `01/80`, `00/00`; type-`02`: `00/00`, `01/40`, `03/00`, `03/20` — the last two inside one capture), which is why they cannot be page identity. Do not chase gap rendering through `b1`/`b2`.

**Gap position/encoding — layout from the Pack RE; live deltas capture-confirmed on types `09`, `0c`, `0e`, `11`.** Decoding `Dash 12 and 13 assetto corsa` under the catalog layout reproduces type-`09` end to end: `clt` data[5-7] counting up (with `0x800000` as PitHouse's *invalid lap time* sentinel), `llt`/`blt` data[8-10]/[11-13] both `0x0247b9` = 149 433 ms, **gap data[14-16] sign-magnitude ms** (e.g. `81 88 7f` = −100 479 ms, consistent with a 48.95 s projected pace against a 149.4 s reference), `spd` data[17-18] = 125 km/h, `pos` data[19] = 1, `gear` data[23]. Type-`0c` reproduces the same way on page 15 (`clt` data[5-7], gap data[8-10], `spd` data[11-12], `rpm` data[13-14] = `0x076b` = 1899, `maxRpm` data[15-16] = `0x1f40` = 8000, gear data[17]). **Type-`06`'s gap slot has never been seen carrying a delta.** The slot itself is not in doubt — type-`06`'s field list and its 24-bit `Time` strategy are firmware-fixed (see "Record layout is firmware-baked" below), and the surrounding layout reproduces byte-exactly against capture (`llt` `01 6e 36` = 93 750 ms, `blt` `01 68 6e` = 92 270 ms, `spd` data[17-18], `rpm` data[19-20], `pos` data[21], `fuel` data[22], `ersR` data[23], gear/DRS/ERS data[24]). What is missing is a single observation of PitHouse *feeding a delta* into it: across every FSR1 capture (`All dashboards` ×2, `Dashboards on time trial`, `FS1 multiple changes`, `GT Style`, `Assetto Corsa capture`, `Dash 12 and 13`, `Dashboard 17 AC`, the manual-change set) data[14-16] on type-`06` is **always zero**, and the one capture that populates it — `GAp pithouse test`, 5 433 type-`06` frames, header `0d/00`, slot non-zero in 100 % — holds a **free-running unsigned ms timer** advancing at 1× real time, pinned 5.6 s below `clt`, which is not what a delta channel produces. Every capture in which PitHouse *did* emit a signed delta was on type `09`, `0c`, `0e` or `11`. Since the channel feeding each slot is host-chosen per dashboard, that is consistent with either reading (the tester's PitHouse dashboard may simply have had a clock mapped to the box); Reproduce with `tools/fsr1-page-field-map.py <capture> --type 0x06`.

**Pages with no gap slot — and the gap does NOT come from a cache (negative result, 2026-08-25).** Several records have no 24-bit gap field: index 4 streams only type-`03`, index 8 only type-`05`, and indices 0/1/5/6/9/10/13/14 are likewise gap-less (`01`, `02`, `03`, `04`, `05`, `08`). On those pages the wheel shows no working gap.

The obvious theory — that the firmware caches the delta from any gap-bearing record, the way `0x0d` supplies tyre temps and lap counts to pages whose primary lacks them — was **tested on hardware and is wrong.** Interleaving type-`0c` (the smallest gap-bearing record) as a 5 Hz one-shot alongside the primary on those pages produced **no working gap** on index 4 or 8: no page flip, no flicker, no freeze, just no effect. So the `0x0d` cache mechanism does not generalise to the gap field — either those pages have no gap box at all, or its source is something the host has not identified. Do not re-try this without new evidence about what feeds those boxes.

Note the captures still cannot settle it either way, and a full sweep of every capture on hand (33 loose `.pcapng` plus 21 archives, `tools/fsr1-page-field-map.py`) did not close the hole: **no capture yet shows PitHouse sitting on a gap-less page while it has a live delta.** The corpus splits cleanly — captures that visit indices 4/8/9 (`Tyre Data with stamps` covers both 4 and 8, single record type each with no interleaved secondary; `FSR1_CM1`; `All dashboards` ×2) all have PitHouse on a **zero delta across every record type**, while the captures that do carry live deltas (`Gap packet capture` — type-`09` at header `00/40`, negative sign-magnitude values like `8003ea` = −1002 ms; `Display shutdown` — type-`0e` on index 12; `Dash 12 and 13`; `Dashboard 17 AC`; `Assetto Corsa capture`; `GT Style`) never leave the gap-bearing pages. `Display shutdown` comes closest — it visits index 9 (type-`03`, 18 786 frames) and later index 12 with a live gap — but the delta only starts populating partway through the **later** index-12 window, so PitHouse had nothing to send during index 9. To settle it, capture PitHouse switching **to** index 4 or 8 mid-session, after the delta is already live.

**Tooling trap — scope the Param-6 regex to Table 7.** The wheel logs `Table 2, Param 6 Written: <N>` for an unrelated parameter family (seen once in `Display shutdown` with N = 50). A loose `Param 6 Written: (\d+)` match follows that as a page index and clamps it to 18, silently streaming the wrong record. `Fsr1Cm1MappingCoordinator._fsr1DashLogRe` is correctly anchored on `Table\s*7,`; keep any new analysis script anchored the same way.

**Always cite Param-6 indices, never user-facing dashboard numbers.** The plugin's dropdown labels index *N* as "Dashboard *N+1*", and testers variously report the label or the index. This has produced repeated off-by-one confusion — a gap report against "dashboard 4/8" means indices 4/8 (types `03`/`05`, no gap slot) or indices 3/7 (both type-`06`, one gap slot, byte-identical frames), and those two readings lead to opposite conclusions.

**Capture tooling caveat.** `usb-capture/extract_moza_frames.scan_moza_frames` does **not** undo the 0x7E7E byte-stuffing and does not verify checksums, so every byte after a `0x7E` payload byte is shifted and the scanner can resync mid-frame. In per-byte field analysis that shows up as phantom extra values — on type-`06` it made `llt`/`blt`/gap look like they alternated between two states. Use `tools/moza_wire.scan_moza_frames_checked` for anything that inspects payload bytes; `tools/fsr1-page-field-map.py` now does.

**Type-`09` TC / ABS — data[21] / data[22], corroborated.** Previously behaviourally guessed. Two live PitHouse sessions on page 11 agree: `All dashboards` (F1 2020) holds data[21] = 2 and data[22] = 1–2 with data[20] sweeping 3–54 (ERS %), and `GT Style` holds data[21] ∈ {0, 7}, data[22] ∈ {0, 7, 8} — TC/ABS levels in both games' ranges. Property names match MOZA's own [`Telemetry.json`](../../../Data/Telemetry.json) (`TCLevel` → `DataCorePlugin.GameData.TCLevel`, `ABSLevel` → `…ABSLevel`), both of which exist on `StatusDataBase`.

**Type-`03` damage boxes — gauge order and polarity (tester-confirmed).** The five bytes after the tyre group are, in on-wheel order, **data[11] FL wing · data[12] FR wing · data[13] ICE · data[14] gearbox · data[15] rear wing** — the last three sit one gauge earlier than the plugin's original labels claimed. Confirmed twice: independently by a tester's own profile overrides (they had bound `wwR`←engine, `engWear`←gearbox, `gbxWear`←rear wing, a clean shift-by-one) and by on-wheel gauge identification. Unlike the tyre boxes, these five carry **DAMAGE, not remaining %**: at 0 the gauge renders green and rises to red, so no `100 −` inversion (tester: "damage zero shows red" under the inverted default; 92 % damage showed green). A live F1 session's values fit — data[11]=85, [12]=27, [13]=27, [14]=1, [15]=1 alongside tyre bytes at 80–86 remaining. Note `Telemetry.json` names these channels `WingWearFL`/`EngineWear`/`GearBoxWear` but points them at `…FrontLeftWingDamage`/`…EngineDamage`/`…GearBoxDamage`, **none of which exist on `StatusDataBase`** — only `CarDamage1..5` do, and those are one undifferentiated pool per game — they cannot tell a wing from a gearbox. The plugin therefore binds the five gauges to the **F1 raw player-car status** members that match them part-for-part (`GameRawData.PlayerCarStatusData.m_{frontLeftWing,frontRightWing,engine,gearBox,rearWing}Damage`), each with **bias +1** — the gauge leaves 0 unlit, so an undamaged part must read 1 to render green (tester-verified 2026-08-25 on dashboards 5/10). Non-F1 games resolve these to 0 and so show all five boxes at 1 (green); remap per profile if the game exposes damage elsewhere. (Until 2026-08 the front boxes both read `CarDamage1`, the rear wing read `CarDamage2`, and ICE/gearbox were unbound — those two gauges sent a literal 0 in every game.)

**Background tyre/status cache — record `0x0d` (verified).** `0x0d` is **not a selectable page** (no index maps to it as a primary). PitHouse **interleaves it sparsely** — ~1 frame per ~100 primary frames (≈0.5 Hz) — alongside whatever dashboard is active, as a one-shot (not a retransmitting stream slot). Verified in `Tyre Data with stamps.pcapng`: `0x0d` appears **only** as single frames between ~108-frame runs of the primary (rode with types `02` and `06`), and never as a long run. It carries **tyre outer+inner temps, tyre pressure, track temp, air temp, car count, current lap, and lap count** — pack order is **outer first** (data[5-9]), inner second (data[10-14]); tester-verified on the brake dash, and the **reverse** of type-`08`, which carries inner first. The firmware caches it and renders those on pages whose primary record lacks the field (e.g. tyre temps on the brake dash `02` and timing dashes; the **"/total"** on Position and Lap is the cached car-count / lap-count). The plugin mirrors this: it streams `0x0d` at ≈0.5 Hz via a one-shot `Send` (primaries stay on full-rate stream slots so the active page keeps its refresh) and lists `0x0d` as a secondary on every page that displays cached data (indices 0, 1, 2, 3, 7, 11, 12, 17) so its channels appear in that page's channel mapper.

#### Per-profile field overrides (plugin-side, not wire protocol)

The plugin lets a user remap any `0x42` field's SimHub channel and apply a Scale/Bias gain; overrides are stored deviation-only — a field at its catalog default persists nothing. Field GEOMETRY (byte span, bit packing, endianness) is **catalog-fixed**: every record's layout was decoded from PitHouse captures and tester box-probing, so the earlier boundary/bit-stepper editors and synthetic "split" fields were removed once the decode was complete (stale geometry overrides in old profiles are ignored on load). None of this changes the bytes on the wire — a mapping only changes which SimHub value drives each field.

### Group `0x43` (67) — Live Telemetry Stream (write-only)

Main game telemetry sent at ~17–20×/sec. See [`../telemetry/live-stream.md`](../telemetry/live-stream.md) for full packet analysis and bit-packing format.

Payload = 2-byte cmd ID + 6-byte header + variable-length bit-packed channel data. Header bytes 0–3 are constant (`32 00 23 32`), byte 4 is a flag/stream selector, byte 5 is constant (`0x20`). Three concurrent streams use consecutive flag values for `package_level` tiers 30/500/2000. Channel data is bit-packed alphabetically by URL suffix per the active dashboard; payload size = `ceil(total_channel_bits / 8)`. Empty tiers send a 2-byte stub.

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| send-live-telemetry | `7D 23` | varies | array | 6-byte header + bit-packed channel data; size depends on dashboard |
| send-telemetry-state | `FC 00` | 3 | array | Session acknowledgment (`session + ack_seq`) ~1×/sec |
| dashboard-transfer | `7C 00` | varies | array | Session-based chunked file transfer / RPC; see [`../dashboard-upload/`](../dashboard-upload/) |
| display-config | `7C 27` | 4–8 | array | Periodic display config push (~1/s), page-cycled alongside `7C 23` |
| dashboard-activate | `7C 23` | 8 | array | Periodic dashboard activate (~1/s), interleaved per page with `7C 27`; declares active pages |
| display-settings | `7C 1E` | 8 | array | Periodic display settings push (~1/s) — brightness/timeout/orientation; sent to all wheel models |
| wheel-input-event | `B8 AA BB` | 3 | array | **DRAFT (2026-05-17, semantics verified across 40 events).** Wheel→host event emitted on `(b2h, grp=0xC3, dev=0x71)` immediately before the wheel's own kind=4 FF-record carrier when the user triggers a dashboard or page change from a wheel-side control. **Byte `AA` = action category**, **byte `BB` = action argument**: `00 02` = next dashboard, `01 02` = previous dashboard, `02 00` = next page within dashboard, `02 01` = previous page within dashboard. Verified across 4 captures totalling 40 events (14 forward dash + 16 backward dash + 10 page changes) with 40/40 prediction match and 0 counterexamples. 0 occurrences across 50 prior captures (~6.5 M lines), not present outside wheel-side input. Byte 2 for dashboard cases (`AA=0x00/0x01`) is always `0x02` — coincides with session id of the FF-record carrier, causation unproven. Not in `rs21_parameter.db`. b8→kind=4 delay: ~0.1 ms for dashboard, ~351 ms for page. See [`../tier-definition/handshake.md`](../tier-definition/handshake.md) § In-game dashboard switch and page change. |

### Groups `0x15`–`0x19` (21–25) — Firmware Flash Transfer

Dedicated wheel-firmware-update protocol, first captured live 2026-07-31
(RS21-W17-MC, `01 02 07 07` → `01 02 09 07`). Distinct from the
dashboard file-transfer path. Summary: `0x16` streams the image in
58-byte frames with a mod-65536 BE u16 offset; `0x15` polls a
received-byte counter; the wheel can push `0x96` NACK/resume frames to
rewind the stream; `0x17` reads a 16-byte per-block digest; `0x18 01`
commits a block, `0x18 02` walks the stored digest table for final
verification; `0x19 00` finalizes. A 32-byte activation trailer is
written last; the wheel applies and reboots on the internal bus with no
CDC re-enumeration (the only reconnect is at update *initiation*).
Update-mode traffic suspends all normal settings polling. Full frame
formats, block cycle, and completion sequence:
[`../findings/2026-07-31-wheel-firmware-update-protocol.md`](../findings/2026-07-31-wheel-firmware-update-protocol.md).

The wheel's **display MCU** updates via a different path entirely —
image pre-staged over session file-transfer, tiny manifest exchange at
apply time, internal flash, no dedicated flash groups. Its firmware
version answers on group `0x43` cmd `04` (reply `0xC3` `84` + 4 version
bytes), not the standard version group. See
[`../findings/2026-07-31-wheel-display-fw-update.md`](../findings/2026-07-31-wheel-display-fw-update.md).

### Old-Protocol Commands (Groups `0x3F` / `0x40`)

Used by older wheel firmware revisions. Observed in protocol captures and retained for backwards compatibility.

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| rpm-indicator-mode | `04` | 1 | int | 1=RPM, 2=Off, 3=On (1-based) |
| old-rpm-color1 | `15 00 00` | 3 | array | |
| old-rpm-color2 | `15 00 01` | 3 | array | |
| old-rpm-color3 | `15 00 02` | 3 | array | |
| old-rpm-color4 | `15 00 03` | 3 | array | |
| old-rpm-color5 | `15 00 04` | 3 | array | |
| old-rpm-color6 | `15 00 05` | 3 | array | |
| old-rpm-color7 | `15 00 06` | 3 | array | |
| old-rpm-color8 | `15 00 07` | 3 | array | |
| old-rpm-color9 | `15 00 08` | 3 | array | |
| old-rpm-color10 | `15 00 09` | 3 | array | |
| old-rpm-brightness | `14 00` | 1 | int | **Small field, NOT a 0–100 percentage** — see note below |

**`old-rpm-brightness` value range.** The single payload byte is a small brightness
count, not a 0–100 percentage. Observed empirically on ES/ESX hardware (issue #113):
sweeping a host 0–100 value made the RPM bar ramp-and-wrap ~3.3 times, i.e. the field
has a period of roughly 30 counts (0 = off, ~29 = full, values ≥ ~30 wrap). Exact
maximum unconfirmed against a PitHouse capture (PitHouse's configurator surfaces this
as a coarser 1–15 scale). The plugin therefore scales SimHub's 0–100 master brightness
into `0..29` before writing (`EsBrightnessMax` in `MozaPlugin.cs`), kept just under the
wrap point so a full slider lands at near-full brightness. Unlike new-protocol wheels,
old-protocol wheels have no per-frame colour scaling, so this register is the *only* way
to dim their RPM LEDs.

### Extended LED Group Architecture (Groups `0x3F` / `0x40`)

Newer wheels organize LEDs into 5 independently controlled groups, extending beyond the RPM (Shift) and Button groups above. Found in rs21_parameter.db. See [`../leds/wheel-groups-0x3F-0x40.md`](../leds/wheel-groups-0x3F-0x40.md) for the high-level group breakdown.

| Group ID | Name | Max LEDs | Purpose |
|----------|------|----------|---------|
| 0 | Shift | 25 | RPM indicator bar |
| 1 | Button | 16 | Button backlights |
| 2 | Single | 28 | Single-purpose status indicators |
| 3 | Rotary | 56 | Rotary encoder ring LEDs |
| 4 | Ambient | 12 | Ambient / underglow lighting |

Per-group commands (G = group ID 0–4, N = LED index):

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| group-brightness | `1B [G] FF` | 1 | int | Plugin command `wheel-group{G}-brightness` (G=2..4). Firmware answers even when hardware absent — cannot be used as a presence check |
| group-normal-mode | `1C [G]` | 1 | int | Telemetry-active mode. Plugin command `wheel-group{G}-mode` |
| group-standby-mode | `1D [G]` | 1 | int | Idle mode. Not yet exposed by plugin |
| group-standby-interval | `1E [G] [2..6]` | 2 | int | 2=breath, 3=circular, 4=rainbow, 5=drift sand, 6=breath color. Not yet exposed by plugin |
| group-led-color | `1F [G] FF [N]` | 3 | array | LED N static RGB. Plugin commands `wheel-rpm-color{1..25}` (G=0), `wheel-button-color{1..16}` (G=1), `wheel-group{G}-color{1..Nmax}` (G=2..4) |
| group-live-colors | `19 [G]` | 20 | array | Bulk live telemetry frame (packed `[idx, R, G, B]` entries, 0xFF padding). **Groups 0/1/3 confirmed**; 2/4 may or may not support. Plugin `wheel-telemetry-rpm-colors`, `wheel-telemetry-button-colors`, `wheel-telemetry-knob-colors` |
| group-live-bitmask | `1A [G]` | 2..8 | int | Per-frame active-LED bitmask (LE). **Groups 0/1/3 confirmed**. Plugin `wheel-send-rpm-telemetry`, `wheel-send-buttons-telemetry`, `wheel-send-knob-telemetry` |

**Static vs live paths**: groups 0/1/3 have two rendering pipelines. Static (`1F`) writes persist in EEPROM and render only when firmware is in idle/constant mode (`wheel-telemetry-mode=2`, `wheel-buttons-idle-effect=1`). Live (`19` + `1A`) writes a volatile frame buffer used while telemetry is active. Group 3 (Rotary/knob) live path confirmed via `knob-rpm-effect.pcapng` (2026-05-03, CS Pro). Groups 2/4 have only the static path in documented commands.

Additional newer wheel commands:

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| meter-auto-rotation | `10` | 1 | int | |
| sleep-breath-brightness | `23 [0/1]` | 1 | int | min (0) / max (1) |
| startup-color | `25` | 3 | array | RGB |
| paddle-thresholds | `26` | 24 | array | 12× 2-byte thresholds |
| knob-active-color | `27 [knob] [role]` | 3 | array | Per-knob "Active position" LED RGB. `knob=0..4` (knob 1..5; CS Pro 0..3, KS Pro 0..4). Role-byte semantics verified live 2026-05-10 against PitHouse: `role=0` is the only writable form — sets the persisted Active LED colour and is what PitHouse's "Active" swatch fires; `role=1` is read-only and returns the live ring-LED colour at the knob's current rotation position. Plugin commands: `wheel-knob{1..5}-active-color` (write/read role 0) and `wheel-knob{1..5}-live-color` (read-only role 1). Earlier docs labelled role 0 as "background/idle" and role 1 as "primary/active" — that mapping was wrong; corrected here and in [`../telemetry/control-signals.md` § Per-knob Active LED colour](../telemetry/control-signals.md). |
| multi-function-switch | `28 [0..2]` | 1 | int | Enable, count, left/right assignment |
| rotary-signal-mode | `2A [N]` | 1 | int | Encoder N (0–4) signal mode (0=Buttons, 1=Knob). **The answers cannot be used to count encoders** — firmware answers every index 0–4 whether or not the encoder is physically present (owner-confirmed 2026-08-25 on the KS: five answers, three knobs), so the count must be recorded per model (`WheelModelInfo.KnobEncoderCount`) and the sweep only bounds an unmeasured rim. Encoders are independent of knob LEDs — most rims have rotary encoders with no addressable knob ring, so `WheelModelInfo.KnobCount` (a group-3 LED count, non-zero only on CS Pro / KS Pro) must not be used to gate this command. PitHouse sweeps the same way, stopping at the rim's real count (`2a [N]`, N=0–3 on a 4-encoder wheel — see [`../findings/2026-04-28-wheel-catalog-read.md`](../findings/2026-04-28-wheel-catalog-read.md)). Some rims re-order: firmware index ≠ physical knob (CS Pro fw 0..3 → knobs 1,4,3,2; KS Pro fw 0..4 → knobs 1,5,4,2,3) |

### Legacy "CS" wheel — Table 8 param-read storm (firmware fault we must not trigger)

The original bare-`CS` wheel (firmware model name `CS`, reports on the bus as
`wheel_wnfw`; RPM-only — 10 LEDs, **no** buttons / flags / knobs / sleep light /
display) does **not** implement large parts of the parameter space newer rims do.
When the plugin reads (or writes) a parameter this firmware lacks, its
param-manager logs one failure per index it can't service and sweeps the whole
table:

```
0e 71 05 [INFO]param_manage.c:424 Table 8: Failed to Read Parameter 0
…
0e 71 05 [INFO]param_manage.c:424 Table 8: Failed to Read Parameter 127
```

These arrive on the firmware-debug channel (wire group `0x0E`, subtype `0x05`,
dev `0x71`). The sweep wedges the param subsystem: identity readback dies, the
wheel stops answering presence polls, and the plugin drops into a ~20 s re-detect
("dogging") loop. Users report this as the wheel "crashing".

**Plugin-caused, not inherent firmware self-validation.** The same wheel
(`wheel_wnfw`) on the same R5 base, driven by **MOZA Pit House**
(`extreme_dogging.pcapng`), emits **zero** `Failed to Read/Write Parameter`
lines — Pit House validates only param Tables 3/4/11/12 and never pokes Table 8.
The plugin produced thousands on identical hardware
(`moza-diagnostics-bundle-20260605-154600.zip`, 3 419 lines). The trigger is a
plugin read/write the wheel can't service. (The benign `[ERRO]diag_svr_event.c
error_code 40/41/42` lines seen once at startup appear under Pit House too and
are **not** the fault signal.)

**Gating rule (do not regress).**

- Never push sleep-light params (`wheel-idle-mode`/`-timeout`/`-speed`/`-color`)
  to a wheel not positively identified as supporting them. Gated on
  `WheelModelInfo.HasSleepLight`; the `CS` entry **and** `WheelModelInfo.Default`
  (unknown models) both set it `false`.
- Never blind-probe extended LED groups (Single/Ambient/knob-brightness) on an
  unidentified wheel — only read a group once a known `WheelModelInfo` says it
  exists. A genuinely new wheel earns these reads by being added to
  `KnownModels`, not by speculative probing.
- The idle keepalives in `PollStatus` — group `0x00` presence poll and the 1-byte
  group `0x43` keepalive — are PitHouse-parity and stay on. The group `0x0E`
  **param poll to the wheel was removed**: PitHouse does not poll the wheel's
  param manager on the matching R9 rig (it polls `0x0E` only on the base), the
  response was always the unset sentinel `FF FF FF FF`, and `0x0E` is the
  `param_manage.c` channel that emits this storm — see
  [`../periodic/group-0x0E-param-reader.md`](../periodic/group-0x0E-param-reader.md).

**Runtime self-protection (implemented 2026-08-10).** `FirmwareDebugLog.ParamStormActive` counts `Failed to Read/Write
Parameter` lines in a trailing 10 s window; ≥ 3 marks a storm (and logs a
one-time SimHub warning so it's on record even if the user never opens the pane).
While a storm is active the plugin skips the heavy LED-capability read batch on
re-detect (so it stops feeding the dogging loop) and the AZOM pane raises a header
banner whose "Enable Serial Capture" button jumps the user to the serial-capture
section so they can grab the traffic for us. The keepalives above are
intentionally exempt.
