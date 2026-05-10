# Wheel sleep-light protocol

The wheel's "sleep light" is a per-wheel idle effect that runs when the
wheel hasn't been driven for a configurable timeout. PitHouse exposes mode,
timeout, per-mode speed, and color settings — all four wire commands were
captured live on 2026-05-10
(`sim/logs/bridge-20260510-115644.jsonl`).

All four commands target the wheel device:

- group `0x3F` (write), echo on `0xBF`
- read group `0x40`, response on `0xC0`
- dev `0x17`

## cmd 0x20 — sleep-light mode

```
20 [mode]    h2b grp=0x3F     1-byte body after cmd
```

Selects which animation the sleep light runs. Mirrors the
`wheel-{telemetry,buttons}-idle-effect` enumeration but uses its own opcode
(0x20 vs 0x1D); only the wheel idle / sleep light uses cmd 0x20.

| `mode` | Label in PitHouse |
|--------|-------------------|
| `0x01` | Breathing         |

(Other modes not yet captured — full enumeration is open work.)

Plugin entry: `wheel-idle-mode`, `[32]`, 1-byte int.

## cmd 0x21 — sleep-light timeout

```
21 [BE u16 minutes]    h2b grp=0x3F     2-byte body after cmd
```

How many minutes of idle before the sleep light turns on. **Units are
minutes**, not seconds or milliseconds.

| Capture body  | PitHouse value |
|---------------|----------------|
| `21 00 01`    | 1 minute       |
| `21 00 0a`    | 10 minutes     |

Plugin entry: `wheel-idle-timeout`, `[33]`, 2-byte int. Existing
`BuildWriteInt` encoding works correctly here because the payload is a
plain 2-byte big-endian int.

Note: PitHouse sometimes retransmits the same value within ~100 ms (one
retransmit observed for the 1-minute setting, none for 10 minutes). Looks
value-dependent; not driven by ack timing.

## cmd 0x22 — sleep-light speed (per-mode)

```
22 [mode] [BE u16 ms]    h2b grp=0x3F     3-byte body after cmd
```

Each sleep-light mode has its own animation speed slider. The first
payload byte selects which mode the speed applies to (matches the
`wheel-idle-mode` selector); the remaining two bytes are big-endian
milliseconds.

| Capture body       | Decode                          |
|--------------------|---------------------------------|
| `22 01 0c d7`      | mode 0x01 (Breathing), 3287 ms  |

Plugin entry: `wheel-idle-speed`, `[34]`, 3-byte array. Previously
registered as `[34, 0]` with a 2-byte int payload — that hardcoded the
mode byte to `0x00`, so any `WriteSetting("wheel-idle-speed", N)` call
silently sent the slider for sleep-mode-0 rather than the active mode.
Fixed in `Protocol/MozaCommandDatabase.cs` on 2026-05-10; callers must now
build the `[mode, ms_msb, ms_lsb]` payload explicitly via `WriteArray`.

## cmd 0x24 — sleep-light color

```
24 FF 01 FF [R] [G] [B]    h2b grp=0x3F     6-byte body after cmd
```

The cmd-id portion is 4 bytes (`24 FF 01 FF`); the trailing 3 bytes are
RGB. Verified setting the color to red (`#FF0000`):

```
hex: 7e 07 3f 17  24 ff 01 ff  ff 00 00  0a
```

Plugin entry: `wheel-idle-color`, `[36, 255, 1, 255]`, 3-byte array. The
existing registration matches PitHouse byte-for-byte — no change required.

## Plugin alignment summary

All four commands are registered in `Protocol/MozaCommandDatabase.cs` as of
2026-05-10. The wheel UI (`Devices/MozaWheelSettingsControl.xaml(.cs)`)
does **not** expose any controls for these settings — sleep-light
configuration is currently driven only via PitHouse. Adding mode,
timeout, speed, and color UI would round out the wheel page.
