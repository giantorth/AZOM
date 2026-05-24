# Moza protocol reference

Hierarchical split of the original `docs/moza-protocol.md`. Layout is **function-first, device-second** — most protocol detail is cross-cutting (telemetry frame format applies to all wheels), so functional folders are the primary axis. Per-device files in [`devices/`](devices/) hold device-scoped command tables (groups, sub-cmds, byte widths) that don't fit cleanly into the cross-cutting topic folders.

## Layout

| Folder | Scope |
|--------|-------|
| [`wire/`](wire/) | Frame header, checksum, 0x7E byte stuffing, response transforms, wheel write echoes, command chaining |
| [`transport/`](transport/) | USB interfaces and endpoints, internal serial bus topology |
| [`identity/`](identity/) | Device identity probes, sub-device wrapping, model name table, dev-type table, per-device identity quirks |
| [`telemetry/`](telemetry/) | Channel catalog, value encoding, live telemetry stream (`0x43/0x17 7D 23`), enable/disable control |
| [`sessions/`](sessions/) | SerialStream session layer (`7c:00`/`fc:00`): chunk format, CRC, ACKs, lifecycle, port allocation, compressed transfers |
| [`tier-definition/`](tier-definition/) | Session 0x01/0x02 handshake, device description, channel catalog response variants (CSP v0 / VGS v2), config parameters |
| [`dashboard-upload/`](dashboard-upload/) | Dashboard upload paths (A: session 0x01 FF-prefix, B: session 0x04 sub-msg), config RPC, mgmt RPC, sub-msg headers, chunk trailers |
| [`channel-config/`](channel-config/) | Group 0x40 burst, post-upload / active display cycle |
| [`pithouse-udp/`](pithouse-udp/) | Second PitHouse external API — plain CBOR-over-UDP on port 40288. Used by the RallySimFans launcher (RBR) for steering-lock control and likely by other wheel-config tools that don't link the MOZA SDK DLL. Distinct from the CoAP SDK on port 40266 (see [`identity/device-catalog-manifest.md`](identity/device-catalog-manifest.md)). |
| [`leds/`](leds/) | LED color commands, base ambient strips (`0x20/0x22`), wheel LED group architecture (`0x3F/0x40` extended) |
| [`settings/`](settings/) | Wheel settings (`0x3F/0x40`, dev `0x17`), dashboard settings (`0x32/0x33`, dev `0x14`), EEPROM direct access (`0x0A`) |
| [`periodic/`](periodic/) | Group `0x0E` parameter reader, `0x1F`, `0x28`, `0x29`, `0x2B` periodic / occasional commands |
| [`devices/`](devices/) | Per-device pages — main hub (`0x12`), wheelbase (`0x13`), dash (`0x14`), wheel (`0x17`), pedals (`0x19`), shifter / handbrake / e-stop, AB9 active shifter. Device ID table cross-links into functional pages |
| [`plugin/`](plugin/) | SimHub plugin implementation notes: startup phases, session management, tier impl, reassembly fallback |
| [`findings/`](findings/) | Dated journal entries from deep-dive sessions. Kept verbatim for traceability; canonical info is reflected in the topical pages |

## Top-level pages

- [`heartbeat.md`](heartbeat.md) — heartbeats, keepalives, unsolicited messages
- [`startup-timeline.md`](startup-timeline.md) — full connect-to-telemetry sequence
- [`open-questions.md`](open-questions.md) — outstanding unknowns

## Reference pages

- [`GLOSSARY.md`](GLOSSARY.md) — jargon, wheel/base model names, firmware eras, protocol-layer terms
- [`FIRMWARE.md`](FIRMWARE.md) — firmware-era matrix: which captures, which wheels, which pages are era-specific
- [`../../usb-capture/CAPTURES.md`](../../usb-capture/CAPTURES.md) — per-capture inventory (wheel, software, scenario, observed traffic)

## Cross-cutting references

- Foundational frame format applies to **all** device traffic. Read [`wire/`](wire/) before anything else.
- Authoritative command DB: `Pit House/bin/rs21_parameter.db` (SQLite, 919 commands). Per-device command tables in [`devices/`](devices/); value-encoding rules in [`telemetry/service-parameter-transforms.md`](telemetry/service-parameter-transforms.md).
- USB capture methodology: see `docs/usb-capture.md`.
- Plugin-side wire divergence and PitHouse-observed deviations: see [`findings/`](findings/).

> **Status (2026-04-30):** Multi-pkg-level dashboards (Grids, Rally V4) rendering live on Type02 firmware (R5 base + W17 wheel). Plugin parity with PitHouse confirmed for Nebula (1 pkg-level), Rally V4 (3 pkg-levels), Grids (2 pkg-levels). New learnings landed:
>
> - **Broadcast count varies by sub-tier count.** Single-pkg-level dashboards = 3 broadcasts × 1 sub-tier. Multi-pkg = 4 broadcasts × N sub-tiers. PitHouse fills wheel tier-slots up to `broadcasts × sub_count`; widgets bound to slots beyond that stay un-subscribed and never animate. Plugin formula: `broadcasts = (subCount == 1) ? 3 : max(4, subCount + 1)`. See [`tier-definition/version-2-compact-vgs.md`](tier-definition/version-2-compact-vgs.md).
> - **PitHouse per-dashboard sub-tier split is custom, NOT derived from `Telemetry.json` `package_level`.** PitHouse Grids splits 8 channels into 5+2+1 (pkg=30/500/2000) even though `Telemetry.json` marks them all pkg=30 or pkg=2000. Plugin currently groups by `Telemetry.json` pkg_level (8+12 for Grids); wheel still renders because tier-def is internally consistent and wheel binds widgets via channel idx, not flag/sub-tier position. Underlying source for PitHouse's per-dashboard split is unknown — likely embedded in PitHouse's own dashboard catalogs, not in the wheel firmware or `Telemetry.json`.
> - **Tyre compression codes `0x10` (`tyre_pressure_1`) and `0x11` (`tyre_temp_1`) were inferred and Type02 wheel firmware does NOT decode them.** Live Grids dashboard with these codes: tyre widgets stay at 0. Workaround: send tyres as `float` (code `0x07`, width `32`); wheel decodes IEEE float and displays raw game-data values directly (PSI / °C). `percent_1` (code `0x0E`) is similarly unverified; switch tyre wear to float if those widgets fail. Other inferred codes (`0x12` `track_temp_1`, `0x13` `oil_pressure_1`, `0x15` `float_600_2`, `0x16` `brake_temp_1`) suspect on Type02 — verify before relying.
> - **Plugin Telemetry.json default SimHub mappings are sparse** — most channels ship with empty `simhub_property` / `simhub_field`, so live game data doesn't bind even when the wheel is subscribed. As of 2026-04-30, ABS/TC + 8 tyre channels mapped (`DataCorePlugin.GameData.*`); ~437 sectors still unmapped. See [`plugin/`](plugin/) for the resolver path.
>
>> - **Dashboard switching** (updated 2026-05-17): FF-record on session 0x02 is the primary switch command. Slot = 0-based index into `configJsonList` (alphabetical). Sessions 0x01-0x03 stay open across switches — no Stop+Start required. After the FF-record:
>>   1. Wheel re-pushes channel catalog (`\x01`-prefix shorthand URLs) on b2h sess=0x01 at +50-1000 ms, ending with a `0x06 04 00 00 00 <u32>` END marker (the tier-def version handshake — host MUST echo this u32 in its tier-def emissions or the wheel rejects the binding).
>>   2. Host emits a TIER-DEF BURST of 3-13 emissions paced ~1 s apart (count scales with dashboard sub-tier complexity), each rebuilt with the wheel's most-recent END value and at an advanced flag-base (monotonic, not reset in-session). Preamble (tag 0x07/0x03) is NOT re-sent — preamble is one-shot per session.
>>   3. Value-frame payload size recovers as the wheel commits widget bindings (~+1.2 s).
>>
>>   PitHouse does NOT re-parse its own catalog from the wheel's push — uses cached mzdash channel metadata + initial preamble catalog indices. Both PitHouse-initiated and wheel-initiated switches share the same handshake; the differentiator is which side emits the kind=4 FF-record. See [`findings/2026-04-30-dashboard-switch-3f27.md`](findings/2026-04-30-dashboard-switch-3f27.md) and [`tier-definition/handshake.md`](tier-definition/handshake.md) § In-game dashboard switch.
> - **Dashboard download** (2026-05-01): PitHouse downloads mzdash files from the wheel on session 0x0B during cold-start. Comma-separated UTF-16LE path list → wheel responds with zlib-compressed mzdash JSON in 4360-byte blocks. See [`dashboard-upload/download-session-0x0b.md`](dashboard-upload/download-session-0x0b.md).
> - **Startup chime & base LEDs** (2026-05-05): Capture-verified from R25 base. 10 built-in chimes (index 1–10), volume 0x00–0xFF, enable/disable toggle. Base ambient LEDs: 6 standby modes, per-mode interval registers, startup/shutdown color. Live RPM indicator via `0x1A` color chunks + `0x1B` bitmask (2 strips × 9 LEDs, independently addressable). See [`leds/base-ambient-0x20-0x22.md`](leds/base-ambient-0x20-0x22.md) and [`devices/wheelbase-0x13.md`](devices/wheelbase-0x13.md) § Group 0x2A.
>
> **Older firmware (VGS, CS, KS) is NOT covered by this banner** — those wheels predate Type02 and use the legacy V2 tier-def shape with different broadcast semantics. See [`FIRMWARE.md`](FIRMWARE.md) for the era matrix.

> **Status (2026-04-29):** Type02 firmware (R5 base + W17 wheel) single-pkg telemetry rendering verified live. Major corrections landed in [`tier-definition/version-2-compact-vgs.md`](tier-definition/version-2-compact-vgs.md) (per-tier interleaved enables, **3-tier broadcast at flags 0/1/2 with same channels**) and [`telemetry/live-stream.md`](telemetry/live-stream.md) (Type02 wheel binds widgets to flag=2, NOT flag=0 — for single-pkg dashboards). Previous descriptions for the V2 tier-def shape were wrong on this firmware era — wheel display stays blank until format matches PitHouse byte-for-byte. Plus `7d23` value frames use legacy N convention (N=8+data, NOT Type02 10+data) on this firmware.

> **Status (2026-04-28):** Hierarchical split complete; leaf pages expanded with frame layouts, field tables, byte offsets, and worked examples (2026-04-28 pass). Some sections that were originally dated "findings" entries have been split out into their topical homes (e.g. `dev_type` table → [`identity/dev-type-table.md`](identity/dev-type-table.md)); see `docs/moza-protocol.md` for the full redirect map.