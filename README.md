<p align="center">
  <img src="docs/images/azom-hero.svg" alt="AZOM — The MOZA Bridge for SimHub" width="760">
</p>

> [!IMPORTANT]
> **The Unofficial MOZA SimHub Plugin is now named AZOM.**

[![Release](https://img.shields.io/github/v/release/giantorth/moza-simhub-plugin)](https://github.com/giantorth/moza-simhub-plugin/releases/latest)
[![Pre-release](https://img.shields.io/github/v/release/giantorth/moza-simhub-plugin?include_prereleases&label=pre-release&color=orange)](https://github.com/giantorth/moza-simhub-plugin/releases)
[![License: GPL v3](https://img.shields.io/github/license/giantorth/moza-simhub-plugin)](LICENSE)
[![Discord](https://img.shields.io/discord/1494517781016608888?label=Discord&logo=discord&logoColor=white&color=5865F2)](https://discord.gg/J4enw43e62)
[![Stars](https://img.shields.io/github/stars/giantorth/moza-simhub-plugin?label=Star&logo=github&color=yellow)](https://github.com/giantorth/moza-simhub-plugin/stargazers)
[![Sponsor](https://img.shields.io/github/sponsors/giantorth?label=Sponsor&logo=github&color=ea4aaa)](https://github.com/sponsors/giantorth)
[![Ko-fi](https://img.shields.io/badge/Ko--fi-Support-ff5e5b?logo=ko-fi&logoColor=white)](https://ko-fi.com/giantorth)
# AZOM

**The MOZA Bridge for SimHub** — an unofficial, open-source SimHub plugin for MOZA sim racing hardware.

> [!NOTE]
> MOZA is a registered trademark of Gudsen Technology Co., Ltd. This project is not affiliated with, endorsed by, or sponsored by MOZA or Gudsen Technology. All trademarks are the property of their respective owners.

![Wheel Startup](docs/examples/IMG_7111.gif)
> _A CS Pro with a custom Sparco rim running ATSR_

A SimHub plugin that provides complete replacement software for your MOZA hardware.

Built using the amazing work of [Boxflat](https://github.com/Lawstorant/boxflat), Linux MOZA control software.

> [!WARNING]
> If you [sponsor future development efforts](https://github.com/sponsors/giantorth) the money will just be used to buy more MOZA hardware.

## Why This Exists

MOZA makes excellent sim racing hardware, but their companion software — Pithouse — is Windows-only. Linux users have no official way to manage LED effects or stream telemetry to your wheel's dashboard. SimHub, on the other hand, runs on Windows and Linux (via Proton/Wine), opening the door for multi-platform hardware control with built-in telemetry support.

This plugin opens up MOZA hardware to the wider world of SimHub.  Drive your leds using [ATSR-EVO](https://github.com/ATSR-Alex/ATSR-Hub-EVO/) plugin.  Map any data point from the thousands in SimHub to display on your wheel dashboards.
The goal is to expand the functionality of MOZA devices to a wider audience by providing tools that work across multiple platforms.  

![AZOM plugin panel](docs/images/BasePage.png)

![Dashboard channel mapping](docs/images/WheelChannelMapping.png)

> [!IMPORTANT]
> **Close Pithouse before using this plugin.** Both applications communicate with MOZA hardware over the same serial port and cannot be open simultaneously. Pithouse must be fully closed (not just minimized) before SimHub can connect.

> [!CAUTION]
> **USE AT YOUR OWN RISK.** This software communicates directly with force feedback hardware capable of producing high torque output that can cause serious injury or property damage. This plugin is provided "as is", without warranty of any kind, express or implied. The authors accept no responsibility or liability for any damage to hardware, injury to persons, or any other loss arising from the use of this software. By using this plugin, you acknowledge the inherent risks of controlling force feedback devices via third-party software and accept full responsibility for any consequences.

## Custom Effects managed by Simhub

https://github.com/user-attachments/assets/f5e77a1b-4b85-438c-957e-18c45d22a216

https://github.com/user-attachments/assets/94ad3e6a-9ae0-46a2-8e2f-4f4343326414

_Thank you to a gracious alpha tester who provided these custom effect and dashboard videos._

## Installation

1. Download the latest `MozaPlugin_<version>.zip` from the [Releases](https://github.com/giantorth/moza-simhub-plugin/releases) page.
2. Extract `MozaPlugin.dll` into your SimHub installation directory. 

> Simhub defaults to `C:\Program Files (x86)\SimHub\`

Restart SimHub — the plugin appears under Settings > Plugins as "AZOM".

**Development builds.** Every open pull request publishes per-commit pre-release builds on the [releases page](https://github.com/giantorth/moza-simhub-plugin/releases). Easier: in the plugin, open Options > Updates and pick the PR in the release-channel dropdown to install and track it. Expect bugs or broken features — use the stable release above if you need something reliable.

**Device setup:** Connect your hardware and restart SimHub. The plugin auto-detects connected devices (wheel model, dashboard) and deploys matching device definitions. A banner in the plugin settings panel will prompt you to restart SimHub, after which the devices appear under Devices ready to add. Requires SimHub 9.11.8+.

## This Plugin is Better With ATSR-EVO

<p align="center"><a href="https://github.com/ATSR-Alex/ATSR-Hub-EVO/"><img src="docs/images/atsr-logomark-mono-white-lrg.webp" alt="ATSR-EVO" width="400"></a></p>

 ATSR-Hub EVO uses a custom LED framework which allows for advanced telemetry and input driven effects and animations. Drive your wheel LEDs in incredibly advanced ways.  

## Discord

[Join the Discord](https://discord.gg/J4enw43e62) if you want to discuss features or development of this plugin.

## Videos

Spanish language with English dub and subtitles availble.

<table>
<tr>
<td width="50%" valign="top">

[![Youtube Video](https://github.com/user-attachments/assets/f19a20b7-13ff-4ff5-a23b-b015149d37cb)](https://www.youtube.com/watch?v=apPXgjnGqD0)
</td>
<td width="50%" valign="top">

[![Youtube Video](https://github.com/user-attachments/assets/31d05cff-9009-4954-8008-d6c0cdabd9b8)](https://www.youtube.com/watch?v=D_ZmB0xn_KY)

</td>
</tr>
</table>
<!-- Generated by https://t.cuts.so/github/video -->

## Features

### SimHub Device Integration

MOZA wheels and dashboards register as native SimHub devices, appearing in SimHub's **Devices** section. This enables full control of your LEDs through SimHub's effects pipeline — no separate telemetry mode needed.

![MOZA wheel device — Inputs tab](docs/images/WheelInputs.png)

- **Per-Model Device Definitions** — Each new wheel attached will get a generated device definition with the LED layout baked in. Definitions are deployed automatically on first detection — just connect your hardware, restart SimHub, and add the device. Requires SimHub 9.11.8+
- **LED Effects System** — Use SimHub's full Button and Telemetry effects configuration UI (RPM indicators, flags, speed limiter animations, scripted effects, etc.) to control your wheel and dashboard LEDs
- **Per-Game Device Profiles** — SimHub's device profile system saves and restores LED effect configurations per game
- **Model-Aware Connection** — Only the device matching the currently connected wheel reports as connected. Swap wheels and the correct device activates automatically
- **Separate Wheel & Dashboard Devices** — Each registers independently with its own profile and LED configuration
- **Individual LED Effects** — SimHub's per-LED effects reach the hardware in both "Combined" and "Individual LEDs Only" (Exclusive) modes. The virtual driver exposes RPM + button LEDs as one contiguous strip (telemetry first, then buttons) so per-LED effects can target the whole sequence; knob ring LEDs are addressable via the Extra/encoders channel
- **Per-Model Wheelbase Device** — Each wheelbase registers as its own SimHub device named for the model ("MOZA R16", "MOZA R21"), with the ambient ring's real LED count (12 on an R16, 18 on R21/R25/R27). Drive it from SimHub's effects pipeline, or use the device page for indicator state, brightness, standby animation (Constant/Breath/Cycle/Rainbow/Flow), sleep mode + timeout, and startup/shutdown colors. R3/R5/R9/R12 bases ship without the LED strip
- **Wheelbase LFE in ShakeIt** — On LFE-capable firmware (>= 1.2.10.10) the wheelbase device can carry a Haptics section, putting the base's three LFE oscillators in SimHub's ShakeIt Motors editor (5-200 Hz). Options -> Device Definitions picks the source: the plugin's own LFE tab (default) or SimHub ShakeIt — the two would sum on the wire, so exactly one owns it. Switching rewrites the device definition, so SimHub needs a restart
- **Per-Wheel Idle & Sleep Effects** — Each wheel's device page has RPM / Buttons / Knobs / Sleep tabs for the hardware's own onboard idle animations (Constant, Breathing, Color Cycle, Rainbow, Sand Flow, RGB Pulse), static RPM/flag/knob colors, and the sleep-light mode + color + standby timeout. These play locally on the wheel when SimHub isn't driving effects (game closed, telemetry paused). Sleep settings persist at the wheel level, not per game
- **360hz and LFE Support** — Supports native control SDK for games that require it (iRacing)

![Knob LED rings and idle effects](docs/images/WheelKnobs.png)

The plugin injects virtual LED drivers so SimHub's effects UI shows each device as connected, even though MOZA uses a proprietary serial protocol. The computed LED colors are forwarded to the hardware each frame.

![Effects List](docs/images/Effects.png)

SimHub contains many effects to choose from and this plugin supports any custom effects that target a device.

Tested:
- Old-protocol wheels (ES series)
- Multiple Bases
- New-protocol wheels (Vision GS / GS V2P / TSW / KS Pro / CS Pro / FSR V2)
- MOZA handbrake 
- Universal Hub (port enumeration + child-device routing)
- AB9 active shifter (mode + feel sliders)
- Dashboard telemetry + screen updates (confirmed on Vision GS, CS Pro, KS Pro, and FSR V2)
- Stand-alone dashboards (CM1 and CM2 Racing Dash, standalone-USB or bridged behind the wheelbase)

TBD:
- Older generation wheels not in the list below

### Dashboard Support

<img src="docs/examples/IMG_7105.gif" alt="Custom Displays" width="320" align="left" style="margin: 0 10 1em 1em;" />

Wheels with an LCD dashboard (Vision GS, CS Pro, KS Pro, and FSR V2 confirmed; others likely work) can receive live telemetry from SimHub — speed, RPM, gear, lap times, fuel, tyre wear, and so on — streamed via MOZA's multi-tier binary telemetry protocol.

- **Auto-detect dashboard folder.** The plugin scans your Pithouse install for the `.mzdash` source folder; an "Auto-detect" button on the wheel device page picks it up in one click. Subfolders are searched recursively, so the dropdown shows every layout you've authored in the dashboard builder.
- **Hot-reload.** Pick a different layout in the Dashboard dropdown and the plugin re-negotiates the wheel's tier definitions and starts streaming the new channel set without restarting SimHub. If you pick the layout already loaded on the wheel, the plugin detects it and skips the renegotiation.
- **Channel mapping.** The wheel device page has a "Channel mappings" expander to override which SimHub property drives each dashboard channel. Type 3+ characters to search the live SimHub property list (substring, case-insensitive). Leave blank to use the plugin's built-in default mapping.
- **String channels.** Dashboards that include text fields (driver name, session type, position labels, etc.) are supported and encoded as UTF-8.
- **Test pattern.** A "Send Test Pattern" button cycles all mapped channels through known values so you can verify a dashboard is wired up correctly without launching a game.

**Important caveats:**

- **SimHub dashboards are not supported.** MOZA wheels render their LCD through firmware using MOZA's proprietary dashboard format. This plugin only streams game data into that format — it cannot push SimHub dashboard templates, HTML overlays, or custom layouts to the screen. Continue using the official MOZA dashboard builder for layout work.

### Per-Game Profiles

All settings are stored per-game via SimHub's profile system and switch automatically when you launch a different game. A profile selector sits at the top of the plugin panel.

### Languages

The plugin UI is localized into **English, Deutsch, Ελληνικά, Español, Français, Italiano, 한국어, Norsk bokmål, Русский, Tiếng Việt, and 简体中文** (11 languages). By default the plugin follows SimHub's own language setting (Settings > General > Culture in SimHub); if SimHub is set to a language the plugin doesn't ship yet, it falls back to your OS UI language, then English. A **Language** picker in the plugin's Options tab lets you override that auto-detection — useful if you want SimHub in one language and the MOZA pane in another.

All translations are embedded directly into `MozaPlugin.dll` — no per-culture satellite assemblies, no extra files to deploy. Translations live in `Resources/Strings.<culture>.resx`. PRs adding a new language are welcome — see the i18n section in [DEVELOPMENT.md](docs/DEVELOPMENT.md) for the four-step recipe.

### Hardware Configuration

The plugin panel (Settings > Plugins > AZOM) exposes read/write control of wheelbase, wheel, handbrake, pedal, and hub settings — rotation angle, FFB strength, damping, wheelbase/game effects, FFB equalizer, output curves, performance output mode, paddle/clutch/knob/stick modes, handbrake modes, pedal calibration, and hub port enumeration — mirroring what Pithouse offers. Tabs auto-show/hide based on what's connected (Base, Wheel, Handbrake, Pedals, AB9 Shifter, mBooster, Hub, Options, Wheel Files, Help). The Help tab dumps live wheel identity, dashboard state, and session info for bug reports, with serial numbers redacted by default.

The Universal Hub gets its own tab listing each connected port and the device attached to it, polled every 2 seconds.

The plugin also remembers the last-used wheelbase and AB9 COM ports across SimHub restarts, recovers serial connectivity after sleep/resume, and handles wheel hotswap (swap wheels mid-session and the device definition switches automatically once the new wheel reports its model).

**Gearshift bump.** A tactile pulse fires through the wheelbase on every SimHub-reported gear change, giving you a physical "thunk" through the wheel on each shift. Configurable in the Base tab.  Supports configurable debounce and suppress on neutral options.

### AB9 Active Shifter

Full configuration support for the MOZA AB9 active shifter, surfaced under its own "AB9 Shifter" tab when one is connected:

- **Mechanical layout** — 5+R, 6+R (two patterns), 7+R (two patterns), or Sequential.
- **Feel** — mechanical resistance, spring, natural damping, natural friction, and max output torque limit, each on a 0–100 slider.
- **Engine vibration** — intensity (0–100) and frequency (0–200 Hz) for engine-driven shaker effect.
- **Gear-shift vibration** — pulse intensity (0–100) on every shift.

### mBooster Pedals

MOZA mBooster pedal haptics get their own **mBooster** tab when one or more units are connected. Each unit is assigned a role (Throttle, Brake, or Clutch), and the plugin renders pedal haptic effects host-side from live telemetry. A **Pedal Trace** sparkline above the Effects card plots the pedal's position over the last 5 seconds, giving a visual reference for when the effects below actually trigger:

- **ABS** — pulses on ABS activation, with a fixed, user-set Frequency (5–30Hz), Intensity (0–100%), and Smoothness (0–100% — pulse modulation depth: 100% is a smooth buzz, 0% a sharper, choppier pulse). A **Test** toggle substitutes live brake position for ABS activation so you can preview it by pressing the pedal, running indefinitely at the live slider values.
- **Engine Vibration** — continuous vibration at a fixed, user-set Frequency (60–200Hz) and Intensity (0–100%) whenever the engine's running above idle. Same kind of **Test** toggle as ABS.
- **Road Texture** — road-surface vibration while driving (game running and the car actually moving), with Intensity (0–100%) scaled live by a road-roughness proxy (vertical chassis G-force — SimHub has no generic suspension-travel telemetry) so it tracks actual bumps instead of running at a constant level, and Smoothness (0–100%) sent straight through as a raw percentage — the firmware shapes the actual noise signal internally, reverse-engineered from real Pit House captures. Same **Test** toggle pattern as the other two.
- **Lockup** — ramps in when a wheel locks under heavy braking, with a fixed, user-set Frequency (10–100Hz) and Intensity (0–100%). Detection (brake + speed + wheel-slip heuristic) is unchanged from before; only the frequency became a fixed slider. A **Test** toggle substitutes live brake position for the wheel-slip check so you can preview it by pressing the pedal.
- **Threshold** — a pulsed envelope on the braking threshold, with four sliders: Trigger Input Level (50–100% — the brake position that fires the effect; the release point stays a fixed 30 points below it), Frequency (5–100Hz), Intensity (0–100%), and Vibration Decay (0–100% — how much the pulse fades after its initial burst: low decay sustains near full strength, high decay drops to a short, sharp tick). Same **Test** toggle pattern as the others, substituting live brake position for the trigger check — the test respects the same trigger threshold as real driving, so it won't fire until you actually press past it.

All five mBooster effects now have their own card in the Effects section.

A **Pedal Feel** section holds a Start/End of Travel slider (a real dual-thumb range slider, 3.8–49.7mm, with the gap between the two ends constrained to 3.8–32.1mm — this one writes real calibration to the device, reverse-engineered from Pit House captures), two End Stop Stiffness sliders (Front Limit / End Limit, 1–10 — how hard the pedal feels at each end of its travel, also a real hardware write reverse-engineered from Pit House captures), a Deadzone slider (0–40kg at the start of pedal travel), a Max Force slider (0–200kg — sets where the input curve's 100% point sits, for pedals that never get pressed all the way to their theoretical max), and a second draggable 5-point curve — an **input curve** that reshapes how the pedal feels. Deadzone, Max Force, and the input curve are host-side only, applied before anything else sees the pedal's position, and don't touch the output curve below.

Both the input curve and the output curve show a live dot on the spline (with a guide line down to the axis) tracking the pedal as it's pressed.

A **Sim Input Mapping** section (Pit House-style) holds a Sensor Output Ratio slider (blends the mBooster's angle sensor and load cell readings), a Max Threshold (kg) slider (the load-cell force at which output reaches 100%, reverse-engineered from real Pit House USB captures — unconfirmed by Moza), and the output curve (with Linear/S-Curve/Exponential/Parabolic presets, same as Pedal Feel) that shapes what's actually sent to the game. Unlike every other curve in the plugin, this one's nodes can also be dragged **horizontally** — drag the last node left to reach 100% output before 100% input, without needing the hardware breakpoint command that doesn't exist for this curve (the plugin resamples the shape at the fixed breakpoints the real wire commands support instead).

An experimental calibration section is also available per device, with direction/min/max raw range plus read/apply buttons.

### Diagnostics & Serial Capture

The Help tab includes a **Serial traffic capture** section for bug reports:

- **Start capture** records every TX/RX serial frame (wheelbase + AB9 pipes) with millisecond timestamps in memory. Nothing is written to disk while capturing, and the buffer is wiped each time SimHub restarts.
- **Stop capture** reveals the captured frames inline (hex dump, one frame per line) and unlocks the export buttons. Per-direction labels (`T`/`R`) and pipe labels (`wheelbase` / `ab9`) make it easy to correlate with protocol docs.
- **Export bundle (ZIP)** writes a timestamped archive containing:
  - `manifest.txt` — bundle header (plugin version, OS, capture summary)
  - `serial-capture.txt` — TX/RX frame log
  - `diagnostics.txt` — snapshot of the Help tab's diagnostic report (identity, dashboard state, session info)
  - `moza-log.txt` — every `[Moza]` log line emitted by the plugin since launch (pulled from the in-process `MozaLog` ring buffer, so flush cadence and SimHub log-file location don't matter)
- **Copy capture to clipboard** copies the frame log without exporting a file.

Use this when reporting protocol bugs or unexpected device behavior — attach the ZIP to the issue and the maintainers have everything needed to reproduce.

### SimHub Properties

The plugin exposes these properties for use in SimHub dashboards and overlays:

| Property | Type | Description |
|----------|------|-------------|
| `AZOM.BaseConnected` | bool | Wheelbase connection status |
| `AZOM.McuTemp` | double | MCU temperature (°C or °F, per the temperature-unit setting) |
| `AZOM.MosfetTemp` | double | MOSFET temperature (°C or °F, per the temperature-unit setting) |
| `AZOM.MotorTemp` | double | Motor temperature (°C or °F, per the temperature-unit setting) |
| `AZOM.BaseState` | int | Wheelbase state |
| `AZOM.CurrentTorque` | double | Live motor torque in Nm, unsigned — direction is dropped, so this is the magnitude whichever way the base is pulling. Refreshed at 5 Hz whether or not the settings panel is open |
| `AZOM.CurrentTorqueRaw` | double | The same live torque in Nm, signed: the sign is the direction the base is pulling |
| `AZOM.MaxTorque` | double | Highest `AZOM.CurrentTorque` seen this session, in Nm. Resets when a game starts |
| `AZOM.TorqueLimit` | double | The wheelbase's rated peak torque in Nm (R9 = 9, R16 = 16, R25 = 25 …); `-1` if the base model isn't recognised. Fixed per base — for your configured output limit see `AZOM.Torque` |
| `AZOM.MaxAngle` | int | Max steering angle (degrees) |
| `AZOM.ClutchSplitPoint` | int | Clutch split point (%) for the current wheel, as shown on the wheel device page (Paddles Mode = Combined) |
| `AZOM.HidConnected` | bool | Whether a device HID surface is being read (live input is available) |
| `AZOM.SteeringAngle` | double | Live steering angle in degrees (0 = center, ± = each lock direction); 0 until max-angle is known |
| `AZOM.SteeringPosition` | double | Live steering as 0–100 (0 = full lock, 50 = center, 100 = full lock); -1 when unknown |
| `AZOM.Throttle` | int | Throttle pedal position (0–100) |
| `AZOM.Brake` | int | Brake pedal position (0–100) |
| `AZOM.Clutch` | int | Clutch pedal position (0–100) |
| `AZOM.Handbrake` | int | Handbrake position (0–100) |
| `AZOM.LeftPaddle` | int | Left analog paddle position (0–100) |
| `AZOM.RightPaddle` | int | Right analog paddle position (0–100) |
| `AZOM.CombinedPaddle` | int | Combined analog paddle axis position (0–100) |
| `AZOM.Ab9Connected` | bool | AB9 active shifter presence |
| `AZOM.Ab9Layout` | string | AB9 mechanical layout of the active profile: `5+R Layout 1`, `6+R Layout 1`, `6+R Layout 2`, `7+R Layout 1`, `7+R Layout 2`, or `Sequential` |

These input properties are populated directly from the device HID surface, so they update live even when no game is running.

#### Wheelbase settings

Every wheelbase setting on the plugin's **Base** tab is also exposed as a property, in the same units the slider shows. Each has a matching set of actions (see below). Values track what the base reported on its last settings read, so they hold their defaults until the base answers after connect.

The numeric ones read `-1` when the value isn't available — the plugin is still loading, or the setting doesn't exist on this firmware (equalizer bands 7–10 on 6-band bases).

| Property | Type | Range | Description |
|----------|------|-------|-------------|
| `AZOM.FfbStrength` | int | 0–100 | Game FFB strength (%) |
| `AZOM.Torque` | int | 50–100 | Base torque output limit (%) — the setting, not the rating; see `AZOM.TorqueLimit` |
| `AZOM.Rotation` | int | 60–2700 | Wheel rotation angle (degrees) |
| `AZOM.WheelSpeedLimit` | int | 0–200 | Maximum wheel speed (%) |
| `AZOM.Interpolation` | int | 0–10 | FFB interpolation |
| `AZOM.GearshiftVibration` | int | 0–5 | Base gear-shift vibration intensity |
| `AZOM.Damper` | int | 0–100 | Wheel damper (%) |
| `AZOM.Friction` | int | 0–100 | Wheel friction (%) |
| `AZOM.Inertia` | int | 100–500 | Natural inertia (Wheelbase Effects) |
| `AZOM.Spring` | int | 0–100 | Wheel spring — the base's own centering force (%) |
| `AZOM.GameDamper` | int | 0–100 | Game damper effect gain (%) |
| `AZOM.GameFriction` | int | 0–100 | Game friction effect gain (%) |
| `AZOM.GameInertia` | int | 0–100 | Game inertia effect gain (%) |
| `AZOM.GameSpring` | int | 0–100 | Game spring effect gain (%) |
| `AZOM.NaturalInertia` | int | 100–4000 | Steering wheel inertia (Protection) |
| `AZOM.SoftLimitStiffness` | int | 1–10 | Soft limit stiffness |
| `AZOM.SpeedDamping` | int | 0–100 | High-speed damping level (%) |
| `AZOM.SpeedDampingPoint` | int | 0–400 | High-speed damping trigger speed (kph) |
| `AZOM.RoadSensitivity` | int | 0–10 | Road sensitivity preset index; -1 until the base reports it |
| `AZOM.Equalizer1` … `AZOM.Equalizer10` | int | 0–400/500 | FFB equalizer bands, in **register** order. Bands 7–10 read -1 on 6-band firmware. Band 6 (100 Hz) caps at 100 on 10-band firmware, the rest at 500; all six cap at 400 on legacy firmware |
| `AZOM.FfbCurveX1` … `X4`, `AZOM.FfbCurveY1` … `Y5` | int | 0–100 | FFB output curve node positions |
| `AZOM.Protection` | bool | | Hands-off protection enabled |
| `AZOM.FfbReverse` | bool | | Force feedback reversal enabled |
| `AZOM.SoftLimitRetain` | bool | | Soft limit "retain game FFB" enabled |
| `AZOM.PerformanceOutput` | bool | | Performance output on full (false = reserved) |
| `AZOM.BaseStatusLed` | bool | | Base status LED on |
| `AZOM.Bluetooth` | bool | | Bluetooth on |
| `AZOM.WorkMode` | int | 0/1 | 0 = base running, 1 = standby |

The equalizer bands are numbered by hardware register, which is **not** frequency order on 10-band firmware. Register order maps to 5/15/25/40/60/100 Hz for bands 1–6 and 10/30/50/80 Hz for bands 7–10.

### SimHub Actions

The plugin registers these actions, bindable to wheel/controller buttons under SimHub's **Controls and events** (or to dashboard controls). They change the same settings as the sliders/toggles in the plugin UI, push to hardware immediately, and persist to the active profile.

Each *step* setting has four actions: `…Up` / `…Down` apply a fine step, and `…UpCoarse` / `…DownCoarse` apply a coarse step. Values clamp to the slider range.

| Action | Range | Fine | Coarse | Effect |
|--------|-------|------|--------|--------|
| `AZOM.FfbStrengthUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–100% | ±5 | ±10 | Wheelbase FFB strength |
| `AZOM.TorqueUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 50–100% | ±5 | ±10 | Wheelbase torque limit |
| `AZOM.RotationUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 60–2700° | ±90° | ±180° | Steering rotation (max angle) |
| `AZOM.WheelSpeedLimitUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–200% | ±5 | ±10 | Maximum wheel speed |
| `AZOM.InterpolationUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–10 | ±1 | ±2 | FFB interpolation |
| `AZOM.GearshiftVibrationUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–5 | ±1 | ±2 | Base gear-shift vibration intensity |
| `AZOM.DamperUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–100% | ±5 | ±10 | Wheel damper |
| `AZOM.FrictionUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–100% | ±5 | ±10 | Wheel friction |
| `AZOM.InertiaUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 100–500 | ±10 | ±50 | Natural inertia (Wheelbase Effects) |
| `AZOM.SpringUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–100% | ±5 | ±10 | Wheel spring — the base's own centering force |
| `AZOM.GameDamperUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–100% | ±5 | ±10 | Game damper effect gain |
| `AZOM.GameFrictionUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–100% | ±5 | ±10 | Game friction effect gain |
| `AZOM.GameInertiaUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–100% | ±5 | ±10 | Game inertia effect gain |
| `AZOM.GameSpringUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–100% | ±5 | ±10 | Game spring effect gain |
| `AZOM.NaturalInertiaUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 100–4000 | ±50 | ±200 | Steering wheel inertia (Protection) |
| `AZOM.SoftLimitStiffnessUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 1–10 | ±1 | ±2 | Soft limit stiffness |
| `AZOM.SpeedDampingUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–100% | ±5 | ±10 | High-speed damping level |
| `AZOM.SpeedDampingPointUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–400 kph | ±10 | ±50 | High-speed damping trigger speed |
| `AZOM.RoadSensitivityUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–10 | ±1 | ±2 | Road sensitivity preset (also rewrites the FFB equalizer curve, exactly like the Base-tab preset buttons) |
| `AZOM.Equalizer1Up` … `AZOM.Equalizer10…DownCoarse` | 0–400/500% | ±5 | ±25 | FFB equalizer bands, in register order (see the property table) |
| `AZOM.FfbCurveX1Up` … `AZOM.FfbCurveY5…DownCoarse` | 0–100 | ±5 | ±10 | FFB output curve node positions |
| `AZOM.ClutchSplitUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–100% | ±5 | ±10 | Clutch split point — the combined-paddle bite point (Paddles Mode = Combined) |
| `AZOM.Ab9EngineIntensityUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–100 | ±5 | ±10 | AB9 engine-vibration intensity |
| `AZOM.Ab9EngineFrequencyUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–200 Hz | ±10 | ±20 | AB9 engine-vibration frequency |
| `AZOM.Ab9GearShiftIntensityUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–100 | ±5 | ±10 | AB9 gear-shift vibration intensity |
| `AZOM.DisplayBrightnessUp` / `…Down` / `…UpCoarse` / `…DownCoarse` | 0–100% | ±5 | ±10 | Wheel screen display brightness |

Wheelbase settings live in the base's parameter store, which is flash. A step action that would leave the value unchanged (already at the top or bottom of its range) writes nothing, so holding a bound button at a limit costs no extra flash writes.

Each wheelbase *toggle* has three actions — `…On`, `…Off` and `…Toggle`:

| Action | Effect |
|--------|--------|
| `AZOM.ProtectionOn` / `…Off` / `…Toggle` | Hands-off protection |
| `AZOM.FfbReverseOn` / `…Off` / `…Toggle` | Force feedback reversal |
| `AZOM.SoftLimitRetainOn` / `…Off` / `…Toggle` | Soft limit "retain game FFB" |
| `AZOM.PerformanceOutputOn` / `…Off` / `…Toggle` | Performance output (on = full, off = reserved) |
| `AZOM.BaseStatusLedOn` / `…Off` / `…Toggle` | Base status LED |
| `AZOM.BluetoothOn` / `…Off` / `…Toggle` | Wheelbase Bluetooth |

`AZOM.BaseStatusLed*` and `AZOM.Bluetooth*` are stored in the base itself and are not part of a per-game profile, so they don't change when you switch games.

| Action | Effect |
|--------|--------|
| `AZOM.DisplayBrightness0` … `AZOM.DisplayBrightness100` | Set wheel screen display brightness to a fixed level (0–100% in steps of 10) |
| `AZOM.WorkModeOff` | Turn off the wheelbase work mode (puts the base into standby) |
| `AZOM.WorkModeOn` | Turn on the wheelbase work mode (normal active state) |
| `AZOM.WorkModeToggle` | Flip the wheelbase between standby and its normal active state |
| `AZOM.Ab9LayoutNext` | Switch the AB9 shifter to the next mechanical layout (wraps around) |
| `AZOM.Ab9LayoutPrev` | Switch the AB9 shifter to the previous mechanical layout (wraps around) |
| `AZOM.Ab9Layout5R1` / `…6R1` / `…6R2` / `…7R1` / `…7R2` / `…Sequential` | Set the AB9 mechanical layout directly |
| `AZOM.DashboardNext` | Switch the wheel's displayed dashboard to the next enabled slot (wraps around) |
| `AZOM.DashboardPrev` | Switch the wheel's displayed dashboard to the previous enabled slot (wraps around) |
| `AZOM.DashboardTelemetryToggle` | Toggle dashboard telemetry on/off for the active wheel page |
| `AZOM.DashboardTelemetryOn` | Enable dashboard telemetry for the active wheel page |
| `AZOM.DashboardTelemetryOff` | Disable dashboard telemetry for the active wheel page |
| `AZOM.DisplayToggle` | Toggle the wheel screen on/off (remembers the on-brightness so toggling back on restores it) |
| `AZOM.TestModeToggle` | Toggle telemetry test mode (synthetic signal sweep) for the active wheel page |
| `AZOM.ClearLeds` | Blank all wheel and dash LEDs |
| `AZOM.CalibrateCenter` | Re-center the wheelbase (same as the Calibrate Center button — hold the wheel at physical center when triggering) |

## Building from Source

See [DEVELOPMENT.md](docs/DEVELOPMENT.md) for build instructions (Windows & Linux cross-compilation), CI/CD pipeline details, and full architecture reference.

Protocol reference: [docs/protocol/](docs/protocol/README.md). USB capture guide: [docs/usb-capture.md](docs/usb-capture.md). SimHub plugin API notes: [docs/simhub.md](docs/simhub.md).

