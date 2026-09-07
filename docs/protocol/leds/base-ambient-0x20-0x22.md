## Base ambient LED control (groups `0x20` write / `0x22` read)

Two LED strips on the wheelbase body, controlled via write group `0x20`
(32) and read group `0x22` (34). Sent to the main controller at dev
`0x12`.

**Strip length is per base model** — see [Strip geometry](#strip-geometry)
below. Every command in this page is index-addressed, so the only thing
that changes across models is the valid LED-index range and the bitmask
width; the frame shapes are identical.

> **Source:** rs21_parameter.db + USB captures from R25 base (2026-05-05).
> See `usb-capture/startupchime/` — "Moza R25 Wheel Base Settings Part 1/2"
> contain full LED read/write sequences. R16 Ultra geometry + live-telemetry
> behaviour from bridge capture `bridge-r16ultra-20260822-190820.jsonl`
> (2026-08-22).

### Strip geometry

| Base | Model-name prefix (grp `0x07` @ dev `0x12`) | LEDs/strip | Total | Bitmask max | Chunk 2 shape | Evidence |
|---|---|---|---|---|---|---|
| R16 Ultra | `R16 Black # MOT-3-V01` | **6** | **12** | `0x3F` | 1 entry (LED 5), wire `N=6` | Measured 2026-08-22 + physical LED count confirmed |
| R25 | `R25 Black # MOT-1 -V01` | 9 | 18 | `0x1FF` | 4 entries (LEDs 5–8), wire `N=18` | Measured 2026-05-05 |
| R21, R27 | *(not captured)* | 9 | 18 | `0x1FF` | 4 entries (LEDs 5–8), wire `N=18` | Project owner confirms 9/side; no wire capture yet |
| R3, R5, R9, R12 | e.g. `R5 Black # MOT-1` | **none** | — | — | — | No ambient strip; these bases silently drop the `0xA2` read |

`Devices/BaseModelInfo.cs` is the code-side copy of this table (model token →
friendly name → LEDs per strip, `0` meaning "no strip"). It also names the
per-model SimHub device definition, so a token added here needs a row there.

Three independent signals agree on the R16 Ultra's 6-per-strip geometry:

1. `0x1B` bitmask never exceeds `0x3F` (6 bits) on either strip across 910
   mask frames.
2. `0x1A` chunk 2 carries exactly one entry, LED index 5.
3. The host's cold-start settings sweep reads `led-color` (`0x20`) and
   `sleep-led-color` (`0x25`) at indices **0–5 only** — 24 reads across both
   strips and both modes, no index ≥ 6.

There is **no geometry register**: nothing in the group `0x22` read sweep
returns a LED count, so a host has to key strip length off the base model
name. PID cannot do it — R16 and R21 share PID `0x0000`.

> The base was never *asked* for an index ≥ 6 in this capture, so its
> response to one is unmeasured. The 6-LED figure is the host's model plus
> the physical LED count on the unit, not a device refusal.

### Frame layout

```
7E [N] 20 12 [cmd] [value bytes] [checksum]
```

| Group | Direction | Cmd ID range | Notes |
|-------|-----------|--------------|-------|
| `0x20` (32) | host → device | per-cmd (see table) | Write — sets ambient LED state |
| `0x22` (34) | host → device | per-cmd | Read — returns currently stored value |

Read responses use `0xA0` / `0xA2` (group | 0x80) and `0x21` (nibble-swap
of `0x12`).

### Per-command summary

Full table in
[`../devices/main-hub-0x12.md` § Group `0x20` / `0x22`](../devices/main-hub-0x12.md).
Selected commands:

| Command | Cmd ID | Bytes | Type | Value semantics |
|---------|--------|-------|------|-----------------|
| `indicator-state` | `1C` | 1 | int | **Three states**, not a toggle: off / SimHub mode / on. Owner-reported (2026-08-25); the capture only ever showed `01`, which is why this was first recorded as on/off. The plugin writes the selector index (0/1/2) — the value↔state mapping past `01` is **unconfirmed by capture** |
| `standby-mode` | `1D` | 1 | int | 0 = off, 1 = constant, 2 = breathing, 3 = color cycle, 4 = rainbow, 5 = sand flow — all measured, see [below](#standby-mode-details) |
| `standby-interval` | `1E [mode]` | 2 | int | Per-mode interval in ms (big-endian u16). Each mode stores its own interval independently |
| `brightness` | `1F FF` | 1 | int | **Percent, 0..100** — host UI "50%" writes `7E 03 20 12 1F FF 32` (R16 Ultra, 2026-08-22). DB lists `1F 02` but the wire uses `1F FF` |
| `led-color` | `20 [strip] [mode] [led]` | 3 | array (RGB) | strip=0/1, **mode = the standby-mode this palette belongs to** (only 1 and 2 observed), led=0..(LEDs/strip − 1) — see [Strip geometry](#strip-geometry) |
| `sleep-mode` | `21` | 1 | int | **Sleep light effect selector**, not a plain enable. Measured: `00` = off, `01` = breathe (R16 Ultra, 2026-08-22). Values ≥ 2 unexplored |
| `sleep-timeout` | `22` | 2 | int | **Minutes**, BE u16 — confirmed by a two-point test, see [below](#sleep-timeout-is-minutes) |
| `sleep-breath-interval` | `23 [sleep-mode]` | 2 | int | **Speed of the SLEEP breathing effect**, BE u16 ms — *not* the standby breath speed, which is `1E 02`. Only `23 01` observed. [Details](#0x23-is-the-sleep-breath-interval) |
| `sleep-led-color` | `25 [strip] [sleep-mode] [led]` | 3 | array (RGB) | Per-LED colour of the sleep effect. Only `sleep-mode`=`01` (breathe) observed |
| `startup-color` | `26` | 3 | array (RGB) | Semantics **unverified** — the register reads back a real value, but the "power-on colour" reading is an interpretation, the setting has no observed effect, and Pit House exposes no such control (owner-reported 2026-08-25). Dropped from the plugin UI; the command and read remain |
| `shutdown-color` | `27` | 3 | array (RGB) | Semantics **unverified** — see `startup-color` above. Dropped from the plugin UI; the command and read remain |

### Standby mode details

Every value 0–5 is measured: each was selected from the host UI on an R16
Ultra (2026-08-22) and the resulting `0x1D` write observed. The UI label
column is the host's own wording.

| Mode | Host UI label | Wire | Interval slot | Per-LED palette |
|------|---------------|------|---------------|-----------------|
| 0 | "off" | `7E 02 20 12 1D 00` | none | none |
| 1 | "constant" | `7E 02 20 12 1D 01` | none (`1E 01` does not exist) | `20 [strip] 01 [led]` |
| 2 | "breathing" | `7E 02 20 12 1D 02` | `1E 02` | `20 [strip] 02 [led]` |
| 3 | "color cycle" | `7E 02 20 12 1D 03` | `1E 03` | none |
| 4 | "rainbow" | `7E 02 20 12 1D 04` | `1E 04` | none |
| 5 | "sand flow" | `7E 02 20 12 1D 05` | `1E 05` | none |

Mode `0` ("off") is the standby *mode*, distinct from the `indicator-state`
(`0x1C`) toggle — selecting "off" writes `1D 00` and leaves `0x1C` at `01`.

Only modes 1 and 2 carry a stored palette, and they are the two that need one:
constant (a static colour per LED) and breathing (a colour per LED to fade).
Cycle, rainbow and flow generate their own colours. This is why the host polls
palettes 1 and 2 only and why the interval table covers 2–5.

#### Interval registers

Interval is written independently of mode selection: `1E 02 13 88` sets the
breathing interval to 5000 ms without switching the active mode.

The host's single "idle speed" control writes **the active mode's slot**:

| Active mode | Write | Value |
|---|---|---|
| 2 | `7E 04 20 12 1E 02 13 88` | 5000 ms |
| 4 | `7E 04 20 12 1E 04 0A 2D` | 2605 ms |

Values are a free BE u16 of milliseconds, not a quantised step list —
2605, 4948 and 2715 ms all accepted alongside round 500 / 2500 / 5000.
Readback appears on the next poll, 0.4–1.5 s later.

Modes 0 and 1 have no interval and the UI offers no speed control for them;
`1E 00` and `1E 01` are never read or written.

#### `0x23` is the sleep breath interval

`0x23 [sleep-mode]` sets the speed of the **sleep** light effect's breathing —
not the standby breath speed, which is `1E 02`:

```
7E 02 20 12 21 01           sleep-mode = 1 (breathe)
7E 04 20 12 23 01 09 CB     sleep breath interval = 2507 ms
```

Measured by setting the sleep effect to breathe and its speed to 2507 ms, an
arbitrary slider value.

#### Palettes and intervals are indexed by effect number

Every per-effect register on this group follows one shape: **the selector byte
after `[strip]` is the effect mode the setting belongs to.**

| Effect | Selector | Per-LED palette | Interval |
|---|---|---|---|
| standby "off" (`1D 00`) | 0 | none | none |
| standby "constant" (`1D 01`) | 1 | `20 [strip] 01 [led]` | none — nothing to time |
| standby "breathing" (`1D 02`) | 2 | `20 [strip] 02 [led]` | `1E 02` |
| standby cycle / rainbow / flow (`1D 03..05`) | 3–5 | none — self-generated | `1E 03` / `1E 04` / `1E 05` |
| sleep "breathe" (`21 01`) | 1 | `25 [strip] 01 [led]` | `0x23 01` |
| sleep "off" (`21 00`) | 0 | none | none |

So the `01` in `25 [strip] 01 [led]` is not a magic constant — it is the
**sleep-mode number**, exactly as the middle byte of `20 [strip] [mode] [led]`
is the standby-mode number. It reads as fixed only because sleep mode 1
(breathe) is the sole sleep effect that has a palette; mode 0 is off. By the
same logic `23 [sleep-mode]` would generalise if a sleep mode ≥ 2 exists.

> Only sleep-mode values `0` and `1` have been observed, so the generalisation
> of the `0x25` / `0x23` selector beyond `01` is inference from the group's
> consistent shape, not measurement.

**Interval values are a free big-endian u16 of milliseconds**, not a
quantised step list — measured values include 2605, 4948 and 2715 ms
alongside the round 500 / 2500 / 5000. Readback lands 0.4–1.5 s later on the
next poll.

Mode `1` remains unidentified but is **not** a dead value: it is what the R16
Ultra's host wrote at connect (`+3.434 s`) to restore saved state, and the
base sat in it until the UI switched to breathing. So mode 1 is a selectable
idle effect with a UI label — that label is the missing piece, and it is
distinct from "breathing" (mode 2). Whether mode `0` is genuinely
"constant" is still an inference from rs21_parameter.db, not measured.

### `sleep-timeout` is minutes

Settled by a two-point test from the host UI on the R16 Ultra (2026-08-22):

| UI setting | Wire | Value |
|---|---|---|
| 5 min | `7E 03 20 12 22 00 05` | 5 |
| 5 hr | `7E 03 20 12 22 01 2C` | 300 |

300 = 5 × 60, so the host converts to **minutes** — there is no separate
hours unit and no scale flag. The R25's long-recorded `00 0F` was therefore
15 minutes.

> The device accepts at least 300. The plugin's own slider
> ([`MozaBaseSettingsControl.xaml:142`](../../../Devices/Ui/MozaBaseSettingsControl.xaml))
> is capped at `Maximum="120"`, so it cannot express anything above 2 hours
> even though the hardware and the vendor UI both go to 5. Not a correctness
> bug — 120 is a valid value — but it caps below hardware capability.

### Observed values from R25 base

| Setting | Value | Interpretation |
|---------|-------|----------------|
| indicator-state | `01` | LEDs on |
| standby-mode | `04` | Rainbow |
| brightness | `64` | 100 = **100%** (full) |
| sleep-mode | `01` | Sleep effect = breathe |
| sleep-timeout | `00 0F` | 15 minutes |
| startup-color | `66 B8 FF` | #66B8FF (light blue) |
| shutdown-color | `66 B8 FF` | #66B8FF (same) |
| all led-color | `56 F7 FC` | #56F7FC (cyan), both strips/modes |

### Observed values from R16 Ultra base

Full cold-start readback, 2026-08-22 capture. These are **this unit's stored
state**, not necessarily factory defaults — the user had been through the
settings UI — so treat them as "values the firmware accepts and returns",
not as a defaults reference.

| Setting | Cmd | Value | Interpretation |
|---------|-----|-------|----------------|
| indicator-state | `1C` | `01` | LEDs on |
| standby-mode | `1D` | `05` at connect, host wrote `01`, later `02` | All three are live values; `02` = breath was set from the UI |
| brightness | `1F FF` | `64`, later set to `32` | 100%, then 50% from the UI |
| standby-interval mode 2 | `1E 02` | `09 C4` | 2500 ms |
| standby-interval mode 3 | `1E 03` | `09 C4` | 2500 ms |
| standby-interval mode 4 | `1E 04` | `01 F4` | 500 ms |
| standby-interval mode 5 | `1E 05` | `09 C4` | 2500 ms |
| sleep-mode | `21` | `01`, later set to `00` | Sleep effect breathe, then switched off from the UI |
| sleep-timeout | `22` | `00 0F`, later set to `00 05` | 15 minutes, then 5 minutes from the UI |
| breath-interval | `23 01` | `13 88` (also `0A 9B`) | 5000 ms |
| startup-color | `26` | `66 B8 FF` | #66B8FF — same as R25 |
| shutdown-color | `27` | `66 B8 FF` | #66B8FF — same as R25 |
| led-color, all strips/modes | `20 [s] [m] [l]` | `56 F7 FC`, some `FF 00 00` | #56F7FC cyan — same default as R25 |
| sleep-led-color, both strips | `25 [s] 01 [l]` | `56 F7 FC`, some `FF 00 00` | Per-LED sleep colour |

Note the per-mode intervals differ from the R25's (3000 / 1000 / 1714 /
100 ms). Since this unit's values were user-touched, that is not yet
evidence of different per-model defaults.

### Worked example: set strip 0 LED 4 to magenta in constant mode

```
7E 06 20 12 20 00 01 04 FF 00 FF [chk]
                │  │  │  │  │  │  │
                │  │  │  │  │  │  └ B
                │  │  │  │  │  └─── G
                │  │  │  │  └────── R
                │  │  │  └───────── led index = 4
                │  │  └──────────── mode = 1 (constant)
                │  └─────────────── strip = 0
                └────────────────── cmd = 0x20 (led-color)
```

### Worked example: set standby mode to breath with 5000ms interval

```
7E 02 20 12 1D 02 [chk]          ← mode = breath
7E 04 20 12 1E 02 13 88 [chk]    ← breath interval = 5000ms
                │  │  │  │
                │  │  └──┴─── interval = 0x1388 = 5000ms (BE u16)
                │  └────────── mode byte = 02 (targets breath register)
                └───────────── cmd = 0x1E (standby-interval)
```

### Write response behavior

A `0x20` write produces **one** reply: an ACK on group `0xA0` (write-group |
`0x80`, dev `0x21`) echoing the request payload byte-for-byte, **12–50 ms**
after the write (n=10, median 26 ms, range 12.5–50.0 ms — the spread is queue
position behind the ~2 s config poll, not per-command cost; the five writes
issued as one burst at `+3.434 s` all ACK'd on the same 26.1 ms tick).

Group `0xA2` is a *read* reply only. Across the whole R16 Ultra capture,
**7104 of 7104** `0xA2` frames matched an outstanding `0x22` read — zero
unsolicited ones.

Do not wait on an `0xA2` to confirm a write — trust the `0xA0` echo or issue
an explicit `0x22` read. A write is **not** followed by an unsolicited
notification: because the host re-reads the whole block every ~2 s (see
[Config poll loop](#config-poll-loop)), the next poll's `0xA2` for the
register you just wrote arrives ~150 ms after the ACK and is easily mistaken
for one.

> Measured on R16 Ultra firmware `RS21-D12-HW BM-CU-V10`. Not re-checked
> against the R25 capture.

### Per-LED colour configuration

Three independent per-LED colour tables live on this group, all
index-addressed by `[strip] [mode] [led]`:

| Command | Cmd | Selector | Host UI control | What it colours |
|---|---|---|---|---|
| `led-color` | `0x20` | `[strip] [mode] [led]` | **"idle effects" colour** | The palette for standby mode `[mode]` — one palette per mode, see [below](#the-0x20-mode-byte-is-the-standby-mode-value) |
| `sleep-led-color` | `0x25` | `[strip] 01 [led]` | **"sleep light effect" colour** | The sleep palette. Middle byte always `01` — no other value observed |

Both UI controls were exercised on a single LED and each produced exactly one
frame, so the two tables are independent — changing the idle colour does not
touch the sleep colour or vice versa.

Payload is 3 bytes of straight `R G B`, no scaling. One frame sets exactly
one LED on one strip; there is no bulk or broadcast form, and changing a
single LED in the host UI puts exactly one frame on the wire.

#### Host UI index ↔ wire index

The host UI numbers the base LEDs **1..N across both strips**, while the wire
addresses them **0-based per strip**. Confirmed by four single-LED changes on
the 6-per-strip R16 Ultra — both ends of the range, on both colour tables:

| UI LED | UI control | Wire frame | strip | led |
|---|---|---|---|---|
| 1 | sleep light effect | `7E 07 20 12 25 00 01 00 10 4C BC` | 0 | 0 |
| 12 | sleep light effect | `7E 07 20 12 25 01 01 05 FF 00 00` | 1 | 5 |
| 1 | idle effects | `7E 07 20 12 20 00 01 00 FF 00 00` | 0 | 0 |
| 12 | idle effects | `7E 07 20 12 20 01 01 05 FF 00 00` | 1 | 5 |

The same UI number resolves to the same `[strip] [led]` pair on both
commands, so the mapping is a property of the UI, not of either table.

So for a base with `L` LEDs per strip:

```
strip = (ui - 1) / L        led = (ui - 1) % L
```

UI 1–6 → strip 0 LEDs 0–5, UI 7–12 → strip 1 LEDs 0–5. On a 9-per-strip base
the same rule gives UI 1–9 → strip 0 and UI 10–18 → strip 1. The UI number is
**not** a wire field — nothing on the wire is 1-based.

#### The `0x20` mode byte **is** the standby-mode value

The byte carries the **standby mode the palette belongs to**. Each standby
mode has its own independent per-LED palette, and the UI's single "idle
effects" colour control writes whichever one is active.

Established by a controlled pair on the R16 Ultra — same UI control, same
LED, only the active `standby-mode` differed:

| `standby-mode` (`0x1D`) | UI action | Write |
|---|---|---|
| `1` | set UI LED 1 idle colour red | `7E 07 20 12 20 00 **01** 00 FF 00 00` |
| `2` (breath) | set UI LED 1 idle colour red | `7E 07 20 12 20 00 **02** 00 FF 00 00` |

Same rule as the interval registers: the control edits the *active* mode's
register (see [Standby mode details](#standby-mode-details)).

The two palettes are **independent storage, not aliases** — 1489 poll replies
across 5 distinct `(strip, led)` pairs showed mode 1 and mode 2 holding
different colours for the same LED at the same time. The clearest case is
strip 1 LED 5, which ended the capture at `mode1 = #FF0000` /
`mode2 = #56F7FC`: a single-LED idle-colour edit made while mode 1 was active
changed mode 1 only and left mode 2 on its old value. Any host UI therefore
needs a per-mode editor, or it silently edits whichever mode happens to be
live.

**Consequence for writers:** a palette write only affects the standby mode
whose number is in the mode byte. Writing mode 1's palette while the base
displays mode 2 changes nothing visible. Read `0x1D` first, or write every
mode you care about.

Only modes **1 and 2** are ever polled, and that set is fixed rather than
follow-the-active-mode: switching the base to mode 3 left the poll still
reading exactly 1 and 2. Modes 3–5 have no palette (they generate their own
colours); mode 0's absence is unexplained.

> Untested: what the UI writes if you change an idle colour while a
> palette-less mode (3–5) is active — `mode=3`, a fallback to 1 or 2, or the
> control being disabled and no frame at all.

#### Worked example: set UI LED 1's sleep colour to blue

Captured verbatim, including the readback that proves it stuck:

```
+5191.568  A2 21 25 00 01 00 56F7FC     poll: LED is currently #56F7FC (cyan)
+5193.828  7E 07 20 12 25 00 01 00 10 4C BC [chk]
                        │  │  │  │  └──────── RGB = #104CBC
                        │  │  │  └─ led   = 0   (UI "LED 1")
                        │  │  └──── fixed 01    (sleep table)
                        │  └─────── strip = 0
                        └────────── cmd   = 0x25 (sleep-led-color)
+5193.874  A0 21 25 00 01 00 10 4C BC     ACK, exact echo, +46 ms
+5193.975  A2 21 25 00 01 00 10 4C BC     next poll returns the new value
```

Every subsequent poll for that selector returned `#104CBC` for the rest of
the capture — the value persists in firmware, no save/commit command needed.

#### Config poll loop

The host re-reads the **entire** ambient config block on a ~2 s loop
(p50 2.015 s, min 0.91 s), 48 distinct selectors per full sweep:

| Cmd | Selectors | Why that many |
|-----|-----------|---------------|
| `0x20` led-color | **24** | 2 strips × 2 modes × 6 LEDs |
| `0x25` sleep-led-color | **12** | 2 strips × 6 LEDs |
| `0x1E` standby-interval | 4 | modes 2, 3, 4, 5 |
| `0x1C` `0x1D` `0x1F` `0x21` `0x22` `0x23` `0x26` `0x27` | 1 each | scalar settings |

The `24` and `12` are the geometry showing through: on a 9-per-strip base the
same sweep would be 36 and 18. Reads are pipelined — a sweep is issued as one
burst and the base answers FIFO, so a write landing mid-sweep can be answered
by a read that was issued *before* it (that is exactly what happens at
`+5193.9` above).

### Live telemetry commands (RPM indicator)

During game telemetry the host drives the base LEDs as an RPM bar using two
live commands on the **write** group `0x20` at dev `0x12` — mirroring the
wheel LED `0x19`/`0x1A` pair but with cmd bytes `0x1A`/`0x1B`:

| Cmd | Role | Sent when |
|-----|------|-----------|
| `0x1A` | live-color-chunk — per-LED palette | Only when the palette changes |
| `0x1B` | live-bitmask — which LEDs are lit | Every update tick, plus a 1 Hz keepalive when static |

Both strips are addressed independently, and both are always written as a
**pair in one burst** — strip 0 first, strip 1 4–25 µs later (453 of 455
bitmask frames paired inside 20 ms). Neither live command produces a
response; unlike the config commands on this group they are fire-and-forget.

> **Sources:** `usb-capture/startupchime/R25 LED Telemetry.pcapng` (R25,
> 9 LEDs/strip, 2026-05-05) and bridge capture
> `bridge-r16ultra-20260822-190820.jsonl` (R16 Ultra, 6 LEDs/strip,
> 2026-08-22 — 524 live frames over 92 s covering ramp, redline blink and
> release).

#### `0x1A` — live-color-chunk

Sets per-LED colors. Same 4-byte-per-entry format as wheel live-colors
(`0x19`):

```
7E [N] 20 12 1A [strip] [idx₀ R G B] [idx₁ R G B] ... [chk]
```

Each chunk carries **at most 5 entries** (20 bytes of LED data), so a strip
is covered by two chunks: chunk 1 = LEDs 0–4, chunk 2 = whatever LEDs
remain. That makes chunk 2's shape a function of strip length:

| Base | LEDs/strip | Chunk 1 | Chunk 2 |
|---|---|---|---|
| R16 Ultra | 6 | LEDs 0–4, 5 entries, wire `N=22` | LED 5 only, **1 entry, wire `N=6`** |
| R25 | 9 | LEDs 0–4, 5 entries, wire `N=22` | LEDs 5–8, 4 entries, wire `N=18` |

> **Do not pad chunk 2 to 20 bytes.** The wheel-LED command (`0x19`) needs
> a `[0xFF, 0, 0, 0]` trailing entry to keep the chunk a multiple of 20 so
> that zero-pad bytes are not interpreted as "set LED 0 black". The base
> firmware processes chunk 2 differently: padding it to `N=22` with an
> `0xFF`-indexed entry silently breaks `bitmask=0x01` (only the first
> LED lit) — 2+ active bits keep working, but a single first LED produces
> nothing. Send chunk 2 at exactly the remaining LED count, no padding.

Colors are only re-sent when the palette changes — not every frame. In the
R16 Ultra capture the entire 92-second effect needed just **28** `0x1A`
frames against 120 `0x1B` frames.

**Worked example — R16 Ultra, both chunks of strip 0 at full bar** (verbatim
from capture, `+180.660 s`):

```
7E 16 20 12 1A 00 00 0000FF 01 1500EA 02 2A00D5 03 3F00C0 04 5500AA [chk]
      │        │  │  └ LED 0..4 = the blue→purple half of a 12-step ramp
      │        │  └ strip = 0
      │        └ cmd = 0x1A
      └ N = 0x16 = 22

7E 06 20 12 1A 00 05 6A0095 [chk]
      │           └ LED 5 only — chunk 2 is ONE entry on a 6-LED strip
      └ N = 6
```

**The two strips do not have to carry the same palette.** On the R16 Ultra
the host sends a single 12-step gradient split across the pair — strip 0
gets the blue half, strip 1 the red half — while the *bitmask* stays in
lockstep, so both strips light the same LED count:

| LED | strip 0 | strip 1 |
|-----|---------|---------|
| 0 | `0000FF` | `7F0080` |
| 1 | `1500EA` | `94006B` |
| 2 | `2A00D5` | `AA0055` |
| 3 | `3F00C0` | `BF0040` |
| 4 | `5500AA` | `D4002B` |
| 5 | `6A0095` | `E90016` |

That contradicts the earlier R25 reading of "PitHouse mirrors identical data
to both strips" — mirroring is one host's choice, not a protocol rule. The
palette itself is entirely host-side: the R25 capture ramps
green → yellow → red → magenta, this one ramps blue → red. Treat any
specific gradient as an application preference, not a device property.

#### `0x1B` — live-bitmask

Controls which LEDs are actually lit. The palette set by `0x1A` is latched
in firmware; `0x1B` gates it.

```
7E 06 20 12 1B [strip] [u32le bitmask] [chk]
```

Bit N = LED N. Payload is always a 4-byte little-endian u32 regardless of
strip length; the used width is the strip's LED count (`0x3F` max on a
6-LED strip, `0x1FF` on a 9-LED strip).

**Bitmask progression, 6-LED strip (R16 Ultra):**

| Mask | Bar | LEDs lit |
|------|-----|----------|
| `0x00` | `......` | 0 (idle / released) |
| `0x01` | `#.....` | 1 |
| `0x03` | `##....` | 2 |
| `0x07` | `###...` | 3 |
| `0x0F` | `####..` | 4 |
| `0x1F` | `#####.` | 5 |
| `0x3F` | `######` | 6 (full bar) |

A 9-LED strip continues the same pattern to `0x7F`, `0xFF`, `0x1FF`.

#### Redline blink

Once the bar fills, the host blinks it by **toggling the bitmask only** —
`0x3F` ⇄ `0x00`, both strips together, with no `0x1A` traffic at all. The
latched palette is what reappears on each ON phase, so a blink costs two
6-byte frames per half-cycle.

Measured on the R16 Ultra (69 ON phases / 68 OFF phases):

| Phase | Duration (median) | Range |
|-------|-------------------|-------|
| ON (`0x3F` held) | **151 ms** | 50–302 ms |
| OFF (`0x00` held) | **101 ms** | 0–202 ms |
| Full period | **252 ms** (≈ 4 Hz) | 202 ms – … |

The quantisation to ~50 ms multiples (50 / 101 / 151 / 202 / 302 ms) says
the host is toggling on its own update tick rather than asking firmware for
a blink mode — there is no "blink" command in this group. A host that wants
a redline flash has to drive it frame by frame.

On release the bar ramps back **down** through the same masks
(`0x1F`, `0x0F`, `0x07`, `0x03`, `0x01`, `0x00`) rather than cutting to
zero, then settles into the 1 Hz `0x00` keepalive.

#### Telemetry send cadence

Measured from the R16 Ultra capture (strip-0 `0x1B` inter-frame gaps):

| State | Median gap | Rate |
|-------|-----------|------|
| Idle (mask `0x00` held) | 1008 ms | **1.0 Hz keepalive** |
| Active (bar moving or blinking) | 101 ms | **~10 Hz** (p90 152 ms) |

- Bitmask (`0x1B`): every update tick, both strips, and re-sent at 1 Hz even
  when unchanged — the firmware blanks the strip if the bitmask goes stale.
- Colors (`0x1A`): on palette change only.
- The idle keepalive keeps sending `0x00`; it does **not** go silent. Going
  silent is what hands the strip back to the firmware standby animation.

#### Open: non-contiguous masks on strip 1

11 of 910 mask frames carry a bar that is *not* a contiguous low-bit run,
and every one of them is on **strip 1**, in two short clusters
(`+202.1 s`, `+204.4 s`, `+215.6 s`, `+217.4–217.7 s`). In each case the
value is the concurrent strip-0 mask with an extra high bit set:

| strip 1 | bar | concurrent strip 0 | delta |
|---|---|---|---|
| `0x27` | `###..#` | `0x07` | `\| 0x20` (LED 5) |
| `0x2F` | `####.#` | `0x0F` | `\| 0x20` |
| `0x37` | `###.##` | `0x07` | `\| 0x30` (LEDs 4+5) |
| `0x13` | `##..#.` | `0x03` | `\| 0x10` (LED 4) |
| `0x11` | `#...#.` | `0x01` | `\| 0x10` |
| `0x10` | `....#.` | `0x00` | `\| 0x10` |

Consistent with a top-LED hold (peak marker) on the falling edge, or with
the host computing strip 1's mask from a lagging value. Not enough frames
to tell which, and it never appears on strip 0. Unresolved.

### Group `0x1F` (31) — Hub status reads

Polled alongside the LED telemetry at ~10 Hz. Read-only, device `0x12`,
responses on group `0x9F` device `0x21`.

| Cmd | Request | Response | Notes |
|-----|---------|----------|-------|
| `4F 08` | `4F 08 00` | `4F 08 FF 00` | Status register (constant in capture) |
| `4F 09` | `4F 09 00` | `4F 09 FF 00` | Status register |
| `4F 0A` | `4F 0A 00` | `4F 0A FF 00` | Status register |
| `4F 0B` | `4F 0B 00` | `4F 0B FF 00` | Status register |
| `4D` | `4D` | `4D 64` | Possibly brightness readback (0x64 = 100) |
| `0A` | `0A` | `0A 00` | Init-time query (sent once) |
| `0F` | `0F` | `0F 00` | Init-time query (sent once) |

### Why two groups for one feature

The dual-group `0x20` / `0x22` split (write vs read) follows the same
convention as group `0x28`/`0x29` for the wheelbase settings (read /
write split with shared cmd IDs). Read group has `set-` cmds removed and
returns currently-stored value of the corresponding write.

### Plugin status

The plugin drives these LEDs. The `base-ambient-*` command block is registered
in [`MozaCommandDatabase`](../../../Protocol/MozaCommandDatabase.cs)
(§ BASE AMBIENT LEDS); the live RPM bar is emitted by
[`MozaBaseLedDeviceManager`](../../../Devices/Led/MozaBaseLedDeviceManager.cs)
as a virtual SimHub LED device; config and per-LED palette writes go through
`HardwareApplier.ApplyBaseAmbientToHardware`. Capability is gated on an `0xA2`
answer to `base-ambient-brightness` (`DeviceProber`), which R9/R12 silently
drop.

**Geometry** is resolved from the base model name by
[`BaseModelInfo`](../../../Devices/BaseModelInfo.cs) — 6 LEDs/strip for `R16`,
9 for `R21`/`R25`/`R27`, 9 for anything unrecognised. It drives three things:
the LED manager's strip split, bitmask width and chunk-2 entry count; the
`LedCount`/`RepeatCount` patched into the deployed `device.json`; and the
per-LED read/write index range.

Because the model name is the only discriminator, `DeviceProber` reads it
**before** the ambient capability probe (the device answers FIFO, so the
reverse order left `BaseModelName` empty at deploy time). A late-arriving model
name re-deploys the definition from `MozaPlugin.Inbound`; the deployer's
staleness check makes that idempotent.

**Ranges implemented:** brightness slider 0–100 (percent), sleep-timeout slider
0–300 (minutes), standby dropdown 6 entries mapping 1:1 onto modes 0–5, sleep
dropdown labelled by effect (Disabled / Breathing) rather than on/off.

**Interval sliders** are per effect and hide when the effect has no interval:
the standby speed row shows only for modes 2–5 and writes `1E [active mode]`;
the sleep breathing speed row shows only while `sleep-mode` is 1 and writes
`0x23 01`.

**Per-LED editor** carries three rows — idle·constant (`20 [strip] 01 [led]`),
idle·breathing (`20 [strip] 02 [led]`) and sleep·breathing
(`25 [strip] 01 [led]`). Three rows rather than one because these are three
separate registers with independently stored values, proven above; collapsing
them would make an edit land on whichever mode happened to be active.

Writers must respect the effect-indexed layout: each palette write carries its
own mode byte, so all three palettes can be written whatever mode is live, but
only the active mode's palette is visible.

> Stored profiles from earlier builds are **not** migrated. A profile whose
> `BaseAmbientStandbyMode` is `0` was written by the old dropdown's "Constant"
> entry and now correctly means *off*; the user re-picks their mode once.
> Brightness is clamped to 100 on write so an old 0..255 value cannot go out
> of range.
