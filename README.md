<p align="center">
  <img src="docs/images/azom-hero.svg" alt="AZOM — The MOZA Bridge for SimHub" width="760">
</p>

[![Release](https://img.shields.io/github/v/release/giantorth/AZOM)](https://github.com/giantorth/AZOM/releases/latest)
[![Pre-release](https://img.shields.io/github/v/release/giantorth/AZOM?include_prereleases&label=pre-release&color=orange)](https://github.com/giantorth/AZOM/releases)
[![License: GPL v3](https://img.shields.io/github/license/giantorth/AZOM)](LICENSE)
[![Discord](https://img.shields.io/discord/1494517781016608888?label=Discord&logo=discord&logoColor=white&color=5865F2)](https://discord.gg/J4enw43e62)
[![Stars](https://img.shields.io/github/stars/giantorth/AZOM?label=Star&logo=github&color=yellow)](https://github.com/giantorth/AZOM/stargazers)
[![Sponsor](https://img.shields.io/github/sponsors/giantorth?label=Sponsor&logo=github&color=ea4aaa)](https://github.com/sponsors/giantorth)
[![Ko-fi](https://img.shields.io/badge/Ko--fi-Support-ff5e5b?logo=ko-fi&logoColor=white)](https://ko-fi.com/giantorth)

# AZOM

**The MOZA Bridge for SimHub** — an unofficial, open-source SimHub plugin that turns SimHub into complete replacement software for your MOZA sim racing hardware, on Windows and on Linux.

### 📖 **Documentation lives at [giant.orth.cc](https://giant.orth.cc/)**

Everything below is a summary. The full guides — install, per-tab walkthroughs, LED and dashboard setup, the complete property and action reference — are on the site.

![Wheel Startup](docs/examples/IMG_7111.gif)
> _A CS Pro with a custom Sparco rim running ATSR_

Built using the amazing work of [Boxflat](https://github.com/Lawstorant/boxflat), Linux MOZA control software.

> [!IMPORTANT]
> **Close Pithouse/Boxflat before using this plugin.** Both applications communicate with MOZA hardware over the same serial port and cannot be open simultaneously. Pithouse (or Boxflat) must be fully closed (not just minimized) before SimHub can connect.

> [!CAUTION]
> **USE AT YOUR OWN RISK.** This software communicates directly with force feedback hardware capable of producing high torque output that can cause serious injury or property damage. This plugin is provided "as is", without warranty of any kind, express or implied. The authors accept no responsibility or liability for any damage to hardware, injury to persons, or any other loss arising from the use of this software. By using this plugin, you acknowledge the inherent risks of controlling force feedback devices via third-party software and accept full responsibility for any consequences.

> [!NOTE]
> MOZA is a registered trademark of Gudsen Technology Co., Ltd. This project is not affiliated with, endorsed by, or sponsored by MOZA or Gudsen Technology. All trademarks are the property of their respective owners.

## Why This Exists

This plugin opens up MOZA hardware to the wider world of SimHub. Drive your LEDs using the [ATSR-EVO](https://github.com/ATSR-Alex/ATSR-Hub-EVO/) plugin. Map any data point from the thousands in SimHub to display on your wheel dashboards.

## Install

1. Download the latest `MozaPlugin_<version>.zip` from the [Releases](https://github.com/giantorth/AZOM/releases/latest) page.
2. Extract `MozaPlugin.dll` into your SimHub installation directory — by default `C:\Program Files (x86)\SimHub\`.
3. Restart SimHub and enable **AZOM** when prompted. Connect your hardware, restart once more, then add the device under **Devices**.

Requires **SimHub 9.12.0+**.

Screen-by-screen walkthrough: **[Install the Plugin](https://giant.orth.cc/guides/install-the-plugin/)** → **[Add Your Device](https://giant.orth.cc/guides/add-your-device/)**. On Linux, start with **[Linux Setup](https://giant.orth.cc/guides/linux-setup/)**.

**Development builds.** Every open pull request publishes per-commit pre-release builds on the [releases page](https://github.com/giantorth/AZOM/releases). Easier: in the plugin, open **Options › Updates** and pick the PR in the release-channel dropdown to install and track it. Expect bugs or broken features — use the stable release if you need something reliable.

## Guides

| | |
|---|---|
| **[Getting Started](https://giant.orth.cc/guides/getting-started/)** | Download to connected wheel in a few minutes |
| **[Install the Plugin](https://giant.orth.cc/guides/install-the-plugin/)** | Every screen of the install, in order |
| **[Linux Setup](https://giant.orth.cc/guides/linux-setup/)** | SimHub under Wine via `linux-simracing-utils` |
| **[Add Your Device](https://giant.orth.cc/guides/add-your-device/)** | Register the wheel as a native SimHub device |
| **[Configure the Wheelbase](https://giant.orth.cc/guides/configure-the-wheelbase/)** | Rotation, FFB strength, damping, equalizer, output curve |
| **[LFE Effects](https://giant.orth.cc/guides/lfe-effects/)** | Host-rendered low-frequency haptics through the base |
| **[Pedals & Handbrake](https://giant.orth.cc/guides/pedals-and-handbrake/)** | Range, direction, calibration, output curves |
| **[mBooster](https://giant.orth.cc/guides/mbooster/)** | Active-pedal feel in mm and kg, plus telemetry haptics |
| **[Pedal Haptics](https://giant.orth.cc/guides/pedal-haptics/)** | S12 vibration units as SimHub ShakeIt devices |
| **[AB9 Active Shifter](https://giant.orth.cc/guides/ab9-shifter/)** | Shift pattern, mechanical feel, vibration |
| **[HGP & SGP Shifters](https://giant.orth.cc/guides/hgp-sgp-shifters/)** | Passive H-pattern and sequential shifters |
| **[Multi-Function Stalks](https://giant.orth.cc/guides/stalks/)** | Button box, or wipers and lights in ETS2/ATS |
| **[Import a Profile](https://giant.orth.cc/guides/import-a-profile/)** | Load a Pit House preset, with a diff before applying |
| **[Wheel LEDs & Knobs](https://giant.orth.cc/guides/wheel-leds-and-knobs/)** | RPM lights, knob rings, onboard idle effects |
| **[Advanced LEDs with ATSR](https://giant.orth.cc/guides/atsr-led-profiles/)** | Telemetry-driven effects from ATSR-EVO |
| **[Dashboard & Channels](https://giant.orth.cc/guides/dashboard-and-channels/)** | Stream telemetry to the wheel LCD, bind every channel |
| **[Wheel Files & Dashboard Upload](https://giant.orth.cc/guides/wheel-files/)** | Upload `.mzdash` layouts, convert SimHub dashboards |
| **[Controls & Actions](https://giant.orth.cc/guides/controls-and-actions/)** | Bind wheel buttons to AZOM actions |
| **[Control Mapper](https://giant.orth.cc/guides/control-mapper/)** | Make several wheels act as one virtual controller |
| **[Properties & Actions Reference](https://giant.orth.cc/guides/properties-and-actions/)** | Every `AZOM.*` property and action, with ranges |
| **[SDK & iRacing](https://giant.orth.cc/guides/sdk-and-iracing/)** | The embedded MOZA SDK server, for iRacing and 360 Hz |
| **[Plugin Options](https://giant.orth.cc/guides/options/)** | Plugin-wide switches, updates, device definitions |
| **[Diagnostics & Bug Reports](https://giant.orth.cc/guides/diagnostics-and-bug-reports/)** | The Help tab: diagnostic report, redacted bundles |

## What It Does

- **Native SimHub devices.** Wheels, dashboards, wheelbases and pedal vibration units register under SimHub's **Devices**, with per-model device definitions deployed automatically on first detection. LEDs run through SimHub's full effects pipeline — RPM indicators, flags, limiter animations, scripted and per-LED effects, per-game device profiles.
- **LCD dashboard telemetry.** Live speed, RPM, gear, lap times, fuel and tyre data streamed to the wheel's screen over MOZA's binary telemetry protocol, with every channel remappable to any SimHub property. Upload `.mzdash` layouts to the wheel, or convert a SimHub dashboard into one.
- **Full hardware configuration.** Read/write control of wheelbase, wheel, handbrake, pedal, shifter, mBooster and hub settings — rotation, FFB strength, damping, equalizer, output curves, paddle/clutch/knob modes, handbrake modes, pedal calibration, hub port enumeration. Tabs appear only for hardware that is connected.
- **Host-rendered haptics.** Wheelbase LFE (three formula-driven low-frequency channels at 50 Hz), mBooster pedal effects, S12 pedal vibration units as ShakeIt Motors devices, and AB9 engine/shift vibration.
- **200+ bindable actions.** Every wheelbase setting, plus display brightness, dashboard switching, clutch bite point and work mode, steppable from a wheel button through SimHub's Controls and events.
- **Per-game profiles.** Every setting is stored per game through SimHub's profile system and switches automatically when you launch a different title.
- **12 languages.** English, Deutsch, Ελληνικά, Español, Français, Italiano, 한국어, Norsk bokmål, Português, Русский, Tiếng Việt, 简体中文 — embedded in the DLL, following SimHub's own language setting by default. PRs adding a language are welcome; see the i18n section in [DEVELOPMENT.md](docs/DEVELOPMENT.md).

Tested on old-protocol wheels (ES series), new-protocol wheels (Vision GS / GS V2P / TSW / KS Pro / CS Pro / FSR V2), multiple bases, the Universal Hub, MOZA handbrake and pedals, the AB9 active shifter, HGP/SGP shifters, mBooster, and stand-alone CM1/CM2 racing dashes.

## This Plugin is Better With ATSR-EVO

<p align="center"><a href="https://github.com/ATSR-Alex/ATSR-Hub-EVO/"><img src="docs/images/atsr-logomark-mono-white-lrg.webp" alt="ATSR-EVO" width="400"></a></p>

ATSR-Hub EVO uses a custom LED framework which allows for advanced telemetry and input driven effects and animations. Drive your wheel LEDs in incredibly advanced ways — see [Advanced LEDs with ATSR](https://giant.orth.cc/guides/atsr-led-profiles/) for ready-made MOZA profiles.

## Custom Effects managed by SimHub

https://github.com/user-attachments/assets/f5e77a1b-4b85-438c-957e-18c45d22a216

https://github.com/user-attachments/assets/94ad3e6a-9ae0-46a2-8e2f-4f4343326414

_Thank you to a gracious alpha tester who provided these custom effect and dashboard videos._

## Videos

Spanish language with English dub and subtitles available.

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

## Community & Support

- **[Discord](https://discord.gg/J4enw43e62)** — discuss features and development.
- **[Report a problem](https://giant.orth.cc/guides/diagnostics-and-bug-reports/)** — the plugin's Help tab submits a redacted diagnostic bundle in one click.
- **[Sponsor](https://github.com/sponsors/giantorth)** / **[Ko-fi](https://ko-fi.com/giantorth)** — the money just buys more MOZA hardware to support.

## Building from Source

See [DEVELOPMENT.md](docs/DEVELOPMENT.md) for build instructions (Windows & Linux cross-compilation), CI/CD pipeline details, and full architecture reference.

Protocol reference: [docs/protocol/](docs/protocol/README.md). USB capture guide: [docs/usb-capture.md](docs/usb-capture.md). SimHub plugin API notes: [docs/simhub.md](docs/simhub.md).

The website is a separate repository: [giantorth/giant.orth.cc](https://github.com/giantorth/giant.orth.cc).
