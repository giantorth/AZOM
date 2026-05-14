# Post-switch silence: stale-catalog tier-def + missing sess=02 init protocol

> **Canonical reference:** [`../sessions/session-0x02-ff-init.md`](../sessions/session-0x02-ff-init.md)
> covers the sess=0x02 init handshake authoritatively, including the
> verified-broken shortcut of replaying captured kind=8/11 bytes
> (locked a W17 wheel 2026-05-13 — required power-cycle). Body-decode
> details supporting the canonical doc live in
> [`2026-05-07-sess02-ff-kinds-reference.md`](2026-05-07-sess02-ff-kinds-reference.md).

Date: 2026-05-07. Scope: investigation only — no code changes proposed
without further approval.

User-confirmed symptoms (BeamNG closed, only TestMode in use):
- TestMode on the initial dashboard works: triangle sweeps display.
- Picking a new dash from the UI changes the wheel display correctly.
- After the switch, the test button never drives the new dashboard.
- Switching keeps working indefinitely.

Inputs: deployment trace
`moza-wire-20260507-161115.jsonl` (75 s, 30 272 frames),
`SimHub.1.txt` plugin log, PitHouse reference capture
`sim/logs/bridge-20260429-163951.jsonl`.

Tools added under `tools/`:
- `tools/bridge-decode-ff-init` — reassembles multi-chunk FF records on
  any session in a PitHouse bridge capture and (where applicable) zlib-
  decompresses them. Validates kind=2/7/8/11 init handshake.
- `tools/trace-sess02-decode` — same shape for our own wire-trace JSONL.
  Lets us check whether our plugin engages the sess=02 control protocol.
- `tools/tierdef-decode` extended to flag any TIER channel record with
  `chIndex=0` (catalog-lookup failure) and produce a summary at the end.

## Two distinct findings, possibly entwined

### Finding A: post-switch tier-def encodes idx=0 because the catalog is stale

`tools/tierdef-decode <wire trace>` confirms the post-switch tier-def
(emission E1, t=+26.47 s) puts `chIndex=0` (lookup failed) on 8 of 12
channel records:

```
TIER flag=0x08 2ch: idx=5 /comp=throttle_pct_1/bw=5,
                    idx=0!/comp=boost_1/bw=16     [1 chIndex=0]
TIER flag=0x09 1ch: idx=0!/comp=boost_1/bw=16     [1 chIndex=0]
... (repeats across the 4 broadcast sections)
E1: 8/12 channels have idx=0
```

The lookup falls back to `chIndex=0` in
`Telemetry/Frames/TierDefinitionBuilder.cs:250-251` when a channel URL is not
in the wheel's advertised catalog at build time.

Cause-and-effect on the wire:
- 16:11:46.950 (trace t=26.239) host sends FF kind=4 DASH_SWITCH.
- 16:11:47.192 (trace t=26.473) host sends new tier-def. Plugin's
  `_wheelChannelCatalog` at this moment still contains Grids' 20 URLs
  (ABSActive, ABSLevel, …, TyreWear*). MaxRpm and Rpm are not in it →
  encoded as `idx=0`. Gear coincidentally is in Grids' catalog at idx=5.
- 16:11:47.574 (trace t=26.881, +382 ms after the tier-def) wheel pushes
  its **new** catalog headed by MaxRpm/Rpm/Gear. By then the bad
  tier-def is already on the wire.

Why the renegotiation timing wins the race in the wrong direction:
`RenegotiateForDashboardSwitch` calls
`WaitForChannelCatalogQuiet(quietMs: 200, timeoutMs: 3000)` *before*
swapping the profile and rebuilding the tier-def. At that moment the
wheel hasn't started pushing the new catalog yet, so the wait is
satisfied by the quiet window (no incoming activity for 200 ms), not by
detecting any new content. Then we flush the tier-def with the stale
catalog. The 12-round blind retransmit (Telemetry/TelemetrySender.cs
`_tierDefBlindFrames`) re-sends the same bad tier-def 12 more times.

End-to-end consequence: value frames on the new flag bytes carry
non-zero TestMode values, but two of three Rally V6 channels are
labelled with `chIndex=0` in the tier-def, so the wheel cannot bind them
to its (now-correct) Rally V6 display elements. This is sufficient on
its own to produce the user's symptom.

### Finding B: PitHouse's sess=0x02 control protocol is entirely absent on our side

`tools/trace-sess02-decode <wire trace> --until-switch` against our
trace reports:

```
h2b sess=0x02: no chunks
b2h sess=0x02: 11 unique-seq chunks → 42 bytes
  TLV tag=0x07 size=1 value=00     (PROTO_VER)
  TLV tag=0x04 size=1 value=00     (CHANNEL_INFO stub)
```

`tools/bridge-decode-ff-init sim/logs/bridge-20260429-163951.jsonl
--until-switch` for the same protocol on PitHouse, pre-first-switch
(321 s elapsed):

```
dir   sess  kind  name                              count
h2b   0x02     2  init_nonce                            4
h2b   0x02     7  init_enum                             4
h2b   0x02     8  init_payload_a (channel catalog)      6
h2b   0x02     9  periodic                            111
h2b   0x02    11  init_payload_b (ffb properties)      28
h2b   0x02    14  wheel_payload                        39
h2b   0x02    15  host_setting                         67
b2h   0x02     9  periodic                             82
b2h   0x02    10  wheel_state_a                         2
b2h   0x02    14  wheel_payload                        38
b2h   0x02    16  wheel_state_b                        17
```

Per-kind reassembled and decompressed bodies:

- **kind=8 (init_payload_a)**: ~1.7 KB on the wire, zlib payload after a
  4-byte header → ~10–11 KB of UTF-16-LE strings. First entries:
  `RpmAbsolute, RpmAbsolute1, RpmAbsolute10, …, RpmPercent, …,
  activePaddleNum, aidedSpringControl, baseMotorType, …`. This is a
  full **telemetry channel catalog** — almost certainly the contents of
  `Telemetry.json` keyed for the wheel.

- **kind=11 (init_payload_b)**: ~2.5 KB on the wire, ~10 KB
  decompressed. Strings: `decrementEqualizerGain,
  decrementEqualizerGain2…6, decrementGameForceFeedbackFilter,
  decrementGameForceFeedbackStrength, decrementInitialSpeedDependent-
  Damping, decrementMaximumGameSteeringAngle, …
  decrementSoftLimitStiffness, …`. This is the **FFB / wheel-tuning
  property catalog** (the `decrementXxx` half — `incrementXxx` and
  setters likely follow).

- **kind=2 (init_nonce)** 16 B body: looks like a session salt /
  identifier (`c996f269 00000000 909dffff 1f3d320c`).

- **kind=7 (init_enum)** 12 B body: small enum, payload starts
  `03 00 00 00 …` (u32=3) — likely a protocol version / capability
  selector.

PitHouse exchange timeline (first 10 s, captured from `bridge-`):
```
t=0.050  h2b OPEN sess=02
t=0.101  b2h TLV 07 size=1            ← wheel announces protocol on b2h
t=0.101  b2h TLV 01 size=97           ← wheel TIER record
t=0.101  b2h TLV 04 size=9            ← wheel CHANNEL_INFO
t=2.105  b2h (same triplet repeated — wheel poll while host silent)
t=2.970  h2b FF kind=2  init_nonce
t=2.970  h2b FF kind=7  init_enum
t=2.970  h2b FF kind=8  init_payload_a (1.7KB, channel catalog)
t=3.079  h2b FF kind=11 init_payload_b (2.5KB, FFB property catalog)
t=5.418  b2h FF kind=10 wheel_state_a       ← wheel ACK after init
t=5.468  b2h FF kind=16 wheel_state_b       ← wheel ACK after init
```

Our trace timeline (same 10 s window):
```
t=0.123  h2b OPEN sess=02
t=0.143  b2h 5 TLV chunks (tag 0x07/0x01/0x04/0x06; same shape as
         PitHouse but tag=0x01 size=81 instead of 97 — wheel TIER
         differs by one channel)
t=4.143  b2h 4-byte heartbeat   00 00 00 00
t=8.143  b2h 4-byte heartbeat
t=12.144 b2h 4-byte heartbeat
... (heartbeat continues every ~4 s)
(no h2b ever, until t=26.239 FF kind=4 switch)
```

So our wheel **does** open the protocol on b2h sess=02 with the same
five TLV records PitHouse's wheel sends, then drops to a 4-byte
heartbeat because the host never replies with kind=2/7/8/11. PitHouse's
wheel only emits kind=10/14/16 *after* the host completes the init
handshake — no init, no wheel-state pushes back.

## Why these two findings probably interact

The wheel's "current catalog" (the URL→index map it later sends back
on b2h sess=01 after a switch) is built from whatever the host has told
it about. PitHouse's host pre-loads the **complete** channel catalog via
kind=8 — at switch time the wheel already knows every channel name that
exists. The host can immediately send a tier-def that references *any*
channel by index, and the wheel binds correctly.

In the plugin's path, the wheel only knows the channels we previously
subscribed to (via tier-def #1's URL records on session 0x01 b2h). When
the user switches to a dashboard whose channels weren't in the previous
subscription, the wheel doesn't yet have those URLs, has to discover
them itself, and pushes the new catalog ~400 ms later. Our renegotiation
already raced ahead and emitted a tier-def with `chIndex=0` for the
unknown URLs.

Two potential paths to a fix (NOT implementing yet):

1. **Pre-load the catalog like PitHouse**: implement the kind=8 (and
   probably kind=11 for FFB tuning) upload at startup. This makes the
   wheel know every channel ahead of time so post-switch tier-defs never
   produce idx=0. It also recreates the conditions under which
   PitHouse's wheel emits kind=10/16 back, which is plausibly the
   trigger for the FF echo on kind=4 switches we still don't see.

2. **Wait for the new catalog before sending tier-def**:
   `RenegotiateForDashboardSwitch` should wait until
   `_wheelChannelCatalog` *contains the new profile's channel URLs*
   before building the tier-def, not just for a 200 ms quiet window.
   Smaller change, but does not address (a) the missing FF echo, (b)
   PitHouse's full sess=02 init protocol, or (c) the failure mode for
   any switch where the wheel takes > 3 s to push the new catalog.

(1) is closer to "what PitHouse does"; (2) is a localized fix that may
or may not be sufficient by itself. Combining both is straightforward.

## Open questions still requiring data, not code

1. **Does kind=8 contain the entire `Data/Telemetry.json` content?**
   The first dozen entries in the decompressed payload (RpmAbsolute,
   RpmPercent, …) line up with `Data/Telemetry.json` channel keys.
   Worth diffing the decompressed payload against `Data/Telemetry.json`
   in the repo to confirm formatting (UTF-16-LE? null-terminated? some
   header?). One pass through `tools/bridge-decode-ff-init <capture>
   --kind 8 --max 1 --full` will give the full decompressed bytes.

2. **What is the on-the-wire content type of kind=11?** We confirmed it
   is FFB-property-name strings. If we are not implementing FFB tuning
   reads/writes today, we may be able to skip kind=11 in our minimum
   viable implementation. But the wheel's emission of kind=10/16 might
   still depend on receiving it.

3. **Does the wheel emit kind=10/16 if we send only kind=8 (no
   kind=11)?** Cannot be answered from data alone — needs an
   experimental capture if we move toward implementation.

4. **What is the body of kind=14 (wheel_payload)?** It's 1.7 KB
   bidirectional, also zlib-shaped. Likely `Dashes/*.mzdash` content.
   Worth decompressing one example to confirm — relevant if we ever
   want to upload custom dashboards.

5. **Does the chIndex=0 fix alone unblock test data after switch, or
   is the missing FF echo (and absence of kind=10/16) also load-
   bearing?** Cannot be answered from existing data — needs an
   experimental capture after a chIndex=0 fix.

## Concrete next data captures (read-only) before any code

To distinguish whether (1) or (2) is sufficient:

- A: deploy a **probe build** that simply waits longer before sending
  the post-switch tier-def (e.g. `WaitForChannelCatalogQuiet(quietMs:
  500, timeoutMs: 3000)` *and* additionally requires that all
  `_profile.Tiers.SelectMany(t => t.Channels).Select(c => c.Url)` are
  present in `_wheelChannelCatalog` before proceeding). If post-switch
  test data starts displaying, Finding A alone explains the symptom.

- B: independent of A, capture a fresh trace with the user toggling
  TestMode on Grids → switch to Rally V6 → toggle TestMode again, no
  game running. Run `tools/tierdef-decode --json` and confirm no
  `chIndex=0` in E1. (This is a verification capture, not a debugging
  one.)

Both A and B require a deploy + capture, not just analysis. The user
should weigh in on whether to proceed with A as a probe.
