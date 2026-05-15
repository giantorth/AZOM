# Findings 2026-05-15 — V2Type02 tier-def `end_marker` is max-chIdx, not channel-count

User reported Simple Rally Mini Dash never accepted test-mode rendering
even though Grids and Mono dashboards rendered fine on the same wheel
(W17 / CS Pro firmware 2026-04+). After many cycles of comparing the
plugin's emission to PitHouse's `sim/logs/bridge-20260514-204307.jsonl`
SR capture, the only structural difference was the **end-marker value**
on the V2Type02 tier-def message.

> "These wheels are very particular about the channel ordering and any
> mistake whatsoever will break the link." — user, 2026-05-15

## Symptom

- Plugin's tier-def for SR: 7 fast channels (idx 1,2,3,4,5,6,9) +
  1 slow channel (idx 8), all bit-widths and compression codes
  byte-identical to PitHouse, sorted idx-ascending, value frames
  emitted at 25/17 bytes matching PitHouse's frame sizes.
- Wheel-side: dashboard renders normally at idle (last value
  displayed), TrackId string widget updates on type=0x05 emits,
  but **every bit-packed widget stays frozen at baseline / zero**
  during test mode. Wheel never bound the widgets to our tier-def.
- Grids and Mono dashboards rendered fine in test mode on the
  same plugin build — their channel idxes were contiguous so
  the bug masked itself for those dashboards.

## Root cause

The `[0x06]` end-marker after each broadcast group in the V2Type02
tier-def carries a 4-byte LE u32 value. PitHouse capture rules:

- **First broadcast in the message**: `marker_value = 0`
- **Every subsequent broadcast**: `marker_value = max chIdx seen in tier-def so far`

The plugin had been emitting `marker_value = channelsPerBroadcast`
(total channel records across one broadcast's sub-tiers, after
chIdx=0 filtering). For dashboards where all chIdx values are
contiguous and start at 1, `channelsPerBroadcast` happens to equal
`max-chIdx` — so the bug was invisible for Grids and Mono.

Simple Rally Mini Dash:
- Fast tier: chIdx 1, 2, 3, 4, 5, 6, **9** (skips 7 because TrackId
  is a string channel routed out-of-band on sess=0x01 type=0x05, and
  skips 8 because TrackLength is in the slow tier)
- Slow tier: chIdx 8

Plugin's tier-def therefore had channel count = 8 but max-chIdx = 9.
PitHouse emitted `end_marker = 9`, plugin emitted `end_marker = 8`.
That single off-by-one byte broke the wheel's widget-binding step.

## PitHouse capture decoded reference

`sim/logs/bridge-20260514-204307.jsonl`, decoded via
`tools/tierdef-decode`:

```
E1  t=+2.76s  flags=0x01..0x02  END=9
  ENABLE 0x00
  TIER flag=0x01  7ch: idx=1/comp=0x0E/bw=10, idx=2/comp=0x07/bw=32,
                       idx=3/comp=0x00/bw=1, idx=4/comp=0x0D/bw=5,
                       idx=5/comp=0x0F/bw=16, idx=6/comp=0x0F/bw=16,
                       idx=9/comp=0x0E/bw=10
  TIER flag=0x02  1ch: idx=8/comp=0x07/bw=32
  END_MARKER val=9
E2  t=+3.73s  flags=0x03..0x04  END=9
  ENABLE 0x01, ENABLE 0x02
  TIER flag=0x03 (same 7ch)
  TIER flag=0x04 (same 1ch idx=8)
  END_MARKER val=9
```

`channels_in_emission = 8`, `END_MARKER = 9 = max chIdx in tier records`.

## Fix

`Telemetry/Frames/TierDefinitionBuilder.cs` `BuildTierDefinitionMessageType02`:
track `maxIdxSeen` across all `WriteTier` calls, pass it (cast to u32)
to `WriteEndMarker` for every broadcast after the first.

```csharp
int maxIdxSeen = 0;
void WriteTier(byte flag, IReadOnlyList<ChannelDefinition> channels)
{
    ...
    foreach (var (chIndex, ch) in resolved)
    {
        if (chIndex > maxIdxSeen) maxIdxSeen = chIndex;
        ...
    }
}
...
WriteEndMarker(b == 0 ? 0u : (uint)maxIdxSeen);
```

Previous code wrote `channelsPerBroadcast` (which was a post-filter count
of channels across one broadcast's sub-tiers). That value happened to
equal max-chIdx for contiguous-idx dashboards but was off-by-one or more
for any dashboard with gaps in its catalog idx assignments.

## Affected paths

Only `BuildTierDefinitionMessageType02` (V2Type02 encoding, what W17 /
CS Pro firmware 2026-04+ use). `BuildTierDefinitionV2` was already
using `maxIdx` correctly. The split between these two paths historically
papered over the bug because the V2Type02 path was hand-derived from
captures of dashboards where the difference didn't matter.

## Open items / unverified claims

- End-marker semantics overall are still partially decoded: the docs
  describe it as a "status/flush flag" with the value being
  "TBD". This finding confirms the value rule but doesn't decode what
  the wheel does with the value internally. PitHouse always emits
  the same max-chIdx so the wheel might just be CRC-validating the
  whole tier-def including this byte; the value itself may be opaque
  to the wheel's bind logic, but **a wrong value blocks binding**.
- We don't know whether the wheel's binding refusal is a CRC-style
  whole-message reject, or whether the end-marker is a per-broadcast
  validity signal the wheel actively reads.
