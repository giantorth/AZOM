# Motion platform — modes, run state, status records and sequences

Decoder: `tools/motion-decode control <jsonl>` (collapsed timeline) and `tools/motion-decode log`
(firmware narration). All frames target the controller, dev `0x12` (replies `0x21`).

## Controller mode — `0x1E/03` set, `0x1F/03` poll

```
h2b  7E 05 1E 12 03 [mode] 00 00 00 [chk]          set mode (echoed verbatim on 9E 21)
h2b  7E 05 1F 12 03 00 00 00 00 [chk]              poll, 2 Hz, always running
b2h  7E 05 9F 21 03 [target] [current] 00 [flags]  reply
```

`target` is the last requested mode, `current` the mode the controller is actually in; they
differ during transitions (`0D 05` = going to 13, currently 5). The firmware log mirrors each
write with `safety_control.c:422 Set next mode:N` and each arrival with `Controller Set curr
mode:N`; every actuator follows with `Actuator Set curr mode:N`.

| Mode | Name | How it is reached | What happens | Evidence |
|------|------|-------------------|--------------|----------|
| `0` | idle | host write, and the default after calibration / self-test | no motion; motors stay enabled | every capture |
| `1` | **game** | host write when the user presses *Start* / a game is detected | controller consumes the `0x22` streams; `ModelMode Set CtrlMode:5` | `100_STROKE` 18.4 s, `spa`, `nurburgring` 46 s |
| `2` | manual motion test | host write when the "Manual Motion Test" toggle is on | controller follows `0x1E/07` setpoints | `Pitch±5`, `roll±5`, `Heave±5`, `±100` (poll reads `02 02` throughout) |
| `3` | position calibration (provisional) | host write right after mode 7 completes | `ModelMode Set CtrlMode:2`; firmware prints `Device Type : 1, eeprom data: 1`, `Output Displacement : 0 0 0 0`, `Calib Length/Width`; actuators enter mode 3 ~3 s later | `Startup-Shutdown` 39.3 s, `nurburgring` 9.0 s |
| `4` | (transient) | never written; seen as `current` for one poll after leaving game mode, with flag `0x01` | — | `100_STROKE` 62.8 s |
| `5` | armed (transitional) | never written directly; the controller passes through 5 on the way to 1, 7, 8 or 13 (`Controller Set curr mode:5` always precedes the target) | — | all captures |
| `7` | travel self-check | host write 3–10 s after the config push on connect (`100_STROKE` begins mid-session with one) | `CtrlMode:6`; error 259 "Travel Not Check" raised then cleared on `selfCheckSuccess`; firmware prints `pitch_offset / roll_offset`; flag `0x80` while running; ~1.6–2 s | `Startup-Shutdown` 37.3 s, `nurburgring` 7.0 s, `100_STROKE` 2.9 s |
| `8` | port self-test | host write after the user clicks *Configure to Self-Test* (preceded by mode 5 + the `0x1E/02` role writes) | each actuator moves in turn so the user can confirm the port mapping; ~14 s; flag `0x20` | `Selftest` 5.9 s, `ports and self test` 22.6 s |
| `13` (`0x0D`) | standby | host write on *Close* (and the controller's own power-on state) | motors disabled: actuators log `Set MotorMode to:0` / `MotorMode From 12 to 0`; `CtrlMode:1` | `Startup-Shutdown` 49.1 s, `100_STROKE` 64.9 s |

Leaving 13 (power-on, or Motion Manager connecting) logs the reverse: `Set MotorMode to:12`,
`MotorMode From 0 to 12`, then `Controller Set curr mode:5` → `0`.

### Flag byte (last byte of the `0x9F/03` reply; the same byte appears in `0x21/01`)

| Bit | Seen when | Reading |
|-----|-----------|---------|
| `0x80` | from ~0.4 s after `set mode 7` until `selfCheckSuccess` | travel self-check running |
| `0x20` | during mode 8 and afterwards while the port map is unconfirmed (`ports and self test` starts with it set in mode 0; cleared by the config push) | port configuration pending |
| `0x04` | controller just powered up (mode 13, before enrol) and right after a cable re-seat (mode 5) | topology changed / actuators not enrolled |
| `0x01` | the transient mode-4 poll after game mode ended | unknown |

## Run state and identify-port — `0x20/02`

```
h2b  7E 03 20 12 02 [sub] [arg] [chk]      reply: 7E 00 A0 21 [chk] (empty ack)
h2b  7E 01 21 12 03 [chk]                  readback: 7E 02 A1 21 03 [sub] [chk]
```

| `sub arg` | Observed at | Reading |
|-----------|-------------|---------|
| `01 00` | right after the config push on connect; after the game stops (`100_STROKE` 62.9 s) | stop / parked |
| `02 00` | ×3 immediately after `set mode 3`; and repeatedly in game mode | hold |
| `03 00` | 0.3 s after the `0x22` stream starts; and repeatedly in game mode | run |
| `04 [port]` | user clicks a port in the "Controller" graphic (`ports and self test`: ports 4, 2, 6, 5) | identify port — the actuator on that port signals (lights/moves) |

In game mode the host flips between `03 00` and `02 00` many times per lap. Lining the writes up
with the Spa replay: `03` arrives when brake pressure rises at speed (0.10–0.27 brake at
37–72 m/s) and `02` after the braking zone; in `100_STROKE` the flips track Brake crossing
~5 % while stationary. The exact rule (which input, which threshold) is open; the stream itself
never stops across these flips.

## Status records — group `0x21`

Polled at 2 Hz each; replies are fixed 13-byte records.

**Controller status** `21 12 01` → `A1 21 01 [b1] 00 [b3] [b4] 00 [flags] 00 00 00 00 00 00`

| Byte | Values | Reading |
|------|--------|---------|
| b1 | `3C` (most captures), `3A` (`ports and self test`, `Coldstart`), `00` at power-up before enrol | unknown — stable per session, not a temperature (the log temps are 25–38 °C and change) |
| b3 | `04` | actuators present |
| b4 | `04`, `00` at power-up | actuators enrolled / axes connected |
| flags | as the mode-poll flag byte | — |

**Port status** `21 12 02 [port]` → `A1 21 02 [port] [present] 00 [slave] 00 00 00 00 [w0 w1 w2 w3]`

| Byte | Values | Reading |
|------|--------|---------|
| present | `01` / `00` | actuator answers on this port |
| slave | `06`..`09` (= low nibble of the slave id), `00` when absent | — |
| w0..w3 | zero outside game mode; in mode 1 a changing 32-bit word — `BF C9 6A 16` (the IEEE-754 pattern of the current yaw, −1.5736) and `84 80 00 59` were seen | unknown; per-port live word during motion |

The host polls only the populated ports once the map is known (`02 03`..`02 06`), all eight
during enrol.

**Run-state readback** `21 12 03` → `A1 21 03 [sub]` — the last `0x20/02` sub-command (`01`/`02`/`03`/`04`).

## Device type — `0x1E/0A`

`1E 12 0A 01` ("set device type 1"), written 6× in a burst at the end of every config push. The
firmware answers each with `[ERRO]controller_cmd.c:502 Set device type to : 1` and reports
`controller_model.c:57 Device Type : 1, eeprom data: 1` in mode 3 — type 1 = this 4-actuator
platform; no other value observed.

## `0x0E/03` notification + `0x0E/04` acknowledge

The controller sends `0E 21 03 01 [code] 00 01` unsolicited; the host answers `0E 12 04 01 [code] 01`
within ~2 ms and the controller echoes `8E 21 04 01 [code] 00 01`. Codes seen: `0D` and `04` at
boot, `03` ~1 s after `set mode 7`, `01` after `set mode 8`, `00` after the game stopped. They
are **not** the mode numbers; meaning unknown.

## Observed sequences

Times are seconds from the first frame of the named capture.

### Motion Manager connects to a powered controller (`Startup-Shutdown`, `Coldstart`, `nurburgring`)

1. `0x0E/03` notifications + `0x0E/04` acks; first firmware log lines (`curr_mode:13`, every
   `Slave(n) curr_mode:13`), actuators `MotorMode 0 → 12`, controller mode 5 → 0.
2. **+3 s** identity probes on `0x12`, then `0x1F` reads (`00 03 04 04 09 06 08`), `0x21/01`,
   `0x21/02` for all 8 ports, `0x1F/02` for all 8 ports.
3. **+3 s** (same second) identity probes on every populated port, `0x27` on each.
4. **+3.6 s** config push: `0x1E/05` × 5 slots, `0x1E/0B` lighting table (28 writes), `0x20/02 01 00`,
   `0x1E/06`, then `0x1E/0A 01` × 6. The firmware answers the table writes with
   `param_manage.c:340 Table 12, Param N Written`. See [`effect-parameters.md`](effect-parameters.md).
5. **+7 s** `set mode 7` (travel self-check) → error 259 → `selfCheckSuccess` ~1.6 s later →
   `set mode 0` → `0x20/02 02 00` → `set mode 3` (×4 in 20 ms) → `0x20/02 02 00` × 2.
6. From here the 2 Hz polls (`0x06` × 8, `0x1F/03`, `0x21/01`, `0x21/02` × 4) and the ~1 Hz
   `0x0E/00` register sweeps run until disconnect. The status pill shows "Connected, pending start".

### Controller power-cycled under a running Motion Manager (`hardware-powerup-software-running`)

Controller answers the mode poll with `03 00 0D 00 04` (standby, flag `0x04`) and `0x21/01` with
`b1=00 b4=00`; the host keeps polling. ~9.5 s later the controller reports mode 5 and the host
immediately replays the config push (step 4 above, effect slots twice), reads the port map,
re-enrols the ports and continues as in the connect sequence. No identity probes on `0x12`.

### Port assignment and self-test (`Selftest`, `ports and self test`)

`set mode 5` → `0x1E/02 [port] 01 [role] 00` for each populated port (1 s apart, each followed by
a `0x1F/02` readback) → `set mode 8` (flag `0x20`) → ~14 s of actuator movement → `set mode 0` →
`0x1F/08` read → full config push (`0x0B` table, `0x05` slots, `0x06`) → status "Self-Test Finished!".

### Game start / stop (`100_STROKE`, `spa`, `nurburgring`)

`set mode 1` (×3 in 40 ms) → `0x22/00` + `0x22/09` streams begin within 10 ms → `0x20/02 03 00`
0.3 s later → gear-shift / ABS events as they occur → … → streams stop → `0x20/02 02 00` /
`03 00` / `02 00` flurry → mode poll shows `current=4 flags=01` once → `0x20/02 01 00` →
(`100_STROKE`) `set mode 13`.

### Close (`Startup-Shutdown`)

`0x20/02 01 00` → `set mode 13` → `Controller Set curr mode:5` → actuators `MotorMode 12 → 0`
→ `curr mode:13` on controller and actuators (~3.3 s) → Motion Manager exits; the 2 Hz polls
stop with it. The platform stays powered in standby.

## Error codes and faults (firmware log)

| Code | Text | When |
|------|------|------|
| 259 | `error_code 259 occurs` / `err_diag_svr.c:69 Travel Not Check` | raised on entering mode 7, cleared by `selfCheckSuccess` |
| 260 | `error_code 260 clear` | cleared 9 s after a controller power-up (actuators back) |
| 269 | `error_code 269 occurs` / `clear` | raised at controller boot (`Controller ErrCode:8`), cleared when Motion Manager connects |
| `Controller ErrCode:4` | — | reported every 5 s while actuators are missing after power-up |

On the cold start the controller dumped `=========== Fault Pre-Data (100 samples) ===========`
and `Fault Post-Data (100 samples)`: 200 lines of `[n] I_total=… P0=… P1=… P2=… P3=…` (total
current and four per-actuator values) around the fault — a ring-buffer dump of the last fault,
emitted once. Full line catalog in [`firmware-log-0x0E.md`](firmware-log-0x0E.md).
