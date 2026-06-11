---
layout: guide.njk
title: Control Mapper
description: Make several wheels or button boxes act as one virtual controller so your AZOM mappings survive a wheel swap.
tags: guide
order: 10
---

[Controls &amp; Actions](/guides/controls-and-actions/) maps each wheel's buttons
individually — fine for a single wheel. But if you hot-swap wheels or run several
controllers, SimHub's **Control mapper** lets them act as one virtual controller, so the
AZOM mappings you set up survive a wheel change instead of being redone per wheel.

> **Most people don't need this.** For a single wheel, mapping AZOM actions straight from
> **Controls and events** is all it takes. Reach for Control mapper only when you're
> juggling multiple wheels or button boxes.

## 1 · Enable the feature

Control mapper is an optional SimHub feature. Turn it on from **Add/remove features**:

![SimHub's enabled-features dialog with Control mapper switched on](/docs/ControlMapperEnable.png)

## 2 · Set the output and check your sources

>For this feature to work, you MUST enable **Recognize Simcube 2 wireless wheels and Fanatec Wheels as individual controllers**

Once enabled, the **Control mapper** page is where you set the output mode and confirm your
source controllers are recognised — for example a *MOZA R5 Base - CS Pro* showing as
**Connected**.

![The Control mapper page showing the target virtual controller and connected source controller](/docs/ControlMapperOption.png)

With your wheels feeding one virtual controller, the AZOM mappings from
[Controls &amp; Actions](/guides/controls-and-actions/) keep working whichever wheel is
attached.
