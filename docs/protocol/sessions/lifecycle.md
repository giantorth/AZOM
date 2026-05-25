### Session open frames

> **Concurrent session map differs by firmware era** — 2025-11 (VGS, CSP, older displays) vs 2026-04+ (KS Pro). Both documented inline below. See [`../FIRMWARE.md`](../FIRMWARE.md) for the firmware-era matrix.

**Host-initiated (type=0x81, 4-byte payload):**
```
7E 0A 43 17 7C 00 [session] 81 [port_lo] [port_hi] [port_lo] [port_hi] FD 02 [checksum]
                   └─chunk ID   └─seq(LE)=port       └─session_id(LE)   └─window=765
```

Pithouse opens **two sessions simultaneously** (0x01 and 0x02) in same USB packet. Wheel responds with `fc:00` acks for both. The `fc:00` session bytes in steady state track **session ack protocol** (incrementing ack_seq for each 7c:00 data chunk received), NOT telemetry flag byte.

**Device-initiated (type=0x81, 6-byte payload):**

Device opens sessions 0x04, 0x06, 0x08, 0x09, 0x0A with 6-byte form (not 4-byte host form):

```
7E 0A C3 71 7C 00 [session] 81 [port_lo] [port_hi] [port_lo] [port_hi] FD 02 [cksum]
```

Port field duplicated (observed every device-initiated open across 4 captures). `port` equals session byte for every device-opened session (0x04→4, 0x06→6, 0x08→8, 0x09→9, 0x0A→10). `FD 02` trailer constant.

### Session close frame

Type=0x00 end marker: **6-byte payload**: `7C 00 [session] 00 [ack_lo] [ack_hi]` (ack_seq may be zero when reclaiming stale session). Length byte must equal 6. A 4-byte payload advertised as length 6 causes wheel (and `sim/wheel_sim.py`) to over-read into next frame and de-sync.

### Host-side stale-session reclamation (cold start vs reload)

The host emits a burst of type=0x00 close frames at startup to reclaim
any sessions left open by a prior host process. Range depends on whether
this is a **cold start in a fresh OS process** or a **plugin reload
within the same SimHub process** (game-switch via the plugin's
persistent-wire pattern):

- **Cold start** → close `0x01..0x0A` (wide). Covers host-managed slots
  PLUS the wheel-managed ones (`0x04..0x0A`). On CS-Pro / KS-Pro the
  wheel was observed to silently swallow fresh session-open frames when
  stale wheel-side session state from a prior SimHub process was still
  live; manually toggling the plugin "Connection enabled" off and on
  recovered the wedge (because that path closes the OS serial port and
  forces the wheel to drop its sessions) but a SimHub restart alone did
  not. The wide close emulates the off/on recovery proactively. The
  configJson handshake re-runs on next connect anyway, so closing the
  wheel-managed sessions on cold start doesn't break anything that
  wasn't going to be re-established a few frames later.

- **Plugin reload mid-SimHub** (persistent wire reuse) → close `0x01..0x03`
  only. Wheel-managed `0x04..0x0A` are intentionally left intact so the
  configJson handshake (sess=0x09 / 0x0a — see [`compressed-0x09-0x0a.md`](compressed-0x09-0x0a.md))
  stays bound; if the host closed those on every game switch the wheel
  would need to re-emit its full configJson burst and the dashboard
  would re-render. PitHouse matches this pattern — captures show no
  host close-burst on game switches.

`TryCloseSession(session, 500ms)` waits up to 500 ms for the wheel's
fc:00 close-ack before moving on; an ack timeout is non-fatal and the
host proceeds to the open phase regardless. The wheel reliably acks
close frames for sessions it considers open and silently ignores closes
for sessions it considers closed.

### Plugin-side gating: don't emit close frames at all if the dashboard
pipeline never ran

`CloseHostSessions()` (called from the plugin's `TelemetrySender.Stop()`
to clean-close `0x01..0x03` on shutdown / reload) is additionally gated
on `MozaPlugin.ShouldDriveDashboard()`. When the wheel is known to have
no display (`WheelModelInfo.HasDisplay == false` — `CS V2.1`, `KS`,
`GS V2P`, `TSW`, `RS V2`, original `CS`), the host never opened sessions
`0x01..0x03` in the first place, and emitting closes for them just adds
noise on group `0x43 dev=0x17`. The screenless wheel's command parser
was observed to mis-handle the unexpected session-control frames and
park into a half-engaged state where settings reads timed out — see the
"Screenless wheel session traffic" note in
[`../devices/wheel-0x17.md`](../devices/wheel-0x17.md) if added (or
file under `findings/` per project convention).

### Port / session-byte allocation

**2026-04 firmware (old):** global monotonic counter shared between host and wheel. Host picks low numbers (1, 2, 3...), wheel picks its own (6, 8, 9...). Next host allocation accounts for wheel-allocated ports. Counter resets on wheel power cycle.

Observed session opens in `moza-startup.json` (2026-04-12):

| Time | Source | Session byte | Port (payload) | Notes |
|------|--------|-------------|----------------|-------|
| 8.756s | Host | 0x01 | 0x0001 | First host session (mgmt/upload) |
| 8.756s | Host | 0x02 | 0x0002 | Second host session (telemetry config) |
| 11.102s | Wheel | 0x08 | 0x0008 | Wheel-initiated keepalive |
| 11.102s | Wheel | 0x09 | 0x0009 | Wheel-initiated configJson RPC |
| 11.187s | Host | 0x03 | 0x000a | Third host session — port 10, not 3! |
| 11.894s | Wheel | 0x06 | 0x0006 | Wheel-initiated keepalive |

**Session byte** (chunk header) and **port number** (payload) different for session 0x03 — session byte is host-local identifier, port is globally allocated.

**2025-11 firmware:** global counter observation **no longer holds**. From `automobilista2-wheel-connect-dash-change.pcapng`: host opens session 0x03 with port 0x0003 (not 0x000a as in 2026-04). Session byte and port now match for every session, both sides. Device-opened sessions 0x04/0x06/0x08/0x09/0x0A all use `port == session`. Implementations should not assume wheel-side port allocation; use `port == session` for everything.

### Concurrent session map

Up to 11 concurrent sessions. Session role depends on firmware generation —
KS Pro (2026-04+) reshuffled the map. Confirmed across captures
(moza-startup, connect-wheel-start-game, moza-unplug-plug-wheel-to-base,
automobilista2-wheel-connect-dash-change, ksp/mozahubstartup,
ksp/putOnWheelAndOpenPitHouse):

#### 2025-11 firmware (VGS, CSP, older displays)

| Session | Opened by | Role | Description |
|---------|-----------|------|-------------|
| 0x01 | **host** | Management | Wheel identity / log push; `0xFF`-prefixed messages |
| 0x02 | **host** | Telemetry | Tier definition, FF-prefixed settings push |
| 0x03 | **host** | Aux config | Tile-server state push host → dev (12-byte envelope, zlib) |
| 0x04 | **device** | **File transfer** | Bidirectional: host uploads `.mzdash`; device sends root directory listing |
| 0x06 | device | Keepalive | Alternating directions, ~3.4s |
| 0x08 | device | Keepalive | Alternating directions, ~3.4s |
| 0x09 | device | **configJson Schema A** | Device pushes Schema A snapshot; host responds with canonical list |
| 0x0A | device | Keepalive + RPC + Schema B deltas + tile-server mirror | Multi-purpose; same envelope (9-byte) for state push delta + RPC; also dev→host tile-server mirror with 12-byte envelope |

#### 2026-04+ firmware (KS Pro)

| Session | Opened by | Role | Description |
|---------|-----------|------|-------------|
| 0x01 | host | **Channel catalog + tier-def + string values** | Typed sub-msg framing: wheel announces per-dashboard channel catalog (`type=0x04`, URL→idx), host sends tier-defs (`type=0x01`, idx → compression/width quads), host pushes string-channel values out-of-band (`type=0x05`, ASCII), mutual seq acks (`type=0x06`). Distinct protocol from sess=0x02 — see [`session-0x01-channel-protocol.md`](session-0x01-channel-protocol.md). Also carries the zlib-wrapped UTF-16 wheel debug log dev→host (kind=14 wheel_payload). |
| 0x02 | host | Telemetry handshake + master catalog | FF-record protocol: init nonce / FFB property catalog (kind=11), kind=8 master channel-name catalog (PitHouse-side full list), kind=15 host settings, kind=14 wheel events. **Does not duplicate** the sess=0x01 tier-def — that is exclusively on sess=0x01 in current firmware. |
| 0x03 | host | **UNUSED** | Open frames + 4-byte zero keepalives only — reserved but no payload (older firmware's tile-server channel) |
| 0x04 | host (also device on dir-listing) | **Tile-server push host→dev** + dir-listing reply dev→host | Multi-purpose by direction. Host→dev sends 12-byte envelope tile-server JSON (relocated from 0x03) — but KS Pro display does NOT render a map UI in PitHouse, so this push is a mozahub-side no-op. Dev→host serves 8-byte-header dir-listing replies for `/home/root` queries. |
| 0x05 | device (host on uploads) | **File transfer** | Host uploads `.mzdash` + content-addressed PNG dependencies via type=0x02/0x03 sub-msgs (multi-file bundle, not a single-file transfer — see [`../dashboard-upload/sess05-bundle-contents.md`](../dashboard-upload/sess05-bundle-contents.md)). Replaces 0x04 in the upload role on KS Pro. b2h on sess=0x05 is empty on current PitHouse — wheel acks land on linked sessions (not yet decoded). |
| 0x06, 0x07, 0x08 | both | Reserved keepalive | Open + 4-byte zero only — channels held but unused |
| 0x09 | device | **UNUSED** | Open frames + 4-byte zero keepalives only — used for state push in older firmware, now empty |
| 0x0A | device | **configJson Schema A snapshot + Schema B deltas + RPC** | All wheel-state traffic consolidated here. Same 9-byte envelope. Snapshot once at connect; deltas after FS mutations. Host RPC calls (`configJson()`, `completelyRemove()`, reset) on this session too. |
| 0x0B | device | **Tile-server mirror dev→host** | Wheel echoes its own tile-server state via 12-byte envelope (relocated from 0x0a in older firmware). `root: "/home/moza/resource/tile_map/"`. KS Pro emits the mirror even though the display lacks a map UI — sim/plugin can ignore. |

Verified 2026-04-26 in
[usb-capture/ksp-deep-investigation-plan.md § Findings](../../../usb-capture/ksp-deep-investigation-plan.md).
Plugin / sim must select session per detected firmware — sending state push
on 0x09 to a KS Pro wheel reaches a session that is reserved-but-unused, and
PitHouse never sees the push.

**Opening order** (cold-start captures):
1. Host opens 0x01, 0x02 (mgmt + telemetry) within ~1 ms of each other (t=0).
2. Host opens 0x03 ~150–450 ms later (port 0x03 new firmware; port 0x0a older).
3. Device opens 0x04, 0x06 ~40–400 ms after host 0x02.
4. Device opens 0x08, 0x09 ~1.5–2.5 s later (retransmitted every 1 s up to 3 tries until host ACKs).
5. Device opens 0x0A last, variably (t=38s or later).

**Sessions 0x08 and 0x09 are retransmitted** until host sends `fc:00` ack. Real wheel sends each up to 3 times at 1 s intervals. Sim implementations should do same if host doesn't ACK immediately.

### Hot-attach: wait for display before opening sessions

The opening order above assumes the wheel is already booted when the host opens its sessions. **Hot-attach (wheel clipped onto a running host)** has very different timing: the wheel MCU answers settings reads ~immediately but its **display sub-device takes ~20 s to boot**, and the wheel silently drops every `7c:00 type=0x81` open frame addressed to its display channel during that window. A host that opens 0x01/0x02 on first wheel-MCU response (e.g. a `wheel-telemetry-mode` read returning a valid frame) gets no fc:00 acks and no subsequent device-initiated open of 0x04/0x06/0x08/0x09/0x0a — the wheel never engaged from its own side, so the dashboard renders locally with zero channel data.

Hosts should defer the sess=0x01/0x02 open burst until the display sub-device identity probe completes (`87 "<display name>"` response on `0x43 dev=0x17`, or any of the parallel HW/FW/MCU-UID responses). See [`../identity/display-sub-device.md`](../identity/display-sub-device.md) and [`../startup-timeline.md`](../startup-timeline.md) for the full timing breakdown and the plugin's gating hook.
