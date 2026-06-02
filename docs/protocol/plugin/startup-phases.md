## Plugin implementation

Replicates Pithouse's observed preamble with direct session allocation.

### Startup phases

**Phase 0 — Session open + config** (before the tick timer starts; `StartInner`):
1. Reclaim stale sessions with type=0x00 end markers — a cold start closes the wide `0x01..0x0A` range, a mid-process reload closes only `0x01..0x03` (see [session-management](session-management.md) § Stale-session reclaim). Sleep ~100ms.
2. Send type=0x81 session opens for 0x01 (mgmt) and 0x02 (telem = `FlagByte`); wait up to 500ms each for fc:00 ack. Proceed with PitHouse defaults if neither acks — real wheels silently accept data even without explicit ack. (`Start()` runs on a background thread so the serial read thread stays free to deliver fc:00 acks.)
3. Hub only: `SendHubSlotEnumeration()` (5-slot Form B burst).
4. Prime + open-request session 0x09 (`PrimeAndOpenSession09`) for the configJson handshake.
5. Queue the dashboard upload to the ThreadPool (`QueueBackgroundUploadIfReady`) — a **60s background FT-burst budget**, deliberately decoupled so it never stalls tier-def/timer (NOT a synchronous 2s foreground wait). The upload runs on **session 0x04** (or whichever FT session the wheel device-inits) per [`../dashboard-upload/path-b-session-04.md`](../dashboard-upload/path-b-session-04.md).
6. Open session 0x03 (aux) and send an empty tile-server state blob via `SendTileServerState()`.
7. Wait for the channel-catalog burst to go quiet (`WaitForChannelCatalogQuiet`), parse it, then `MaybeSwapProfileForCatalog`.
8. Wait for a real catalog to arrive (so the session roles are known), then send the FF-init handshake on the **mirror** session (`ResolveFfSession()` — the opposite of the dynamically-resolved tier-def session, typically 0x02 when tier-def is on 0x01); probe the Display sub-device via 0x43.
9. Arm the tick timer.

The V2 preamble + tier definition are **not** sent in Phase 0 — tier-def first emits at the Preamble→Active transition (`ApplySubscription` in `TickPreamble`), once the tick timer is running. Flag bytes are 0x00-based, not session-port-based.

**Phase 1 — Preamble** (~1 second, timer running):
7. Ack incoming 7c:00 channel data on telemetry session with fc:00 (session=FlagByte).
8. Send heartbeats only — no telemetry, no enable, no channel config.
9. Detect Display sub-device from 0x87 model name response.

**Phase 2 — Active** (continuous, after preamble):
10. Send `0x40` channel config burst (1E enables for pages 0-1 channels 2-5, then 28:00, 28:01, 09:00, 28:02).
11. Begin `0x41/FD:DE` enable signal (~30+ Hz).
12. Begin `0x43/7D:23` bit-packed telemetry (flags 0x00/0x01/0x02, ~30 Hz per tier).
13. Begin `0x2D/F5:31` sequence counter (~30 Hz).
14. Begin periodic streams at ~1 Hz: heartbeats, dash keepalives (0x43 to dev 0x14, 0x15, 0x17), display config (7C:27) + dashboard activate (7C:23) interleaved per page, session ack (FC:00 with session=FlagByte and current ack seq).
15. Begin `0x40/28:02` telemetry mode polling (~3 Hz).

RPM LEDs (`0x3F/1A:00`) and button LEDs (`0x3F/1A:01`) handled separately by `MozaDashLedDeviceManager` and `MozaLedDeviceManager`. Zero preamble.

**Disable → re-enable:** `Stop()` resets `FramesSent`. `Start()` short-circuits when the sender is already Active with a live connection (persistent-sender reuse across a game-switch plugin reload); concurrent `Start()` calls are serialized via `_startSemaphore` and a cancelling `_startCts` (supersession — a second Start cancels the prior in-flight `StartInner`). A true re-enable still runs the full probe/tier-def/preamble, but only after the silence gate and only when not reusing a live persistent sender.
