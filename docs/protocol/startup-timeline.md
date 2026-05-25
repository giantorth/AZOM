## Complete telemetry startup timeline

Two captures provide complementary views.

### Concurrent outbound streams during active telemetry

| Stream | Rate | Device | Group/Cmd | Purpose | Required? |
|--------|------|--------|-----------|---------|-----------|
| Sequence counter | ~45/s | base (0x13) | `0x2D/F5:31` | Frame sync to base | TBD |
| Telemetry enable | ~48/s | wheel (0x17) | `0x41/FD:DE` data=`00:00:00:00` | Mode/enable flag | Likely — entire session |
| **Live telemetry** | ~31/s | wheel (0x17) | `0x43/7D:23` | Bit-packed game data | Yes |
| Heartbeat | ~1/s each | all devices (18–30) | `0x00` n=0 | Keep-alive / presence | Likely |
| RPM LED position | ~4/s | wheel (0x17) | `0x3F/1A:00` | LED bar position | Separate feature |
| Telemetry mode | ~3/s | wheel (0x17) | `0x40/28:02` data=`01:00` | Set/poll multi-channel mode | Likely |
| Dash keepalive | ~1.5/s | dash (0x14), 0x15, wheel (0x17) | `0x43` n=1, data=`00` | Keep-alive for dash and wheel sub-devices | Yes — Pithouse sends to all three |
| Display config | ~1/s | wheel (0x17) | `0x43/7C:27` | Page-cycled display params | Yes |
| Dashboard activate | ~1/s | wheel (0x17) | `0x43/7C:23` | Declares active dashboard pages | Yes |
| Status push | ~1/s | wheel (0x17) | `0x43/FC:00` | Session ack with session=FlagByte and current ack seq (NOT zeros) | Yes — Pithouse uses real session/seq |
| Settings block | ~1/s | wheel (0x17) | `0x43/7C:00` | Config sync | No (file transfer) |
| Button LED | ~1/s | wheel (0x17) | `0x3F/1A:01` | Button LED state | Separate feature |

### Preamble detail — from `moza-startup.json` (2026-04-12, raw Wireshark JSON)

Most precise source, decoded directly from raw USB packets:

| Offset | Frame | Notes |
|--------|-------|-------|
| +0.000 | `7c:00` type=0x81 session 0x01 + 0x02 | Opens two SerialStream sessions simultaneously |
| +0.009 | (IN) `fc:00` acks for both sessions | Wheel accepts immediately |
| +0.013 | (IN) `7c:00` data on session 0x02 | Wheel dumps channel registrations (v1/gameData/Rpm etc.) |
| +0.053-0.087 | `fc:00` acks (seq 04→17) | Host acks each incoming data chunk |
| +0.064-0.070 | `7c:00` tier definition TO wheel | Host sends tier config (channel indices, compression codes, bit widths) |
| +0.072 | First `7d:23` telemetry (flag=0x00) | Interleaved with acks — smaller "probe" tier, n=14 |
| +0.100-1.000 | `7d:23` flag=0x00 (~25 frames) | ~30Hz, heartbeats only — no 0x41 enable yet |
| +0.700-0.970 | Identity probes to wheel/base/pedals | Groups 0x00, 0x02-0x11 |
| +0.970 | **`0x0E` debug poll starts** | Parameter table reads at ~9Hz to 0x12/0x13/0x17 |
| +1.054 | **First `0x41/FD:DE` enable** | 1.05s after session opens |
| +1.089 | `0x40` channel config (1E, 09:00) | Deferred until after session exchange |
| +1.124-1.127 | `7c:00` additional config on session 0x02 | Second batch of tier data |
| +1.130 | **First `7d:23` with flag=0x02** (n=24) | Full telemetry — session exchange complete |
| +1.200 | Display sub-device probe | Identity commands via 0x43 (model="Display") |

### Full connect-to-telemetry — from `connect-wheel-start-game.json`

Wheel plugged in cold, then Assetto Corsa started:

| Phase | Time | Events |
|-------|------|--------|
| **Idle** | t=0–7.8s | Heartbeats, keepalives, `0x0E` debug poll. Only dev18/19/23 respond |
| **Wheel detected** | t=7.82s | Identity probe: 0x09 → 0x04 → 0x06 → 0x02 → 0x05 → 0x07 → 0x0F → 0x11 → 0x08 → 0x10 |
| **Config burst** | t=8.2–9.1s | ~50 `0x40` commands (channel enables, page config, LED config). `0x40/28:02` polling at ~3 Hz |
| **Dashboard upload** | t=21.4–23.5s | `0x43/7c:00` chunked file transfer. Display sub-device probed |
| **Pre-game** | t=24–30.5s | `0x40/28:02` polling (response always `00:00`), heartbeats, keepalives |
| **Game starts** | t=30.568s | `0x41/FD:DE` enable + `0x2D/F5:31` seq counter start simultaneously |
| **Telemetry** | t=30.600s | `0x43/7D:23` live data (flag=0x02). ~31 frames/s steady state |

### Hot-attach timing (wheel clipped onto a running plugin)

The cold-start tables above assume the wheel is already powered when the host opens its sessions. **Attaching the wheel to the base while the host is already running has very different timing** because the wheel boots in two phases:

| t (s) | Event | Notes |
|-------|-------|-------|
| 0.00 | Wheel physically clipped to base | Wheel MCU power-on |
| ~0.05 | `wheel-telemetry-mode` reads start to answer | Wheel MCU is alive; settings reads on group 0x40 dev=0x17 round-trip normally |
| 0.05–20 | **Display sub-device still booting** | Wheel ignores all `0x43 dev=0x17` traffic addressed to its display channel — including session-open frames `7c:00 type=0x81` on sess=0x01/0x02 |
| ~20.0 | Display sub-device identity probe burst answers | All ten `0x43 7C` responses (`87 "Display"`, HW/FW, MCU UID, etc.) arrive within ~30 ms of each other. **From this moment the wheel acks session opens normally.** |
| ~20.1 | Host opens sess=0x01/0x02 — both fc:00-ack within ~10 ms | First time the wheel engages the dashboard pipeline |
| ~20.1–20.3 | Wheel pushes channel catalog on sess=0x01/0x02 + sess=0x09 configJson burst | Same shape as cold-start; just shifted in time by the display boot |
| ~20.5 | First `7d:23` telemetry on flag=0x02 | Live values start updating |

**Verified W17 capture 2026-05-25** (`moza-wire-20260525-084125.jsonl`, R5 base, COM33, plugin v0.18-dev with display-detected gate).

A plugin that starts the session pipeline on wheel-MCU detection alone — without waiting for the display — sees no fc:00 acks and no catalog push for the full ~20 s display boot, and the wheel never engages sess=0x01/0x02 (its view: those sessions were never opened, since the open frames arrived while the display was still booting). The dashboard layout renders locally on the wheel from its own stored mzdash but every channel sits at zero until SimHub is restarted. The plugin gate documented in [`identity/display-sub-device.md`](identity/display-sub-device.md) bridges this window — see that page for the implementation hook.

### Game-start handshake — from live capture (R5 base, W17, 2026-04-29)

Within ~1.5 s of the first game-tick frame, in addition to the streams above:

| Offset | Frame | Notes |
|--------|-------|-------|
| +0.00 | `0x28/0x13 01 00 00` → `0xA8/0x31 01 01 c2` | Re-read base param `limit` (450). See [`periodic/group-0x28.md`](periodic/group-0x28.md) |
| +0.00 | `0x28/0x13 17 00 00` → `0xA8/0x31 17 01 c2` | Re-read `max-angle` (450) |
| +0.05 | `0x28/0x13 02 00 00` → `0xA8/0x31 02 03 e8` | Re-read `ffb-strength` (1000) |
| +0.30 | `0x2B/0x13 02 00 00` → `0xAB/0x31 02 00 00` | Hub set/ack (semantic TBD; see [`periodic/group-0x2B.md`](periodic/group-0x2B.md)) |
| +0.70 | `0x43/0x17 7C 00 03 01 73 00 00 00 00 00` | Slot-03 commit/idx marker |
| +0.70 | `0xC3/0x71 7C 00 02 01 9D 04 00 00 00 00` | Slot-02 ack |
| +1.20 | `0x43/0x17 7C 00 02 01 NN 04 ff …` | Slot-02 widget records start streaming |
| +1.30 | `0x43/0x17 7D 23 32 …` | Tier-`0x23` telemetry begins |
| +1.40 | `0x41/0x17 FD DE 00 00 00 00 …` | FFB tick stream (~20 Hz) |
| +1.40 | `0x2D/0x13 F5 31 00 00 00 00 …` | Game tick stream (~20 Hz) |
| +1.50 | `0x3F/0x17 1A NN MM …` | RPM-bar LED stream begins. See [`leds/color-commands.md`](leds/color-commands.md) |
