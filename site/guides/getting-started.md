---
layout: guide.njk
title: Getting Started
description: Install AZOM and connect your MOZA hardware to SimHub in a few minutes — then dive into the detailed guides.
tags: guide
order: 1
---

AZOM turns SimHub into complete replacement software for your MOZA hardware — LED effects,
LCD dashboard telemetry, and full device configuration on Windows and Linux. This page is
the quick path from download to a connected wheel; each step links to a detailed
walkthrough if you want it.

> **Before you start:** Pithouse and SimHub both talk to MOZA hardware over the same
> serial port and **cannot run at the same time**. Fully close Pithouse — not just
> minimized — before launching SimHub.

## Requirements

- **SimHub 9.11.8** or newer
- A MOZA wheelbase connected over USB
- Windows, or Linux running SimHub via Proton/Wine

## The short path

> **Ensure PitHouse is closed entirely and not just minimized**

1. **Install the plugin.** Drop `MozaPlugin.dll` into your SimHub directory — on Windows
   that's `C:\Program Files (x86)\SimHub\` — and enable **AZOM** when SimHub prompts you.
   Full walkthrough: [Install the Plugin](/guides/install-the-plugin/).
2. **Add your device.** Restart with your hardware connected, then add your wheel under
   **Devices** so LEDs and dashboards light up. See [Add Your Device](/guides/add-your-device/).
3. **Verify the connection.** Open the **AZOM** panel — with a base connected you'll see
   live steering angle, temperatures and FFB settings populate.

For advanced LED work, the [ATSR-EVO](https://github.com/ATSR-Alex/ATSR-Hub-EVO/)
integration unlocks sophisticated telemetry-driven effects.

> **Use at your own risk.** This software drives force-feedback hardware capable of high
> torque. It is provided "as is", without warranty.
