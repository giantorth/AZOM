### Wheel write echoes

Certain wheel writes are **echoed verbatim** by firmware though they carry
no read-back semantics. The echo follows the standard response transform —
group with bit 7 toggled (`0x3F` → `0xBF`), device with nibbles swapped
(`0x17` → `0x71`) — but the payload mirrors the request bytes exactly,
including any per-call values (LED indices, brightness levels, channel CC
numbers) that vary frame-to-frame.

### Why echo recognition matters

Without echo recognition, every per-frame LED color write at 60 Hz would
log "unmatched response" because `MozaCommandDatabase` doesn't have
entries for live writes (color is per-call data, not stored state). The
plugin's response parser short-circuits on echo prefixes via
`MozaProtocol.IsWheelEcho()` and treats them as wheel-alive signals — they
contribute to the connection-keepalive timer but are never decoded as
command results.

### Echo response wire layout

```
7E [N] [group | 0x80] [swap_nibbles(device)] [payload echo] [checksum]
```

Example — wheel echoing a per-LED color write (`1F 00 FF 0A` red):

```
Host → wheel:
7E 06 3F 17 1F 00 FF 0A FF 00 00 [chk]
              └─group─┘ └────payload────────┘
              0x3F=write   set RPM LED 10 = (0xFF, 0x00, 0x00)

Wheel → host (echo):
7E 06 BF 71 1F 00 FF 0A FF 00 00 [chk]
       │   │  └────────── payload echoed verbatim ──────┘
       │   └ device 0x17 → 0x71 (nibble swap)
       └─── group 0x3F → 0xBF (bit 7 toggled)
```

### Match algorithm

`MozaProtocol.IsWheelEcho()` (in
[`Protocol/MozaProtocol.cs:192`](../../../Protocol/MozaProtocol.cs)):

```
1. Untoggle bit 7 of received group → original group byte
2. Swap nibbles of received device → original device ID
3. Walk WheelEchoPrefixes; for each (group, device, prefix-bytes):
   - if group/device match AND first N payload bytes match prefix → echo
4. No match → real response, dispatch to command handlers
```

Match is against the **prefix** of the payload, not the full payload —
because the trailing bytes (LED index, RGB, brightness value) vary per
call and aren't part of the prefix.

### Recognized echoes

Mirrors `sim/wheel_sim.py:_WHEEL_ECHO_PREFIXES`. All entries
target dev `0x17` (wheel) on group `0x3F` unless noted:

| Prefix | Cmd / variant | Purpose |
|--------|---------------|---------|
| `1F 00` | per-LED color | RPM LED page 0 (legacy) |
| `1F 01` | per-LED color | RPM LED page 1 (legacy) |
| `1E 00` | channel CC enable | Page 0 |
| `1E 01` | channel CC enable | Page 1 |
| `1B 00` | brightness | Page 0 |
| `1B 01` | brightness | Page 1 |
| `1C 00` | page config | |
| `1D 00` | page config | |
| `1D 01` | page config | |
| `27 00` | LED group colour | Group 0 = RPM strip (display config page 0) |
| `27 01..05` | LED group colour | Knobs 1..5 (CS Pro has 4, KS Pro has 5) |
| `2A 00..03` | unknown paged | Sub-IDs 0..3 |
| `0A 00` | misc config | |
| `24 FF` | display / idle config | |
| `20 01` | misc config | |
| `1A 00` | RPM LED telemetry | Live bitmask write (group 0 = RPM). Not echoed by the KS Pro † |
| `19 00` | RPM LED color | Live color chunk write (group 0 = RPM). Not echoed by the KS Pro † |
| `19 01` | button LED color | Live color chunk write (group 1 = button). Not echoed by the KS Pro † |

† Zero `BF 71 19`/`BF 71 1A` frames from a KS Pro in the plugin capture of
bundle M3D9WHJE (~69k live LED writes) and in PitHouse's
`usb-capture/ksp/gfdsgfd.pcapng`. Which wheels do echo these is uncaptured.
| `0B`* | newer-wheel LED cmd | *Group `0x3E` (not `0x3F`); 1-byte prefix |

The single `0x3E` entry exists because newer-wheel LED commands sometimes
ride a different write group; the bit-7 toggle still produces a valid
`0xBE` echo response.

### Plugin / sim parity

The C# array (`MozaProtocol.WheelEchoPrefixes`) and Python tuple
(`_WHEEL_ECHO_PREFIXES`) must stay in sync — when adding a new echo
recognizer in one, mirror it in the other. The sim emits these echoes
unconditionally on matching writes; the plugin consumes them silently.
