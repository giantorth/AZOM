### Session management

Plugin manages two host-opened sessions and accepts every device-initiated
session that PitHouse-style firmware emits during connect. See
[`../sessions/lifecycle.md`](../sessions/lifecycle.md) for the firmware-era
session map and [`../sessions/type-0x81-channel-open.md`](../sessions/type-0x81-channel-open.md)
for the open frame layout.

### Host-opened sessions

| Session | Symbol / use | Open behavior |
|---------|--------------|---------------|
| `0x01` | Management — wheel identity, log push, channel catalog binary | Plugin opens with `type=0x81`; waits up to **500 ms** for `fc:00` ACK before proceeding |
| `0x02` | Telemetry — `FlagByte`, tier definition + FF-prefixed settings push | Same as 0x01; opens in same USB write |
| `0x03` | Aux — historical tile-server channel | **Fire-and-forget**: opened for doc compliance; plugin never writes here, but ACKs any unsolicited device data so wheel doesn't retransmit-stall |

Plugin builds `7E 0A 43 17 7C 00 [session] 81 [port] [port] FD 02 [chk]`
for each via [`SendSessionOpen`](../../../Telemetry/TelemetrySender.cs)
(line 1762). The two telemetry sessions are sent concurrently so the
wheel sees one USB packet with both opens.

### Device-initiated sessions

Wheel emits `type=0x81` opens during connect for `0x04`, `0x06`, `0x08`,
`0x09`, `0x0A` (older firmware) or `0x05`/`0x07`/`0x09`/`0x0A` (KS Pro on
Universal Hub). Plugin handler
[`OnMessageDuringPreamble`](../../../Telemetry/TelemetrySender.cs)
(line 1296) handles each:

```csharp
if (type == 0x81) {
    int openSeq = data[6] | (data[7] << 8);
    info.Port = (byte)(openSeq & 0xFF);
    SendSessionAck(session, (ushort)openSeq);
    if (session >= 0x04 && session <= 0x0b
        && _dispatcher.GetOwner(session) == null) {
        _uploader.NoteDeviceInit(session);  // tracks FT candidate + signals upload pump
    }
}
```

Key behaviors:

- **`fc:00` ACK echoes the open's `seq`** (`openSeq` from payload bytes
  6–7). Without this, PitHouse's monotonic port counter rejects the ACK
  as stale and retries forever.
- **Handler stays subscribed for the whole connection**, not just the
  ~1 s preamble window. Otherwise post-upload directory refreshes on
  `0x04`, configJson state pushes on `0x09`, and RPC replies on `0x0A`
  would be silently dropped.
- **Any device open in `0x04..0x0b` becomes a file-transfer candidate**;
  `WheelUploadCoordinator.ChooseUploadSession()` re-runs after each open
  to pick the right upload session for the current firmware (KS Pro can
  land on `0x05`, `0x06`, or `0x07` depending on the `7c:23` trigger).
  Sessions claimed by the `SessionDispatcher` (e.g. `DashboardDownloader`
  on `0x0B`) are skipped — `NoteDeviceInit` is gated by the dispatcher
  ownership check above.

### Stale-session reclaim

Before opening, plugin sends `type=0x00` end markers on ports `0x01..0x10`
to reclaim stale sessions left by a previous SimHub crash. Without this,
fresh opens are silently ignored — the wheel still considers the previous
host-port active and won't accept a new `type=0x81` for the same byte.

```
7E 06 43 17 7C 00 [session] 00 00 00 [chk]    # type=0x00 end marker
```

(Length byte must be 6; a length-6 frame with shorter payload causes
over-read into the next frame.)

`CloseHostSessions()` (called from `Stop()` after `FlushPendingWrites`)
emits the close burst for `0x01/0x02/0x03` only. Wheel-managed
`0x04..0x0a` are left alone — closing them (especially `0x09` configJson)
severs wheel-side state and the wheel does NOT re-init.

### Wheel sess=0x09 internal timeout (2026-05-08)

The wheel maintains a ~10–14 second internal timeout on its sess=0x09
dashboard-binding state. While that timer is running the wheel **silently
ignores every host emission on sess=0x09** — primes, open requests,
anything. New `b2h 7c 00 09 81 ...` device-init events do not fire until
the timer expires. See [`../findings/2026-05-08-wheel-sess09-timeout.md`](../findings/2026-05-08-wheel-sess09-timeout.md)
for wire-trace evidence and the timing matrix.

The plugin handles this with a **silence gate** in `Stop`/`StartInner`:

- `Stop()` records `_lastStopUtcTicks = DateTime.UtcNow.Ticks` at the very
  end (after the close burst has been queued + slept-for-drain).
- `StartInner()`, running on a ThreadPool thread (so the UI doesn't
  block), computes elapsed since `_lastStopUtcTicks` and
  `Thread.Sleep`s the remainder up to `MinSilenceAfterStopMs = 11000` (11s).
- `_lastStopUtcTicks` is **static**, so it survives plugin instance recycle
  inside the same SimHub process. Game-switch reloads the plugin (new
  instance) but the wheel-side timeout is wall-clock and applies across
  both instances.
- Cold-start (`_lastStopUtcTicks == 0` initially) skips the gate.

Dashboard switches **do NOT route through Stop+Start in the current default**
(`EnableHotRenegotiation = true` in `MozaPluginSettings`). The hot-reneg path
keeps sessions 0x01-0x03 open across switches and emits a paced multi-emission
tier-def burst echoing the wheel's END marker — see
[`../tier-definition/handshake.md`](../tier-definition/handshake.md) § In-game
dashboard switch. UI cooldown shrinks to `HotSwitchCooldownMs` (200 ms).

The legacy `OnDashboardSwitched → RestartForSwitch` Stop+Start cycle (with the
full 11 s silence gate) is reached only from:

- `EnableHotRenegotiation = false`,
- Game-switch / wheel hot-swap (`MozaPlugin.ResetWheelDetection`),
- sess=0x02 engagement-watchdog exhaustion (`TickSession02EngagementWatchdog`
  escalation after 5-round backoff).

A normal user dashboard switch in default config never trips the silence gate.

### sess=0x09 establishment retry

Even with the silence gate, a single dropped chunk under Wine SerialPort
R/W contention can stall the prime+open-request emission. The plugin runs
`TickRetryS09IfNotEstablished` once per tick during Active state:

- Guard: `_state != Idle` AND `!_connection.IsConnected ? skip` AND
  `_sessions.GetOrCreate(0x09).DeviceInitiated == false` AND
  `_s09RetryRounds < S09RetryMaxRounds (10)` AND `now - _s09RetryLastTickCount
  >= S09RetryIntervalMs (1000)`.
- Re-emits `SendSessionPrime(0x09, ...)` + `SendConfigJsonOpenRequest(0x09, ...)`
  with fresh seqs (advances `0x000B + round*0x10` to dodge wheel-side
  dedupe).
- Stops automatically when wheel device-inits 0x09 (the chunk handler
  sets `info.DeviceInitiated = true`).
- Logs `[Moza] sess=0x09 not yet device-initiated; retry round N/10` on
  each emit; logs `retry budget exhausted` warning at round 10.
- Resets `_s09RetryRounds = 0` in `Stop()` so each Start cycle gets a
  fresh budget.

Retry guard is `DeviceInitiated == false` — established sessions are
untouched, including post-dashboard-switch sessions where 0x09 stays
alive across the switch.

### ACKs

`fc:00` is the SerialStream ACK type:

```
7E 05 43 17 FC 00 [session] [ack_seq_lo] [ack_seq_hi] [chk]
```

Plugin tracks ack state per session:

| Session | Ack tracking |
|---------|--------------|
| `FlagByte` (0x02) | `_sessionAckSeq` — bumped on every received `7c:00 type=0x01` chunk |
| `_mgmtPort` (0x01) | `_mgmtAckSeq` — same logic |
| `_uploadSession` | acks every chunk, plus message-count threshold for sub-msg-reply detection |
| `0x03` aux | unsolicited data ACKed verbatim |

ACK-seq is **highest contiguous seq received** (Stop-and-Wait); plugin
sends an ACK for every received chunk, not cumulatively.

### Source

[`Telemetry/TelemetrySender.cs`](../../../Telemetry/TelemetrySender.cs)
(`OnMessageDuringPreamble`, `SendSessionOpen`, `SendSessionAck`,
`SendSessionClose`).
[`Telemetry/Sessions/SessionRegistry.cs`](../../../Telemetry/Sessions/SessionRegistry.cs)
(`SessionRegistry`, `SessionInfo`).
