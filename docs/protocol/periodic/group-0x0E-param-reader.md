## Other periodic commands

### Group 0x0E parameter table reader / debug console (host → devices 0x12/0x13/0x17, ~9 Hz)

Pithouse sends 158 per session. Host reads EEPROM parameters sequentially and receives firmware debug log output.

**Request format:** `7E 03 0E [device] 00 [table] [index] [checksum]`
- `table`: EEPROM table number (0x00 = base config, 0x01 = alt)
- `index`: parameter index, incremented sequentially (0x01, 0x03, 0x04, ...)

**Response format (group 0x8E):**
- **Parameter values** (cmd=00:00, n=7): `[index] 00 00 [value bytes]` — stored parameter at index
- **Debug log text** (cmd=05:xx, variable length): ASCII firmware log output, e.g.:
  - `"RFloss[avg:0.00000%] recvGap[avg:4.25699ms]"` — NRF radio stats
  - `"INFO]param_manage.c:340 Table 2, Param 43 Written: 0"` — EEPROM write confirmation

Debug log entries confirm `0x40/1E` channel config commands write to EEPROM. Diagnostic only — **not required for telemetry**.

Starts ~1s after session opens. Per-device targeting is **setup-dependent**: on the R5 `extreme_dogging` capture Pithouse polls base (0x12), wheel (0x17, 68 frames) and pedals (0x13); on the R9 + bare-"CS" capture (`cs v2(1).pcapng`) it polls `0x0E` **only on the base** (`0e12`/`0e13`) and sends **zero** `0e17` to the wheel.

**Wheel poll removed from the plugin (do not re-add to 0x17).** The plugin briefly sent a fixed wheel param poll `7E 03 0E 17 00 00 01` every ~5 s, added as a presumed keepalive. It was removed because:
- The response is always the unset sentinel — `8E 71 00 00 01 FF FF FF FF` (index 01 → value `0xFFFFFFFF`); the bare-"CS" rim has these param tables unprovisioned, so no useful data ever comes back. (Pithouse on R5 sweeps indices 01..06+, all `FF FF FF FF`.)
- Pithouse does **not** poll the wheel's param manager on the matching R9 rig, so it isn't load-bearing there.
- Group `0x0E` is the param-manager channel (`param_manage.c`) — the same code that emits the legacy-"CS" `Table 8: Failed to Read Parameter` storm (see [`../devices/wheel-0x17.md`](../devices/wheel-0x17.md)); poking it on the wheel is at best pointless and at worst implicated in that storm.

**A second, accidental `0x0E` emitter also existed and is now gone.** Twelve slots of
the telemetry widget-poll cycle (`TelemetrySender.SendOneWidgetPoll`) were written to
probe `0x0E` on devs `0x12`/`0x13`/`0x17`/`0x19`, but never did: the slot's
`byte dev = (byte)(s / 3 switch { 0 => 0x12, … })` parses as `s / (3 switch { … })`,
i.e. `s / 25`, which is `0` for every slot — so all twelve addressed **device `0x00`**
and the `cmd` ternary chain fell through to its `0x13` default. An audit of 35
diagnostic bundles (`tools/poll-audit`) found exactly three distinct frames on the wire
— `0e 00 ǀ 00 00`, `0e 00 ǀ 00 01`, `0e 00 ǀ 00 13`, ~241 of each — and none of the
twelve intended ones. The block was **deleted rather than repaired**, for the same
reasons as the wheel poll above: repairing it would start sending `0e`→`0x17` traffic
this plugin has never actually emitted, on the channel implicated in the storm.

The base/pedal `0x0E` polls are unaffected; the plugin keeps the base alive via its group-`0x40` `StatusPollCommands`.

Short-form host poll also sent ~1 Hz to device 0x13: 3-byte payload `00 01 XX` with 16-bit BE countdown counter starting at 0x013A (314). Base echoes back + 4 unknown bytes.
