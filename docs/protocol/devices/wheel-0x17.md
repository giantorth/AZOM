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

On this unit, group `0x42` **replaces** the usual display-telemetry path. The documented `0x41` `FD DE` enable, the `0x43`/`7D 23` bit-packed value stream, **and the entire session-`0x02` / tier-definition channel-catalog handshake are all absent**: the wheel never advertises a channel catalog (no v0 URL or v2 compact advertisement, no `7C 00` session opens, no wheel→host `0xC3` catalog frames; `0x43` carries only a 1-byte cmdid-`00` keepalive poll). Instead PitHouse pushes **pre-computed display field values** to the wheel (`0x17`) as fixed-layout records at ~28 Hz during gameplay. Because no catalog is negotiated, the layout is a firmware-baked fixed schema rather than a channel-index-driven packing.

This is documented as **observed** on this firmware. The record framing and type schema are proven; per-field channel semantics are only partially decoded (see "candidate mapping" below).

**Frame format** (host→wheel `0x17`):

```
7E <len> 42 17 [type] [b1] [b2] <fixed-layout data> <csum>
```

- `type` (first payload byte) selects a record with a **fixed length**. At startup PitHouse enumerates each record type once with an all-zero payload (declaration); at runtime only a subset carries live data. **14 types are observed across all captures** — `01 02 03 04 05 06 08 09 0b 0c 0d 0e 11 12`; `07 0a 0f 10` never appear (the enumeration is not a dense `0x01`–`0x12` sweep).
- `b1` — per-type sub/validity byte: `0x00` on the zero/enumeration frame, a type-specific non-zero value when populated.
- `b2` — stream/mode flag (type `0x02`: `00`/`20`; type `0x0E`: `00`/`80`/`c0`).

**Record types** (len = total payload bytes incl. `type`/`b1`/`b2`; "live" = carries changing values in these captures):

| type | len | state | b1 (populated) | b2 seen | notes |
|------|-----|-------|----------------|---------|-------|
| `01` | 25 | declared-only | — | 00 | zero enumeration frame |
| `02` | 18 | **live (gameplay)** | 03 | 00, 20 | main per-frame record (layout below) |
| `03` | 19 | declared-only | — | 00 | |
| `04` | 23 | live | 06 | 00 | |
| `05` | 25 | declared-only | — | 00 | |
| `06` | 25 | live | 0c | 00 | richest dashboard — see decoded layout below |
| `08` | 23 | declared-only | — | 00 | |
| `09` | 24 | live | 01 | 00, 80 | |
| `0b` | 15 | live | 00 | 00, 04 | |
| `0c` | 18 | declared-only | — | 00 | |
| `0d` | 25 | live | 00 | 00 | |
| `0e` | 24 | live | 0d | 00, 80, c0 | `b2` mode flips `c0`→`80` mid-session |
| `11` | 25 | declared-only | — | 00 | |
| `12` | 25 | declared-only | — | 00 | |

**Type `0x02` layout** (proven across 8720/8720 frames; offsets are payload-relative):

```
off:  0    1    2    3  4  5   6  7   8  9  10 11 12 13  14  15  16  17
      02   b1   b2   00 00 00  W0 00  W1 00 W2 00 W3 00  S   F   00  G
```

- `[6] [8] [10] [12]` (`W0..W3`) — a **4-element array, equal in 100 % of frames** (e.g. all `0x1A`=26 when the engine is running, `0` idle). The companion high bytes `[7] [9] [11] [13]` are always `00`. Candidate: per-wheel channel (tyre temp/pressure).
- `[14]` (`S`) — scalar `0..158`; rises with engine activity. Candidate: RPM / engine load.
- `[15]` (`F`) — `0` while the RPM-LED bar (group `0x3F` `1A 00`) is dark, `75` (`0x4B`) the instant any RPM LED lights. Engine-running flag.
- `[17]` (`G`) — small enum `0..5`; loosely rises with revs. Candidate: gear.

**Candidate semantics are unproven.** They were derived only by time-aligning these fields against the group-`0x3F` RPM-LED bitmask *within the same capture* — no external ground-truth telemetry labels exist for these captures. The structure (record types, fixed lengths, the 4-element array, the `0x4B` engine flag) is proven; the exact channel identity of each numeric field is not. The other live types have **decoded field offsets/widths** (below) but still-undecoded channel semantics — "no capture = I don't know."

**The active record type tracks the displayed dashboard.** The *populated* type is not global — it follows which built-in dashboard the wheel shows. `FSR1 with game` (single dashboard) emits type `02` for its entire run; `FS1 multiple changes` shifts `0e` → (`09`) → `06` as dashboards are cycled. So the field/channel set is **per-dashboard, not globally fixed** — at least two dashboards drove distinct record types (`0e` and `06`).

Each dashboard's record type is a **wholly distinct fixed layout** — different length and a different per-type `b1` (`02`→`03`, `04`→`06`, `06`→`0c`, `09`→`01`, `0d`→`00`, `0e`→`0d`; constant within a type, likely a layout/field-count id) — with non-overlapping field offsets. Two anchors recur across the rich dashboards: the `0x4B` **engine-running flag** a few bytes before the end, and a small **gear enum (`0..5`) in the last data byte**. Switching dashboards changes *which* fields are sent **and how many** — they are different layouts, not the same fields rearranged.

**Decoded field layouts.** Payload-relative offsets, from per-offset variance analysis over all `usb-capture/fsr1/` captures (`tools/fsr1-0x42-extract` + `tools/fsr1-field-decode`). Field **offsets and widths are proven**; **channel semantics remain unproven** (best-guess from RPM-LED correlation / field position). Bytes not listed are constant `00` padding. The plugin encodes this catalog in [`Telemetry/Fsr1DashboardCatalog.cs`](../../../Telemetry/Fsr1DashboardCatalog.cs) and exposes each field for user channel assignment.

- **Type `02`** — len 18, b1 `03` (see byte map above): `[6][8][10][12]` u8 4-element wheel array (companion hi bytes `00`); `[14]` u8 scalar `0..158` (RPM/load); `[15]` engine flag (`0`/`0x4B`, also `0x7E` in some `b2=20` frames); `[16]` u8 secondary flag (`0`/`0x4B`); `[17]` u8 gear `0..5`.
- **Type `06`** — len 25, b1 `0c` (richest): `[5]` u8 flag/high (`00`/`01`/`80`); `[6,7]` **u16-BE** primary ramp (`0..65535`); `[8]` u8; `[18]` u8 (`0..102`); `[19]` u8 (`0..92`); `[20]` u8 (`0..255`); `[21]` u8; `[22]`/`[23]` engine flag (`0x4B`); `[24]` u8 gear `0..5`.
- **Type `0e`** — len 24, b1 `0d`: `[11]` u8 (`0..54`); `[12]` u8 (`0..32`); `[13]` u8 (`0..255`); `[14]` (`01`/`7E`); `[15]` (`0`/`1`); `[16]`/`[17]` engine flag (`0x4B`); `[23]` u8 gear `0..5`.
- **Type `09`** — len 24, b1 `01` (sparse): `[6,7]` **u16-BE**; `[18]` u8 small enum (`0..3`); `[19]`=`01`, `[23]`=`02` constant.
- **Type `0d`** — len 25, b1 `00`: two live 5-byte groups `[5..9]` and `[10..14]` (u8 each) + `[18]` u8 (`1..15`). Grouping undecoded.
- **Type `04`** — len 23, b1 `06`: **static** in these captures (frozen `24`@6, `21`@15, `21`@18, `34`@19, `4B`@21) — not actively driven. **Type `0b`** (len 15, b1 `00`) likewise static (`20`@12).

**Channel identification — status.** The fields are *not yet mapped to specific Telemetry.json channels*; only the type-`02` candidates above are guessed (from internal RPM-LED correlation). Findings from PitHouse (`MOZA Pit House/bin`):

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

`g32/81` is sent **only at switch time** — the single-dashboard gameplay run (`FSR1 with game`) contains zero `g32/81` frames; it just streams the active type. The 19 index positions map **many-to-one** onto the live record types (pages within a dashboard share a layout). Partial index→type map observed: `14`→`04`, `7`→`09`(+`0d`), `15`→`06`, `18`→`0c`, `17`→`11`(+`12`); a mid-session gameplay switch capture is needed to nail all 19. Open item: whether the `0x40` config re-sweep is strictly required on a host switch or merely habitual.

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
