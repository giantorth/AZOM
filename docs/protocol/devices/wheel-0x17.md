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
| key-combination | `13` | 4 | array | RW. Read form: 1-byte request `13` returns 4-byte current value (`FF FF FF FF` = unset) |
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
| paddles-calibration | `08` | 1 | int | Write-only |

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
- Mapping the `0x42` fields therefore needs either binary RE of PitHouse's `0x42`-builder (exe references `Steering Wheel/FSR-Official.json`, `Protocol - FSR`; a Ghidra workflow already exists in `usb-capture/pithouse-re.md`) or a controlled capture with known telemetry. Until then, treat the type-`02` candidates as unconfirmed. Each switch coincides with a **full group `0x40` config re-sweep** (~30 config writes re-pushed, repeating ~every 2 s while the user is in the wheel/menu). **No dedicated dashboard-switch opcode was observed** (no `7C 25`, no `kind=4`, no `B8` event), and the wheel sends **nothing** about channels or dashboards on the serial bus: every wheel→host frame is either a solicited reply to a host config read/write (`0x80` ack, `0xC0`/`0xBF` group-0x40/0x3F replies, `0x8E` group-0x0E replies, `0xB2`/`0xB3` poll replies, identity replies) or an unsolicited **diagnostic log string** (group `0x0E` cmd `05xx` — `NRFloss`, `RotaryMode`, `Calib…`, `[INFO]…`). There is no catalog advertisement and no structured input-event frame (0 real `B8` frames — the 690 b2h frames containing a `0xB8` byte are all value bytes like `0bb8`=3000 in config readbacks), and nothing on the `0x42` group from the wheel (`0xC2` count = 0). The wheel *does*, however, announce its **current dashboard/page index** over serial as a `0x0E` diagnostic-log line (`Table 7 Param 6 Written: <N>`, `N`=`0..18`) on each switch — see "Dashboard switching" below. For value computation **the host owns the channel/value decision**; for dashboard *selection* either side can drive it — the **host** via a dedicated group-`0x32` cmd-`0x81` index write, the **wheel** via an HID button combo — see "Dashboard switching" below.

**Dashboard switching — verified.** The wheel has **19 dashboard/page positions** (index `0..18`); the active index selects which `0x42` record type the screen renders. Either the host or the wheel can change the index, and the wheel always reports its current index on the serial bus.

- **Host-initiated** (PitHouse UI; the plugin's dropdown). The host sends a dedicated **select command — group `0x32`, cmd `0x81`** — with the target index as a big-endian u32:
  ```
  7E 05 32 17 81 00 00 00 <index>          (index 0..18; e.g. 0x11=17)
  reply: 7E 05 B2 71 81 00 00 00 <index>   (wheel echoes/acks)
  ```
  The wheel switches to that page and logs `Table 7, Param 6 Written: <index>`. **Verified 7/7** in `dashboard change through pithouse, connected to base` (no wheel HID activity in that capture; each `g32/81 <N>` write is followed ~20 ms later by the wheel adopting index `N`). PitHouse then re-pushes the `0x40` config sweep and streams the `0x42` record type that matches the new page. *(Earlier drafts of this page mislabeled group `0x32` as a read poll, and then guessed that emitting the `0x42` record type was the switch — both wrong. The `0x32/0x81` write is the selector; the `0x42` stream is just the per-page value data.)*
- **Wheel-initiated** (button **combo**, no host command). A held modifier — **HID byte 21 bit `0x08`** — plus a direction tap — **HID byte 18 bit `0x01`/`0x04`** — on the 42-byte EP `0x83` input report. The wheel changes its own page in firmware (`0x01` = +1 wrap `18→0`, `0x04` = −1 wrap `0→18`) and reports the new index via the `0x0E` log below. The host sends **no** `g32/81` here — it follows by streaming the matching type. The structured `B8` wheel-input event used by standard display wheels (§ Group 0x43) is **absent** on this gen.
- **Reading the active index (either path).** The wheel emits an *unsolicited* group-`0x0E` diagnostic log on every change: `[INFO]param_manage.c:340 Table 7, Param 6 Written: <N>`, `N` = absolute index `0..18`. (The `B2/81` ack carries the same value.) So a host can always recover the absolute active dashboard from the serial stream — no HID press-counting needed.

`g32/81` is sent **only at switch time** — the single-dashboard gameplay run (`FSR1 with game`) contains zero `g32/81` frames; it just streams the active type. The 19 index positions map **many-to-one** onto the live record types (pages within a dashboard share a layout).

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

The contributor's exact channels were game/hub-specific (`ATSRHubMain.Telemetry.*`, `PersistantTrackerPlugin.*`, plus generic `DataCorePlugin.*`); the **meaning** of each slot is the durable finding, since channel→slot assignment is host-chosen and user-overridable. The catalog seeds each decoded slot's default with the canonical `simhub_property` from [`Data/Telemetry.json`](../../../Data/Telemetry.json) (MOZA's own channel catalog) — e.g. speed → `DataCorePlugin.GameData.SpeedKmh`, fuel remaining laps → `DataCorePlugin.GameData.FuelLaps`, fuel/lap → `DataCorePlugin.GameData.FuelConsumeLap`. The one exception is **light stage** (record `12` @21): Telemetry.json has only individual light bools (`HighBeamLight`, `RainLight`, …), no aggregate, so it ships with no default. The `0x42` path is byte-aligned `u16` (not tier-def bit-packing), so only the property *names* are taken from Telemetry.json, not its compression codes.

#### Per-profile field overrides & synthetic splits (plugin-side, not wire protocol)

The plugin lets a user retune any `0x42` field without changing the wire format: move its start/end data byte, flip endianness (width 2), apply Scale/Bias, and remap its SimHub channel. Overrides are stored deviation-only — a field at its catalog default persists nothing. A user may also **split** a field into two — the new "synthetic" field is net-new (absent from the static catalog), owns a sub-span of the record's data bytes, and carries its own channel mapping; it's stored in the profile and merged into the field set at every enumeration point (stream/UI/probe/viz). The record stays a contiguous gapless partition of `[5, PayloadLen-1]`; *remove split* reclaims the bytes into a neighbour. None of this changes the bytes on the wire — it only changes which SimHub value drives each byte span.

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
| old-rpm-brightness | `14 00` | 1 | int | |

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
| rotary-signal-mode | `2A [N]` | 1 | int | Encoder N (0–4) signal mode |

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
- The load-bearing idle keepalives in `PollStatus` — group `0x00` presence poll,
  group `0x0E` param poll, 1-byte group `0x43` keepalive — are PitHouse-parity
  and **must stay on**; they hold the wheel's param subsystem up. They are not
  the storm trigger and must not be backed off.

**Runtime self-protection.** `FirmwareDebugLog` counts `Failed to Read/Write
Parameter` lines in a trailing 10 s window; ≥ 3 marks a storm (and logs a
one-time SimHub warning so it's on record even if the user never opens the pane).
While a storm is active the plugin skips the heavy LED-capability read batch on
re-detect (so it stops feeding the dogging loop) and the AZOM pane raises a header
banner whose "Enable Serial Capture" button jumps the user to the serial-capture
section so they can grab the traffic for us. The keepalives above are
intentionally exempt.
