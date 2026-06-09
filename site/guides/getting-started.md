---
layout: guide.njk
title: Getting Started
description: Install AZOM and connect your MOZA hardware to SimHub in a few minutes.
tags: guide
order: 1
---

AZOM turns SimHub into complete replacement software for your MOZA hardware — LED
effects, LCD dashboard telemetry, and full device configuration on Windows and Linux.
This guide gets you from download to a connected wheel.

> **Before you start:** Pithouse and SimHub both talk to MOZA hardware over the same
> serial port and **cannot run at the same time**. Fully close Pithouse — not just
> minimized — before launching SimHub.

## Requirements

- **SimHub 9.11.8** or newer
- A MOZA wheelbase connected over USB
- Windows, or Linux running SimHub via Proton/Wine

## Install the plugin

1. Download the latest `MozaPlugin_<version>.zip` from the
   [Releases page](https://github.com/giantorth/moza-simhub-plugin/releases/latest).
2. Extract `MozaPlugin.dll` into your SimHub directory — on Windows this defaults to
   `C:\Program Files (x86)\SimHub\`.
3. Restart SimHub. The plugin appears under **Settings › Plugins** as “MOZA Control”.
4. Plug in your hardware and restart once more. Devices are auto-detected and their
   definitions deployed — then add them under **Devices**.

## Verify the connection

Open **Settings › Plugins › MOZA Control**. With a base connected you should see live
steering angle, temperatures, and FFB settings populate:

![The MOZA Control plugin panel inside SimHub](/docs/Screenshot.png)

If the panel stays empty, confirm Pithouse is fully closed and that no other app holds
the serial port, then restart SimHub.

## Next steps

- Map SimHub data points to your wheel's LCD dashboard channels.
- Drive your wheel LEDs through SimHub's effects pipeline — see the
  [ATSR-EVO](https://github.com/ATSR-Alex/ATSR-Hub-EVO/) integration for advanced effects.
- Bind wheel buttons to AZOM actions like FFB strength, rotation, and dashboard switching.

> **Use at your own risk.** This software drives force-feedback hardware capable of high
> torque. It is provided “as is”, without warranty.
