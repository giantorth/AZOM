## Wheel LED group architecture (groups `0x3F` write / `0x40` read)

Newer wheels organize LEDs into **5 independently controlled groups**
addressed via a single `[group_id]` byte in the command payload. Groups
share command IDs (`1B`, `1C`, `1D`, `1E`, `1F`, `19`, `1A`) — only the
group byte differs.

> **Source:** rs21_parameter.db plus live-capture verification on CS Pro
> (W17) for groups 0/1; groups 2-4 are documented in DB but per-frame
> support varies by wheel model.

### Group catalog

| Group ID | Name | Max LEDs | Wheels with this group | Purpose |
|----------|------|----------|------------------------|---------|
| 0 | Shift | 25 | All wheels with RPM strip | RPM indicator bar |
| 1 | Button | 16 | Most wheels | Button backlights |
| 2 | Single | 28 | KS Pro, CS Pro | Single-purpose status indicators |
| 3 | Rotary | 56 | KS Pro (5-knob), CS Pro (4-knob) | Rotary encoder ring LEDs |
| 4 | Ambient | 12 | KS Pro | Ambient / underglow lighting |

### Frame layout

```
7E [N] 3F 17 [cmd] [group_id] [...] [checksum]
```

| Byte | Value | Meaning |
|------|-------|---------|
| 0 | `0x7E` | Frame start |
| 1 | `[N]` | Payload length |
| 2 | `0x3F` | Wheel-config write group (use `0x40` for read) |
| 3 | `0x17` | Device wheel |
| 4 | cmd | See per-cmd table |
| 5 | group_id | 0..4 |
| 6+ | varies | Per-cmd value bytes |
| –1 | chk | Frame checksum |

### Per-group commands

`G` = group ID (0–4); `N` = LED index within group (0..max-1).

Plugin commands use the group's semantic prefix
(`wheel-rpm-`, `wheel-button-`, `wheel-single-`, `wheel-knob-`, `wheel-ambient-`)
rather than `wheel-group{G}-`.

| Command | Cmd | Wire payload | Plugin command(s) | Notes |
|---------|-----|--------------|-------------------|-------|
| group-brightness | `1B [G] FF` | 1-byte int | `wheel-{single,knob,ambient}-brightness` (G=2..4) | Firmware answers regardless of hardware presence — cannot be used as a strict presence check |
| group-normal-mode | `1C [G]` | 1-byte int | `wheel-{single,knob-led,ambient}-mode` | Telemetry-active mode |
| group-idle-effect | `1D [G] [effect_id]` | 1-byte int | `wheel-{telemetry,buttons,single,knob,ambient}-idle-effect` | Idle animation. Effect IDs `0..6`: `0`=Off, `1`=Constant, `2`=Breathing, `3`=Color Cycle, `4`=Rainbow, `5`=Sand Flow, `6`=RGB Pulse. Verified live for groups 0/1/3 on 2026-05-10. Earlier docs called this `group-standby-mode` |
| group-idle-interval | `1E [G] [effect_id] [BE u16 ms]` | 3-byte payload | `wheel-{telemetry,buttons,knob}-idle-interval` | Per-effect speed slider. Payload is `[effect_id, ms_msb, ms_lsb]` (NOT a flat 2-byte int — earlier docs were incorrect). Verified live 2026-05-10 |
| group-led-color (Inactive) | `1F [G] [sub] [N] [RGB]` | 3-byte RGB | RPM: `wheel-rpm-color{1..25}` (G=0, sub=`0xFF`); Buttons: `wheel-button-color{1..16}` (G=1, sub=`0xFF`); Knob ring: `wheel-knob-bg-color{1..56}` (G=3, **sub=`0x01`**); Single/Ambient: `wheel-{single,ambient}-color{N}` (G=2/4, sub=`0xFF`) | LED N persistent base color. **Sub byte `0x01` for the knob ring** (PitHouse's "Inactive" swatch wire); `0xFF` elsewhere. Earlier docs assumed `0xFF` universally |
| group-live-colors | `19 [G]` | 20-byte (5×idx+RGB) | per-group telemetry color cmd | Bulk live telemetry frame. Groups 0/1/3 confirmed |
| group-live-bitmask | `1A [G]` | 2..8-byte int LE | `wheel-send-{rpm,buttons,knob}-telemetry` | Per-frame active-LED bitmask. Groups 0/1/3 confirmed |
| knob-active-color | `27 [knob] 00 [RGB]` | 3-byte RGB | `wheel-knob{1..5}-active-color` | Per-knob Active LED override (cmd 0x27 ROLE=0). Drives the single LED at the knob's current rotation position. Verified 2026-05-10. The `27 [knob] 01 …` variant is **read-only** (`wheel-knob{1..5}-live-color`) — returns the live LED color at the active position; PitHouse never writes role=1 |

### Static vs live rendering pipelines

Groups 0 and 1 have **two parallel pipelines** that the firmware
multiplexes based on the active mode:

| Pipeline | Cmds | Where state lives | When it renders |
|----------|------|-------------------|-----------------|
| Static | `1F [G] FF [N]` | EEPROM (per-LED RGB) | Idle/constant mode (`telemetry-mode = 2`, `buttons-idle-effect = 1`) |
| Live | `19 [G]` + `1A [G]` | Volatile frame buffer | While telemetry is actively pumping the bitmask |

Groups 2 and 4 have only the static path documented — live frame writes
are not exercised by any current capture. **Group 3 (Rotary/knob)** live
path confirmed via `knob-rpm-effect.pcapng` (2026-05-03, CS Pro): PitHouse
sends `19 03` color chunks and `1A 03` bitmasks during telemetry to drive
knob indicator LEDs in sync with RPM.

### Worked example: light KS Pro single-LED group LED 5 red

```
7E 06 3F 17 1F 02 FF 05 FF 00 00 [chk]
            │  │  │  │  │  │  │
            │  │  │  │  └──┴──┴ B
            │  │  │  │  └────── G
            │  │  │  │  └────── R
            │  │  │  └───────── LED index N = 5
            │  │  └──────────── (0xFF separator)
            │  └─────────────── group ID G = 2 (Single)
            └────────────────── cmd = 0x1F (group-led-color)
```

### See also

- [`color-commands.md`](color-commands.md) — frame layouts and chunk
  format for the `0x19` and `0x1A` live commands
- [`../telemetry/control-signals.md`](../telemetry/control-signals.md) —
  RPM LED telemetry (`0x1A 00`), LED group colour (`0x27 [G] [role]`)
- [`../devices/wheel-0x17.md` § Extended LED Group Architecture](../devices/wheel-0x17.md)
  — full per-group command table
- [`../wire/wheel-write-echoes.md`](../wire/wheel-write-echoes.md) —
  echo prefixes for `1F`, `19`, `1A` writes
