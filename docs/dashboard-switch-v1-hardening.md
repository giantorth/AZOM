# Dashboard-switch hardening plan — v1 only

## Context

The v1 telemetry pipeline (`Telemetry/TelemetrySender.cs`) handles dashboard
switching with a **Stop+Start cycle** after emitting an FF kind=4 frame.
This baseline (commit `ff5e987`, "guard dash switch during cooldown") is
the working reference — switches succeed reliably in real-world use.

A prior attempt to layer echo-retry / sess02-engagement gating / optimistic
dropdown-revert on top of this baseline made things actively worse (see
`/home/rorth/.claude/plans/the-current-uncommitted-changes-linear-sparkle.md`
§"Why the current attempt is broken" for the full post-mortem). That
attempt has been reverted; the simpler baseline restored.

The v2 telemetry host (`Telemetry2/`) implements a different model
(in-place renegotiate via `ApplySubscription(force:true)`, no Stop+Start,
event-driven SwitchState machine). **v2 does not actually work** — it's
slated for removal. Its design is **not** load-bearing inspiration; the
hardening here keeps v1's working protocol mechanism intact and only
adds robustness around it.

## Goals

1. **Don't touch what works**: the FF kind=4 emit + Stop+Start cycle is
   the proven path. Keep the protocol mechanism unchanged.
2. **Better feedback**: user should see *something* happen between
   clicking the dropdown and the wheel switching. The 10-second
   `MinSilenceAfterStopMs` window is currently silent.
3. **Wheel↔UI sync**: dropdown should always reflect what the wheel is
   actually showing — including wheel-knob-driven changes. But
   `Settings.TelemetryProfileName` should only change when the user
   interacts with the SimHub dropdown.
4. **Recovery from stuck switches**: today, if the post-Start preamble
   never reaches Active for whatever reason, the dropdown stays
   disabled forever (or until full reconnect). Add a bounded timeout.
5. **Observability**: surface enough state in the diagnostics panel
   that a future failure mode can be characterised from a single
   diag-bundle, not theory-shopping.

## Non-goals

- **No in-place renegotiate**. The plan-agent's research claimed the
  wheel only needs `ApplySubscription(force:true)` after a kind=4,
  citing `docs/protocol/findings/2026-04-30-dashboard-switch-3f27.md`.
  But v2 implements exactly that and doesn't work end-to-end. The gap
  between "the protocol allows it" and "we have a working
  implementation of it" is real. Don't bet on it without evidence.
- **No echo-confirmed retry burst**. The prior attempt's
  `EmitDashboardSwitchBurst` (up to 3× kind=4 emits) and
  `_pendingKind4Armed` latch were both root causes of the bugs we
  just removed. The wheel reliably echoes the first kind=4 in healthy
  cycles; transport-level loss is already handled by
  `SessionRetransmitter`.
- **No optimistic dropdown revert / name-lookup**. Replaced by
  "dropdown reflects wheel state, settings reflect user intent".
- **No sess02-engagement latch**. The CS-Pro symptom
  (`_session02EngagedSinceStart` never flipped true on second
  Stop+Start) is gone the moment we drop the latch.

## Working baseline (what stays)

| Element | Where | Behaviour |
|---|---|---|
| `SendDashboardSwitch(uint slot)` | `Telemetry/TelemetrySender.cs:1240` | Emits FF kind=4 on session 0x02; guards against `_state != Active` and `IsInSilenceCooldown` |
| `RestartForSwitch()` | `Telemetry/TelemetrySender.cs:1347` | Sleeps `PreStopDrainMs` → Stop+Start; `StartInner` waits `MinSilenceAfterStopMs` (10 s) for wheel sess=0x09 timeout |
| `IsInSilenceCooldown` | `Telemetry/TelemetrySender.cs:1271` | Public read; UI disables switch controls while true |
| `SwitchToProfile(uint, MultiStreamProfile?)` | `Telemetry/TelemetrySender.cs:1323` | Auto-test entry point; calls `SendDashboardSwitch` + `RestartForSwitch` |
| UI dropdown handler | `Devices/MozaWheelSettingsControl.xaml.cs:1003` | Writes `Settings.TelemetryProfileName` eagerly, calls `SendDashboardSwitch` + `_plugin.OnDashboardSwitched()` |
| `_plugin.OnDashboardSwitched()` | `MozaPlugin.cs:1767` | `ApplyTelemetrySettings` + `sender.RestartForSwitch()` |

All of the above stay. The hardening adds around them, not into them.

## Observed/anticipated failure modes

| Failure | What user sees | What's actually happening |
|---|---|---|
| Stuck-disabled dropdown post-cooldown | Dropdown stays greyed > 15 s | Post-Start preamble didn't reach Active; `IsInSilenceCooldown` is false but pipeline state is wrong |
| Silent silence | Dropdown disabled for 10 s, no indication | UI shows "Sending — N frames sent" or generic status while the switch is mid-Stop+Start |
| Wheel-side knob switch ignored | User presses physical knob, plugin dropdown stays on old name | `WheelActiveSlot` event isn't wired anywhere in UI today |
| Settings drift across plugin restart | Plugin opens, wheel on slot=2, dropdown shows slot=0 (saved profile) | No startup-time reconciliation between saved profile and wheel's reported slot |
| Auto-test corrupts state | Auto-test fires kind=4 during preamble or cooldown | `SendDashboardSwitch` guard returns early but auto-test doesn't notice |
| Rapid double-click race | User clicks Grids, then Mono in <1 s | Both `SendDashboardSwitch` calls suppressed by the `_state != Active` guard during the in-flight Stop+Start; only Grids's `Settings.TelemetryProfileName` write persists, Mono never reaches the wheel |

The CS-Pro 2026-05-10 diag bundle symptom (dropdown latched off after
2nd switch) was specifically caused by the
`_session02EngagedSinceStart` latch in the reverted attempt — it
disappears with that latch gone. Reproduce on the baseline before
investing in fixes targeted at it.

## Proposed changes

The hardening is four small, independent improvements. Each could ship
on its own; together they form a coherent UX improvement.

### 1. Status label feedback during switch

**File**: `Devices/MozaWheelSettingsControl.xaml.cs`

Today `TelemetryStatusLabel` cycles between "Disabled" / "Test pattern
— N frames sent" / "Sending — N frames sent". During a switch the user
sees the same "Sending" text while the dropdown is greyed for 10 s, with
no indication what's happening.

Change `TelemetryProfileCombo_SelectionChanged` (the wheel-reported
branch only) to:

1. Capture the target name before kicking off the switch.
2. Set a `_switchInFlight` flag + `_switchTargetName` field.
3. Subscribe (once, in `EnsureSubscribedToSender`) to a new
   `TelemetrySender.StateChanged` event (see §4 below). When state
   transitions out of post-cooldown back into Active, clear the flag.

In `RefreshTelemetryStatus`, when `_switchInFlight == true`, override
the label to `$"Switching to {_switchTargetName}…"`. After clear,
fall through to existing logic. If the flag is set for > ~15 s without
clearing, override to `"Switch failed — try again"` and clear the flag.

**Scope**: ~40 lines. No protocol changes. No new state machine. Pure
UI surface.

### 2. Wheel-side slot tracking + dropdown sync

**Files**: `Telemetry/TelemetrySender.cs`, `Devices/MozaWheelSettingsControl.xaml.cs`

The reverted attempt added `_wheelActiveSlot`, `WheelActiveSlot`, and
`WheelActiveSlotChanged` — those parts were **correct**. Re-introduce
them, but without the gating logic that latched onto them.

**TelemetrySender.cs**:

- Add `private volatile int _wheelActiveSlot = -1;` field.
- Add `public int? WheelActiveSlot { get { var v = _wheelActiveSlot; return v < 0 ? (int?)null : v; } }`.
- Add `public event Action<uint>? WheelActiveSlotChanged;`.
- Re-introduce `TryDetectKind4Echo(byte[] payload, out uint slot)` (the
  static helper from the reverted attempt — it correctly handles both
  the 13-byte b2h form AND the FF-wrapped form).
- In the session-02 inbound chunk path (the chunk-handling branch
  around `Telemetry/TelemetrySender.cs:2615`), after the existing
  catalog-CRC handling, call `TryDetectKind4Echo` on the payload. On
  hit: update `_wheelActiveSlot` (Interlocked.Exchange), fire
  `WheelActiveSlotChanged` if the slot changed.

**No `_pendingKind4Armed` flag, no `DashboardSwitchEchoCompleted`
event, no Stop+Start scheduling from the RX thread.** Just slot
tracking.

**MozaWheelSettingsControl.xaml.cs**:

- In `EnsureSubscribedToSender` (the same lazy-subscribe pattern used
  for §1's `StateChanged`), wire `WheelActiveSlotChanged +=
  OnWheelActiveSlotChanged`. Unsubscribe on `Unloaded` / pipeline
  swap.
- `OnWheelActiveSlotChanged(uint slot)`: marshal to dispatcher.
  - If `_switchInFlight && _switchTargetName == wheel-reports-this-slot-name`:
    we've successfully switched to the user's target. Clear
    `_switchInFlight`.
  - Look up `state.ConfigJsonList[(int)slot]` → name. Find that name
    in `TelemetryProfileCombo.Items`. If found and `SelectedIndex !=
    that index`: sync the dropdown (with `_suppressEvents = true`).
    Do NOT write `Settings.TelemetryProfileName` — wheel-side knob
    presses don't change saved profile.
- On `Loaded` / first wire-up, if `WheelActiveSlot` already has a
  value (we wired up after the first kind=4 arrived), apply it
  immediately so the dropdown reflects the wheel before any user
  action.

**Scope**: ~100 lines including the helper. Re-uses the working v1
detection logic that was already correct in the reverted attempt; just
strips the gating misuse.

### 3. Watchdog timeout for stuck switches

**File**: `Telemetry/TelemetrySender.cs`

Today, if `RestartForSwitch` returns but the post-Start preamble never
reaches Active (wheel doesn't respond on session 0x09, hub
re-enumeration mid-cycle, etc.), the user has no recovery path short
of full reconnect.

Add a watchdog timestamp + tick check:

- Add `private long _switchStartedUtcTicks;` — set in
  `RestartForSwitch` before `Stop()`.
- Add `private const int SwitchWatchdogMs = 15000;` — 50 % longer than
  `MinSilenceAfterStopMs` so a healthy switch never trips it.
- In `OnTimerElapsedInner`, **only during preamble**, check if
  `(now - _switchStartedUtcTicks) > SwitchWatchdogMs` AND we're still
  not yet Active. If so: log a warn, fire a new
  `SwitchWatchdogExpired` event, and reset
  `_switchStartedUtcTicks = 0` so we don't fire repeatedly.

The watchdog doesn't *fix* anything on its own — it gives the UI a
signal to surface (per §1's "Switch failed — try again" path) and
gives us a hook to add automatic recovery later if a class of failure
proves systematic.

**Scope**: ~30 lines. No behaviour change unless a switch actually
hangs.

### 4. `StateChanged` event for clean UI subscription

**File**: `Telemetry/TelemetrySender.cs`

§1 and §3 both want notification when the pipeline enters or leaves
Active. Today the UI polls `Enabled` / `IsInSilenceCooldown` at 500 ms
via `_refreshTimer`. That works but is laggy and forces the UI to
poll-and-derive instead of reacting to events.

Add `public event Action<TelemetryState, TelemetryState>? StateChanged;`
(args: prev, next). Fire from `TransitionTo` after the `_state` field
write, before the log call. Wrap in `try/catch` so a buggy subscriber
can't poison the state machine.

UI subscribers (the new `_switchInFlight` clear-on-Active and the
watchdog UI handler) attach via the same `EnsureSubscribedToSender`
lazy pattern.

**Scope**: ~15 lines.

### 5. Startup reconciliation

**File**: `MozaPlugin.cs` (subscribe to existing
`WheelActiveSlotChanged` event from §2, no new event needed)

When the plugin starts and connects:
- The wheel pushes its current slot kind=4 within ~8 ms of session
  0x02 opening.
- §2's `WheelActiveSlotChanged` fires → UI dropdown syncs to wheel's
  slot.
- But `Settings.TelemetryProfileName` may not match — user's saved
  intent was "Grids", wheel boots to "Core".

In `MozaPlugin`, subscribe to `_telemetrySender.WheelActiveSlotChanged`
once on init. Handler logic:
- Only act on the FIRST kind=4 per session (track with a `bool
  _startupReconciled` flag; reset on Stop/Start).
- If `Settings.TelemetryProfileName` is non-empty AND maps to a slot
  in `state.ConfigJsonList` AND that slot != wheel-reported slot:
  call `OnDashboardSwitched((uint)savedSlot)` to issue the
  startup-restore switch.
- Otherwise: no-op. Wheel's state is fine.

**Important**: only fires on the *first* kind=4 after connect. Later
kind=4 frames (from host-initiated switches or wheel-knob presses)
must NOT re-trigger startup reconciliation — that would create a
loop with §2's dropdown sync.

**Scope**: ~30 lines.

## Out of scope (deferred — needs evidence)

### Skipping Stop+Start (in-place renegotiate)

The plan agent's earlier proposal claimed `ApplySubscription(force:true)`
could replace the entire Stop+Start cycle, citing the protocol findings
doc. **v2 implements this and doesn't work.** Before betting on it
again, we'd need:

1. A working capture of PitHouse switching dashboards **without** any
   session 01/02/03 close+open burst between kind=4 and the new
   tier-def. The findings doc claims this exists but I haven't
   verified it firsthand.
2. A minimal v2-style implementation tested against real hardware
   across at least 3 wheel models, including the firmware that
   shows the CS-Pro symptom.
3. A clear understanding of WHICH state Stop+Start resets that v2's
   in-place renegotiate skips — and whether the wheel actually tolerates
   not resetting those pieces. The plan agent claimed
   `_tierDefPreambleSent` and `_nextFlagBase` "shouldn't be reset"
   for a switch; if that's wrong, the wheel will silently drop the
   new tier-def.

This deserves its own investigation phase, not a redesign-by-theory.

### Echo-confirmed retries

The prior attempt's burst-emit + late-ack handling was the source of
multiple bugs (`_pendingKind4Armed` latching, RX-thread Stop+Start
race). The baseline's "fire once, Stop+Start, let preamble re-engage"
works in practice. Don't reintroduce echo-retry without a captured
failure mode that calls for it.

### Operation queue / supersede semantics

Rapid double-click is rare in practice; today the second click is
suppressed silently by the cooldown gate and the user clicks again
post-cooldown. §1's status label makes this visible. A formal
supersede mechanism is a feature, not a bug fix; defer.

## Files touched

- `Telemetry/TelemetrySender.cs` — add `_wheelActiveSlot`,
  `WheelActiveSlot`, `WheelActiveSlotChanged`, `TryDetectKind4Echo`,
  `StateChanged`, `_switchStartedUtcTicks`, `SwitchWatchdogExpired`,
  RX hook for kind=4 in session-02 chunk path, watchdog tick check.
  No removals, no Stop+Start changes.
- `Devices/MozaWheelSettingsControl.xaml.cs` — `_switchInFlight`,
  `_switchTargetName`, `EnsureSubscribedToSender` helper, dropdown
  sync handler, watchdog handler, status label override.
- `MozaPlugin.cs` — startup reconciliation handler.

Total ≈ 250 lines net new, no removals. Buildable and shippable
incrementally (each numbered section in §"Proposed changes" can ship
alone, in any order).

## Verification

For each shipped section, run all of:

1. **§1 status label**: click dashboard, watch status label —
   should show "Switching to X…" within one refresh tick (500 ms),
   clear to "Sending — N frames sent" when wheel actually engages
   post-restart. Trip the watchdog by disabling the wheel during the
   cooldown window; status should flip to "Switch failed".
2. **§2 wheel-side sync**: dashboard is "Core" in dropdown, press
   physical knob to switch to "Pulse" — dropdown should sync to
   "Pulse" within ~50 ms of the kind=4 arriving. Check
   `Settings.TelemetryProfileName` did NOT change. Reload plugin —
   dropdown still says "Core" (saved profile honoured); wheel may
   say "Pulse"; the startup reconciliation (§5) issues a
   host-initiated switch back to "Core".
3. **§3 watchdog**: simulate stuck preamble (pull USB during the
   silence cooldown) — within 15 s the watchdog fires and the
   status label flips to "Switch failed". Plugin doesn't otherwise
   crash or get stuck.
4. **§4 StateChanged**: log every fire of `StateChanged` during a
   healthy switch cycle. Expect: Active → Idle (from `Stop()` in
   `RestartForSwitch`) → Starting → Preamble → Active. Five fires
   per switch. UI handlers run on the StateChanged thread; verify
   no XAML thread-affinity exceptions.
5. **§5 reconciliation**: with `Settings.TelemetryProfileName =
   "Pulse"` and wheel cold-booted to "Core", start plugin. Expect:
   wheel pushes kind=4(0) → dropdown briefly shows "Core" → log line
   "[Moza] Startup: wheel on slot=0 but saved 'Pulse' maps to slot=2
   — issuing host-side switch" → switch runs → wheel ends on
   "Pulse" → dropdown reflects "Pulse". `Settings.TelemetryProfileName`
   unchanged from "Pulse" throughout.

Wire-trace each scenario with `tools/trace-summary` and compare
against the baseline (current `dev` head): no new sessions opened,
no new kind=4 emissions per switch cycle, tier-def content identical.
Any divergence means an inadvertent protocol-level change snuck in.

## Implementation order

If shipping incrementally, do them in this order — each unblocks the next:

1. **§4 `StateChanged` event** first — it's load-bearing for §1 and §3
   but harmless on its own.
2. **§2 wheel-side slot tracking** — independent value, no other
   sections depend on it for correctness. Highest user-visible win.
3. **§1 status label** — depends on §4.
4. **§3 watchdog** — depends on §4.
5. **§5 startup reconciliation** — depends on §2. Lowest priority;
   most failure modes resolve themselves on first user interaction.

Bundling sections 1–5 into one commit is also fine — total diff is
small. Splitting is appropriate if any section turns out trickier
than expected.
