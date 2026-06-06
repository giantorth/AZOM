## RPM and button LED color commands

Live LED color frames for the wheel's RPM strip and button matrix. Sent at
SimHub's update cadence (typically 60 Hz when telemetry is active). Two
companion commands per group: a **20-byte color chunk** and a **bitmask
selector** that picks which indices are lit. See
[`../devices/wheel-0x17.md` § Group `0x3F` Live Telemetry](../devices/wheel-0x17.md)
for the per-command rows.

### Frame layouts

**RPM color chunk** (`wheel-telemetry-rpm-colors`):

```
7E 14 3F 17 19 00 [20 bytes: 5 × (idx, R, G, B)] [checksum]
```

**RPM active-LED bitmask** (`wheel-send-rpm-telemetry`):

```
7E [N] 3F 17 1A 00 [active_mask LE] [window_mask LE] [checksum]
```

**8-byte form is canonical** — `[active_mask:u32 LE] [window_mask:u32 LE]`.
PitHouse emits this form on **every** wheel captured, and the plugin now does
too (`BuildWindowedBitmaskBytes`). `window_mask` defines which LEDs the firmware
treats as addressable and `active_mask` is the subset currently lit; both must be
present. Verified windows:

| Wheel (base) | `window_mask` | Capture |
|--------------|---------------|---------|
| CS V2.1 (R9) | `0x000003ff` (10 LEDs, bit 0 = first) | `idk.pcapng`, `MOZA CS V2.pcapng` |
| CS Pro / W17 (R5) | `0x00001ff8` (13-LED bar) … `0x0000ffff` (16-LED) | 2026-04-29 capture, `automobilista2-*` |

Different dashboards swap `window_mask` on the fly (`f81f0000` → 13-LED RPM bar;
`ffff0000` → 16-LED redline / wide-zone). The plugin's SimHub LED path always
renders a full bar, so it sends `window_mask = (1 << RpmLedCount) - 1` (full set,
bit `i` = LED `i`): `0x03ff` for CS V2.1, `0xffff` for CS Pro.

Two legacy widths appear in older captures and are **no longer emitted by the
plugin** (a bare `[bitmask:u16]` with no window left CS V2.1's first LED stuck
lit): the 2-byte form (`[bitmask:u16]`, older / small bars) and the 4-byte form
(`[bitmask:u32]`, ≥17-LED bars). The 8-byte `u32` active mask covers every bit
both of those did.

Mask progression with rising RPM (8-byte form, `window_mask = 0x00001ff8`):
```
00 00 00 00  f8 1f 00 00   ← idle (no LEDs lit)
00 08 00 00  f8 1f 00 00   ← stage 1 (bit 3)
00 18 00 00  f8 1f 00 00   ← stage 2
00 38 00 00  f8 1f 00 00   ← stage 3
00 78 00 00  f8 1f 00 00   ← stage 4
00 f8 03 00  f8 1f 00 00   ← stage 5
00 f8 0f 00  f8 1f 00 00   ← stage 6
00 f8 1f 00  f8 1f 00 00   ← full bar
00 07 e0 00  ff ff 00 00   ← redline-zone-only mode (different window)
```

`1A 01 ff …` is a scene-reset / mode-toggle variant emitted at dashboard switch and game-state transitions.

**Knob color chunk** (`wheel-telemetry-knob-colors`):

```
7E 16 3F 17 19 03 [20 bytes: 5 × (idx, R, G, B)] [checksum]
```

**Knob active-LED bitmask** (`wheel-send-knob-telemetry`):

```
7E 0C 3F 17 1A 03 [active_mask:u32 LE] [window_mask:u32 LE] [checksum]
```

8-byte form only (same as RPM 8-byte form). `window_mask` = `0x0000000F`
(4 knobs, CS Pro) or `0x0000001F` (5 knobs, KS Pro). Each bit = one
rotary knob indicator.

Mask progression (CS Pro, 4 knobs, RPM-synced effect):
```
00 00 00 00  0f 00 00 00   ← idle (no knobs lit)
01 00 00 00  0f 00 00 00   ← 1 knob
03 00 00 00  0f 00 00 00   ← 2 knobs
07 00 00 00  0f 00 00 00   ← 3 knobs
0f 00 00 00  0f 00 00 00   ← all 4 knobs lit
```

Companion color writes set per-knob RGB (gradient from blue → purple → red):
```
19 03  00 00 00 FF   01 3F 00 C0   02 7F 00 80   03 BF 00 40   FF 00 00 00
       knob 0=blue   knob 1        knob 2        knob 3=red    (padding)
```

**Sticky-bitmask discipline (knob group).** Writing `active_mask = 0` to
`1A 03` while the firmware was previously in the "telemetry owns the
knobs" state drops the knob LEDs back to their stored EEPROM defaults
(per-knob `27 [knob] 00` Active colours, ring `1F 03 01 [N]` Inactive
colours) for ~1 frame before the next non-zero bitmask re-engages the
live path. Visible as a default-colour flash whenever a host-driven
animation passes through an all-black frame. PitHouse never trips this
in observed captures — 286/286 knob-bitmask writes in
`sim/logs/bridge-20260517-081336.jsonl` are `active=0/window=0`,
i.e. PitHouse drives no dynamic knob telemetry, so it has no active
state to lose. Hosts that **do** drive per-frame knob colour chunks
must hold the bitmask sticky across mid-session black frames: keep
streaming colour chunks (so the firmware buffer renders the actual
black frame), but suppress the bitmask write when its current value
would be zero. Release the bitmask to zero only on explicit teardown
(disconnect, mode switch). Plugin implementation: the knob block in
`Devices/MozaLedDeviceManager.cs` Display() gates the `1A 03` write on
`knobBitmask != 0`; the keepalive re-emits the last non-zero value.

**Button color chunk** (`wheel-telemetry-button-colors`):

```
7E 14 3F 17 19 01 [20 bytes: 5 × (idx, R, G, B)] [checksum]
```

**Button active-LED bitmask** (`wheel-send-buttons-telemetry`):

```
7E 0A 3F 17 1A 01 [active_mask:u32 LE] [window_mask:u32 LE] [checksum]
```

Same 8-byte `active+window` form as RPM. The button `window_mask` is **per-wheel**:
PitHouse drives wheels whose button layout is non-contiguous with `window` = the
full set of mapped protocol indices, and contiguous-button wheels with `window = 0`:

| Wheel | Button layout | `window_mask` | Capture |
|-------|---------------|---------------|---------|
| CS V2.1 | non-contiguous (indices 0,1,3,6,8,9) | `0x0000034b` — **buttons stay dark when 0** | `idk.pcapng` |
| CS Pro (W17) | contiguous (0..7) | `0x00000000` | `automobilista2-nebula-pithouse.pcapng` |

The plugin derives this from `WheelModelInfo.ButtonWindowMask` (OR of the
`ButtonLedMap` bits when the map is non-null, else `0`).

| Byte | Value | Meaning |
|------|-------|---------|
| 0 | `0x7E` | Frame start |
| 1 | `[N]` | Payload length (0x14 = 20 for color chunk; 0x0A = 10 for the 8-byte active+window bitmask) |
| 2 | `0x3F` | Wheel-config write group |
| 3 | `0x17` | Device wheel |
| 4 | `0x19` (color) / `0x1A` (bitmask) | Cmd ID byte 1 |
| 5 | `0x00` (RPM) / `0x01` (button) / `0x03` (knob) | Cmd ID byte 2 — selects LED group |
| 6.. | LED entries / bitmask | See per-command sections below |

### 20-byte color chunk format

Each chunk packs **5 LEDs × 4 bytes**:

| Offset within chunk | Field | Notes |
|---------------------|-------|-------|
| `0` | LED index | Physical LED position (`0xFF` = unused padding) |
| `1` | R | Red 0..255 |
| `2` | G | Green 0..255 |
| `3` | B | Blue 0..255 |
| `4..7` | Next LED | …repeats five times |

Chunks per group:

| Group | LED count | Chunks needed |
|-------|-----------|---------------|
| RPM (`0x19 00`) | 10 LEDs (legacy), 16 LEDs (CS Pro), 18 LEDs (KS Pro) | 2 chunks for ≤10, 4 chunks for 16 (last padded), 4 chunks for 18 (last padded) |
| Button (`0x19 01`) | 14 (VGS) / 8 (CS V2.1, CS Pro) / varies | 3 chunks (last padded) |
| Knob (`0x19 03`) | 4 (CS Pro) / 5 (KS Pro) | 1 chunk (last padded) |

**Padding rule:** unused entries within a chunk MUST use index `0xFF`. Zero
padding (`00 00 00 00`) is interpreted as "set LED 0 to black" by firmware,
causing button 0 to flicker on every frame. See
[`Devices/MozaLedDeviceManager.cs:472`](../../../Devices/MozaLedDeviceManager.cs)
(`SendColorChunks`).

### Bitmask format

Selects which LEDs are currently lit. The plugin emits the **8-byte
`active+window` form** for every group (RPM, button, knob) via
[`Devices/MozaLedDeviceManager.cs`](../../../Devices/MozaLedDeviceManager.cs)
(`BuildWindowedBitmaskBytes`):

```
[active_mask:u32 LE] [window_mask:u32 LE]
```

- `window_mask` = the wheel's full addressable LED set for that group
  (RPM: `(1 << RpmLedCount) - 1`; button: `WheelModelInfo.ButtonWindowMask`;
  knob: `(1 << KnobCount) - 1`). Held constant frame-to-frame.
- `active_mask` = the lit subset. Bit `i` lit ↔ LED `i` has non-black color
  in the chunk write.

Plugin sends the bitmask only when it changes (or every frame when
`AlwaysResendBitmask` is set), regardless of color-chunk cadence.

### Example (CS V2.1 — 10 RPM LEDs, alternating red/blue)

Color chunks (2 × 20-byte):

```
chunk 0 (LEDs 0..4):
  7E 14 3F 17 19 00
    00 FF 00 00   01 00 00 FF   02 FF 00 00   03 00 00 FF   04 FF 00 00
  [chk]
chunk 1 (LEDs 5..9):
  7E 14 3F 17 19 00
    05 00 00 FF   06 FF 00 00   07 00 00 FF   08 FF 00 00   09 00 00 FF
  [chk]
```

Bitmask (all 10 lit), 8-byte active+window form:

```
7E 0A 3F 17 1A 00 FF 03 00 00 FF 03 00 00 [chk]   # active=0x03FF lit, window=0x03FF
```

### Wheel echo

Both write commands echo verbatim — see
[`../wire/wheel-write-echoes.md`](../wire/wheel-write-echoes.md) entries for
prefixes `19 00`, `19 01`, `19 03`, `1A 00`, `1A 01`, `1A 03` (group `0x3F`, dev `0x17`).

### Static (settings) vs live (telemetry) paths

Groups `0x19`/`0x1A` are the **live** path: per-frame writes that render
only while telemetry is active (`wheel-telemetry-mode != 2`). The static
path uses cmd `0x1F [G] FF [N]` to persist a per-LED color in EEPROM (see
[`../devices/wheel-0x17.md` § Extended LED Group Architecture](../devices/wheel-0x17.md)).
The two pipelines coexist: static colors render in idle mode; live colors
override while a frame is feeding the bitmask.
