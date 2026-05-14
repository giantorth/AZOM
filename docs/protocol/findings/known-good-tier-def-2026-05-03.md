# Known-good tier-def behavior — 2026-05-03 15:28

## Status

- **Pre-switch test mode**: WORKS (eyeball-confirmed by user)
- **Post-switch test mode**: BROKEN
- **Dashboard switching itself**: WORKS (FF-record activates target slot)

This document captures the plugin state at the moment pre-switch test
mode was confirmed working, so subsequent attempts to fix post-switch
don't regress the pre-switch baseline.

## Test methodology

Auto-test harness fires on wheel-detect + Test-mode-on. PreSwitchTest
phase emits 200 test frames against the startup-loaded dashboard. User
eyeballs the wheel display. PostSwitchTest fires another 200 frames
after FF-record dash switch.

200-frame PASS in the log only confirms frames were SENT — not that
the wheel rendered. User eyeball is the rendering verification.

## Plugin behavior (in-source, currently deployed)

### Tier-def builder (`Telemetry/Frames/TierDefinitionBuilder.cs`)

Type02 path (Wireshark `wheel-catalog`-indexed builder, used when
firmware era = `New2026_04_Type02`):

```
[preamble — only on first tier-def per session]
07 04 00 00 00 02 00 00 00 03 00 00 00 00

[for each prior flag byte, if any]
00 01 00 00 00 [flag]                  ← enable prev tier from earlier sub

[for each new tier i, flag = flagBase + i]
01 [size LE] [flag] [N × 16-byte channel records]
  per channel: idx LE, comp LE, bw LE, 0 LE

[end-marker]
06 04 00 00 00 [max channel idx LE]
```

No trailing per-tier enables for the new flags (REMOVED — was breaking
pre-switch test mode).

### Profile expansion (`Telemetry/TelemetrySender.cs:~430`)

Multi-broadcast restored: each sub-tier replicated `max(3, N+1)` times
with consecutive flag bytes within a flagBase. So Grids' 2 mzdash tiers
become 8 tier declarations (4 broadcasts × 2 sub-tiers) before the
builder runs.

### Flag-base advance (`Telemetry/TelemetrySender.cs:~1383`)

`_nextFlagBase` (private byte) starts 0, advances by `profile.Tiers.Count`
after each `SendTierDefinition()` call. Cleared only on Stop/Start.

Effect: post-switch tier-def uses fresh flag bytes (e.g. 0x08+ when
prior dash used 0..7). Flag bytes never reused across switches in one
session.

### Value-frame stamping (`Telemetry/TelemetrySender.cs:~2790`)

Frame loop uses `flagByte = subFlagBase + i` where `subFlagBase` is
read from `_activeSubscription.FlagBase`. Originally was `(byte)i`
(plain tier index). Pairs with flag-base advance.

### Prior-flag enables in subsequent tier-defs (`TierDefinitionBuilder.cs:~103`)

Each `SendTierDefinition` call after the first emits one
`[tag=0x00][size=1][flag]` enable record per flag byte declared in
prior tier-defs, BEFORE the new tier declarations.

### End-marker (`TierDefinitionBuilder.cs:~130`)

Single end-marker per tier-def message, val = max channel index
referenced by the new tier declarations in this message. Not per-tier.

### Retransmits

- `_retransmitter.DueRetransmits(intervalMs: 200, maxRetries: 100)`
- `TierDefBlindMaxRounds = 12`, `TierDefBlindIntervalMs = 200`

### Auto-test (`Telemetry/DashboardSwitchAutoTest.cs`)

Fires once per session when `EnableAutoTestOnConnect = true`:
1. PreSwitchTestMs = 6000 ms TestMode on startup dash
2. Switch to opposite-of-`AutoTestLastSlot` slot via FF-record
3. WaitRenegotiate up to 10000 ms for catalog push
4. PostSwitchTestMs = 6000 ms TestMode on target dash
5. Persist `AutoTestLastSlot` for next-run alternation

## Observed wire shape (latest plugin trace 152816)

Initial (Mono, flagBase=0x00, 12 tiers via 4×3 broadcast):
```
preamble
TIER flag=0..0x0b N=8|2|2 channels (rotating per pkg_level)
END val=12 (max idx in catalog of 12)
```

Post-switch (Core, flagBase=0x0C, priorFlags=0x00..0x0b, 8 tiers via 4×2):
```
preamble
ENABLE 0..0x0b (12 prior-flag enables)
TIER flag=0x0c..0x13 N=5|1 channels
END val=12 (max idx)
```

## Critical files (current state)

- `Telemetry/TelemetrySender.cs` — `_nextFlagBase`, `_declaredFlags`,
  `_priorSubscriptions`, value-frame loop
- `Telemetry/Frames/TierDefinitionBuilder.cs` — `BuildTierDefinitionMessageType02`

## What's been tried + reverted this session

- Multi-broadcast → single-broadcast (REVERTED: killed test mode entirely)
- Cumulative redeclaration of prior tiers in section 1 (REVERTED)
- Single end-marker val=0 + section-2 end-marker val=max-idx (REVERTED)
- Preamble re-sent every tier-def msg (REVERTED: matches PitHouse but unnecessary)
- Reduced retransmit caps to 3 (REVERTED)
- Trailing per-tier enables for new flags (REVERTED — this revert FIXED pre-switch)

## Outstanding bug

Post-switch test mode does not render on the wheel.

Hypothesis to investigate (not yet tried):
- Wheel needs explicit "subscribe new flag bytes" message after the
  tier-def, separate from the in-tier-def declarations
- Wheel rejects flag-base advance under some condition (would need
  test with flagBase=0 reused)
- Channel idx mapping bug — in some post-switch tier-defs,
  `idxByUrl.TryGetValue` returns idx=0 for legitimately-known URLs
  due to catalog backref-merge order
