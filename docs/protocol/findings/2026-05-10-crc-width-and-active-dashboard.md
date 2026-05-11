# Re-verification: chunk CRC is 4 bytes; active dashboard lives at 28:00/28:01

**Date:** 2026-05-10
**Wheel:** CS Pro (W17), firmware `RS21-W17-MC SW`, display `RS21-W17-HW RGB-` (latest)
**Source captures:**
- `~/CS-Pro-moza-diagnostics-bundle-20260510-073015.zip` (catalog parser empty, `crcRejects=70`)
- `~/CS-Pro-moza-diagnostics-bundle-20260510-191959.zip` (catalog populated after fix)
- 524 historical wire-trace files in `~/.local/share/Steam/.../SimHub/Logs/moza-wire-*.jsonl` (2026-04-29 → 2026-05-09)

## What this finding records

Two corrections landed in the authoritative protocol docs today. This finding is the audit trail; the docs themselves are the spec.

1. **Inbound session-data chunk CRC is 4 bytes LE, always.** Spec doc updated: [`../sessions/chunk-format.md`](../sessions/chunk-format.md) §"Session data chunk CRC — 4 bytes LE".
2. **Wheel's active dashboard is reported via `28:00` / `28:01` readbacks, not via the configJson state push.** Spec docs updated: [`../channel-config/group-0x40-burst.md`](../channel-config/group-0x40-burst.md) §"28:00/28:01 response format", [`../dashboard-upload/config-rpc-session-09.md`](../dashboard-upload/config-rpc-session-09.md) §"What the state blob does NOT contain".
3. **Boot-time `0x0E` debug-log burst can defeat first-frame port validation.** Spec doc updated: [`../heartbeat.md`](../heartbeat.md) §"Boot-time `0x0E` burst on cold connect".

## CRC re-verification — what was actually true

The 2026-05-09 commit `dca55a8` ("Fix CRC byte error") flipped inbound CRC validation from 4-byte to 3-byte, claiming "154/154 chunks matched 3-byte CRC, 0/154 matched 4-byte" against a same-day capture. That claim turned out to be a tautology produced by an off-by-one in the verification script.

**Empirical reverification (CS Pro, latest firmware):**

| Source | Chunks scanned | 4-byte CRC matches | 3-byte CRC matches |
|---|---:|---:|---:|
| `CS-Pro-…-073015.zip` serial-capture.txt | 398 | **398 (100 %)** | 0 |
| 524 historical JSONL traces (11 days) | 227,713 | **227,497 (99.91 %)** | 0 |

The 216 / 227,713 non-match rate on 4-byte is the floor — corrupt/truncated frames produce that level of noise even on a healthy 4-byte link.

**Why the original "3-byte 154/154" verification was wrong.** `moza_trace.py:68-70` documents the b2h JSONL format as `[resp_group, resp_dev, payload…]` with **no** wire-checksum trailer. The 2026-05-09 script almost certainly treated the JSONL hex as if it had a trailing checksum byte, shifting the data window by one. Under that shift, computing `crc32(chunk[:-3]) & 0xFFFFFF` and comparing against `chunk[-3:]` becomes algebraically identical to the canonical `crc32(chunk[:-4]) == chunk[-4:]` test — always matches when the 4-byte CRC matches, "proving" 3 bytes when really proving 4. See [`../sessions/chunk-format.md`](../sessions/chunk-format.md) §"Tautology trap" for the full table of equivalent forms.

**Production impact of the regression.** `Telemetry/TelemetrySender.cs` catalog-feed and tile-server-feed paths checked 3-byte CRC with no fallback (`SessionDataReassembler.StripCrcTrailer` had a 4-byte fallback, which is why configJson / RPC / upload paths survived). On the catalog feed every chunk failed, leaving the parser empty. `SendTierDefinition` then fell back to alphabetical channel indices, the wheel couldn't bind any of them to dashboard widgets, and the visible failure mode was "dashboards mostly work but they don't respond without a lot of fiddling and restarts/resets." Reverted in the same patch as this finding; CS Pro diagnostics now show `crcRejects=0`, catalog populated with 6 entries, tier-def encoding `idx=wheel-catalog`.

## Active-dashboard signal — where it actually lives

Open question coming into this session: how does the host learn which dashboard the wheel is rendering at startup or after a switch?

Capture analysis answers:

| Source | Direction | Carries active dashboard? | Notes |
|---|---|---|---|
| `28:00 data=00` reply on group `0x40` | wheel → host | **Yes** — slot index into `configJsonList` | `u8` payload. Sample CS Pro reply: `00` → `configJsonList[0]` = `Core` (alphabetical first). Wheel retains across power cycles. Plugin reads but doesn't decode. |
| `28:01 data=00` reply on group `0x40` | wheel → host | **Yes** — active page within current dashboard | `u16 LE`. Sample: `00 00` → page 0. |
| `kind=4` FF-record echo on session `0x02` | wheel → host | Yes for switches, not at startup | Only emitted in response to host-initiated kind=4. Host treats echo as "switch confirmed". |
| configJson state push on session `0x09` | wheel → host | **No** | Lists installed/enabled dashboards but no `activeSlot`/`currentDashboard`/`selectedIndex` field in either 2025-11 or 2026-04 schema. |
| Plugin's saved `TelemetryProfileName` | host-local | host-side cache only | Drifts from wheel state after any reload where the wheel kept rendering something else. |

**Practical resolution path** (plugin TODO, not yet implemented):

```python
# pseudo:
state    = configJson_state()          # session 0x09 → WheelDashboardState
slot     = read_28_00()                # u8 from group 0x40 reply
active   = state.configJsonList[slot]  # e.g. "Core"
```

Plugin currently sends both `28:00` and `28:01` reads during `SendChannelConfig` (`TelemetrySender.cs:3866-3867`) and the response bytes appear raw in the Diagnostics tab as `wheel 28:xx raw:`. Surfacing `WheelDashboardState.ActiveDashboardName` would close the host/wheel desync that currently triggers the `Tier-def has N/24 unbound channels` warning + auto kind=4 re-emit ("Catalog re-sync probe") when the user's saved profile doesn't match what the wheel is actually rendering.

## `0x0E` boot burst — implication for port detection

The wheel emits a brief burst of `0x0E` debug-log frames (group `0x0E`, dev `0x21`, severity `0x05`, ASCII text) on cold connect — sensor readings, pedal calibration state, output-mode settings — before settling to the ~0.5 Hz steady-state cadence already documented in [`../heartbeat.md`](../heartbeat.md). The probe response to a base-identity read can arrive interleaved with this burst, not before it.

Pre-2026-05-10, `MozaSerialConnection`'s cached-port validation captured the **first** received frame's group byte (`_firstRxGroup`, set under `CompareExchange(…, 0)` so subsequent frames couldn't update it) and rejected the port if it wasn't the expected response group. On a wheel where the boot burst landed first, this rejected the valid port even though the probe reply arrived a few ms later in the same window. Patch: the read loop now records every response group it sees in `_rxGroupsSeen` and the validator scans that bitmap, with a 1500 ms window and probe-re-emit every 250 ms. Same fix extended to `ProbeMozaDeviceCore` (500 ms budget, 200 ms re-emit, 25 ms slice).

This is a host-side process issue, not a protocol ambiguity, but the protocol-doc note in [`../heartbeat.md`](../heartbeat.md) records the boot-burst behaviour so future host implementations don't repeat the bug.
