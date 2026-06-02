# Firmware era reference

Moza wheel/base firmware has shipped multiple incompatible protocol changes. This page maps each era to the captures it appears in, the wheels it's been verified on, and the topical pages that document its specifics.

> **Status (2026-04-27):** Eras inferred from capture metadata and observed wire behaviour. Exact firmware version strings are not extracted (firmware byte from session 0x01 desc not yet decoded into a version string).

## Known eras

| Era | Capture(s) | Wheels seen | Notable wire behaviour |
|-----|-----------|-------------|------------------------|
| **Pre-2025** (legacy) | `12-04-26-2/simhub-startup-*.pcapng` | VGS (via SimHub old build) | Session-port-based flag byte assignment; tier config CRC sometimes missing on final chunk |
| **2025-11** | `usb-capture/latestcaps/automobilista2-*.pcapng`, `12-04-26/moza-startup.pcapng`, `12-04-26-2/moza-startup-*.pcapng`, `connect-wheel-start-game.pcapng` | VGS, CS | Session 0x04 dashboard upload via `0x8A` LOCAL marker; sub-msg 1/2 path/content split; configJson schema = 11 top-level fields |
| **2026-04 legacy** | `09-04-26/dash-upload.pcapng` | VGS | Session 0x01 management RPC carries dashboard upload as FF-prefix envelope (3 fields). Path A in `dashboard-upload/` |
| **2026-04+** (current PitHouse) | `usb-capture/latestcaps/pithouse-switch-list-delete-upload-reupload.pcapng` (CSP on R9), `usb-capture/ksp/putOnWheelAndOpenPitHouse.pcapng` (KS Pro on R12) | CSP, KS Pro | Dashboard upload session is **dynamic** (0x05 or 0x06) opened via `7c:23` trigger; `0x8C` LOCAL marker; 6-byte sub-msg header; per-chunk metadata trailer; `ff*4` sentinel + 1-byte XOR status; pedal device `0x19` appears on KS Pro |
| **Type02 / 2026-04-30** (R5 + W17) | live bridge captures (no pcapng yet) | W17 on R5 base | Subset of 2026-04+ era. Tier-def uses **legacy N convention** (N=8+data, NOT 10+data) on the `7d:23` value frame; broadcast pattern is **3 broadcasts × 1 sub-tier** for single-pkg dashboards, **4 broadcasts × N sub-tiers** for multi-pkg (Grids, Rally V4); inferred tyre compression codes `0x10`/`0x11` are NOT decoded by this firmware (use `float` `0x07` instead); per-broadcast end-marker (NOT per sub-tier); enables interleaved between broadcasts; channel index sourced from the wheel's catalog response on session `0x02`, NOT alphabetic order |
| **FSR / RS21-D03** (group 0x42 push) | `usb-capture/fsr1/*.pcapng` | `FSR` **V1** display wheel (sw `RS21-D03-MC FW`) on `S03 HUB` — distinct from FSR V2 (`W13`) | **No tier-definition path at all.** No session opens, no v0/v2 channel-catalog advertisement, no `0x41` `FD DE` enable, no `0x43`/`7D 23` value stream. Host pushes pre-computed display values via undocumented **group `0x42`** fixed-schema records (`[type][b1][b2][data]`, types `01`–`12` enumerated at startup) at ~28 Hz. `0x43` is keepalive-only. See [`devices/wheel-0x17.md`](devices/wheel-0x17.md) § Group 0x42 |

## Wheels and bases tested

| Hardware | First seen in | Notes |
|----------|---------------|-------|
| VGS Formula | All `moza-startup-*` captures | Integrated display, version 2 compact tier defs. **Older firmware** — predates Type02; broadcast/end-marker semantics may differ from R5+W17 captures |
| CS / CS V2.1 | `cs-to-vgs.pcapng`, `vgs-to-cs.pcapng` | Same protocol family as VGS (older firmware) |
| CSP | `latestcaps/pithouse-switch-list-delete-upload-reupload.pcapng` | **Version 0 URL-subscription tier defs** (different from VGS/CS); 2026-04 firmware |
| KS Pro | `ksp/putOnWheelAndOpenPitHouse.pcapng` | 2026-04+ firmware era; introduces dev `0x19` pedal |
| W17 on R5 | live bridge captures (`sim/logs/bridge-*.jsonl`) | **Type02 firmware (2026-04-30)** — subset of 2026-04+. V2 compact tier-def with legacy N=14 framing on `7d:23` value frames, 3 or 4 broadcast pattern, inferred tyre codes broken — use `float` |
| `FSR` V1 / RS21-D03 on `S03 HUB` | `usb-capture/fsr1/*.pcapng` | **FSR V1** display wheel (distinct from FSR V2 = `W13`), model-name reply `FSR` (hw `RS21-D03-HW FW-C`, sw `RS21-D03-MC FW`, rev `U-V04`). **No catalog/session handshake**; live display values via group `0x42` push. Hub-attached (`0x12`/`0x21`) |
| ES | (see [`identity/known-wheel-models.md`](identity/known-wheel-models.md)) | Identity caveat — responses don't follow standard pattern |
| R9 base | CSP capture | Identity bytes byte-identical between dev `0x12` and dev `0x13` |
| R12 base | KS Pro capture | Same identity-cascade behaviour as R9 |
| R5 base | W17 live captures | Type02 host of W17 wheel; same identity-cascade pattern as R9/R12 |

## Topical pages by firmware sensitivity

### Era-critical (read with firmware in mind)

| Page | Era specifics |
|------|---------------|
| [`dashboard-upload/README.md`](dashboard-upload/README.md) | 3-row matrix of upload variants per era |
| [`dashboard-upload/path-a-session-01-ff.md`](dashboard-upload/path-a-session-01-ff.md) | 2026-04 legacy only |
| [`dashboard-upload/path-b-session-04.md`](dashboard-upload/path-b-session-04.md) | 2025-11 firmware |
| [`dashboard-upload/upload-handshake-2026-04.md`](dashboard-upload/upload-handshake-2026-04.md) | 2026-04+ firmware |
| [`dashboard-upload/6-byte-submsg-header.md`](dashboard-upload/6-byte-submsg-header.md) | 2026-04+ firmware (6B; legacy 8B fallback) |
| [`dashboard-upload/per-chunk-trailer.md`](dashboard-upload/per-chunk-trailer.md) | 2026-04+ firmware (continuation chunks) |
| [`dashboard-upload/config-rpc-session-09.md`](dashboard-upload/config-rpc-session-09.md) | configJson schema differs across eras (`rootDirPath` field added in 2025-11) |
| [`dashboard-upload/session-04-root-dir.md`](dashboard-upload/session-04-root-dir.md) | 2025-11 firmware (53-byte prefix, 176B trailing tail not zlib) |
| [`sessions/lifecycle.md`](sessions/lifecycle.md) | Concurrent session map differs: 2025-11 (VGS/CSP/older displays) vs 2026-04+ (KS Pro) |
| [`sessions/compressed-0x09-0x0a.md`](sessions/compressed-0x09-0x0a.md) | Session 0x09 compressed format predates 2026-04+ session 0x0a equivalent |
| [`identity/wheel-probe-sequence.md`](identity/wheel-probe-sequence.md) | 2026-04 firmware uses **short-form probes** only; older firmware uses sub-byte variants |
| [`identity/pedal-0x19.md`](identity/pedal-0x19.md) | KS Pro / 2026-04+ only |
| [`identity/hub-base-cascade.md`](identity/hub-base-cascade.md) | Verified on CSP R9 + KSP R12 |
| [`identity/dev-type-table.md`](identity/dev-type-table.md) | Per-wheel — table grows as more wheels are captured |
| [`devices/wheel-0x17.md`](devices/wheel-0x17.md) (Extended LED Group Architecture) | Newer firmware only; rotary/ambient groups present in DB but not always physical |
| [`tier-definition/version-0-url-csp.md`](tier-definition/version-0-url-csp.md) | CSP-only host response shape |
| [`tier-definition/version-2-compact-vgs.md`](tier-definition/version-2-compact-vgs.md) | VGS/CS response shape |

### Firmware-agnostic (stable across eras)

These pages document foundational behaviour that hasn't changed across observed eras:

- [`wire/`](wire/) — frame format, checksum, byte stuffing, response transforms
- [`transport/`](transport/) — USB topology, internal bus mapping
- [`telemetry/channels.md`](telemetry/channels.md) — channel encoding types and namespaces
- [`telemetry/live-stream.md`](telemetry/live-stream.md) — `7D 23` frame structure (multi-stream architecture stable across captures)
- [`leds/color-commands.md`](leds/color-commands.md) — LED RGB encoding
- [`heartbeat.md`](heartbeat.md) — group `0x00` keepalive

## Per-capture inventory

See [`../../usb-capture/CAPTURES.md`](../../usb-capture/CAPTURES.md) for the full per-file breakdown (wheel, software, scenario, observed traffic counts).
