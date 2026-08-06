# Wheel `key-combination` (cmd `0x13`) — shortcut-enable bitfield

The wheel's `key-combination` setting (groups `0x3F` write / `0x40` read,
dev `0x17`) was documented as an opaque 4-byte array with
"`FF FF FF FF` = unset". Wrong on both counts: it is a **bitfield of
per-shortcut enable flags** (1 = enabled), and all-ones is the factory
default — **every shortcut enabled**, not "unset".

Wire-verified 2026-07-31 by toggling each entry of PitHouse's "key combo
settings" list individually on an **RS21-W17-MC SW** wheel (fw reply
`84 71 01 02 07 07`), base fw 1.2.10.10, capture
`sim/logs/bridge-20260731-064830.jsonl` (t=1785511893…1785512610).

## Bit map

Payload is `13 [b0] [b1] [b2] [b3]`. Identified bits (all others rested
at 1 throughout — this wheel's list has exactly 8 entries, so the
remaining bits are either other models' shortcuts or unused):

| Byte | Bit | Mask | Shortcut (PitHouse label / combo) |
|------|----:|------|-----------------------------------|
| b1 | 0 | `0x01` | Max steering angle → **360** (btn 33 + left stick **up**) |
| b1 | 1 | `0x02` | Max steering angle → **540** (btn 33 + left stick **right**) |
| b1 | 2 | `0x04` | Max steering angle → **720** (btn 33 + left stick **down**) |
| b1 | 3 | `0x08` | Max steering angle → **900** (btn 33 + left stick **left**) |
| b3 | 0 | `0x01` | Left-stick mode change (press both thumb sticks until rev lights blink) |
| b3 | 1 | `0x02` | Switch digital dash display (btn 32 + left stick left/right = prev/next dash; btn 20 + right stick up/down = template, left/right = dash screen) |
| b3 | 3 | `0x08` | Change wheelbase setting (btn 34 + left stick up/down/left/right) |
| b3 | 7 | `0x80` | Wheel dash template (btn 19 + up/down = change wheel dash, btn 19 + left/right = change dash screen) |

The four angle presets map to the four stick directions in byte-order
up/right/down/left — bits 0–3 of b1.

## Observed transitions

Starting state `FF F0 FF F5` (mode-change + dash-template on, everything
else off):

| t | Write | Bit changed | Action |
|---|-------|-------------|--------|
| 1785511893.374 | `FF F0 FF F4` | b3.0 → 0 | mode change OFF |
| 1785511932.197 | `FF F0 FF F5` | b3.0 → 1 | mode change ON |
| 1785512045.529 | `FF F0 FF FD` | b3.3 → 1 | wheelbase setting ON |
| 1785512122.844 | `FF F1 FF FD` | b1.0 → 1 | angle 360 ON |
| 1785512210.322 | `FF F3 FF FD` | b1.1 → 1 | angle 540 ON |
| 1785512325.069 | `FF F7 FF FD` | b1.2 → 1 | angle 720 ON |
| 1785512361.712 | `FF FF FF FD` | b1.3 → 1 | angle 900 ON |
| 1785512454.576 | `FF FF FF FF` | b3.1 → 1 | digital dash ON |
| 1785512608.674 | `FF FF FF 7F` | b3.7 → 0 | dash template OFF |
| 1785512609.277 | `FF FF FF FF` | b3.7 → 1 | dash template ON |

## Behaviour

- Each write is echoed on group `0xBF` (dev `0x71`) with the full value —
  standard wheel-settings write ack.
- PitHouse polls the register every ~2 s (`0x40` read → `0xC0` reply);
  read-backs reflect a write within one poll cycle.
- EEPROM location: the wheel's group-`0x0E` debug log emits
  `[INFO]param_manage.c:340 Table 2, Param 26 Written: -1 -1` after the
  all-ones write (the u32 rendered as two s16 halves) — the bitfield
  persists in **Table 2 Param 26**.

## Doc/plugin alignment

- Settings table updated: [`../devices/wheel-0x17.md`](../devices/wheel-0x17.md)
  (`key-combination` row now points here).
- Plugin command DB (`Protocol/MozaCommandDatabase.cs`) does not register
  wheel cmd `0x13` at all (verified 2026-07-31); register a
  `wheel-key-combination` 4-byte array if the plugin ever exposes
  shortcut toggles.
