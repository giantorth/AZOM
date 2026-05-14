# Investigation plan: post-switch telemetry not reaching wheel

Date: 2026-05-07. Source: latest deployment traces in
`~/.local/share/Steam/steamapps/compatdata/2825720939/.../SimHub/Logs/`,
the most recent being `moza-wire-20260507-161115.jsonl` and
`SimHub.1.txt`. PitHouse reference: `sim/logs/bridge-20260429-163951.jsonl`.

This is an investigation plan, not an implementation plan. No code changes
without explicit approval. The goal is to isolate the root cause of: "v1
pipeline can drive a dash and switch dashes, but cannot drive test data
after a switch."

## What the latest trace actually shows (16:11:15 session, 75 s, BeamNG running)

Trace timeline (relative to trace t=0 = first byte after COM33 connect):

| t (s) | Event |
|-------|-------|
|  0.0 | host->wheel CLOSE sess=01/02/03 (cold start hygiene) |
|  0.1 | host->wheel OPEN sess=01/02/03 |
|  1.4 | first cold-start tier-def chunks (sess=01) |
|  1.5 | wheel->host OPEN sess=05 (first time) |
|  3.4 | wheel->host OPEN sess=07 (first time) |
|  10.3 | TestMode True (Grids) — VFs become non-zero |
| 13.3 | wheel->host OPEN sess=05 again (re-open) |
| 23.2 | wheel->host OPEN sess=05 + sess=07 again |
| 24.0 | TestMode False |
| **26.2** | **host->wheel FF DASH_SWITCH (slot=10, Rally V6) on sess=02 seq=3** |
| **26.2** | **NO FF echo from wheel — PitHouse always echoes within ~77 ms** |
| 26.5 | host begins streaming new tier-def (flagBase=0x08, 6 chunks) |
| ~27   | wheel updates internal catalog (MaxRpm/Rpm/Gear) — switch *did* land internally |
| 28.6 | TestMode True again |
| 32.9 | TestMode False |
| 33.1 | wheel->host OPEN sess=05 yet again |
| ≥36.2 | every value frame from now on is **zero-payload** |

Plugin-log markers:

```
16:11:46.950  Sent dashboard-switch FF-record: slot=10 on session 0x02 seq=3
16:11:47.192  Sending type02-section tier definition: flagBase=0x08, prev=0x00/8t/2spb,
              preamble (0 chunks) + 324 bytes in 6 chunks on session 0x01 (8 tiers)
16:11:47.574  Wheel channel catalog updated (size 20→20):
              [1]=v1/gameData/MaxRpm [2]=v1/gameData/Rpm [3]=v1/gameData/Gear
16:11:47.394  Blind retransmit round 1/12 (6 chunks)   ← 12 rounds of brute retx
   ...
16:11:49.265  TestMode changed to True
16:11:49.704  Blind retransmit round 12/12 (6 chunks)
16:11:53.635  TestMode changed to False
```

So the plugin:
- sent the switch,
- sent a new tier-def the wheel partially understood (it updated its
  catalog),
- went into a 12-round blind-retransmit storm because no ack came,
- and emitted value frames whose payloads decode to all-zero throughout
  the second TestMode window.

## Comparison with PitHouse `bridge-20260429-163951.jsonl` (Rally V4 capture, 1 known good switch)

### A. Volume and content of session 0x02 BEFORE the first switch

| Direction | PitHouse (321 s pre-switch) | Plugin (26 s pre-switch) |
|-----------|-----------------------------|--------------------------|
| h2b sess=02 messages | 7 219 | **3** |
| h2b sess=02 FF records | **264** | 1 (just kind=4) |
| h2b FF kinds present | 2, 7, 8, 9, 11, 14, 15 | 4 |
| b2h sess=02 messages | 1 237 | 25 |
| b2h sess=02 FF records | **140** | **0** |
| b2h FF kinds present | 9, 10, 14, 16 | (none) |

PitHouse's wheel sends a TLV stream on sess=02 b2h within 51 ms of open
(seq 6–10): tag=0x07 (proto-ver), tag=0x01 size=0x61 (TIER), tag=0x04
(CHANNEL_INFO), tag=0x06 (END). It is a *parallel* catalog/control stream
on session 0x02. We never read or use this.

### B. PitHouse host-side init handshake at t≈3 s on sess=02

```
seq=3  FF kind=2  size=16   [16 B body]            (timestamp/nonce-shaped)
seq=4  FF kind=7  size=12   [12 B body, u32=3]
seq=5  FF kind=8  size=1740 [zlib-compressed blob, 78 da magic]
seq=N  FF kind=11 size=2572 [zlib-compressed blob, 78 da magic]
```

kind=8 / kind=11 are large zlib payloads — almost certainly compressed
mzdash / dashboard-data uploads. PitHouse runs this handshake before any
DASH_SWITCH. The plugin does not implement any of this.

### C. The switch itself

PitHouse: `h2b FF kind=4` on sess=02 → `b2h FF kind=4` echo on sess=02
within 77 ms → `h2b FC ack` of the echo. Plugin: `h2b FF kind=4` → no
echo, no ack, plugin falls back to the 12-round blind retransmit.

### D. Existing comment in `TelemetrySender.cs:846-852` is wrong

It claims "PitHouse shows ALL FF records on session 0x01" and that we
moved to 0x02 as a workaround. The bridge data (verified across all 35
captures referenced in the 2026-05-07 alignment doc) shows the opposite:
**every meaningful FF record in PitHouse is on sess=0x02**, including the
init kinds 2/7/8/11.

## Hypothesis ranking

### H1 (most likely): the wheel ignores standalone FF kind=4 because it has not seen the kind=2/7/8/11 init handshake on sess=02

Evidence:
- the wheel's catalog update at 47.574 *does* show the wheel switched
  internally,
- but it did not echo the FF kind=4, did not start emitting different
  state on b2h sess=02, and the post-switch sess=05/07 re-opens look like
  the wheel resetting transport-layer state because of an
  un-acknowledged control flow.

If H1 is correct, the wheel is in a "polite-but-confused" state where it
processed the slot change but has not entered the post-switch
acknowledgment phase that gates value-frame display.

### H2: post-switch test data really is all-zero because TestMode timing/data path is broken for the new profile

Evidence:
- pre-switch TestMode: ~166 non-zero VFs / 5 s on flags 0x00,0x02,0x04,0x06,
- post-switch TestMode (4.4 s active window): only ~89+55 non-zero VFs
  per flag, then 100 % zero,
- but BeamNG was running in both windows — so a chunk of the pre-switch
  "non-zero" was game data, and the test-mode-only delta is smaller than
  it looks.

`BuildTestFrame` is keyed off the *current* `_profile` and rebuilds the
buffer from `_profile.Channels`. After `applyProfile?.Invoke()`, `_tiers`
is replaced (log line "tiers=8" stays consistent). Worth verifying that
`tier.Builder._profile` is the *new* profile and that `_testPhase` is
sane post-rebuild.

### H3: 12-round blind retransmit is corrupting the post-switch state

Evidence:
- 12 rounds × 6 chunks = 72 redundant tier-def chunks pumped into sess=01
  in the 2.5 s after the switch,
- this collides with the wheel's own catalog re-advertisement on b2h
  sess=01 at 47.574,
- the wheel reopens sess=05 at trace t=33.1 (~7 s after switch) — could
  be its way of resetting after the retx storm.

PitHouse never blind-retransmits; it relies on FC acks.

### H4: profile/subscription bookkeeping after the switch sets the wrong tier mapping

The post-switch tier-def computes prev-sub correctly (`prev=0x00/8t/2spb`)
and `_activeSubscription` is updated to flagBase=0x08. The Rally V6
profile is "8 tiers / 2spb / 4 broadcasts". `BuildTestFrame` is called
with `flagByte = subFlagBase + i`, i.e. 0x08..0x0F — which is what we see
on the wire. So the flag mapping looks consistent. Lower probability than
H1/H2/H3 but worth confirming.

### H5: session 0x05 / 0x07 churn is independent and unrelated

The wheel reopens these even before the switch (t=1.5, 13.3, 23.2 — three
times pre-switch). Probably a separate bug, *probably* not the cause of
post-switch silence — but if it indicates the wheel periodically resets,
the post-switch dormancy could just be one of those resets that never
recovers because the host stopped its end of the protocol.

## Questions to answer before changing code

Each step is "look at data, do not yet edit code".

### Step 1 — confirm what "cannot send test data after a switch" actually means on the wheel

User to clarify (one of):
- (a) wheel screen shows no movement / shows stale values,
- (b) wheel goes blank,
- (c) wheel shows the new dashboard layout but values frozen at zero,
- (d) wheel reverts to prior dashboard.

Different answers point to different layers (display vs. telemetry vs.
session state).

### Step 2 — verify the wheel really did switch internally

Do a fresh trace where:
- TestMode is enabled BEFORE the switch (should produce known
  non-zero pattern on Grids flags 0x00/0x02/0x04/0x06),
- switch to Rally V6 with TestMode left on,
- capture for ≥30 s post-switch.

Check: does the wheel display anything for the new dash layout? Does
b2h sess=01 (catalog) reflect Rally V6 channels (MaxRpm/Rpm/Gear)? In
the existing 16:11 trace the answer to the second is yes. Need the first
from the user.

### Step 3 — decode b2h sess=02 fully across the latest trace

Use `tools/bridge-tierdef-decode` style on the wire trace's sess=02
payloads (need a small new tool; not built yet). Specifically check:
- did the wheel ever push the `07/01/04/06` TLV catalog on b2h sess=02
  that PitHouse always sees within 51 ms of open?
- if not, the wheel is *not* engaging session 0x02 the way PitHouse
  expects — maybe because we never opened it the way PitHouse does.

Compare the literal byte sequence of our `OPEN sess=02` against
PitHouse's. Different port byte? Different ordering relative to other
opens?

### Step 4 — quantify the test-data-only contribution post-switch

The 161115 trace cannot answer H2 cleanly because BeamNG was running.
Need a trace with:
- BeamNG **not** running (or in main menu),
- full pre/post-switch sequence with TestMode toggled BEFORE and AFTER
  the switch.

Then any non-zero VF is unambiguously from `BuildTestFrame`. If
post-switch TestMode produces zero VFs even without game telemetry, H2
is confirmed and we look at `_profile`/`_tiers` swap atomicity.

### Step 5 — decode the kind=2/7/8/11 init payloads in PitHouse

Approximate decoding:
- kind=2 (16 B): looks like nonce + flags. Compare body across captures
  — same format, varying values? If varying, probably a
  client-identifier or session salt.
- kind=7 (12 B, u32=3): looks like a small enum. Compare across
  captures.
- kind=8 / kind=11 (1.7 KB / 2.5 KB, zlib-compressed): unzip them and
  inspect. If they are mzdash JSON or dashboard layout data, this is the
  upload protocol we have been calling "bridge-tierdef" earlier — except
  on sess=02, not sess=01.

Add a reusable tool under `tools/`: `tools/bridge-decode-ff-init` that
scans any bridge capture and dumps the kind=2/7/8/11 sequence with
zlib-decompressed bodies.

### Step 6 — confirm or rule out H3 by trying a "no-blind-retx" wire test

Not a code change yet — first measure: in the 161115 trace, line up the
12 retx rounds with the wheel's b2h activity. Does the wheel push more
on b2h sess=01 *after* the retx storm ends, or *during*? If "after",
retx is interfering. If "during", retx is harmless and we cross H3 off.

`tools/trace-switch-diff` already groups by epoch — extend it (small
addition) to a per-second event histogram covering the [switch,
switch+5 s] window with separate buckets for h2b retx, b2h sess=01,
b2h sess=02, and value-frame flag bins.

### Step 7 — check whether the wheel ever ran the kind=10/14/16 wheel→host pushes

PitHouse b2h sess=02 FF kinds (pre-switch): 9 (82×), 14 (38×), 16 (17×),
10 (2×). These are the wheel telling the host things — likely brightness
state, settings ack, dashboard catalog ready.

In our trace, b2h sess=02 had **0** FF records. The wheel never pushed
state to us on sess=02. This very strongly implies the wheel does not
consider the session 0x02 protocol to be active on our side.

If we figure out *what trigger* on the wire causes the wheel to start
emitting kind=10/14/16, we likely also figure out what the wheel needs
in order to echo kind=4.

## Tooling work needed (read-only / analysis)

These are reusable, so per the user's "tools/ vs /tmp" rule they go in
`tools/`:

1. `tools/bridge-decode-ff-init` — extract and zlib-decompress kind=8
   and kind=11 bodies from any bridge capture; dump kind=2/7/9/10/11/14/15/16
   bodies with hex + ASCII fallback. Walks all sessions, not just 0x02.

2. `tools/trace-sess02-decode` — decode sess=02 TLV / FF records in our
   own wire trace (mirror of `bridge-tierdef-decode` but for our trace
   format). Surfaces "wheel never pushed kind=N" gaps.

3. `tools/switch-second-by-second` — extend `tools/trace-switch-diff` to
   bucket every event into 250 ms bins around the switch, separated by
   direction × session × frame-class, so collision/idle patterns become
   visible.

4. (optional) `tools/bridge-vs-trace-ff-diff` — given a PitHouse capture
   and a plugin trace covering equivalent phases (cold start, switch,
   post-switch), print a side-by-side FF-kind / sess timeline.

## Code paths to read carefully (before any edit)

These are the files most likely to change once we know which hypothesis
is right; flagged here so they can be opened in the same plan:

- `Telemetry/TelemetrySender.cs`
  - `SendSessionPropertyBody` (lines 846–859) — wrong comment, hard-coded
    sess=0x02, no init handshake
  - `RenegotiateForDashboardSwitch` (lines 942–1008) — mute window, prev
    sub snapshot, ApplySubscription
  - tier-def emission (lines 1450–1543) — preamble gating, blind retx
    setup (`_tierDefBlindFrames`)
  - tick loop (lines 2820–2916) — value-frame emission, mute gate,
    TestMode vs game gating
- `Telemetry/Frames/TelemetryFrameBuilder.cs:157` — `BuildTestFrame`, profile
  binding
- `Telemetry/SessionRetransmitter.cs` — what exactly the 12-round blind
  retx loop does
- `MozaPlugin.cs:1225` — `OnDashboardSwitched` and the profile-apply
  lambda passed in
- `Protocol/SessionPropertyPushBuilder` — body format for FF kind=4 (and
  whether kinds 2/7/8/11 already have helpers we are not using)

## Decision points the user needs to weigh in on

Before any patch:

1. Should we investigate the **wheel-side init handshake** path (H1) at
   the cost of implementing kinds 2/7/8/11/14/15/16 — i.e. a new control
   protocol? This is a substantial body of work and is the only path
   that *fully* matches PitHouse.

2. Or should we first verify H2 in isolation (test data path) since it
   is local and quick to confirm? If H2 alone explains the symptom we
   may not need the full sess=02 protocol to ship "drive new dash with
   test data after switch."

3. Is the user comfortable with another instrumented capture (Step 4)
   on a clean machine state — BeamNG closed, TestMode toggled
   deliberately — to disambiguate test vs game data?

4. Confirm the failure mode (Step 1, a/b/c/d) — this single answer
   collapses the search space considerably.
