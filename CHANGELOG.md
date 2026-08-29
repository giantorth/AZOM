# Changelog

All notable changes to the AZOM plugin are documented here.

## [1.6.0]

### Added

- **Master Channel Defaults editor.** One dialog (Dashboard tab → Master Defaults) sets the
  plugin-wide default mapping for every telemetry channel, with the same property picker and
  ƒ(x) formula editor as the per-dashboard list. Per-dashboard overrides still win.
- **Dashboard file management.** A new Files tab on the wheel and dash pages uploads a `.mzdash`
  to the device — from your dashboard folder or a picked file — and lists, enables and deletes
  the dashboards it already holds.
- **Wheelbase LEDs and LFE are now a single SimHub device** on SimHub 9.12+, shipping a set of
  ShakeIt default effects. A **Wheelbase LFE source** option (Options tab) chooses between the
  plugin's LFE tab and SimHub ShakeIt.
- **Wheelbase ambient LED controls** — per-LED colours for both strips, idle and sleep effects
  with their own animation speeds, and the sleep timeout. R16 Ultra strip length added.
- **Wheelbase product images** — the base now shows its own render in SimHub's Devices list.
- **Knob mode selector for wheels without knob LEDs.** Rims with rotary encoders but no LED
  rings now get the same BUTTON/KNOB selector, sized to the wheel's real encoder count.
- **Live wheelbase torque graph.** A selector on the Base tab switches the right-hand chart
  between the serial-traffic graph and live motor torque in Nm, scaled to the base's rated
  output so you can read headroom at a glance. Torque is sampled at 10 Hz while that graph is
  on screen and the poll stops when it isn't.
- **`AZOM.CurrentTorque` property** — live motor torque in Nm for dashboards and overlays,
  refreshed on the same status sweep as the temperatures.
- **Forza Horizon compatibility toggle** (Options tab → Game Compatibility) reads and sets the
  wheelbase's compatibility mode, including a value MOZA Pit House set.

### Fixed

- **Per-zone LED brightness sliders now work.** SimHub's "Brightness limiter and balance"
  sliders only scaled live colour frames, so a zone set to Static had no reachable dimmer at
  all; each zone (RPM / buttons / knobs) now writes its own firmware brightness register.
- **Knob rings no longer go dark when nothing is assigned to them.** The plugin claimed every
  ring as soon as SimHub offered the encoders channel and held them black; they now stay on the
  wheel's own colours until an effect actually lights them.
- **Switching the knob LEDs to Static no longer blacks them out.** It re-sent an unread palette
  over the wheel's colours and addressed ring LEDs by knob number; it now sends the palette you
  saved, addresses every ring LED, and reads the wheel's colours back when you have none.
- **A wheelbase that doesn't answer the firmware question is asked again** for ~25 s. That
  answer unlocks LFE haptics and the 10-band equalizer, and one missed reply used to leave both
  switched off until a restart.
- **Wheelbase identity is latched.** A rim swap or brief reconnect blanked the base model, which
  reverted a 6-LED base to the 9-LED wire layout mid-session — three LEDs dark and the bar spread
  over the wrong length.
- **Two wheel settings were tracked under the wrong command name** — RPM display mode and knob
  brightness — so the wheel's stored value never primed the plugin's cache. Settings are also
  verified with a read-back now.
- **Locked wheel identity and LED caches survive a plugin reload.**
- **The display watchdog no longer kills a live session.**
- **A base- or hub-relayed HGP is no longer reported as an SGP.** Both answer the same settings
  reads, so the model is now decided by the device-type reply.
- **A channel override no longer inherits the built-in scale.** The bundled scale is calibrated
  for its own property, so overriding one silently zeroed integer channels and saturated
  percentages; reverting an override restores the default property *and* scale.
- **The clutch axis reads again under Wine/Proton**, which renames it to a usage the HID reader
  didn't track.
- **FSR1 gap/delta no longer goes stale.** Pages whose record carries no gap slot showed whatever
  the firmware last cached; a small gap-bearing record is now interleaved to keep it fresh.
- **FSR1 damage gauges read green when undamaged** — the gauge leaves 0 unlit, so per-part damage
  is biased by one.
- **More FSR1 catalog and dashboard field corrections.**
- **Dashboard catalog chunks arriving out of order are reassembled** instead of leaving holes;
  keepalive and buffer cleanup fixed alongside.
- **Several dashboard uploads in a row now work**, and deleting a dashboard no longer leaves a
  gap in the device's list.
- **Linux/Proton — MOZA ports are found through sysfs** by VID/PID and opened through Wine's own
  COM mapping, which clears the resyncs and clustered chunk loss seen on the raw device path.
- **Linux/Proton — fixed a cold-start crash** when the hardware is first attached, and the log
  spam from device enumeration.

### Changed

- **Base tab header tidied.** Calibrate Center now sits directly under the steering arc it acts
  on, and the performance-output and graph selectors share one row instead of stacking.
- **The diagnostics report now includes the wheelbase itself.** A "Base identity" section reports
  the base model and firmware — including whether the firmware question was ever answered — plus
  whether LFE haptics and the 10-band equalizer are unlocked and which condition is failing.
- **The wheel firmware era override has been removed.** Auto mode is now the default.
- **Legacy USB Detection options have been removed.** AB9 / AB6 detection always runs, and 
  serial-probe fallback always remains an option.
- **Limit updates to wheel and Always resend bitmask have been removed.** Both are now always off.
- **The SDK tab is gone.** Its CoAP and UDP control toggles moved to the Options tab; request
  activity is now logged to the diagnostics bundle instead of an on-screen list.

## [1.5.7]

### Changed
- **Fixed antivirus false positive.** Windows Defender incorrectly flagged 1.5.6 as a virus. 
  The plugin was never unsafe — SimHub 9.12 LED compatibility built code on the fly at startup, 
  which Defender's heuristics mistake for malware. Now supports all versions without runtime 
  code generation.

## [1.5.6]

### Fixed

- **Now works with SimHub 9.12.0.** Changes in the LED device interface, stopped the plugin
  loading at all.  Backwards compataible with prior SimHub releases.
- **Fixed incorrect routing for some mBooster configurations**

### Changed

- **Bundled SimHub updated to v9.12.0** (from v9.11.22).

## [1.5.5]

### Added

- **Every wheelbase setting is now a SimHub property and a bindable action.** Damper, friction,
  inertia, spring, the four game-effect gains, soft limit, high-speed damping, road sensitivity,
  the FFB equalizer bands, the FFB curve nodes and the clutch split point join the handful that
  were already exposed — each with fine/coarse `Up`/`Down` actions, and `On`/`Off`/`Toggle` for
  the switches. Full list in the README.
- **mBooster G-Force (Inertial Pedal Feel)** *(experimental)* — moves the pedal under your foot in
  proportion to live longitudinal G instead of vibrating, with Max Pedal Travel and Response Speed.
- **mBooster Natural Friction and Segmented Damping** — two more hardware Pedal Feel settings: a
  friction slider, and a damping profile that splits pedal travel into three draggable segments
  with independent levels for pressed and released.
- **mBooster profile import picks a target pedal.** A PitHouse Pedals preset now shows which pedal
  role it carries and which attached mBooster will receive it.
- **PitHouse Pedals presets import onto CRP / CRP2 / SRP pedals** — previously only an mBooster
  could receive one.
- **FSR V1 display brightness** now follows the brightness slider and actions.

### Fixed

- **Track temperature, and the channels sharing its update group, now display.** Track/air/fuel
  temperature, brake temperatures and oil pressure were sent in a format this firmware rejects,
  taking down every channel on the same update rate with them — typically best lap and last lap.
- **Channels left over from a previously-shown dashboard no longer break a whole tier.** 
- **A pedal set plugged into the wheelbase no longer breaks wheel detection.** 
- **Wheelbase LFE haptics now work on bases that report their firmware differently.** 
- **Device questions that go unanswered are retried again.** 
- **An AB6/AB9 that drops off USB no longer goes silent.** The effects are now re-registered
  on every reconnect.
- **FSR V1 field corrections.** Tyre wear now reads as remaining rather than used, tyre pressures
  use the correct corner order, and the light-stage and TC-R gauges decode properly.
- **A lone mBooster on a chain-capable hub ignored its configured role.** 
- **Directly-attached pedals and handbrakes showed placeholder settings.** Neither read its stored
  calibration back on connect, so the tab showed defaults instead of what the device holds.

### Changed

- **The FSR V1 field editor drops its wire-boundary controls.** The byte/bit steppers and the
  merge/split buttons are gone now that the record layouts are decoded from firmware; per-field
  channel assignment and Reset remain.
- **The plugin's log is less chatty.** Repeating status lines are now logged only when they change.

## [1.5.4]

### Added

- **Português (Portuguese) language** has been added.
- **Minimum blinker time** slider added for stalks in truck sim mode. 
- **The bug-report reference can be selected and copied.** 
- **Minimum steering angle lowered to 60°.** Lowest possible setting accepted by firmware.
- **AB6 active shifter.** The AB6 (USB PID `0x1002`) is now a recognised device.
- **10-band FFB Effect Equalizer.** On wheelbase firmware **1.2.10.10+.** 
- **EQ sensitivity presets.** Road sensitivity presets are now supported for both 6 and 
  10 band devices.

### Fixed

- SimHub log no longer floods during gameplay.
- An AB6 shifter no longer corrupts the steering readout or the wheel's button table.
- Diagnostics no longer shows a stale COM port for an unplugged active shifter.
- Remembered COM port now works under Wine/Proton.
- SimHub's Arduino scan no longer delays the wheelbase connection.
- Standalone-USB CM2 RPM/flag LEDs work on the 2026-06 meter firmware.
- AB9/AB6 gear shifts no longer get more violent the longer you play, and the gear-shift
  Vibration Intensity slider now scales the whole effect.
- AB9/AB6 FFB effect handles are read from the device instead of assumed.
- An mBooster on the base's or hub's pedal port is now detected.
- mBooster roles no longer stick to an axis with no pedal wired.
- A standalone mBooster keeps its settings once its real axis resolves.
- CM2/CM1 dash works on a rig with no wheel attached, and a bridged CM1 no longer gets
  the CM2 device definition.
- Paddle, knob and joystick settings no longer bleed between two wheels.
- Display fixes for stayings dead after a game switch.

### Changed

- **Bundled SimHub updated to v9.11.22** (from v9.11.21).

## [1.5.3] - 2026-07-30

### Fixed

- **RS wheel button LEDs.** The RS rim profiles (Leather/Alcantara, Round/D-Shape) now
  carry 10 button LEDs, restoring the button-LED controls for the RS V2 — its firmware
  reports "RS Leather # W00", which resolves to the RS Leather Round profile that
  previously claimed no button LEDs. The RS V2 profile's count is also corrected
  from 14 to 10.
- **Standalone mBooster pedals are no longer mistaken for a chain.** A lone unit reports
  the same presence bytes as a two-pedal chain, which could route its effects to the wrong
  motor address; stale calibration the host keeps for detached pedals could also leave the
  unit's role unresolved, and an empty HID axis could claim a role ahead of the real pedal,
  pinning that input at 0. Chain detection, role resolution, and axis-to-input mapping now
  follow the device's own connected-pedal diagnostic.
- **Unknown wheel names.** Unrecognized wheel models no longer carry the firmware's
  "# code" module suffix (e.g. "# W00") in their SimHub device name.

## [1.5.2] - 2026-07-29

### Added

- **Outdated-firmware warning.** When a current-generation wheel (device ID 0x17/0x15)
  answers detection like a legacy-protocol wheel — seen on an FSR V2 whose firmware never
  replied to the telemetry-mode probe — the plugin now shows a banner recommending a wheel
  and wheelbase firmware update in MOZA Pit House, names the affected wheel once its model
  resolves, and records the advisory in diagnostics bundles.
- **CM1 Racing Dash — full default channel map.** The base/hub-bridged CM1 dash's field
  stream is now decoded and mapped: every field ships a default SimHub binding.
- **HGP and SGP shifters can now be used at the same time.** They're fully independent devices —
  each with its own detection, tab, and per-profile settings.
- **HGP shifter type selector.** The HGP tab lets you switch the shifter between H-pattern
  and Sequential — for anyone wanting to run sequential-shifting mods — and doubles as the
  recovery path for shifters flipped by the 1.5.1 bug (see Fixed).
- **MOZA Stalks truck-sim mode — rebindable keys.** Every key the stalks send is now set with a
  press-a-key capture field: the wiper forward/back and light-cycle keys, both indicator keys
  (previously fixed at P / - / L / [ / ]), and the per-button key assignments (previously a
  fixed preset list). Captured keys are stored as layout-independent scan codes and displayed
  using your keyboard layout's own key names, so international layouts can bind any key.
- **Wheelbase LFE as a SimHub ShakeIt device (prototype).** A new "MOZA Wheelbase LFE
  haptics" device in SimHub's Devices list exposes the base's three summed LFE oscillators
  as ShakeIt Motors channels, so any ShakeIt effect can be routed to the wheelbase through
  SimHub's full effects editor. Automatically hides legacy LFE tab when the new device is 
  used.
- **"Report a problem" — built-in bug reporting (About tab).** Describe the issue (with an
  optional contact) and the plugin uploads the report together with a diagnostics bundle —
  diagnostics snapshot, startup and rolling serial captures (hardware identifiers masked),
  the plugin log, and the plugin's settings — and hands back a ticket ID. Oversized bundles
  drop the rolling capture segment to fit the upload cap. The local diagnostics ZIP export
  gains the same settings file.
- **AB9 mechanical layout — SimHub property and actions.** `AZOM.Ab9Layout` shows the current
  layout on dashboards/StreamDeck (and `AZOM.Ab9Connected` reports shifter presence), while new
  bindable actions change it on the fly: `AZOM.Ab9LayoutNext` / `AZOM.Ab9LayoutPrev` cycle the
  six layouts, and `AZOM.Ab9Layout5R1` … `AZOM.Ab9LayoutSequential` jump straight to one.
  Closes issue #112.
- **MOZA × Porsche Mission R and ESSENZA SCV12 wheels recognized.** The two wheels now get 
  correct LED handling and product images.

### Changed

- **Per-PR development builds.** Dev-branch builds and the rolling `dev-latest` pre-release
  are retired; CI now publishes a pre-release for every commit pushed to an open pull request
  (newest 5 kept per PR, all deleted when the PR closes). The release-channel dropdown
  (About > Updates) lists Stable plus every open PR — pick a PR to track its newest build,
  and switching back to Stable offers the stable build even from a newer-numbered PR build.
  Users on the old Development channel are migrated to Stable automatically.
- **Base-tab temperature graph keeps its history.** MCU/MOSFET/motor temperatures are now
  sampled by a background timer for the plugin's whole lifetime (every 0.5 s) instead of only
  while the settings panel is open, so the graph shows its full rolling window the moment you
  open the Base tab.

### Fixed

- **ES / ESX RPM LED brightness now follows the master brightness slider.** The Global
  Brightness slider under Devices → Moza ES → LEDs had no effect (except full-off at 0):
  old-protocol wheels dim only via a firmware register the master path never wrote, and both
  of SimHub's fast callbacks go quiet at idle, so the write is driven from the steady poll
  timer. The 0–100 slider is scaled into the firmware's small brightness range (it is not a
  0–100 percentage) so the sweep no longer wraps, and a short settle absorbs the startup
  brightness-mode churn that otherwise flickered the bar. Closes issue #113.
- **ESX wheel detection.** The ESX reports its firmware model name as "RSX", which the
  model table never matched; it now resolves to its own device definition and artwork.
- **Display rotation stuck on for non-VGS display wheels.** Selecting the wrong firmware era
  for the wheel could stream telemetry in a format the wheel misinterprets; Display wheels 
  other than the VGS now get the rotation setting turned off on every connection.
- **Unresponsive display fix — the "stale-session wedge."** If a prior
  SimHub instance exited with the wheel session still open, the wheel would keep acking
  everything the new instance sent while never re-engaging the display (no session device-init,
  no dashboard list, no slot report), leaving the dash dead indefinitely. Cold start now detects
  that a session-close was acked (the wheel still held a stale session), holds ~11 s of
  session-layer silence so the firmware tears it down, then re-closes before opening fresh; the
  DisplayWatchdog also recognizes the wedge and triggers the proven off/on recovery cycle if one
  slips through.
- **Dead display after a reconnect.** After a port bounce the wheel's session layer can get
  stuck replaying a stale open-ack; the plugin's single telemetry-session open attempt timed
  out and the pipeline came up looking active with nothing reaching the display. The open is
  now retried at ~1 s intervals for up to 10 rounds (matching Pit House), and if the wheel
  still refuses, the proven off/on recovery cycle runs instead of continuing into a dead lane.
- **Dead display after power-cycling the wheel mid-session.** Rebooting the wheel while
  telemetry was running could leave the session the dashboard binds through completely silent
  while cached state kept every health check green — the display stayed dark until a manual
  off/on. The DisplayWatchdog now detects the asymmetry (binding session dead while the
  wheel's other session stays live) and triggers the recovery cycle.
- **FSR1 default mappings — broad capture-verified correction pass.** Brake bias, the gap
  fields, the side pages, and the layouts of several built-in dashboards now decode correctly.
- **A bad FSR1 field mapping can no longer freeze the whole display.** One malformed
  partition/field used to abort the entire send tick, blanking every page until a page
  change; the bad record is now skipped (and logged) while the rest keep streaming, and
  field writes are bounds-guarded.
- **1.5.1 could flip an HGP shifter into sequential mode.** 1.5.1 kept one shared set of
  shifter profile fields and one shifter "owner", so with an SGP in the picture (attached
  alongside the HGP, or used earlier under the same profile) profile apply could write the
  SGP's shifter-type at the HGP — the shifter stores it, so the HGP then acts as a
  sequential shifter even outside SimHub, and MOZA's own software does not put it back.
  The plugin no longer captures or profile-applies shifter-type at all, and the HGP
  tab gains a **Shifter type** selector (H-pattern / Sequential) that writes the device
  directly with a read-back — the recovery path for affected HGPs. The value each
  shifter reports is also logged on every connect.
- **A shifter on its own USB port no longer hides one behind the base or hub.** The
  relayed-shifter probe (dev `0x1A`) stopped as soon as any shifter was detected anywhere,
  so a standalone HGP suppressed detection of an SGP plugged into the wheelbase; the probe
  and its model resolvers are now gated per pipe.

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

[1.5.3]: https://github.com/giantorth/AZOM/compare/v1.5.2...v1.5.3
[1.5.2]: https://github.com/giantorth/AZOM/compare/v1.5.1...v1.5.2
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
