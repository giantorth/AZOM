# 2026-04-29 — session `0x01` host→wheel property push (`ff` records)

PitHouse pushes per-property setting updates (dashboard brightness, display
standby timeout, etc.) to the wheel-integrated dashboard sub-device via
**SerialStream session `0x01` data chunks** carrying an `ff`-tagged record.
This is **distinct from** the wheel-settings group `0x3F`/`0x40` (dev `0x17`)
and the standalone-MDD group `0x32`/`0x33` (dev `0x14`) writes — same
properties (e.g. RPM brightness) may exist in multiple paths but the
PitHouse Settings UI sliders for the integrated dashboard send only the
session-0x01 push. Reverse-engineered from CSP wheel sim captures while
moving brightness and display-standby sliders.

## Outer wire frame

Standard MOZA frame, group `0x43` to dev `0x17`, carrying a SerialStream
chunk per [`../sessions/chunk-format.md`](../sessions/chunk-format.md):

```
7e <LEN> 43 17  7c 00  01 01 <seq:u16 LE>  <net_data...>  <chunk_crc32_LE:4>  <frame_chk>
```

- `7c 00` — group/device tag (chunk container)
- `01` — session ID (mgmt/tier session)
- `01` — type = data
- `seq:u16 LE` — chunk sequence (PitHouse increments per frame)
- `net_data` — variable; structure documented below
- `chunk_crc32_LE` — standard 4-byte CRC32 over `net_data`
  (per [`../sessions/chunk-format.md`](../sessions/chunk-format.md))

## Net-data record (`ff` push)

```
ff  <size:u32 LE>  <inner_crc32_LE:4>  <kind:u32 LE>  <value:size-4 bytes LE>
```

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0      | 1    | tag = `0xff`        | Same `0xff` sentinel used in tier-def TLV streams; here it marks a property-push record |
| 1      | 4    | `size:u32 LE`       | Byte count of `kind ‖ value`. Equals 4 + sizeof(value). Observed: `0x08` (u32 value) and `0x0c` (u64 value) |
| 5      | 4    | `inner_crc32_LE`    | **`zlib.crc32(<kind LE><value LE>)`** stored little-endian. Verified against 7 captured samples |
| 9      | 4    | `kind:u32 LE`       | Property family / encoding selector. See table below |
| 13     | size-4 | `value:LE`        | Little-endian unsigned int. Width = `size - 4` |

After the record, the chunk's standard 4-byte CRC32 trailer covers the
entire `net_data` (including `ff`, size, inner CRC, kind, value).

## Verified samples

| Property              | size | kind | value             | inner CRC32 (wire) | Captured chunk seq |
|-----------------------|------|------|-------------------|--------------------|--------------------|
| brightness = 0        | 8    | 1    | `0x00000000`      | `f7 df 88 a9`      | (0% slider)        |
| brightness = 25       | 8    | 1    | `0x00000019`      | `e2 c7 99 84`      | 0x01cb             |
| brightness = 41       | 8    | 1    | `0x00000029`      | `43 3f b2 74`      | (41% slider)       |
| brightness = 50       | 8    | 1    | `0x00000032`      | `dd ef aa f3`      | 0x0146             |
| brightness = 100      | 8    | 14   | `0x00000064`      | `0f ad ec c4`      | repeats — baseline |
| display standby 3 min | 12   | 10   | `0x000000000002bf20` (180000 ms)  | `1a d2 61 f3` | 0x01a7 |
| display standby 25 min| 12   | 10   | `0x0000000000016e360` (1500000 ms) | `b9 d3 ce a6` | 0x01cc |

Inner-CRC verification (Python, `zlib.crc32`):

```python
>>> import zlib, struct
>>> struct.pack("<I", zlib.crc32(struct.pack("<II", 1, 25)))
b'\xe2\xc7\x99\x84'   # matches brightness=25 wire bytes
>>> struct.pack("<I", zlib.crc32(struct.pack("<IQ", 10, 1500000)))
b'\xb9\xd3\xce\xa6'   # matches standby=25min wire bytes
```

All seven samples match. The "hash" is therefore not a property identifier
nor a nonce — it is a deterministic CRC32 of `(kind ‖ value)` and serves as
a redundant integrity check over the property+value pair, on top of the
chunk-level CRC32.

## `kind` field interpretation (updated 2026-05-02)

Session 0x02 FF records serve multiple purposes: property pushes, startup
handshake, compressed catalog uploads, LED color state, and dashboard
switching. `kind` is a property/command identifier. Directions differ:
h2b (host→wheel) and b2h (wheel→host) can use the same `kind` value for
entirely different record formats.

### Host → Wheel (h2b)

| `kind` | Name | Value width | Notes |
|--------|------|-------------|-------|
| 1      | brightness (slider) | u32 LE (0–100) | User-driven slider updates. Transient — PitHouse only sends while slider moves |
| 2      | session init | 12B: `[unix_ts:u32] [0:u32] [tz_offset_sec:i32]` | Sent once at startup (seq=3). tz_offset observed: -25200 = UTC-7 |
| 4      | dashboard switch | 12B: `[field1=4:u32] [slot:u32] [0:u32]` | Slot = 0-based configJsonList index. See `2026-04-30-dashboard-switch-3f27.md` |
| 5      | display rotation (VGS) | 1B: `[mode:u8]` (size=5) | 0=off, 1=smooth, 2=immediate. VGS-only IMU display counter-rotation. One-shot on change. See [`../sessions/session-0x02-ff-init.md`](../sessions/session-0x02-ff-init.md) § Runtime property pushes |
| 7      | init command | 8B: `[3:u32] [0:u32]` | Always val=3. Sent once at startup (seq=4). Precedes channel catalog |
| 8      | channel catalog | ~2 KB zlib | Compressed master channel catalog: UTF-16LE channel names (RpmAbsolute1, etc). 12,708 bytes decompressed. Multi-chunk FF record spanning ~40 session-data chunks. Sent once at startup |
| 9      | LED color push (18B) | `[00] [addr:1B] [00 00 00 43 00 01 FF FF] [c0 c0 c1 c1 c2 c2 00 00]` | Push LED/knob colors. addr=target controller (0x6C, 0x97, 0xA4). Color bytes doubled. Periodic + around switches |
| 9      | LED mode (11B) | `[00] [addr:1B] [00 00 00 02 00 00 00] [00] [val]` | LED mode command to different controllers (addr=0x1B, 0x1F, 0x27). Rare |
| 9      | LED color (36B) | Two 18B entries concatenated | Dual-controller color push in single FF record |
| 10     | standby timeout | u64 LE (ms) | Display standby timeout in milliseconds. Sent on slider change |
| 11     | action catalog | ~2.5 KB zlib | Compressed button/action catalog: UTF-16LE action names (decrementEqualizerGain1, etc). 9,874 bytes decompressed. Multi-chunk. Sent once at startup, heavily retransmitted |
| 14     | **device log request** | u32 LE | "Send me up to N log lines" — always 100. **SOLVED 2026-08-07**, was misread as "brightness baseline". See [`../sessions/session-0x02-ff-init.md`](../sessions/session-0x02-ff-init.md) § Device log pull |
| 15     | **device log receipt** | u32 LE | "I consumed N lines" — the device drops that many. The "quasi-random 1–37" below is simply how many lines came back. **SOLVED 2026-08-07** |

### Wheel → Host (b2h)

| `kind` | Name | Value width | Notes |
|--------|------|-------------|-------|
| 4      | switch echo | 12B (same as h2b) | Wheel echoes back the switch command |
| 9      | LED color report | 25B: `[00] [addr] [...0A...] [hex_color_UTF16]` | Wheel reports current LED color as hex string (e.g. "#64d90a"). Different format from h2b |
| 10     | standby echo | varies | Single occurrence per session |
| 14     | **device log payload** | 4B reserved + zlib, multi-chunk | `[count u32 BE]` + `count × ([byteLen u32 BE][UTF-16BE])` — the display's MOZADash log, answering the h2b kind=14 request. **SOLVED 2026-08-07**, was guessed as "dashboard state" |
| 16     | address report | 16B: two `[addr:u32] [0:u32]` pairs | Wheel-only. 4 records per session |

### ~~Periodic heartbeat pattern~~ — SUPERSEDED 2026-08-07

The kind=14 + kind=15 pair is **not** a heartbeat. It is the device log
pull: kind=14 requests up to 100 lines, kind=15 acknowledges how many the
host consumed (which is why its value tracks small counts in steady state).
Nothing in the session layer requires either record. Canonical decode:
[`../sessions/session-0x02-ff-init.md`](../sessions/session-0x02-ff-init.md)
§ Device log pull. The plugin now emits both from
`TelemetrySender.TickEmitDeviceLogPoll`.

### Startup sequence on session 0x02

PitHouse startup sends kinds in this order (each sent once, some
heavily retransmitted):
1. kind=2 (timestamp init) at seq=3
2. kind=7 (init command, val=3) at seq=4
3. kind=8 (channel catalog, multi-chunk) starting seq=5
4. kind=11 (action catalog, multi-chunk) at seq=44+
5. kind=9 (LED color pushes) starting seq=92+
6. kind=14/15 periodic heartbeat begins

Plugin currently sends **none** of these startup records.

## Capture method

CSP wheel sim (`/home/rorth/src/moza-simhub-plugin/sim/wheel_sim.py`)
running on Linux USB gadget; PitHouse on Windows connects over USB-IP. Sim
records every received frame in `sim_recent` with handler-tag. Slider
moves on PitHouse's "MOZA Wheel" → integrated-dashboard tab produced
unique `7e 1b 43 17 7c 00 01 01 ...` (size=8) and `7e 1f 43 17 7c 00 01 01
...` (size=12) frames matching the moved property's value. Sim does not
emit these — they originate from PitHouse, confirmed by `_record(tag,
frame)` only being called on inbound frames in
[`../../sim/wheel_sim.py:3383`](https://github.com/giantorth/moza-simulator).

## Cross-references

- [`../sessions/chunk-format.md`](../sessions/chunk-format.md) — outer
  chunk framing and chunk-level CRC32
- [`../tier-definition/version-0-url-csp.md`](../tier-definition/version-0-url-csp.md)
  — same `0xff` sentinel byte appears in the tier-def TLV stream
  (different content / different session phase)
- [`../settings/wheel-0x17.md`](../settings/wheel-0x17.md) — wheel
  config via group `0x3F` (separate path; not used by the dashboard
  brightness slider)
- [`../settings/dashboard-0x14.md`](../settings/dashboard-0x14.md) —
  standalone MDD dashboard via group `0x32` (separate physical device)
