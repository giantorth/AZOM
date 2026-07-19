# Changelog

All notable changes to the AZOM MOZA SimHub plugin are documented here.

## [1.5.1] - 2026-07-18

### Added

- **Wheel product images** — device definitions now deploy a matching wheel render, so each
  MOZA device shows its own picture in SimHub's Devices list instead of a generic placeholder.
  Covers ES, FSR V1, FSR V2, KS, KS Pro, CS Pro, TSW, Vision GS, CS, CS V2.1, GS, GS V2P, RS, 
  RS V2, CM1, CM2 and Lamborghini Revuelto.  Art is also added up on already-deployed 
  definitions at startup as needed.
- **Next/previous-dashboard actions now work on FSR1 and CM1.** These displays don't speak
  the same protocol, so the actions previously did nothing; they now cycle the FSR V1's
  fixed pages and the CM1's 13, wrapping around.
- **FSR1 default bindings for previously dead fields** — fuel remaining laps, ERS deploy left,
  ERS harvested, fuel mix (was mislabelled "Fuel class"), and session time left.
- **mBooster Traction Control, Wheel Spin, and Gear Shift** — three new pedal-motor effects
  alongside the existing five. Traction Control mirrors ABS's pulse, driven by the game's TC flag;
  Wheel Spin is its acceleration-side counterpart, triggered by a wheel-slip heuristic instead of
  the game's own TC decision; Gear Shift fires a short pulse on every gear change, with its own
  Vibrate on Neutral and Debounce controls, mirroring the wheelbase's own gearshift bump. All
  three share Engine Vibration's wire slot rather than an unconfirmed effect type of their own.
- **mBooster pedal rows are now renameable** — each connected pedal in the mBooster tab's device
  list gets an editable display name (saved per profile), useful for telling pedals apart on a
  multi-unit rig.
- **"Redeploy all definitions" button** (Options tab) — force-rewrites every SimHub device
  definition. Mostly useful for demos.
- **"Show all tabs" option** (Options tab, advanced) — reveals every plugin tab regardless of
  hardware detection, useful for demos.
- **HGP and SGP shifters now get their own tabs** instead of sharing one, so both can be
  configured independently. *(A base/hub-relayed HGP's identity discriminator isn't confirmed yet,
  so its tab may not appear on that topology until that lands — a standalone-USB HGP and the SGP
  on either topology are unaffected.)*

### Changed

- **Wheel LED writes are paced again instead of streamed.** All live wheel LED traffic (RPM,
  button, and knob — colours and bitmasks) moved off the stream lanes back onto the paced FIFO.
- **LED keepalive shortened to 0.75 s** (from 1.0 s) — the firmware drops live-LED ownership
  1000 ms after the last feed, so scheduling at exactly 1.0 s always arrived late.
- **FSR1 tyre/status record is now a background cache.** Record `0x0d` is no longer a selectable
  page; it's appended as a secondary record to the pages that need it and sent at ~0.5 Hz, so
  tyre temps and pressures, track/air temp, car count, and the "/total" on Position and Lap now
  render on pages whose primary record doesn't carry them — without slowing the primary's
  refresh. These rows are marked `(cache)` in the channel mapper.
- **mBooster settings UI reorganized.** A single pedal-row list at the top of the mBooster tab
  (one row per connected pedal, with its Role and — for the selected row — Display Name inline)
  replaces the old trio of a device dropdown, a per-axis Pedal Roles panel, and a separate
  "configure pedal" dropdown. Pedal Feel and Sim Input Mapping are now one combined, collapsible
  card. The raw wire-level Calibration card is hidden by default (still experimental).
- **mBooster Engine Vibration frequency is telemetry-derived again**, not a fixed user slider —
  it now follows the same parametric model as the AB9 shifter's engine-vibration effect (frequency
  scales with RPM toward redline), so only Intensity remains a user control.
- **mBooster Pedal Trace is now a combined overlay.** The sparkline in the mBooster tab plots
  Throttle/Brake/Clutch simultaneously instead of just the selected device, and the Pedals tab
  gained its own live position graph.

### Fixed

- **Windows 11 timer resolution — smoother LED animations and correct display cadence.** An
  inverted power-throttling flag told Windows to *ignore* the plugin's 1 ms timer request and pin
  every timer to the default 15.625 ms grid, so a 20 ms worker actually fired at 31.25 ms and
  50 Hz loops ran at ~32 Hz. 
- **Button/knob colour ordering** — a colour could land after the bitmask that lights it, flipping
  a group out of sync with the RPM strip; each colour now precedes its bitmask.
- **ES wheel page simplified to a single tab-less page.** Updated settings page to better reflect
  the capabilities of this wheel.
- **mBooster routing on chained / multi-unit setups.** Effects and hardware calibration
  (Direction/Min/Max, output curve, travel, endstops, sensor ratio, max threshold) were addressed
  by HID axis index, which doesn't necessarily match the physical chain order — on a multi-pedal
  rig this could send a setting or an effect's vibration to the wrong physical pedal. Each device's
  role (Throttle/Brake/Clutch) is now resolved automatically from its own calibration read-back and
  used for addressing, falling back to the old axis-index routing whenever it can't be resolved.
- **mBooster hardware calibration now re-applies on a profile switch.** It previously only pushed
  on (re)connect or the manual "Apply Cal" button, so switching SimHub profiles with the device
  still connected silently left the *previous* profile's calibration live on the hardware.
- **mBooster Gear Shift pulses could be missed** if the effect worker's own ~20 ms timer sampled
  slower than a gear change came in — the one-tick change flag was replaced with a monotonic
  counter each worker tracks independently, so a shift can no longer land between samples.
- **Selected mBooster pedal no longer jumps to a different device** a couple of seconds after
  opening settings — once a standalone unit's real wired axis resolves, the selection now follows
  the same physical device onto the correct axis instead of falling back to the first device in
  the list.
- **mBooster Threshold effect no longer fires continuously** instead of only on a threshold cross.

## [1.5.0] - 2026-07-12

mBooster custom-effect and engine-vibration work contributed by
[@tacodevhaydz](https://github.com/tacodevhaydz).

### Added

- **Wheelbase LFE (Low-Frequency Effects) — host-rendered base haptics.**
  Requires base firmware **1.2.10.10+**. Three channels — Engine (continuous),
  ABS, and Gearshift — are computed every frame and streamed to the base at 50 Hz.
  - Each channel's **Trigger / Frequency / Intensity / Smoothness** can be a static
    slider *or* a live NCalc / SimHub-property formula evaluated per tick.
  - **Presets** — four presets (Additive Engine, Big Rig, Detuned V8,
    Road Rumble) plus save / export / import of your own (JSON). The built-in
    engine presets scale intensity by throttle position.
  - Dedicated **LFE panel** with a live oscilloscope drawing each slot's amplitude
    envelope and calculated-value readouts. LFE settings ride along in profile import.
- **MOZA Multi-Function Stalks — Truck-sim mode.** The stalks work as a plain button
  box, or translate stalk positions into keyboard input for ETS2/ATS (only while the
  truck game is foreground): wiper and light-knob positions step the game's cycling
  controls to the mapped stage, turn-signal positions tap the indicators, plus a
  "Re-sync wipers" action.
- **mBooster custom telemetry effects** (experimental) — user-created, formula-driven
  vibration effects on the pedal motor. Each has a name, a live SimHub-property / NCalc
  formula, Frequency and Intensity, threshold-pulse or continuous-proportional modes,
  and a sustained Test toggle.
- **Multiple mBoosters in any topology** — one controller per physical unit keyed by a
  stable USB instance ID (settings survive replug), correct HID + CDC interface pairing,
  and up to three axes per unit routed independently to Throttle / Brake / Clutch by
  per-axis role.
- **Nearly complete FSR1 dashboard support** — every built-in dashboard field ships a
  best-guess default SimHub binding (all overridable); corrected DRS/ERS bit-packing and
  gear bias, plus lap-time and tyre-temp scaling fixes (capture-verified).
- **HGP / SGP shifter support** — reverse-direction toggle and paddle-sync on both;
  SGP adds two configurable LEDs (8-color palette + brightness), HGP adds an H-pattern
  calibration routine.
- **Lamborghini Revuelto (W11) wheel** — a screenless, button-only wheel (16 dimming
  backlit buttons, no RPM LEDs). Added general **non-RPM-LED wheel support**: no phantom
  RPM/flag LED run for button-only wheels, and the max addressable button-LED count
  raised to 16.
- **VGS rotation-mode selector** — off / smooth / immediate for the VGS wheel's
  self-leveling display (per-profile).
- **Mzpreset file import** — Supports importing presets using the new mzprest file format.

### Changed

- **Performance** — hot-path allocations cut (catalog-hash dedup, radar reflection caches,
  gated wire debug, retransmitter fast path, V0/string-channel throttles); radar/track-map
  car positions computed once per game frame and shared across dual-display senders;
  per-worker NCalc engine instances for haptics formulas.

### Fixed

- **Knob mode is correctly applied on profile change** instead of only applying at launch.
- **Pedal/handbrake max travel zeroed for new users** — fixed a race that could set the
  calibrated max travel to zero.
- **CM2 flag LEDs** — the LED bitmask no longer incorrectly blocks flag led activation.
- **Variable-size color packets** — button color data is sent without the previous fixed
  padding, caused issues for some wheels.
- **FSR1 display** now restarts correctly on reload.
- **mBooster pedals** stay in the correct order.
- **Concurrency races** — Interlocked 64-bit stamps, copy-on-write settings dictionaries,
  and timer/thread teardown guards.
- **Lifecycle leaks** — CM1 handler detach, update-banner rehook, park-retry timer dispose,
  and LED-driver restore on plugin End.

## [1.4.0] - 2026-07-05

mBooster pedal feel and effects work contributed by [@tacodevhaydz](https://github.com/tacodevhaydz).

### Added

- **mBooster pedals — expanded into a full pedal-feel and haptic-effects system.**
  The mBooster tab (per-unit Throttle / Brake / Clutch roles, multiple units
  supported) gained:
  - **Effects** — five cards, each with a live **Test** toggle that substitutes
    pedal position so you can preview by pressing:
    - **ABS** — pulses on ABS activation (Frequency 5–30 Hz, Intensity, Smoothness).
    - **Engine Vibration** — continuous above idle at a fixed Frequency (60–200 Hz)
      and Intensity (replaces the previous RPM-derived mapping).
    - **Road Texture** (new effect type) — road-surface vibration scaled live by
      vertical chassis G-force (roughness proxy), with a firmware-shaped Smoothness control.
    - **Lockup** — ramps in on wheel lock under braking (Frequency now a fixed slider).
    - **Threshold** — pulsed braking-threshold envelope (Trigger Input Level,
      Frequency, Intensity, Vibration Decay).
  - **Pedal Feel** — hardware calibration: dual-thumb Start/End of Travel slider, 
    Front/End End-Stop Stiffness, plus host-side Deadzone (kg), Max Force (kg), and 
    a 5-point input curve.
  - **Sim Input Mapping** — Sensor Output Ratio, Max Threshold, and an output 
    curve whose nodes can be dragged horizontally.
  - **Brake Fade (experimental)** — as brake temperature rises past a configurable
    onset, dynamically rewrites two real calibrations in lockstep (longer Travel End, 
    higher Max Threshold), then restores your configured values as the brakes cool.
  - **Pedal Trace** sparkline (last 5 s) and a live position dot on both curve splines.
  - New reusable UI controls: `MozaRangeSlider` (dual-thumb) and an extended
    `MozaCurveEditor` (draggable spline, Linear/S-Curve/Exponential/Parabolic
    presets, horizontal-drag mode).
- **CM2 Racing Dash — new-era firmware support.** Recognizes two CM2 LED firmware
  eras (legacy RPM-ramp vs. 2026-06 indicator) with bidirectional detection so a
  firmware downgrade recovers.
- **Radar and track-map dashboard channels.** Closes issue #79.
  - **Track map** — every car's position on a mini circuit map
    (`patch/Location_0..63`); please report issues where it does not work as expected.
  - **Radar** — close-proximity spotter data channels(`patch/ri0..63`).
- **FSR1 mapping features** — dashboard fields can be merged, split, or sub-byte / bit-packed (two values
  sharing a byte) with independent bit-offset/width steppers. Addresses #32.
- **Automatic standby mode** — optionally powers the wheel/display to standby after a
  configurable idle timeout (default 10 min), gated on no active game and no HID/UI activity.
- **Host sleep/resume recovery** — hooks `SystemEvents.PowerModeChanged` and forces a
  clean reconnect of the wheel and USB CM2 on resume, rebuilding sessions the firmware
  silently drops behind a half-open serial port (fixes blank display after host sleep).
- **LED master-brightness follows the firmware level** — the brightness slider now
  writes the firmware group brightness (debounced), with per-frame RGB compensated so
  it isn't applied twice.
- **Support adjustable FFB Output sliders** — The FFB output sliders can be dragged left 
  and right supporting full functionality.

### Changed

- **Bundled SimHub updated to v9.11.21** (from v9.11.17).
- **Notification banners** in device pages.  Banner notices now appear in both locations.
- **Background responsiveness** — new `ProcessResponsivenessManager` opts the process
  out of Windows EcoQoS power throttling and the background timer-resolution clamp
  while a game is active, so control writes land live instead of only after alt-tab.
- **Dual-display routing** — a dedicated CM2 sender drives the CM2 regardless of the
  connected wheel; the main sender is now always wheel-only, so the two can't collide
  (CM2 wire target chosen by topology: `0x12` standalone-USB, `0x14` bus-bridged).
- **Session lifecycle** — cold start now performs a narrow `0x01–0x03` session close
  first; catalog-advertise bursts are debounced into a single tier-def emit.
- **FSR1 display refresh** raised from ~29 Hz (35 ms) to ~50 Hz (20 ms), matching expected
  active-play cadence; byte-probe now ramps 0→255→0 so every byte box visibly pulses.
- **FFB parameters are only written when changed** — the applier diffs against a
  per-base cache instead of re-pushing on every hot-attach.
- **Radar/track-map channels are hidden from the channel-mapper UI** (plugin-driven).
- CM1-vs-CM2 identification is now positive-evidence only (removed the 25 s
  no-catalog timeout that mislabeled slow CM2s); CM1 detection is no longer persisted.
- Serial capture moved to the Options tab; About tab updated; removed refresh button.

### Fixed

- **AB9 shifter mode selector** — flight-sim/shifter mode values were
  backwards.
- **UDP steering-lock ordering** — `base-limit` is now written before `base-max-angle`,
  so lowering the lock (e.g. RBR 2700° → per-car) lands in one shot instead of being
  silently clamped until the next write or alt-tab.
- **CM2 cold-start livelock** — a fresh `Start()` is only issued when genuinely idle,
  fixing CS-Pro + bus CM2 setups that started dark.
- **CM2 dash dropping on wheel-rim reset** — dash detection is re-asserted after a reset.
- **Cross-sender stall on a shared bus** — SilenceGate timestamps are per-instance, so
  a CM2 stop no longer stalls the wheel's reopen (~11 s).
- **DisplayWatchdog** no longer restarts a working radar/track-map dashboard on a late
  configJson state, and waits for the live dashboard list before retrying.
- **Dropped-catalog recovery** — re-advertised channel chunks union-fill missing
  indexes and preserve unchanged stable channels on a saturated 115200 link.
- **FSR1 over-throttling and gapless layout** — partitions are guaranteed gapless and
  non-overlapping (stale configs auto-repair) so the wheel never renders a dead byte.
- **FFB detect→reapply→reset loop** on marginal R5 / bare CS bases, via changed-only writes.
- **Fixed import of interpolation** — interpolation no longer imports at 10x the correct value.
- **LED fixes** — Wheel detection is no longer gated on the virtual LED driver; LED keepalive 
  no longer pauses mid-game; LED writes are throttled during catalog negotiation so they don't
  starve inbound radar/track-map channel chunks.


## [1.3.0] - 2026-06-17 — Ncalc, CM2 and ES work, improved channels

> **Breaking:** KS Pro users must reconfigure knob effects — 4 ghost buttons were
> removed, shifting the knob LED index by 4. Updated ATSR profiles are available in
> the [ATSR LED profiles guide](https://giant.orth.cc/guides/atsr-led-profiles/).

### Added

- **NCalc / JavaScript formula support** for dashboard channels (custom formulas).
- **Full ES wheel detection** (move your LED profile to the new device after updating).
- **FSR1 dashboard field editor**.
- **Complete CM2 Racing Dash support**.
- **Shifter/Flight mode selector** for the AB9.
- Interpolation setting; base restart button; paddle calibration button; improved calibration.
- Smarter notification banners; knob revert-to-stored-color / static-color-on-idle options (WIP).

### Changed

- LEDs now **auto-idle after 45 s** without effects (configurable timeout).
- Dashboard telemetry now defaults on for new users.
- Correct LED commands for CS wheels; removed unused parameter polling.

### Fixed

- Multiple percentage (%) data-type fixes on dashboards; timestamp and other patch channels.
- Channel list updates correctly from UI changes; custom channel bindings load on cold-start.
- Double USB hub detection for hub-connected setups; base/ambient LED keepalive.
- Wheel colors read on startup every time so LEDs reflect the correct state immediately.

## [1.2.2] - 2026-06-11 — AZOM

- Plugin renamed to **AZOM**; guides/website launched at <https://giant.orth.cc/>.

### Added

- Action to calibrate wheel center.
- 한국어 (Korean) language support.

### Changed

- **Breaking:** "Controls & Actions" mappings reset — re-bind any actions.
- Bumped SimHub v9.11.14 → v9.11.15; refactor/split code cleanup; removed invalid firmware-era setting.

### Fixed

- Better auto-recovery for an unresponsive display at first startup.
- Better handling for old-model wheels, with an in-plugin error notice; additional (incomplete) FSR1.

## [1.2.1] - 2026-06-09 — Better support for older wheels

### Changed

- No longer causes connection drops with older-model wheels.
- Better support for combined Hub + Base setups (wheel on either device).
- CM2 display support (telemetry LEDs may be incomplete); continued FSR1/CM1 progress.
- Updated German translations (thanks @NTenic-Hadrev).

### Fixed

- Only write changed settings to the wheel; more robust wheel keepalive.

## [1.2.0] - 2026-06-08 — More hardware, better dashboards

### Added

- Direct USB-attached pedals (CRP2) and handbrakes (standalone peripheral registry).
- CM2 support via broadcast addressing with correctly routed display sessions.
- Additional USB PIDs; ES(X) wheels in the Control Mapper; initial FSR1/CM1 display support.
- Independent lanes for multiple displays; actions to toggle dashboard and test mode.
- Norwegian (bokmål) translation (thanks @synjan).

### Changed

- Always use the wheel-advertised catalog (removed the manual dash-folder option).
- Gated reads from incapable wheels; throttled Control Mapper reflection and car-position updates.
- Display probe only fires once the wheel model resolves; bumped SimHub v9.11.13 → v9.11.14.

### Fixed

- Corrected compression/data types for tyre pressure & temp, air/track temp, brake temp, and more.
- String channels follow the correct session; timestamp handling; CS V2 fixes.
- Save performance output, pedal limits, and button color overrides to profile.
- Game-data channels map correctly on a late catalog update.
- *(Radar and Track Map data still in progress this release.)*

## [1.1.1] - 2026-06-02 — Hub Hotfix

### Added

- Bindable display-brightness actions; `WorkModeOff` software e-stop action.
- New AB9 engine-vibration and intensity methods; AB9 settings follow the active profile.

### Changed

- **Cerberus watchdog** — combined the multiple watchdogs into one master watchdog.

### Fixed

- Hub-only setups work again (regression from v1.1.0); missing translation strings and typos.
- Added Greek translation (thanks pugsang); updated French (thanks Fraustiz).

## [1.1.0] - 2026-06-01 — Better dashboards, streamlined updates

### Added

- Combined base + hub configurations (no simultaneous multiple wheels).
- Device axes exposed as SimHub properties; "Controls and Events" integration.
- Auto-update flow — notification banner that restarts SimHub and shows release notes.
- Initial CM2 device support; SDK server start/stop without quitting SimHub.

### Changed

- Converged recovery ladder (auto-retry after park, flap cap, screenless degraded state).
- Cold-start catalog recovery (gap-aware ack + bounded session re-request).
- Lock-free interlocked session watchdog; better multi-device HID reads; lower dashboard bandwidth.

### Fixed

- LED brightness slider; halved steering angle on profile import.
- Moved profile import to its own tab so it can't disappear on small displays.

## [1.0.0] - 2026-05-29 — first stable release (complete PitHouse replacement)

Covers nearly all MOZA sim hardware — wheelbases, wheels, dashboards, hubs, the AB9
active shifter, and the mBooster pedal. Staged through release candidates
**rc1** (2026-05-25), **rc2** (2026-05-25), and **rc3** (2026-05-28).

### Added

- Completely overhauled UI; multilingual (English, Deutsch, Español, Français,
  Italiano, Русский, Tiếng Việt, 简体中文).
- Profile import for wheelbase and pedal profiles.
- **Control Mapper** — add each MOZA wheel for custom layouts (requires SimHub's
  "Recognize Simcube/Fanatec as individual controllers").
- **360 Hz / LFE** support via optional SDK service (required for full iRacing support).
- Legacy UDP steering control (set/read steering angle; tested with RSF for RBR).
- **mBooster support** — multi-device aware, per-device role (Throttle/Brake/Clutch),
  settings persisted across reconnects.
- AB9 live engine-RPM shaker; gearshift bump through the wheelbase.
- Dashboard hot-switching without on-disk `.mzdash` files (channels negotiated directly).
- Reworked channel-mapping UX with live SimHub property search; automatic update notifications.
- Exposed `Moza.*` SimHub properties (BaseConnected, McuTemp, MosfetTemp, MotorTemp,
  BaseState, FfbStrength, MaxAngle).

### Fixed (across the RC cycle)

- Wheelbase connection and steering rotation persist across game switches.
- Wheel telemetry capability no longer re-evaluated on game switch; LED/mode per-profile.
- Gearshift vibration saved per profile; ACK priority lane for responsiveness under load.
- Numerous dashboard session-reliability fixes; wheel hotswap; SDK game-switch bugs.
- Corrected rejection of very small / very large packets.

## [0.9.2] - 2026-05-18

> **Breaking:** migrates to a new profile layout; downgrading requires reconfiguring settings.

### Added

- Wheel-initiated hot dashboard switching.
- Profile-system refactor + dashboard-switch state machine (no config bleed between wheels).
- AB9 H-pattern shifter support (device manager, frequency slider, per-profile config).
- Wheel-base LED support ("MOZA Wheel Base" device extension); improved test-signal generator.
- Shift-debounce / ignore-on-neutral options for gearshift vibration.

### Changed / Fixed

- Text/string dashboard channels with proper UTF-8; smarter already-on-dash detection.
- Case-insensitive dashboard-folder auto-detect; new device-detection method (ID-collision fix).
- Per-session catalog parser; fixed hub detection, in-game telemetry, and base LED padding.

## [0.9.1] - 2026-05-11 — Reliability, Channel Mapping, Gearshift Vibration

### Added

- Channel picker searches the full SimHub property list.
- Gearshift-vibration setting; wheel sleep color; base performance mode; button/knob mode selectors.

### Fixed

- Stable dashboard links (sequence locks, ACK retries, retransmit during preamble).
- Off-by-one CRC error in dashboard framing; knob colors (broken in 0.9.0); idle effect config.
- Knob keepalive cooldown to prevent excessive traffic when telemetry isn't flowing.

## [0.9.0] - 2026-05-09 — Switching Dashboards

> **Breaking:** AB9 detection is now opt-in (enable the toggle after upgrading);
> LED keepalive now defaults on.

### Added

- Firmware-era auto-detection (Era2024 / Era2025 / Era2026).
- Dashboard-folder library ("Set Folder…") + per-wheel folder auto-detect and mapping.
- Dashboard hot-reload and channel switching; display brightness (0–100) and standby controls.
- Per-LED knob ring colors for W17/W18 (up to 56 LEDs); knob telemetry on CSP/KSP.
- Remember last successful COM port.

### Changed

- `EraPolicy` abstraction centralizes all wire-protocol axis decisions; periodic frames cached.
- Comprehensive protocol documentation added.

### Fixed

- Cold-start closes sessions 01/02/03 before opening (fixes session-02 engagement failures).
- Debounced brightness slider; thread-safe per-wheel profile slots; CSP button/LED counts.

## [0.8.3] - 2026-04-26 — dashboards work for new users

- Fixed new users not getting display detection to fire; added debug logging/ZIP bundle.
- Redact serial numbers in logs and diagnostics; additional hub support.

## [0.8.2] - 2026-04-25 — Bugfixes, Hubs, AB9

- Addressed several memory leaks and logspam issues; changed hub-detection logic.
- Added prototype (incomplete) AB9 support.

## [0.8.1] - 2026-04-24

### Added

- Per-knob background + primary LED colors (KSP/CSP); RPM LEDs drive >16-LED wheels (KS/CS Pro 18).
- Per-button "Default during telemetry"; LED test/diagnostic panel moved to main settings.

### Fixed

- No longer hangs on shutdown when the wheel was unplugged first; serial numbers masked in diagnostics.

## [0.8.0] - 2026-04-23 — first dashboard support

First release that can drive the wheel's built-in dashboards (requires the matching
`.mzdash` file flashed on the wheel).

### Added

- Firmware dashboard upload path (session 0x04 file transfer: TLV paths, MD5, zlib).
- Session 0x09 configJson RPC client (2025-11 / 2026-04 schemas); device-initiated session opens.
- Wheel simulator harness for testing.

### Changed

- Heavy protocol overhaul; stronger session handshake; bumped SimHub to v9.11.11.

## [0.7.0] - 2026-04-20

### Added

- Universal Hub support; KS Pro (W18) support with 3/N/3 flag LEDs.
- Individual LED profiles in combined mode; idle RPM LED colors; color swatches; d-pad mode settings.
- Experimental LED diagnostic panel; CI dev-build pipeline publishing pre-release ZIPs.

### Fixed

- Frame-boundary/checksum collision (byte stuffing); wheel hotswap; session ACK routing by port.
- Startup crash when device not found; ES wheel detection; sleep/resume serial recovery.

## [0.6.14] - 2026-04-15

- UI shows wheel/paddle/pedals/handbrake positions; fixed a race condition.
- Migrated non-LED settings to the plugin side for per-game profiles; individual knob-mode config.

## [0.6.13] - 2026-04-15

- Added advanced telemetry options to test `.mzdash` uploads (full-flow replication).

## [0.6.10] – [0.6.12] - 2026-04-13 to 04-14

- Safer serial startup; telemetry flag-byte handling options; protocol default changes.

## [0.6.5] – [0.6.9] - 2026-04-12

- Early display-protocol reverse-engineering iterations: continued telemetry init, dynamic
  wheel-profile creation for unknown wheels, and assorted bugfixes while chasing display activation.

## [0.2.0] – [0.6.4] - 2026-04-04 to 04-11

- Initial development: wheelbase control and build pipeline, per-wheel profiles, first device
  definitions, RPM range settings, blink colors, and the first telemetry/dashboard init attempts.

[1.5.1]: https://github.com/giantorth/AZOM/compare/v1.5.0...v1.5.1
[1.5.0]: https://github.com/giantorth/AZOM/compare/v1.4.0...v1.5.0
[1.4.0]: https://github.com/giantorth/AZOM/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/giantorth/AZOM/compare/v1.2.2...v1.3.0
[1.2.2]: https://github.com/giantorth/AZOM/compare/v1.2.1...v1.2.2
[1.2.1]: https://github.com/giantorth/AZOM/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/giantorth/AZOM/compare/v1.1.1...v1.2.0
[1.1.1]: https://github.com/giantorth/AZOM/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/giantorth/AZOM/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/giantorth/AZOM/compare/v0.9.2...v1.0.0
[0.9.2]: https://github.com/giantorth/AZOM/compare/v0.9.1...v0.9.2
[0.9.1]: https://github.com/giantorth/AZOM/compare/v0.9.0...v0.9.1
[0.9.0]: https://github.com/giantorth/AZOM/compare/v0.8.3...v0.9.0
[0.8.3]: https://github.com/giantorth/AZOM/compare/v0.8.2...v0.8.3
[0.8.2]: https://github.com/giantorth/AZOM/compare/v0.8.1...v0.8.2
[0.8.1]: https://github.com/giantorth/AZOM/compare/v0.8.0...v0.8.1
[0.8.0]: https://github.com/giantorth/AZOM/compare/v0.7.0...v0.8.0
[0.7.0]: https://github.com/giantorth/AZOM/compare/v0.6.14...v0.7.0
[0.6.14]: https://github.com/giantorth/AZOM/compare/v0.6.13...v0.6.14
