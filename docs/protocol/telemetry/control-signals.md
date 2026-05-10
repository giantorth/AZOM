## Telemetry control signals

### Dash telemetry enable (group 0x41, device 0x17, cmd `[0xFD, 0xDE]`)

Sent ~100×/s. Data always `00 00 00 00`. Likely mode/enable flag — value 0 = telemetry active.

### Sequence counter (group 0x2D, device 0x13, ~50 Hz)

Cmd `[0xF5, 0x31]`. Data: `00 00 00 XX` where XX increments by 1 each send. Base unit sequence counter.

### RPM LED telemetry (group 0x3F, device 0x17, cmd `[0x1A, 0x00]`)

Sent ~once/s. 8 data bytes = 4 × 16-bit LE values:

```
[current_pos, 0x0000, 0x03FF, 0x0000]
```

- `current_pos = current_rpm / max_rpm × 1023` — 10-bit RPM fraction
- Value 3 always 1023 (fixed denominator)
- Values 2 and 4 always 0

### Knob LED telemetry (group 0x3F, device 0x17, cmd `[0x1A, 0x03]`)

Live knob indicator bitmask, sent in sync with RPM during telemetry.
Uses the 8-byte active+window form:

```
7E 0C 3F 17 1A 03 [active_mask:u32 LE] [window_mask:u32 LE] [checksum]
```

- `window_mask` = `0x0000000F` (CS Pro, 4 knobs) or `0x0000001F` (KS Pro, 5 knobs)
- `active_mask` = subset of window — each bit = one knob indicator LED
- Knob 0 = bit 0, knob 1 = bit 1, etc.

PitHouse fills knobs progressively alongside RPM: as RPM rises, knob bits
light left→right; at redline all are lit; on RPM drop they drain in sync.
Companion color writes (`0x19 0x03`) set per-knob RGB — observed gradient
from blue (knob 0) through purple to red (knob 3). See
[`../leds/color-commands.md`](../leds/color-commands.md) for frame layout and
worked examples.

Source: `knob-rpm-effect.pcapng` (2026-05-03, CS Pro W17).

### Per-knob Active LED colour (group 0x3F, device 0x17, cmd `[0x27, <knob>, <role>]`)

Sets the **Active position LED colour** for a rotary knob — the colour shown at
whichever ring LED is currently at the knob's rotation position. Wire frame
(6-byte body + checksum):

```
7E 06 3F 17 27 <knob> <role> <R> <G> <B> <chk>
```

- `knob` — knob index 0..4 (knob 1..5; CS Pro uses 0..3, KS Pro uses 0..4).
  Indices beyond the physical knob count are silently ignored by firmware.
- `role` — verified live against PitHouse 2026-05-10 (capture
  `sim/logs/bridge-20260510-111708.jsonl`,
  `findings/2026-05-10-knob-led-cmd27.md`):
  - `0x00` — **WRITE**: sets the knob's stored Active LED colour. The wheel
    paints whichever ring LED is the knob's current rotation position with this
    RGB. **READ** (on group `0x40`): returns the persisted Active colour.
  - `0x01` — **READ-only**: returns the *live* LED colour currently visible at
    the knob's active rotation position (varies as the knob is turned).
    PitHouse never WRITES `role=0x01`; doing so leaves the wheel echoing but
    nothing changes on screen.
- `R G B` — 24-bit RGB, 0x00..0xFF each channel.

Earlier docs in this codebase named role 0 as "background/idle" and role 1 as
"primary/active". Live PitHouse capture proved that misleading: role 0 IS the
Active colour (PitHouse's "Active" swatch), and role 1 is read-only state. The
**Inactive / background** ring colours are written via cmd 0x1F 0x03 0x01 per
LED — see [`../leds/wheel-groups-0x3F-0x40.md`](../leds/wheel-groups-0x3F-0x40.md)
under `wheel-knob-bg-color{N}`.

Captured examples (CS Pro, W17 — 2026-05-10):

```
7E 06 3F 17 27 00 00 F7 00 00 …   # knob 1 Active = red
7E 06 3F 17 27 01 00 00 FF 00 …   # knob 2 Active = green
7E 06 3F 17 27 02 00 00 00 FF …   # knob 3 Active = blue
7E 06 3F 17 27 03 00 FF F6 85 …   # knob 4 Active = yellow
```

Wheel echoes `(group | 0x80)` / swapped device nibble / payload mirror — plugin
recognizes via `WheelEchoPrefixes` entries for `(0x3F, 0x17, 0x27, 0x00..0x04)`.
The `0x27 <group> 0xFF` form reads *brightness* for the corresponding LED
group, not colour.

Command names in `MozaCommandDatabase` (post-2026-05-10 rename):
- `wheel-knob{1..5}-active-color` — write/read role 0 (3-byte RGB payload)
- `wheel-knob{1..5}-live-color` — read-only role 1 (3-byte RGB payload)

Wire knob byte = knob_number − 1 (knob 1 → 0x00, knob 5 → 0x04).
