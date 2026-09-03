-- Moza Racing Protocol — Wireshark Lua Dissector
--
-- Generic dissector for the MOZA Racing internal serial protocol as it rides
-- the CDC-ACM bulk pipe of a wheelbase / CM2 / AB9 composite USB device.
-- Hooks the usbcom (CDC bulk data) layer.
--
-- Installation: copy to your Wireshark personal plugins folder, then
--   Wireshark > Analyze > Reload Lua Plugins  (or restart Wireshark)
--
-- Personal plugin folder (Help > About Wireshark > Folders shows the real one):
--   Linux:      ~/.local/lib/wireshark/plugins/   (older builds: ~/.config/wireshark/plugins/)
--   macOS:      ~/.local/lib/wireshark/plugins/
--   Windows:    %APPDATA%\Wireshark\plugins\
--
-- Or ad-hoc:  tshark -X lua_script:moza_dissector.lua -r capture.pcapng -Y moza
--
-- CAUTION: an installed copy in the personal plugin folder SHADOWS
-- `-X lua_script:` — Wireshark loads the folder first and the second
-- Proto("moza") registration is silently dropped, so you get the installed
-- version's output while thinking you tested the file you passed. When
-- iterating, either keep the installed copy in sync or isolate the run
-- (`HOME=$(mktemp -d) tshark -X lua_script:... `).
--
-- ── Frame format ───────────────────────────────────────────────────────────
--   7E [N] [group] [device] [N-byte payload] [checksum]
--
-- N counts payload bytes only. Response frames: group |= 0x80, device nibbles
-- swapped (0x13 -> 0x31, 0x17 -> 0x71).
--
-- ── 0x7E byte stuffing (docs/protocol/wire/checksum.md) ────────────────────
-- Every 0x7E from frame index 2 onward (group, device, payload, checksum) is
-- DOUBLED on the wire. N is a count of *decoded* payload bytes, so the wire
-- length of a frame is N+5 plus one extra byte per 0x7E in group/dev/payload/
-- checksum. This dissector de-stuffs before decoding, exactly as
-- Protocol/MozaSerialConnection.cs ReadLoop does.
--
-- Checksum is computed over the WIRE representation:
--   chk = (0x0D + 0x7E + N + sum(body) + 0x7E * count(0x7E in body)) & 0xFF
-- where body = group, device, payload (checksum byte itself excluded).
-- See MozaProtocol.CalculateWireChecksum / CalculateWireChecksumFromParts.
--
-- Frames from senders that predate the escape fix (docs/protocol/wire/checksum.md
-- § Plugin impl note, 2026-04-22) carry lone 0x7E bytes. Those are recovered via
-- a fallback reading and marked `moza.legacy_unstuffed`; the checksum decides
-- which reading is real, so mixed-era captures decode fully either way.
--
-- ── Useful display filters ─────────────────────────────────────────────────
--   moza                          any Moza frame
--   moza.checksum_status          frames whose checksum failed both readings
--   moza.legacy_unstuffed         legacy sender (pre-2026-04-22)
--   moza.escapes > 0              frames that carried doubled 0x7E on the wire
--   moza.ss.session == 2          one SerialStream session
--   moza.ff.kind == 4             dashboard-switch FF records
--   moza.telem.flag               live-telemetry value frames
--   moza.cmd_name contains "led"  by decoded command name
--
-- ── Self-test ──────────────────────────────────────────────────────────────
--   lua5.4 tools/dissector-selftest.lua docs/moza_dissector.lua
--
-- ── Reference ──────────────────────────────────────────────────────────────
-- docs/protocol/  (wire/, transport/, identity/, telemetry/, sessions/,
--                  tier-definition/, dashboard-upload/, leds/, settings/,
--                  periodic/, devices/)
-- Protocol/MozaProtocol.cs, Protocol/MozaCommandDatabase.cs,
-- Protocol/MozaSerialConnection.cs (framing), Protocol/MozaMBoosterProtocol.cs,
-- Protocol/MozaBaseLfeProtocol.cs

local moza = Proto("moza", "Moza Racing Protocol")

local START = 0x7E
local MAGIC = 0x0D   -- checksum seed

-- ─── Request group names ────────────────────────────────────────────────────
-- Response groups are derived as (req | 0x80) further down.
-- Group numbers cross-checked against Protocol/MozaProtocol.cs +
-- Protocol/MozaCommandDatabase.cs and the per-group docs.

local REQ_GROUPS = {
    [0x00] = "Bus Heartbeat",

    -- Identity / probe groups (docs/protocol/identity/)
    [0x01] = "Lifecycle (soft reboot)",
    [0x02] = "Ident: Product Type",
    [0x04] = "Ident: Device Type",
    [0x05] = "Ident: Capabilities",
    [0x06] = "Ident: MCU UID",
    [0x07] = "Ident: Model Name",
    [0x08] = "Ident: HW Version",
    [0x09] = "Ident: Presence Check",
    [0x0A] = "EEPROM Direct Access",
    [0x0E] = "Param Reader / Debug Log",
    [0x0F] = "Ident: SW Version",
    [0x10] = "Ident: Serial Number",
    [0x11] = "Ident: Identity-11",

    -- Wheel firmware flash transfer (docs/protocol/findings/
    -- 2026-07-31-wheel-firmware-update-protocol.md)
    [0x15] = "FW Flash: Byte Counter",
    [0x16] = "FW Flash: Image Stream",
    [0x17] = "FW Flash: Block Digest",
    [0x18] = "FW Flash: Commit / Verify",
    [0x19] = "FW Flash: Finalize",

    -- Main / hub (dev 0x12) and AB9 shifter (own USB device, dev 0x12)
    [0x1E] = "Main: Output / AB9 Read",
    [0x1F] = "Main: Settings / AB9 Write",
    [0x20] = "Base Ambient LED Write / AB9 Effect Stream",
    [0x22] = "Base Ambient LED Read",

    -- Pedals (dev 0x19); 0x24 is also the mBooster motor-write group
    [0x23] = "Pedals: Settings Read",
    [0x24] = "Pedals: Settings Write / mBooster Motor",
    [0x25] = "Pedals: Output Read",
    [0x26] = "Pedals: Calibration Write",

    -- Wheelbase (dev 0x13)
    [0x28] = "Base: Settings Read",
    [0x29] = "Base: Settings Write",
    [0x2A] = "Base: Calibration / Chime / Partner-SDK",
    [0x2B] = "Base: Status Read",
    [0x2C] = "Base: Motor Run-State Write",
    [0x2D] = "Base: Seq Counter / Discrete Events",

    -- Dash / meter (dev 0x14), also FSR1 wheel select+brightness on dev 0x17
    [0x32] = "Dash: Settings Write",
    [0x33] = "Dash: Settings Read",

    -- CM1 racing dash keyed value stream (dev 0x14)
    [0x35] = "CM1: Value Stream (fast lane)",
    [0x36] = "CM1: Value Stream (slow lane)",

    -- Wheel (dev 0x17)
    [0x3E] = "Wheel LED (newer-wheel variant)",
    [0x3F] = "Wheel: Config / LED Write",
    [0x40] = "Wheel: Config / LED Read",
    [0x41] = "Wheel: Telemetry Enable",
    [0x42] = "FSR1: Display Data Push",
    [0x43] = "Telemetry / SerialStream",

    -- Observed on the wire but undecoded; see docs/protocol/open-questions.md.
    --   0x21 -> dev 0x12 on the wheelbase pipe. One-shot per PitHouse connect:
    --           `7E 01 21 12 03` -> `7E 05 A1 21 03 AA 55 01 90` (constant).
    --   0x4C -> dev 0x12 on the MOZA Stalks pipe (PID 0x0024, model `S07`).
    --           ~1 Hz `7E 05 4C 12 07 00 00 00 00` -> `7E 03 CC 21 07 00 00`.
    --   0x5A -> dev 0x1B. The plugin's own handbrake presence poll
    --           (TelemetryFrameCache.HandbrakePresenceFrame): 1-byte cmd 00,
    --           0xDA reply is 20 bytes, contents undecoded.
    [0x21] = "Main: one-shot probe (undecoded)",
    [0x4C] = "Stalks: status poll (undecoded)",
    [0x5A] = "Handbrake: presence poll",

    [0x46] = "E-Stop: Status Poll",

    -- Shifter (dev 0x1A)
    [0x51] = "Shifter: Settings Read",
    [0x52] = "Shifter: Settings Write",
    [0x53] = "Shifter: Output Read",
    [0x54] = "Shifter: Calibration Write",

    -- Handbrake (dev 0x1B)
    [0x5B] = "Handbrake: Settings Read",
    [0x5C] = "Handbrake: Settings Write",
    [0x5D] = "Handbrake: Output Read",
    [0x5E] = "Handbrake: Calibration Write",

    [0x64] = "Hub: Connected Device Status",
}

local GROUP_NAMES = {}
for g, name in pairs(REQ_GROUPS) do
    GROUP_NAMES[g] = name
    GROUP_NAMES[g + 0x80] = name .. " [resp]"
end
-- E-Stop pushes unsolicited status on 0xC6 with the REQUEST device id (0x1C),
-- so it is not purely a response group. Keep the derived name but note it.
GROUP_NAMES[0xC6] = "E-Stop: Status (resp / unsolicited push)"

-- ─── Device names ───────────────────────────────────────────────────────────
-- Internal-bus addresses (docs/protocol/transport/usb-topology.md
-- § Internal bus addressing, docs/protocol/devices/README.md).

local REQ_DEVICES = {
    [0x12] = "Main/Hub",         -- also AB9 and standalone CM2/pedals on own pipe
    [0x13] = "Base",
    [0x14] = "Dash/Meter",
    [0x15] = "Wheel(secondary)",
    [0x17] = "Wheel",
    [0x18] = "ES Wheel",         -- base-MCU steering module; 0x17 is silent on ES
    [0x19] = "Pedals",
    [0x1A] = "Shifter",
    [0x1B] = "Handbrake",
    [0x1C] = "E-Stop",
    [0x1D] = "Reserved/mBooster2",
    [0x1E] = "Reserved/mBooster3",
}

local DEVICE_NAMES = {}
for d, name in pairs(REQ_DEVICES) do
    DEVICE_NAMES[d] = string.format("%s(0x%02X)", name, d)
    local swapped = ((d & 0x0F) << 4) | ((d & 0xF0) >> 4)
    DEVICE_NAMES[swapped] = string.format("%s-resp(0x%02X)", name, swapped)
end

-- ─── Sub-command tables ─────────────────────────────────────────────────────
-- Keyed by a lowercase hex string of the command-ID prefix. Lookup tries the
-- longest prefix first (4 bytes down to 1), so `1f 00 ff 03` resolves ahead of
-- the generic `1f`. Names follow the device command tables in
-- docs/protocol/devices/ and docs/protocol/leds/.

local CMDS = {}

-- Group 0x0A — EEPROM direct access (docs/protocol/settings/eeprom-0x0A.md)
CMDS[0x0A] = {
    ["0005"] = "select-table",   ["0006"] = "read-table",
    ["0007"] = "select-address", ["0008"] = "read-address",
    ["0009"] = "write-int",      ["000a"] = "read-int",
    ["000b"] = "write-float",    ["000c"] = "read-float",
}

-- Group 0x01 — main lifecycle
CMDS[0x01] = { ["02"] = "soft-reboot" }

-- Group 0x1E — main output (dev 0x12)
CMDS[0x1E] = { ["39"] = "output" }

-- Group 0x1F — main settings (docs/protocol/devices/main-hub-0x12.md § 0x1F)
-- plus the PitHouse poll set from docs/protocol/periodic/group-0x1F.md and the
-- AB9 writes from docs/protocol/devices/ab9-shifter.md (own USB device).
CMDS[0x1F] = {
    ["08"] = "get-led-status",       ["09"] = "set-led-status",
    ["13"] = "set-compat-mode",      ["17"] = "get-compat-mode",
    ["33"] = "set-work-mode",        ["34"] = "get-work-mode",
    ["35"] = "set-default-ffb",      ["36"] = "get-default-ffb",
    ["46"] = "get-ble-mode",         ["47"] = "set-ble-mode",
    ["4c"] = "set-interpolation",    ["4d"] = "get-interpolation",
    ["4e08"] = "set-spring-gain",    ["4f08"] = "get-spring-gain",
    ["4e09"] = "set-damper-gain",    ["4f09"] = "get-damper-gain",
    ["4e0a"] = "set-inertia-gain",   ["4f0a"] = "get-inertia-gain",
    ["4e0b"] = "set-friction-gain",  ["4f0b"] = "get-friction-gain",
    -- PitHouse hub-config poll set (semantics undecoded)
    ["0a"] = "hub-poll 0a",  ["0f"] = "hub-poll 0f",  ["10"] = "hub-poll 10",
    ["18"] = "hub-poll 18",  ["19"] = "hub-poll 19",  ["20"] = "hub-poll 20",
    ["21"] = "hub-poll 21",  ["23"] = "hub-poll 23",  ["25"] = "hub-poll 25",
    ["55"] = "calibration-scalar", ["56"] = "calibration-triples",
    -- AB9 active shifter (dev 0x12 on VID 346E PID 1000)
    ["d300"] = "ab9 shifter-mode-set",
    ["5d"]   = "ab9 mode/online-toggle",
    ["0a01"] = "ab9 gearshift-intensity",
    ["0a05"] = "ab9 engine-vib-config",
    ["0b02"] = "ab9 engine-pulse-a",
    ["0b03"] = "ab9 engine-pulse-b",
}

-- Groups 0x20 / 0x22 — base ambient LEDs
-- (docs/protocol/leds/base-ambient-0x20-0x22.md)
local AMBIENT_CMDS = {
    ["1a"] = "live-color-chunk",  ["1b"] = "live-bitmask",
    ["1c"] = "indicator-state",   ["1d"] = "standby-mode",
    ["1e"] = "standby-interval",  ["1f02"] = "brightness",
    ["1fff"] = "brightness",      ["20"] = "led-color",
    ["21"] = "sleep-mode",        ["22"] = "sleep-timeout",
    ["2301"] = "sleep-breath-interval", ["24"] = "sleep-brightness",
    ["25"] = "sleep-led-color",   ["26"] = "startup-color",
    ["27"] = "shutdown-color",
}
CMDS[0x20] = AMBIENT_CMDS
CMDS[0x22] = AMBIENT_CMDS

-- Groups 0x23 / 0x24 — pedals settings (docs/protocol/devices/pedals-0x19.md)
local PEDAL_CMDS = {
    ["01"] = "throttle-dir", ["02"] = "throttle-min", ["03"] = "throttle-max",
    ["04"] = "brake-dir",    ["05"] = "brake-min",    ["06"] = "brake-max",
    ["07"] = "clutch-dir",   ["08"] = "clutch-min",   ["09"] = "clutch-max",
    ["0d"] = "compat-mode",
    ["0e"] = "throttle-y1", ["0f"] = "throttle-y2", ["10"] = "throttle-y3",
    ["11"] = "throttle-y4", ["1b"] = "throttle-y5",
    ["12"] = "brake-y1",    ["13"] = "brake-y2",    ["14"] = "brake-y3",
    ["15"] = "brake-y4",    ["1c"] = "brake-y5",
    ["16"] = "clutch-y1",   ["17"] = "clutch-y2",   ["18"] = "clutch-y3",
    ["19"] = "clutch-y4",   ["1d"] = "clutch-y5",
    ["1a"] = "brake-angle-ratio",
    ["1e"] = "throttle-hid-source", ["1f"] = "throttle-hid-cmd",
    -- mBooster shares group 0x24 (Protocol/MozaMBoosterProtocol.cs)
    ["b1"] = "mBooster motor-write",
}
CMDS[0x23] = PEDAL_CMDS
CMDS[0x24] = PEDAL_CMDS
CMDS[0x25] = { ["01"] = "throttle-output", ["02"] = "brake-output",
               ["03"] = "clutch-output" }
CMDS[0x26] = { ["0c"] = "throttle-calib-start", ["0d"] = "brake-calib-start",
               ["0e"] = "clutch-calib-start",   ["10"] = "throttle-calib-stop",
               ["11"] = "brake-calib-stop",     ["12"] = "clutch-calib-stop" }

-- Groups 0x28 / 0x29 — wheelbase settings
-- (docs/protocol/devices/wheelbase-0x13.md § 0x28/0x29,
--  Protocol/MozaCommandDatabase.cs "base-*")
local BASE_CMDS = {
    ["01"] = "limit",              ["02"] = "ffb-strength",
    ["04"] = "inertia",            ["07"] = "damper",
    ["08"] = "friction",           ["09"] = "spring",
    ["0a"] = "speed",              ["0c"] = "road-sensitivity",
    ["0d"] = "protection",         ["0e"] = "equalizer1",
    ["0f"] = "equalizer2",         ["10"] = "equalizer3",
    ["11"] = "equalizer4",         ["12"] = "torque",
    ["13"] = "natural-inertia",    ["14"] = "equalizer5",
    ["16"] = "natural-inertia-en", ["17"] = "max-angle",
    ["18"] = "ffb-reverse",        ["19"] = "speed-damping",
    ["1a"] = "speed-damping-point",["1b"] = "soft-limit-strength",
    ["1c"] = "soft-limit-retain",  ["1e"] = "performance-output",
    ["1f"] = "soft-limit-stiffness",
    ["2201"] = "ffb-curve-x1", ["2202"] = "ffb-curve-x2",
    ["2203"] = "ffb-curve-x3", ["2204"] = "ffb-curve-x4",
    ["2205"] = "ffb-curve-y1", ["2206"] = "ffb-curve-y2",
    ["2207"] = "ffb-curve-y3", ["2208"] = "ffb-curve-y4",
    ["2209"] = "ffb-curve-y5",
    ["2c"] = "equalizer6",         ["2d"] = "protection-mode",
    ["2e"] = "gearshift-vibration",
    ["32"] = "equalizer7", ["33"] = "equalizer8",
    ["34"] = "equalizer9", ["35"] = "equalizer10",
    ["fe"] = "ffb-disable",
}
CMDS[0x28] = BASE_CMDS
CMDS[0x29] = BASE_CMDS

-- Group 0x2A — calibration / startup chime / partner-SDK
CMDS[0x2A] = {
    ["01"] = "calibration",
    ["4300"] = "music-preview",     ["4301"] = "music-index-set",
    ["4302"] = "music-index-get",   ["4303"] = "music-enabled-set",
    ["4304"] = "music-enabled-get",
    ["4400"] = "music-volume-set",  ["4401"] = "music-volume-get",
    ["40"] = "feedforward (partner-SDK)",
    ["41"] = "high-freq-torque (partner-SDK)",
}

-- Group 0x2B — base status read
CMDS[0x2B] = {
    ["01"] = "state",       ["02"] = "state-err",
    ["04"] = "mcu-temp",    ["05"] = "mosfet-temp", ["06"] = "motor-temp",
    -- 07: BE16 biased +500, 0.1 Nm/count => (raw-500)/10. PitHouse torque-curve
    -- panel polls it at ~18 Hz; read-only, nothing written to enable it.
    ["07"] = "live-torque",
}

-- Group 0x2C — motor run-state
CMDS[0x2C] = { ["01"] = "motor-run-state (partner-SDK)" }

-- Group 0x2D — sequence counter / discrete events
CMDS[0x2D] = {
    ["f531"] = "sequence-counter",
    ["76"]   = "gearshift-event",
    ["77"]   = "lfe-effect",
}

-- Groups 0x32 / 0x33 — dash settings + FSR1 wheel page select
-- (docs/protocol/devices/dash-0x14.md § 0x32/0x33,
--  docs/protocol/devices/wheel-0x17.md § Dashboard switching)
local DASH_CMDS = {
    ["00"] = "fsr1 display-brightness (write)",
    ["80"] = "fsr1 display-brightness (commit)",
    ["81"] = "fsr1 dashboard-select",
    ["05"] = "rpm-timings",       ["07"] = "rpm-display-mode",
    ["0800"] = "flag-colors",
    ["0900"] = "rpm-blink-color1", ["0901"] = "rpm-blink-color2",
    ["0902"] = "rpm-blink-color3", ["0903"] = "rpm-blink-color4",
    ["0904"] = "rpm-blink-color5", ["0905"] = "rpm-blink-color6",
    ["0906"] = "rpm-blink-color7", ["0907"] = "rpm-blink-color8",
    ["0908"] = "rpm-blink-color9", ["0909"] = "rpm-blink-color10",
    ["0a00"] = "rpm-brightness",  ["0a02"] = "flags-brightness",
    ["0b00"] = "rpm-color",       ["0b02"] = "flag-color",
    ["0d"] = "rpm-mode",          ["0e"] = "rpm-value",
}
CMDS[0x32] = DASH_CMDS
CMDS[0x33] = DASH_CMDS

-- Group 0x41 — telemetry enable
CMDS[0x41] = { ["fdde"] = "send-telemetry (enable)" }

-- Group 0x46 / 0xC6 — E-Stop
CMDS[0x46] = { ["00"] = "receive-status", ["01"] = "get-status" }

-- Group 0x21 — one-shot main probe (undecoded, PitHouse-only)
CMDS[0x21] = { ["03"] = "probe (reply AA 55 01 90)" }

-- Group 0x4C — MOZA Stalks status poll (undecoded)
CMDS[0x4C] = { ["07"] = "status poll" }

-- Group 0x5A — handbrake presence poll (reply body undecoded)
CMDS[0x5A] = { ["00"] = "presence" }

-- Groups 0x51..0x54 — shifter (docs/protocol/devices/shifter-0x1A.md)
local SHIFTER_CMDS = {
    ["01"] = "hid-mode",   ["02"] = "shifter-type/apply-mode",
    ["03"] = "brightness", ["04"] = "colors",
    ["05"] = "direction",  ["06"] = "paddle-sync",
}
CMDS[0x51] = SHIFTER_CMDS
CMDS[0x52] = SHIFTER_CMDS
CMDS[0x53] = { ["01"] = "output-x", ["02"] = "output-y" }
CMDS[0x54] = { ["03"] = "calibration-start", ["04"] = "calibration-stop" }

-- Groups 0x5B..0x5E — handbrake (docs/protocol/devices/handbrake-0x1B.md)
local HANDBRAKE_CMDS = {
    ["01"] = "direction", ["02"] = "min", ["03"] = "max",
    ["04"] = "hid-mode",  ["05"] = "y1",  ["06"] = "y2", ["07"] = "y3",
    ["08"] = "y4",        ["09"] = "y5",  ["0a"] = "button-threshold",
    ["0b"] = "mode",
}
CMDS[0x5B] = HANDBRAKE_CMDS
CMDS[0x5C] = HANDBRAKE_CMDS
CMDS[0x5D] = { ["01"] = "output" }
CMDS[0x5E] = { ["03"] = "calibration-start", ["04"] = "calibration-stop" }

-- Group 0x64 — hub connected-device status
-- (docs/protocol/devices/main-hub-0x12.md § 0x64)
CMDS[0x64] = {
    ["02"] = "base",     ["03"] = "port1",    ["04"] = "port2",
    ["0501"] = "port3",  ["06"] = "pedals1",  ["07"] = "pedals2",
    ["08"] = "pedals3",
    ["0101"] = "slot1 probe", ["0102"] = "slot2 probe", ["0103"] = "slot3 probe",
}

-- Groups 0x3F / 0x40 — wheel config + LED groups
-- Sources: docs/protocol/devices/wheel-0x17.md (§ 0x3F/0x40, Old-Protocol,
-- Extended LED Group Architecture), docs/protocol/leds/color-commands.md,
-- docs/protocol/leds/wheel-groups-0x3F-0x40.md,
-- docs/protocol/channel-config/group-0x40-burst.md.
-- LED group ids: 0 Shift/RPM, 1 Button, 2 Single, 3 Rotary/knob, 4 Ambient.
local WHEEL_CMDS = {
    ["00"] = "colors",              ["01"] = "brightness",
    ["02"] = "rpm-timings",         ["03"] = "paddles-mode",
    ["04"] = "rpm-indicator-mode (old proto)",
    ["05"] = "stick-mode",
    ["07"] = "set-rpm-display-mode",
    ["08"] = "get-rpm-display-mode",
    ["0801"] = "paddles-calib-start", ["0802"] = "paddles-calib-save",
    ["09"] = "clutch-point",        ["0900"] = "config-reset (0x40 burst)",
    ["0a"] = "knob-mode",           ["0b"] = "paddle-adaptive-mode",
    ["0c"] = "device-info",         ["0d"] = "paddle-button-mode",
    ["0e00"] = "flag-colors1",      ["0e01"] = "flag-colors2",
    ["0f00"] = "rpm-blink-color1",  ["0f01"] = "rpm-blink-color2",
    ["0f02"] = "rpm-blink-color3",  ["0f03"] = "rpm-blink-color4",
    ["0f04"] = "rpm-blink-color5",  ["0f05"] = "rpm-blink-color6",
    ["0f06"] = "rpm-blink-color7",  ["0f07"] = "rpm-blink-color8",
    ["0f08"] = "rpm-blink-color9",  ["0f09"] = "rpm-blink-color10",
    ["10"] = "meter-auto-rotation",
    ["13"] = "key-combination",
    ["1400"] = "old-rpm-brightness",
    ["1500"] = "old-rpm-color",     ["1502"] = "flag-color",
    ["16"] = "rpm-interval",        ["17"] = "rpm-mode",
    ["18"] = "rpm-value",
    ["1900"] = "live-colors g0/RPM",   ["1901"] = "live-colors g1/button",
    ["1902"] = "live-colors g2/single",["1903"] = "live-colors g3/knob",
    ["1904"] = "live-colors g4/ambient",
    ["1a00"] = "live-bitmask g0/RPM",  ["1a01"] = "live-bitmask g1/button",
    ["1a02"] = "live-bitmask g2/single",["1a03"] = "live-bitmask g3/knob",
    ["1a04"] = "live-bitmask g4/ambient",
    ["1b00"] = "brightness g0/RPM",    ["1b01"] = "brightness g1/button",
    ["1b02"] = "brightness g2/flags",  ["1b03"] = "brightness g3/knob",
    ["1b04"] = "brightness g4/ambient",
    ["1c00"] = "telemetry-mode / normal-mode g0",
    ["1c01"] = "normal-mode g1",       ["1c02"] = "normal-mode g2",
    ["1c03"] = "normal-mode g3",       ["1c04"] = "normal-mode g4",
    ["1d00"] = "telemetry-idle-effect / standby-mode g0",
    ["1d01"] = "buttons-idle-effect / standby-mode g1",
    ["1d02"] = "standby-mode g2",      ["1d03"] = "standby-mode g3",
    ["1d04"] = "standby-mode g4",
    ["1e00"] = "telemetry-idle-interval / chan-enable page0",
    ["1e01"] = "buttons-idle-interval / chan-enable page1",
    ["1e03"] = "chan-enable page3",
    ["1f00"] = "led-color g0/RPM",     ["1f01"] = "led-color g1/button",
    ["1f02"] = "led-color g2/single",  ["1f03"] = "led-color g3/knob",
    ["1f04"] = "led-color g4/ambient",
    ["20"] = "idle-mode",           ["21"] = "idle-timeout",
    ["22"] = "idle-speed",          ["23"] = "sleep-breath-brightness",
    ["24"] = "idle-color",          ["25"] = "startup-color",
    ["26"] = "paddle-thresholds",
    -- cmd 0x27 is dual-purpose: knob "Active" LED colour (0x3F write / 0x40
    -- read role byte) and the per-page dashboard binding fingerprint seen in
    -- the 0x40 config burst. Docs cover both; disambiguate by payload length.
    ["27"] = "knob-active-color / page-binding-fingerprint",
    ["2800"] = "get-dashboard-mode",   ["2801"] = "get-active-page",
    ["2802"] = "set-multi-channel-mode",
    ["2900"] = "display-setting 29:00",
    ["2a"] = "rotary-signal-mode / display-setting",
}
CMDS[0x3F] = WHEEL_CMDS
CMDS[0x40] = WHEEL_CMDS
CMDS[0x3E] = { ["0b"] = "newer-wheel LED cmd" }

-- Group 0x43 — telemetry / SerialStream (docs/protocol/telemetry/,
-- docs/protocol/sessions/, docs/protocol/devices/wheel-0x17.md § 0x43)
CMDS[0x43] = {
    ["7d23"] = "live-telemetry",
    ["7c00"] = "session chunk",
    ["fc00"] = "session ack",
    ["7c23"] = "dashboard-activate",
    ["7c27"] = "display-config",
    ["7c1e"] = "display-settings",
    ["b8"]   = "wheel-input-event",
    -- 1-byte forms addressed to the display sub-device are identity probes,
    -- decoded separately (see decode_43).
}

-- Chunk type names (docs/protocol/sessions/chunk-format.md)
local SS_TYPE_NAMES = {
    [0x00] = "control / end marker",
    [0x01] = "data",
    [0x81] = "session open",
}

-- FF-record kinds on session 0x01/0x02
-- (docs/protocol/sessions/session-0x02-ff-init.md)
local FF_KINDS = {
    [2]  = "init_nonce",     [4]  = "dashboard-switch",
    [5]  = "handshake",      [7]  = "init_enum",
    [8]  = "init_catalog_a", [9]  = "ticker",
    [10] = "wheel_init_a",   [11] = "init_catalog_b",
    [14] = "device-log request", [15] = "device-log ack",
    [16] = "wheel_init_b",
}

-- Session 0x01 typed sub-msg registry
-- (docs/protocol/sessions/session-0x01-channel-protocol.md)
local SESS01_TYPES = {
    [0x00] = "end/ack marker",
    [0x01] = "tier-def (subscription)",
    [0x03] = "wheel handshake response",
    [0x04] = "catalog URL announcement",
    [0x05] = "string value push",
    [0x06] = "seq-ack",
    [0x07] = "init / tier-def version",
}

-- 6-byte upload sub-message types
-- (docs/protocol/dashboard-upload/6-byte-submsg-header.md)
local SUBMSG_TYPES = {
    [0x01] = "ready/progress ack (dev->host)",
    [0x02] = "metadata (host->dev)",
    [0x03] = "content (host->dev)",
    [0x08] = "dir-listing probe (host->dev)",
    [0x0a] = "dir-listing reply (dev->host)",
    [0x11] = "complete ack (dev->host)",
}

-- Identity response groups wrapped inside 0x43/0xC3 for the display
-- sub-device (docs/protocol/identity/display-sub-device.md)
local WRAPPED_IDENT = {
    [0x80] = "keepalive ack",  [0x82] = "product type",
    [0x84] = "device type",    [0x85] = "capabilities",
    [0x86] = "MCU UID",        [0x87] = "model name",
    [0x88] = "HW version",     [0x89] = "presence",
    [0x8F] = "SW version",     [0x90] = "serial number",
    [0x91] = "identity-11",
}

-- FSR1 group-0x42 record types → dashboard index list
-- (docs/protocol/devices/wheel-0x17.md § Full index→type map)
local FSR1_TYPES = {
    [0x01] = "type 01 (idx 0, power-on default)",
    [0x02] = "type 02 (idx 1, brake dash)",
    [0x03] = "type 03 (idx 4, 9)",
    [0x04] = "type 04 (idx 5, 6, 13, 14)",
    [0x05] = "type 05 (idx 8)",
    [0x06] = "type 06 (idx 2, 3, 7)",
    [0x08] = "type 08 (idx 10)",
    [0x09] = "type 09 (idx 11)",
    [0x0B] = "type 0b",
    [0x0C] = "type 0c (idx 15, 18)",
    [0x0D] = "type 0d (background tyre/status cache)",
    [0x0E] = "type 0e (idx 12)",
    [0x11] = "type 11 (idx 17, GT Style A)",
    [0x12] = "type 12 (idx 17, GT Style B)",
}

-- mBooster effect ids (Protocol/MozaMBoosterProtocol.cs)
local MB_EFFECTS = {
    [1] = "ABS", [2] = "Lockup", [3] = "Threshold", [4] = "Engine",
    [9] = "Road Texture",
}

-- LFE effect ids (Protocol/MozaBaseLfeProtocol.cs, group 0x2D cmd 0x77)
local LFE_EFFECTS = { [0] = "gearshift", [1] = "engine", [2] = "abs" }

-- Groups whose payload has no command-ID prefix at all: the payload is a
-- record/value stream. Never label their first bytes as a "Cmd ID".
--   0x00 bus heartbeat (N=0)
--   0x35/0x36 CM1 keyed value stream (payload = [key u16 BE][f32 BE] * N)
--   0x42 FSR1 display push (payload = [type][b1][b2][00][00][data])
--   0x43 handled entirely by decode_43 (opcode, keepalive, or wrapped identity)
local NO_CMD_ID = {
    [0x00] = true, [0x35] = true, [0x36] = true, [0x42] = true, [0x43] = true,
}

-- ─── Proto fields ───────────────────────────────────────────────────────────

local pf = {
    start       = ProtoField.uint8 ("moza.start",     "Start (0x7E)",       base.HEX),
    n           = ProtoField.uint8 ("moza.n",         "Payload Length (N)", base.DEC),
    group       = ProtoField.uint8 ("moza.group",     "Group",              base.HEX, GROUP_NAMES),
    device      = ProtoField.uint8 ("moza.device",    "Device",             base.HEX, DEVICE_NAMES),
    cmd         = ProtoField.bytes ("moza.cmd",       "Cmd ID"),
    cmd_name    = ProtoField.string("moza.cmd_name",  "Cmd Name"),
    data        = ProtoField.bytes ("moza.data",      "Data"),
    checksum    = ProtoField.uint8 ("moza.checksum",  "Checksum",           base.HEX),
    chk_calc    = ProtoField.uint8 ("moza.checksum_calculated", "Calculated Checksum", base.HEX),
    chk_status  = ProtoField.string("moza.checksum_status",     "Checksum Status"),
    is_response = ProtoField.bool  ("moza.is_response", "Is Response"),
    wire_len    = ProtoField.uint32("moza.wire_len",  "Wire Length (bytes)", base.DEC),
    escapes     = ProtoField.uint32("moza.escapes",   "0x7E Escapes",        base.DEC),
    legacy      = ProtoField.bool  ("moza.legacy_unstuffed", "Legacy Unstuffed Frame"),

    -- Live telemetry (0x43 7D 23)
    t_const4    = ProtoField.bytes ("moza.telem.const4",  "Const (32 00 23 32)"),
    t_flag      = ProtoField.uint8 ("moza.telem.flag",    "Tier Flag Byte",  base.HEX),
    t_const20   = ProtoField.uint8 ("moza.telem.const20", "Const (0x20)",    base.HEX),
    t_live      = ProtoField.bytes ("moza.telem.live",    "Bit-packed Channel Data"),

    -- SerialStream chunk (0x43 7C 00)
    ss_session  = ProtoField.uint8 ("moza.ss.session",  "Session ID",       base.HEX),
    ss_type     = ProtoField.uint8 ("moza.ss.type",     "Chunk Type",       base.HEX, SS_TYPE_NAMES),
    ss_seq      = ProtoField.uint16("moza.ss.seq",      "Sequence",         base.DEC),
    ss_port     = ProtoField.uint16("moza.ss.port",     "Port / Session Id",base.DEC),
    ss_window   = ProtoField.uint16("moza.ss.window",   "Receive Window",   base.DEC),
    ss_ackseq   = ProtoField.uint16("moza.ss.ack_seq",  "Close Ack Seq",    base.DEC),
    ss_data     = ProtoField.bytes ("moza.ss.data",     "Chunk Net Data"),
    ss_crc      = ProtoField.uint32("moza.ss.crc",      "Chunk CRC-32 (LE)",base.HEX),
    ss_crc_calc = ProtoField.uint32("moza.ss.crc_calculated", "Calculated CRC-32", base.HEX),
    ss_crc_st   = ProtoField.string("moza.ss.crc_status","CRC Status"),

    -- FF-record envelope (session 0x01 / 0x02)
    ff_size     = ProtoField.uint32("moza.ff.size",     "FF Record Size",   base.DEC),
    ff_crc      = ProtoField.uint32("moza.ff.crc",      "FF Inner CRC-32",  base.HEX),
    ff_kind     = ProtoField.uint32("moza.ff.kind",     "FF Kind",          base.DEC, FF_KINDS),

    -- 6-byte upload sub-message header
    sm_type     = ProtoField.uint8 ("moza.submsg.type", "Sub-msg Type",     base.HEX, SUBMSG_TYPES),
    sm_size     = ProtoField.uint16("moza.submsg.size", "Sub-msg Body Size",base.DEC),

    -- Session 0x01 typed sub-msg header
    sm_type_01  = ProtoField.uint8 ("moza.sess01.type", "Sub-msg Type",     base.HEX, SESS01_TYPES),
    sm_size32   = ProtoField.uint32("moza.sess01.size", "Sub-msg Body Size",base.DEC),
    chan_idx    = ProtoField.uint8 ("moza.sess01.channel_idx", "Channel Index", base.DEC),
    chan_url    = ProtoField.string("moza.sess01.channel_url", "Channel URL"),
    chan_seq    = ProtoField.uint32("moza.sess01.seq", "Seq-ack Value",     base.DEC),

    -- 9-byte compressed message envelope (sessions 0x09 / 0x0a)
    env_csz     = ProtoField.uint32("moza.env.comp_size",   "Compressed Size + 4", base.DEC),
    env_usz     = ProtoField.uint32("moza.env.uncomp_size", "Uncompressed Size",   base.DEC),

    -- Session ack (0x43 FC 00)
    ack_session = ProtoField.uint8 ("moza.ack.session", "Ack Session ID",   base.HEX),
    ack_seq     = ProtoField.uint16("moza.ack.seq",     "Ack Sequence",     base.DEC),

    -- Sequence counter (0x2D F5 31)
    seq_counter = ProtoField.uint8 ("moza.seq.counter",  "Counter Value",   base.DEC),

    -- LED live path (0x3F 19/1A)
    led_group   = ProtoField.uint8 ("moza.led.group",    "LED Group",       base.DEC),
    led_index   = ProtoField.uint8 ("moza.led.index",    "LED Index",       base.DEC),
    led_rgb     = ProtoField.bytes ("moza.led.rgb",      "RGB"),
    led_active  = ProtoField.uint32("moza.led.active_mask","Active Mask",   base.HEX),
    led_window  = ProtoField.uint32("moza.led.window_mask","Window Mask",   base.HEX),

    -- LFE (0x2D 77)
    lfe_effect  = ProtoField.uint8 ("moza.lfe.effect",   "Effect Id",       base.DEC, LFE_EFFECTS),
    lfe_play    = ProtoField.uint8 ("moza.lfe.play",     "Play Flag",       base.DEC),
    lfe_period  = ProtoField.uint16("moza.lfe.period",   "Period (BE, ms)", base.DEC),
    lfe_freq    = ProtoField.uint16("moza.lfe.freq",     "Frequency (BE raw)", base.DEC),
    lfe_amp     = ProtoField.uint16("moza.lfe.intensity","Intensity (BE raw)", base.DEC),

    -- mBooster (0x24 B1)
    mb_effect   = ProtoField.uint8 ("moza.mb.effect",    "Effect Type",     base.DEC, MB_EFFECTS),
    mb_enable   = ProtoField.uint8 ("moza.mb.enable",    "Enable",          base.DEC),
    mb_param1   = ProtoField.uint8 ("moza.mb.param1",    "Param1",          base.DEC),
    mb_freq     = ProtoField.uint16("moza.mb.freq",      "Frequency (BE raw)", base.DEC),
    mb_amp      = ProtoField.uint16("moza.mb.amp",       "Amplitude (BE raw)", base.DEC),

    -- FSR1 group 0x42
    fsr_type    = ProtoField.uint8 ("moza.fsr1.type",    "Record Type",     base.HEX, FSR1_TYPES),
    fsr_b1      = ProtoField.uint8 ("moza.fsr1.b1",      "Sub-header b1",   base.HEX),
    fsr_b2      = ProtoField.uint8 ("moza.fsr1.b2",      "Sub-header b2",   base.HEX),
    fsr_data    = ProtoField.bytes ("moza.fsr1.data",    "Record Data"),
    fsr_index   = ProtoField.uint32("moza.fsr1.index",   "Dashboard Index (BE)", base.DEC),

    -- CM1 keyed value stream (0x35 / 0x36)
    cm1_key     = ProtoField.uint16("moza.cm1.key",      "Field Key",       base.HEX),
    cm1_value   = ProtoField.float ("moza.cm1.value",    "Value (BE f32)"),

    -- Identity
    id_string   = ProtoField.string("moza.identity.string", "Identity String"),
    id_subcmd   = ProtoField.uint8 ("moza.identity.subcmd", "Sub-command",  base.HEX),
    id_bytes    = ProtoField.bytes ("moza.identity.bytes",  "Identity Bytes"),
    inner_group = ProtoField.uint8 ("moza.identity.inner_group", "Wrapped Inner Response", base.HEX, WRAPPED_IDENT),

    -- Group 0x0E
    dbg_sev     = ProtoField.uint8 ("moza.debug.severity", "Log Severity",  base.HEX),
    dbg_text    = ProtoField.string("moza.debug.text",     "Debug Log Text"),
    param_table = ProtoField.uint8 ("moza.param.table",    "EEPROM Table",  base.HEX),
    param_index = ProtoField.uint8 ("moza.param.index",    "Parameter Index", base.HEX),
    param_value = ProtoField.bytes ("moza.param.value",    "Parameter Value"),

    -- Generic value view
    cfg_data    = ProtoField.bytes ("moza.cfg.data",       "Value Bytes"),
}

moza.fields = {}
for _, v in pairs(pf) do moza.fields[#moza.fields + 1] = v end

-- ─── Expert info ────────────────────────────────────────────────────────────

local ef = {
    bad_chk   = ProtoExpert.new("moza.expert.bad_checksum", "Bad frame checksum",
                                expert.group.CHECKSUM, expert.severity.WARN),
    bad_crc   = ProtoExpert.new("moza.expert.bad_crc", "Bad chunk CRC-32",
                                expert.group.CHECKSUM, expert.severity.WARN),
    esc_viol  = ProtoExpert.new("moza.expert.escape_violation",
                                "0x7E escape violation (lone 0x7E in frame body)",
                                expert.group.MALFORMED, expert.severity.WARN),
    truncated = ProtoExpert.new("moza.expert.truncated",
                                "Frame truncated — continues in a later USB transfer",
                                expert.group.REASSEMBLE, expert.severity.NOTE),
    legacy    = ProtoExpert.new("moza.expert.legacy_unstuffed",
                                "Legacy unstuffed frame (lone 0x7E in body)",
                                expert.group.PROTOCOL, expert.severity.NOTE),
}
moza.experts = { ef.bad_chk, ef.bad_crc, ef.esc_viol, ef.truncated, ef.legacy }

-- ─── Helpers ────────────────────────────────────────────────────────────────

local function group_label(g)
    return GROUP_NAMES[g] or string.format("Group 0x%02X", g)
end

local function device_label(d)
    return DEVICE_NAMES[d] or string.format("Dev 0x%02X", d)
end

local function is_response_group(g) return (g & 0x80) ~= 0 end
local function base_group(g)        return g & 0x7F end

-- CRC-32 (reflected, poly 0xEDB88320, init/xorout 0xFFFFFFFF) — the zlib /
-- Ethernet CRC used by session chunk trailers and FF-record inner CRCs.
local crc_tbl = {}
for i = 0, 255 do
    local c = i
    for _ = 1, 8 do
        if (c & 1) == 1 then c = (c >> 1) ~ 0xEDB88320 else c = c >> 1 end
    end
    crc_tbl[i] = c & 0xFFFFFFFF
end

local function crc32(tvbr)
    local bytes = tvbr:bytes()
    local crc = 0xFFFFFFFF
    for i = 0, bytes:len() - 1 do
        crc = (crc >> 8) ~ crc_tbl[(crc ~ bytes:get_index(i)) & 0xFF]
        crc = crc & 0xFFFFFFFF
    end
    return (crc ~ 0xFFFFFFFF) & 0xFFFFFFFF
end

-- Longest-prefix command-name lookup for a group.
local function cmd_lookup(group, tvb, off, n)
    local tbl = CMDS[base_group(group)]
    if tbl == nil or n < 1 then return nil, 0 end
    local maxlen = math.min(4, n)
    for l = maxlen, 1, -1 do
        local key = tvb(off, l):bytes():tohex(true)
        local name = tbl[key]
        if name then return name, l end
    end
    return nil, 0
end

local function read_ascii(tvb, off, maxlen)
    local s = {}
    for i = 0, maxlen - 1 do
        local b = tvb(off + i, 1):uint()
        if b == 0 then break end
        if b >= 0x20 and b <= 0x7E then s[#s + 1] = string.char(b)
        else s[#s + 1] = "." end
    end
    return table.concat(s)
end

local function is_ascii(tvb, off, len)
    if len <= 0 then return false end
    local printable = 0
    for i = 0, len - 1 do
        local b = tvb(off + i, 1):uint()
        if b >= 0x20 and b <= 0x7E then printable = printable + 1
        elseif b ~= 0x00 and b ~= 0x0A and b ~= 0x0D then return false end
    end
    return printable > 0
end

-- ─── Payload decoders ───────────────────────────────────────────────────────
-- All decoders take (tvb, tree, off, n) where `off` is the offset of the
-- payload's first byte in the (de-stuffed) frame tvb and `n` is N.

-- 0x43 / 7D 23 — live telemetry value frame
-- docs/protocol/telemetry/live-stream.md
local function decode_live_telemetry(tvb, t, off, n)
    if n < 8 then
        t:add(pf.data, tvb(off + 2, n - 2)):append_text("  [short 7D 23 frame]")
        return
    end
    t:add(pf.t_const4,  tvb(off + 2, 4))
    local flag = tvb(off + 6, 1):uint()
    t:add(pf.t_flag,    tvb(off + 6, 1))
        :append_text("  [tier selector; Type02 fw binds widgets to the highest flag]")
    t:add(pf.t_const20, tvb(off + 7, 1))
    local live_len = n - 8
    if live_len <= 0 then
        t:add(pf.data, tvb(off + 6, 2)):set_text(
            string.format("Stub frame (flag=0x%02X) — tier has no active channels", flag))
        return
    end
    local lt = t:add(pf.t_live, tvb(off + 8, live_len))
    lt:append_text(string.format("  (%d bytes / %d bits, flag=0x%02X)",
        live_len, live_len * 8, flag))
end

-- ── Application framing carried inside session chunk net data ──────────────
--
-- Each session runs its own envelope. Only the framing that STARTS at the head
-- of a chunk's net data can be identified from a single frame — application
-- messages span chunks and this dissector deliberately does not reassemble, so
-- everything below is labelled "first sub-msg in chunk".
--
--   sess 0x01/0x02 → 5-byte typed sub-msg  [type u8][size u32 LE]
--               (docs/protocol/sessions/session-0x01-channel-protocol.md)
--   sess 0x01/0x02 → FF-record envelope  [FF][size u32 LE][crc u32 LE][kind u32 LE]
--               (docs/protocol/sessions/session-0x02-ff-init.md)
--   sess 0x04..0x07 → 6-byte upload sub-msg [type u8][size u16 LE][pad 3]
--               (docs/protocol/dashboard-upload/6-byte-submsg-header.md)
--   sess 0x09/0x0a → 9-byte compressed envelope [flags][csz+4 u32 LE][usz u32 LE]
--               (docs/protocol/sessions/compressed-0x09-0x0a.md)
--   sess 0x03/0x0b → 12-byte tile-server envelope (layout not decoded here)

local function try_ff_record(tvb, t, off, len)
    if len < 13 then return false end
    if tvb(off, 1):uint() ~= 0xFF then return false end
    local size = tvb(off + 1, 4):le_uint()
    if size < 4 or size > 0x100000 then return false end
    local ft = t:add(moza, tvb(off, math.min(len, 9 + size)), "FF property record")
    ft:add_le(pf.ff_size, tvb(off + 1, 4))
    ft:add_le(pf.ff_crc,  tvb(off + 5, 4))
    ft:add_le(pf.ff_kind, tvb(off + 9, 4))
    local kind = tvb(off + 9, 4):le_uint()
    ft:append_text(string.format(": kind=%d (%s), size=%d",
        kind, FF_KINDS[kind] or "unknown", size))
    if 9 + size > len then
        ft:append_text("  [spans further chunks — not reassembled]")
    end
    return true
end

local function try_sess01_submsg(tvb, t, off, len)
    if len < 5 then return false end
    local ty = tvb(off, 1):uint()
    local name = SESS01_TYPES[ty]
    if name == nil then return false end
    local size = tvb(off + 1, 4):le_uint()
    if size > 0x100000 then return false end
    local st = t:add(moza, tvb(off, math.min(len, 5 + size)), "Sess-01 sub-msg")
    st:add(pf.sm_type_01, tvb(off, 1))
    st:add_le(pf.sm_size32, tvb(off + 1, 4))
    st:append_text(string.format(": %s, body=%d B (stride %d)", name, size, 5 + size))
    if ty == 0x04 and size >= 2 and 5 + size <= len then
        st:add(pf.chan_idx, tvb(off + 5, 1))
        local url_len = size - 1
        if url_len > 0 and is_ascii(tvb, off + 6, url_len) then
            st:add(pf.chan_url, tvb(off + 6, url_len),
                   read_ascii(tvb, off + 6, url_len))
        end
    elseif ty == 0x06 and size == 4 and 9 <= len then
        st:add_le(pf.chan_seq, tvb(off + 5, 4))
    end
    return true
end

local function try_upload_submsg(tvb, t, off, len)
    if len < 6 then return false end
    local ty = tvb(off, 1):uint()
    if SUBMSG_TYPES[ty] == nil then return false end
    if tvb(off + 3, 3):uint() ~= 0 then return false end   -- pad must be 00 00 00
    local size = tvb(off + 1, 2):le_uint()
    local st = t:add(moza, tvb(off, 6), "Upload sub-msg header")
    st:add(pf.sm_type, tvb(off, 1))
    st:add_le(pf.sm_size, tvb(off + 1, 2))
    st:append_text(string.format(": %s, body=%d B (stride %d)",
        SUBMSG_TYPES[ty], size, 6 + size))
    return true
end

local function try_compressed_envelope(tvb, t, off, len)
    if len < 11 then return false end
    if tvb(off, 1):uint() ~= 0x00 then return false end
    local csz = tvb(off + 1, 4):le_uint()
    local usz = tvb(off + 5, 4):le_uint()
    if csz < 6 or csz > 0x400000 or usz == 0 or usz > 0x4000000 then return false end
    if tvb(off + 9, 1):uint() ~= 0x78 then return false end   -- zlib magic
    local et = t:add(moza, tvb(off, math.min(len, 9)), "Compressed message envelope")
    et:add_le(pf.env_csz, tvb(off + 1, 4)):append_text("  [zlib stream is this minus 4]")
    et:add_le(pf.env_usz, tvb(off + 5, 4))
    et:append_text(string.format(": zlib %d B -> %d B", csz - 4, usz))
    return true
end

-- Sniff the head of a chunk's net data according to the session's envelope.
local function decode_chunk_body(tvb, t, off, len, session)
    if len <= 0 then return end
    local ok = false
    if session == 0x01 or session == 0x02 then
        -- Both envelopes appear on both sessions depending on firmware era:
        -- FF-records dominate sess 0x02, the 5-byte typed framing sess 0x01,
        -- but KS Pro captures carry type=0x04 catalog URLs on 0x02 as well.
        ok = try_ff_record(tvb, t, off, len) or try_sess01_submsg(tvb, t, off, len)
    elseif session >= 0x04 and session <= 0x07 then
        ok = try_upload_submsg(tvb, t, off, len)
    elseif session == 0x09 or session == 0x0A then
        ok = try_compressed_envelope(tvb, t, off, len)
    end
    if not ok then
        -- No recognisable header at this offset: continuation bytes of a
        -- multi-chunk application message, or an envelope this dissector does
        -- not decode (sess 0x03 / 0x0b tile-server, legacy 8-byte upload hdr).
        t:append_text("  [continuation / undecoded envelope]")
    end
end

-- 0x43 / 7C 00 — SerialStream chunk
-- docs/protocol/sessions/chunk-format.md, type-0x81-channel-open.md
local function decode_chunk(tvb, t, off, n)
    if n < 6 then
        if n > 2 then t:add(pf.data, tvb(off + 2, n - 2)) end
        return
    end
    local session = tvb(off + 2, 1):uint()
    local ctype   = tvb(off + 3, 1):uint()
    t:add(pf.ss_session, tvb(off + 2, 1))
    t:add(pf.ss_type,    tvb(off + 3, 1))

    if ctype == 0x81 then
        -- 7C 00 [sess] 81 [seq LE] [port LE] [window LE]   (N = 10)
        t:add_le(pf.ss_seq, tvb(off + 4, 2))
        if n >= 8  then t:add_le(pf.ss_port,   tvb(off + 6, 2)) end
        if n >= 10 then
            t:add_le(pf.ss_window, tvb(off + 8, 2))
                :append_text("  [0x02FD = 765 in every observed open]")
        end
        if n > 10 then t:add(pf.data, tvb(off + 10, n - 10)) end
        return
    end

    if ctype == 0x00 then
        -- Close / end marker: seq field carries the ack seq being reclaimed.
        t:add_le(pf.ss_ackseq, tvb(off + 4, 2))
        if n > 6 then t:add(pf.data, tvb(off + 6, n - 6)) end
        return
    end

    -- type 0x01 data chunk: net data + 4-byte CRC-32 LE trailer.
    t:add_le(pf.ss_seq, tvb(off + 4, 2))
    local chunk_len = n - 6
    if chunk_len < 4 then
        if chunk_len > 0 then t:add(pf.ss_data, tvb(off + 6, chunk_len)) end
        return
    end
    local net_len = chunk_len - 4
    if net_len > 0 then
        t:add(pf.ss_data, tvb(off + 6, net_len))
    else
        t:append_text("  [keepalive: 0-byte net data]")
    end
    local wire_crc = tvb(off + 6 + net_len, 4):le_uint()
    local calc_crc = (net_len > 0) and crc32(tvb(off + 6, net_len)) or 0
    local ci = t:add_le(pf.ss_crc, tvb(off + 6 + net_len, 4))
    if wire_crc == calc_crc then
        ci:append_text("  [OK]")
    else
        ci:append_text(string.format("  [BAD: computed 0x%08X]", calc_crc))
        t:add(pf.ss_crc_calc, tvb(off + 6 + net_len, 4), calc_crc)
        t:add_proto_expert_info(ef.bad_crc)
    end

    if net_len > 0 then
        decode_chunk_body(tvb, t, off + 6, net_len, session)
    end
end

-- 0x43 / FC 00 — session ack
local function decode_ack(tvb, t, off, n)
    if n < 5 then return end
    t:add(pf.ack_session, tvb(off + 2, 1))
    t:add_le(pf.ack_seq,     tvb(off + 3, 2))
    if n > 5 then t:add(pf.data, tvb(off + 5, n - 5)) end
end

-- 0x43 / 7C 23, 7C 27, 7C 1E — periodic display / activate / settings pushes.
-- docs/protocol/channel-config/group-0x43-active-display-cycle.md
local function decode_display_cycle(tvb, t, off, n)
    if n <= 2 then return end
    local d = t:add(pf.data, tvb(off + 2, n - 2))
    if n >= 8 then
        d:append_text(string.format("  [const=0x%02X flag=0x%02X b2=%d b4=%d]",
            tvb(off + 2, 1):uint(), tvb(off + 3, 1):uint(),
            tvb(off + 4, 2):le_uint(), tvb(off + 6, 2):le_uint()))
    end
end

-- Display sub-device identity, wrapped in 0x43 / 0xC3.
-- docs/protocol/identity/display-sub-device.md
local function decode_wrapped_identity(tvb, t, off, n, is_resp)
    if n < 1 then return end
    local b0 = tvb(off, 1):uint()
    if is_resp then
        t:add(pf.inner_group, tvb(off, 1))
        if n < 2 then return end
        local rest_off, rest_len = off + 1, n - 1
        -- 0x87/0x88/0x8F/0x90 carry a 1-byte sub-cmd then an ASCII string.
        if b0 == 0x87 or b0 == 0x88 or b0 == 0x8F or b0 == 0x90 then
            t:add(pf.id_subcmd, tvb(off + 1, 1))
            rest_off, rest_len = off + 2, n - 2
        end
        if rest_len > 0 then
            if is_ascii(tvb, rest_off, rest_len) then
                local s = read_ascii(tvb, rest_off, rest_len)
                t:add(pf.id_string, tvb(rest_off, rest_len), s)
            else
                t:add(pf.id_bytes, tvb(rest_off, rest_len))
            end
        end
    else
        t:add(pf.id_subcmd, tvb(off, 1)):append_text(
            string.format("  [identity probe group 0x%02X wrapped in 0x43]", b0))
        if n > 1 then t:add(pf.data, tvb(off + 1, n - 1)) end
    end
end

-- Group 0x43 dispatcher.
local function decode_43(tvb, t, off, n, group)
    local is_resp = is_response_group(group)
    if n == 0 then return end
    local b0 = tvb(off, 1):uint()

    if n >= 2 then
        local b1 = tvb(off + 1, 1):uint()
        if b0 == 0x7D and b1 == 0x23 then return decode_live_telemetry(tvb, t, off, n) end
        if b0 == 0x7C and b1 == 0x00 then return decode_chunk(tvb, t, off, n) end
        if b0 == 0xFC and b1 == 0x00 then return decode_ack(tvb, t, off, n) end
        if b0 == 0x7C and (b1 == 0x23 or b1 == 0x27 or b1 == 0x1E) then
            return decode_display_cycle(tvb, t, off, n)
        end
        if b0 == 0xB8 and n >= 3 then
            local a, b = tvb(off + 1, 1):uint(), tvb(off + 2, 1):uint()
            local what = "unknown"
            if     a == 0x00 and b == 0x02 then what = "next dashboard"
            elseif a == 0x01 and b == 0x02 then what = "previous dashboard"
            elseif a == 0x02 and b == 0x00 then what = "next page"
            elseif a == 0x02 and b == 0x01 then what = "previous page" end
            t:add(pf.data, tvb(off + 1, n - 1))
                :append_text(string.format("  [wheel input: %s]", what))
            return
        end
    end

    -- Everything else on 0x43/0xC3 is display sub-device identity traffic
    -- (1-byte keepalive `00`, presence `09`, and the 0x02..0x11 probes).
    decode_wrapped_identity(tvb, t, off, n, is_resp)
end

-- Group 0x0E — parameter reader (host->dev) / firmware debug log (dev->host)
-- docs/protocol/periodic/group-0x0E-param-reader.md
local function decode_param_debug(tvb, t, off, n, group)
    if n < 1 then return end
    local c0 = tvb(off, 1):uint()
    -- Device-initiated firmware logs keep group 0x0E (not 0x8E) and only swap
    -- the device id — e.g. `0e 71 05 "[INFO]param_manage.c…"`. So treat a
    -- leading 0x05 as a log line regardless of the group's response bit.
    if is_response_group(group) or c0 == 0x05 then
        if c0 == 0x05 then
            -- 05 [severity/idx] + ASCII log line
            t:add(pf.dbg_sev, tvb(off, 1)):append_text("  [info log]")
            local text_off = off + 1
            local text_len = n - 1
            if text_len > 0 and is_ascii(tvb, text_off, text_len) then
                local s = read_ascii(tvb, text_off, text_len)
                t:add(pf.dbg_text, tvb(text_off, text_len), s)
            elseif text_len > 0 then
                t:add(pf.data, tvb(text_off, text_len))
            end
            return
        end
        if c0 == 0x00 and n >= 3 then
            t:add(pf.param_index, tvb(off + 1, 1))
            if n > 3 then t:add(pf.param_value, tvb(off + 3, n - 3)) end
            return
        end
    end
    -- Request: 00 [table] [index]
    if n >= 3 and c0 == 0x00 then
        t:add(pf.param_table, tvb(off + 1, 1))
        t:add(pf.param_index, tvb(off + 2, 1))
        if n > 3 then t:add(pf.data, tvb(off + 3, n - 3)) end
        return
    end
    t:add(pf.data, tvb(off, n))
end

-- Group 0x2D — sequence counter / gearshift / LFE
-- docs/protocol/devices/wheelbase-0x13.md § 0x2D
local function decode_2d(tvb, t, off, n)
    if n < 1 then return end
    local c0 = tvb(off, 1):uint()
    if c0 == 0xF5 and n >= 6 then
        t:add(pf.seq_counter, tvb(off + 5, 1))
        t:add(pf.data, tvb(off + 2, n - 2))
        return
    end
    if c0 == 0x76 then
        t:add(pf.data, tvb(off + 1, n - 1)):append_text(
            "  [gearshift trigger; intensity comes from 0x29 cmd 0x2E]")
        return
    end
    if c0 == 0x77 and n >= 10 then
        t:add(pf.lfe_effect, tvb(off + 2, 1))
        t:add(pf.lfe_play,   tvb(off + 3, 1))
        t:add(pf.lfe_period, tvb(off + 4, 2))
        local fr = tvb(off + 6, 2):uint()
        local am = tvb(off + 8, 2):uint()
        t:add(pf.lfe_freq, tvb(off + 6, 2))
            :append_text(string.format("  (%.2f Hz)", fr / 65536.0 * 200.0))
        t:add(pf.lfe_amp,  tvb(off + 8, 2))
            :append_text(string.format("  (%.1f%%)", am / 65535.0 * 100.0))
        return
    end
    if n > 1 then t:add(pf.data, tvb(off + 1, n - 1)) end
end

-- Group 0x24 cmd 0xB1 — mBooster motor write
-- Protocol/MozaMBoosterProtocol.cs
local function decode_mbooster(tvb, t, off, n)
    -- 7E 09 24 12  b1 EF EN 00 P1 FH FL AH AL  CK  -> N = 9 (cmd + 8 payload)
    if n < 9 then return end
    t:add(pf.mb_effect, tvb(off + 1, 1))
    t:add(pf.mb_enable, tvb(off + 2, 1))
    t:add(pf.mb_param1, tvb(off + 4, 1))
    local fr = tvb(off + 5, 2):uint()
    local am = tvb(off + 7, 2):uint()
    t:add(pf.mb_freq, tvb(off + 5, 2))
        :append_text(string.format("  (%.2f Hz)", fr / 65536.0 * 200.0))
    t:add(pf.mb_amp,  tvb(off + 7, 2))
        :append_text(string.format("  (%.1f%%)", am / 65535.0 * 100.0))
end

-- Groups 0x3F / 0x40 — wheel config, LED live path
local function decode_wheel_cfg(tvb, t, off, n)
    if n < 1 then return false end
    local c0 = tvb(off, 1):uint()
    local c1 = (n >= 2) and tvb(off + 1, 1):uint() or nil

    -- 19 [G] — 20-byte live colour chunk: 5 x [idx, R, G, B]
    if c0 == 0x19 and c1 ~= nil and n >= 6 then
        t:add(pf.led_group, tvb(off + 1, 1))
        local entries = math.floor((n - 2) / 4)
        for i = 0, entries - 1 do
            local eo = off + 2 + i * 4
            local idx = tvb(eo, 1):uint()
            local et = t:add(moza, tvb(eo, 4), string.format("LED entry %d", i))
            et:add(pf.led_index, tvb(eo, 1))
            et:add(pf.led_rgb,   tvb(eo + 1, 3))
            if idx == 0xFF then
                et:append_text(": padding (0xFF)")
            else
                et:append_text(string.format(": led %d = #%02X%02X%02X", idx,
                    tvb(eo + 1, 1):uint(), tvb(eo + 2, 1):uint(), tvb(eo + 3, 1):uint()))
            end
        end
        local rem = (n - 2) % 4
        if rem > 0 then t:add(pf.data, tvb(off + 2 + entries * 4, rem)) end
        return true
    end

    -- 1A [G] — live bitmask: [active u32 LE][window u32 LE] (8-byte form)
    if c0 == 0x1A and c1 ~= nil then
        t:add(pf.led_group, tvb(off + 1, 1))
        if n >= 10 then
            t:add_le(pf.led_active, tvb(off + 2, 4))
            t:add_le(pf.led_window, tvb(off + 6, 4))
                :append_text("  [full addressable LED set for this group]")
            if n > 10 then t:add(pf.data, tvb(off + 10, n - 10)) end
        elseif n > 2 then
            t:add(pf.cfg_data, tvb(off + 2, n - 2))
                :append_text("  [short bitmask form]")
        end
        return true
    end

    -- 1F [G] FF [N] — static per-LED colour
    if c0 == 0x1F and n >= 7 then
        t:add(pf.led_group, tvb(off + 1, 1))
        t:add(pf.led_index, tvb(off + 3, 1))
        t:add(pf.led_rgb,   tvb(off + 4, 3))
        return true
    end

    -- Generic: cmd prefix already added by the caller; let the shared
    -- value-bytes path render the tail.
    return false
end

-- Group 0x42 — FSR1 fixed-schema display push
-- docs/protocol/devices/wheel-0x17.md § Group 0x42
local function decode_fsr1(tvb, t, off, n)
    if n < 5 then
        if n > 0 then t:add(pf.data, tvb(off, n)) end
        return
    end
    t:add(pf.fsr_type, tvb(off, 1))
    t:add(pf.fsr_b1,   tvb(off + 1, 1))
    t:add(pf.fsr_b2,   tvb(off + 2, 1))
        :append_text("  [sub-header; does NOT gate field presence — see docs]")
    if n > 5 then
        t:add(pf.fsr_data, tvb(off + 5, n - 5))
            :append_text("  [u16-BE gauge slots from offset 5, u8 tail]")
    end
end

-- Groups 0x35 / 0x36 — CM1 keyed value stream: N/6 records of
-- [key u16 BE][value f32 BE].  docs/protocol/devices/dash-0x14.md § 0x35
local function decode_cm1(tvb, t, off, n)
    local recs = math.floor(n / 6)
    for i = 0, recs - 1 do
        local ro = off + i * 6
        local rt = t:add(moza, tvb(ro, 6), string.format("Record %d", i))
        rt:add(pf.cm1_key,   tvb(ro, 2))
        rt:add(pf.cm1_value, tvb(ro + 2, 4))
        rt:append_text(string.format(": key=0x%04X value=%g",
            tvb(ro, 2):uint(), tvb(ro + 2, 4):float()))
    end
    local rem = n % 6
    if rem > 0 then t:add(pf.data, tvb(off + recs * 6, rem)) end
end

-- Top-level identity groups 0x02..0x11 (unwrapped).
-- docs/protocol/identity/wheel-probe-sequence.md
local function decode_identity(tvb, t, off, n, group)
    if n < 1 then return end
    local bg = base_group(group)
    local has_subcmd = (bg == 0x07 or bg == 0x08 or bg == 0x0F or bg == 0x10)
    if is_response_group(group) then
        local so, sl = off, n
        if has_subcmd and n >= 2 then
            t:add(pf.id_subcmd, tvb(off, 1))
            so, sl = off + 1, n - 1
        end
        if sl > 0 and is_ascii(tvb, so, sl) then
            local s = read_ascii(tvb, so, sl)
            t:add(pf.id_string, tvb(so, sl), s)
        elseif sl > 0 then
            local it = t:add(pf.id_bytes, tvb(so, sl))
            if bg == 0x09 then it:append_text("  [sub-device count]")
            elseif bg == 0x06 then it:append_text("  [12-byte STM32 MCU UID]")
            elseif bg == 0x04 then it:append_text("  [byte 2 = dev_type]") end
        end
    else
        if has_subcmd then
            t:add(pf.id_subcmd, tvb(off, 1))
            if n > 1 then t:add(pf.data, tvb(off + 1, n - 1)) end
        else
            t:add(pf.data, tvb(off, n))
        end
    end
end

-- ─── Core frame parser ──────────────────────────────────────────────────────

-- De-stuff one frame starting at `off` in `tvb`.
-- Mirrors Protocol/MozaSerialConnection.cs ReadLoop exactly.
-- Returns: status, needed, body_bytearray, wire_len
--   status = "ok" | "escape" | "truncated"
local function destuff(tvb, off, len, n)
    local needed = n + 3            -- group + device + payload + checksum
    local body = ByteArray.new()
    body:set_size(needed)
    local decoded = 0
    local wp = off + 2
    while decoded < needed do
        if wp >= len then return "truncated", needed, body, wp - off end
        local b = tvb(wp, 1):uint(); wp = wp + 1
        if b == START then
            if wp >= len then return "truncated", needed, body, wp - off end
            local e = tvb(wp, 1):uint(); wp = wp + 1
            if e ~= START then return "escape", needed, body, wp - off end
            body:set_index(decoded, START)
        else
            body:set_index(decoded, b)
        end
        decoded = decoded + 1
    end
    return "ok", needed, body, wp - off
end

-- Wire checksum over the de-stuffed body (group, device, payload), matching
-- MozaProtocol.CalculateWireChecksumFromParts.
local function wire_checksum(n, body, body_len)
    local sum = MAGIC + START + n
    for i = 0, body_len - 1 do
        local b = body:get_index(i)
        sum = sum + b
        if b == START then sum = sum + START end
    end
    return sum & 0xFF
end

-- Plain (non-escape-aware) checksum over verbatim wire bytes. This is what a
-- legacy sender that never doubled 0x7E produced — the SimHub plugin's old
-- CalculateChecksum(), fixed 2026-04-22 (docs/protocol/wire/checksum.md
-- § Plugin impl note). Captures taken before that fix contain lone 0x7E bytes
-- inside 7D 23 telemetry payloads and must be read this way.
local function plain_checksum(n, body)
    local sum = MAGIC + START + n
    for i = 0, n + 1 do sum = sum + body:get_index(i) end
    return sum & 0xFF
end

-- Resolve one frame at `off`. Tries the stuffed reading first, then the legacy
-- unstuffed reading; the checksum decides which is real, so mixed-era captures
-- both decode. Returns:
--   mode      "stuffed" | "raw" | nil
--   body      ByteArray of [group, device, payload..., checksum] (nil if mode nil)
--   wire_len  bytes consumed on the wire
--   chk_ok    whether the accepted reading's checksum validated
--   fail      "truncated" | "escape" when mode is nil
local function read_frame(tvb, off, len, n)
    local needed = n + 3
    local status, _, body, wire_len = destuff(tvb, off, len, n)

    if status == "ok" and wire_checksum(n, body, n + 2) == body:get_index(needed - 1) then
        return "stuffed", body, wire_len, true
    end

    if off + 2 + needed <= len then
        local raw = tvb(off + 2, needed):bytes()
        if plain_checksum(n, raw) == raw:get_index(needed - 1) then
            return "raw", raw, 2 + needed, true
        end
    end

    -- Neither reading validated: surface the complete-but-corrupt frame if we
    -- have one, otherwise report why we could not frame it at all.
    if status == "ok" then return "stuffed", body, wire_len, false end
    return nil, nil, wire_len, false, status
end

local function parse_frames(tvb, pinfo, tree)
    local len         = tvb:len()
    local offset      = 0
    local frame_count = 0
    local info_items  = {}

    while offset < len do
        if tvb(offset, 1):uint() ~= START then
            offset = offset + 1
        elseif offset + 2 > len then
            break                                   -- need start + N
        else
            local n = tvb(offset + 1, 1):uint()
            if n > 200 then
                -- Not a plausible length; resync on the next 0x7E.
                offset = offset + 1
            else
                local needed = n + 3
                local mode, body, wire_len, chk_ok, fail =
                    read_frame(tvb, offset, len, n)

                if mode == nil and fail == "truncated" then
                    local ti = tree:add(moza, tvb(offset, len - offset),
                        string.format("Moza [partial]: N=%d, %d/%d body bytes in this transfer",
                            n, len - offset - 2, needed))
                    ti:add_proto_expert_info(ef.truncated)
                    break
                end

                if mode == nil then
                    local ei = tree:add(moza, tvb(offset, math.min(wire_len, len - offset)),
                        "Moza [escape violation]: lone 0x7E inside frame body, checksum invalid both ways")
                    ei:add_proto_expert_info(ef.esc_viol)
                    offset = offset + 1              -- resync like the read loop
                else
                    -- Present the frame from a de-stuffed tvb only when the wire
                    -- actually carried escapes; otherwise dissect in place so the
                    -- byte view stays on the real packet.
                    local escapes = wire_len - (needed + 2)
                    local ftvb, foff
                    if escapes > 0 then
                        local full = ByteArray.new()
                        full:set_size(needed + 2)
                        full:set_index(0, START)
                        full:set_index(1, n)
                        for i = 0, needed - 1 do
                            full:set_index(2 + i, body:get_index(i))
                        end
                        ftvb = full:tvb(string.format("Moza frame (de-stuffed, %d escape%s)",
                            escapes, escapes == 1 and "" or "s"))
                        foff = 0
                    else
                        ftvb = tvb
                        foff = offset
                    end

                    local group  = body:get_index(0)
                    local device = body:get_index(1)
                    local is_resp = is_response_group(group)
                    local bg = base_group(group)
                    local grp_s = group_label(group)
                    local dev_s = device_label(device)
                    local dir_s = is_resp and "RSP" or "REQ"

                    local payload_off = foff + 4
                    local cmd_name, cmd_len = nil, 0
                    if n >= 1 then
                        cmd_name, cmd_len = cmd_lookup(group, ftvb, payload_off, n)
                    end

                    -- Frame label
                    local label
                    if bg == 0x00 and n == 0 then
                        label = string.format("Moza #%d [%s]: Bus Heartbeat -> %s",
                            frame_count + 1, dir_s, dev_s)
                    elseif bg == 0x43 and n == 1 then
                        local kb = ftvb(payload_off, 1):uint()
                        if kb == 0x00 then
                            label = string.format("Moza #%d [%s]: Dash Keepalive -> %s",
                                frame_count + 1, dir_s, dev_s)
                        elseif kb == 0x80 then
                            label = string.format("Moza #%d [%s]: Dash Keepalive Ack -> %s",
                                frame_count + 1, dir_s, dev_s)
                        else
                            label = string.format("Moza #%d [%s]: %s -> %s  probe:%02X",
                                frame_count + 1, dir_s, grp_s, dev_s, kb)
                        end
                    else
                        local cs = ""
                        if cmd_name then
                            cs = string.format(" [%s]", cmd_name)
                        elseif n >= 2 then
                            cs = string.format(" cmd:%s",
                                ftvb(payload_off, math.min(2, n)):bytes():tohex())
                        elseif n == 1 then
                            cs = string.format(" cmd:%02X", ftvb(payload_off, 1):uint())
                        end
                        label = string.format("Moza #%d [%s]: %s -> %s%s  (N=%d)",
                            frame_count + 1, dir_s, grp_s, dev_s, cs, n)
                    end

                    -- Root item always spans the real wire bytes.
                    local ftree = tree:add(moza, tvb(offset, wire_len), label)
                    ftree:add(pf.start,  ftvb(foff, 1))
                    ftree:add(pf.n,      ftvb(foff + 1, 1))
                    ftree:add(pf.group,  ftvb(foff + 2, 1))
                        :append_text(is_resp and "  [RESPONSE]" or "  [REQUEST]")
                    ftree:add(pf.device, ftvb(foff + 3, 1))
                    ftree:add(pf.is_response, ftvb(foff + 2, 1), is_resp)
                    ftree:add(pf.wire_len, tvb(offset, wire_len), wire_len)
                    if escapes > 0 then
                        ftree:add(pf.escapes, tvb(offset, wire_len), escapes)
                            :append_text("  [0x7E doubled on wire; body shown de-stuffed]")
                    end
                    if mode == "raw" then
                        ftree:add(pf.legacy, tvb(offset, wire_len), true)
                            :append_text("  [lone 0x7E in body; pre-2026-04-22 sender]")
                        ftree:add_proto_expert_info(ef.legacy)
                    end

                    -- Checksum. `mode` records which reading validated:
                    -- "stuffed" = escape-aware (current firmware + plugin),
                    -- "raw" = legacy unstuffed sender.
                    local expected = (mode == "raw")
                        and plain_checksum(n, body)
                        or  wire_checksum(n, body, n + 2)
                    local actual   = body:get_index(needed - 1)
                    local ci = ftree:add(pf.checksum, ftvb(foff + 4 + n, 1))
                    if expected == actual then
                        ci:append_text(mode == "raw" and "  [OK, legacy unstuffed]" or "  [OK]")
                    else
                        ci:append_text(string.format("  [BAD: computed 0x%02X]", expected))
                        ftree:add(pf.chk_calc, ftvb(foff + 4 + n, 1), expected)
                        ftree:add(pf.chk_status, ftvb(foff + 4 + n, 1), "CHECKSUM MISMATCH")
                        ftree:add_proto_expert_info(ef.bad_chk)
                    end

                    -- Payload
                    if n >= 1 then
                        if cmd_len > 0 then
                            ftree:add(pf.cmd, ftvb(payload_off, cmd_len))
                            ftree:add(pf.cmd_name, ftvb(payload_off, cmd_len), cmd_name)
                        elseif n >= 2 and not NO_CMD_ID[bg] then
                            -- Unrecognised opcode: show the conventional 2-byte
                            -- command ID so it can still be grepped/filtered.
                            ftree:add(pf.cmd, ftvb(payload_off, 2))
                        end

                        local handled = true
                        if bg == 0x43 then
                            decode_43(ftvb, ftree, payload_off, n, group)
                        elseif bg == 0x0E then
                            decode_param_debug(ftvb, ftree, payload_off, n, group)
                        elseif bg == 0x2D then
                            decode_2d(ftvb, ftree, payload_off, n)
                        elseif bg == 0x42 then
                            decode_fsr1(ftvb, ftree, payload_off, n)
                        elseif bg == 0x35 or bg == 0x36 then
                            decode_cm1(ftvb, ftree, payload_off, n)
                        elseif bg == 0x24 and ftvb(payload_off, 1):uint() == 0xB1 then
                            decode_mbooster(ftvb, ftree, payload_off, n)
                        elseif bg == 0x3F or bg == 0x40 or bg == 0x3E then
                            handled = decode_wheel_cfg(ftvb, ftree, payload_off, n)
                        elseif bg == 0x41 then
                            if n > 2 then
                                ftree:add(pf.cfg_data, ftvb(payload_off + 2, n - 2))
                                    :append_text("  [always 00 00 00 00 in captures]")
                            end
                        elseif (bg == 0x32 or bg == 0x33)
                               and ftvb(payload_off, 1):uint() == 0x81 and n >= 5 then
                            ftree:add(pf.fsr_index, ftvb(payload_off + 1, 4))
                        elseif bg >= 0x02 and bg <= 0x11 and bg ~= 0x0A and bg ~= 0x0E then
                            decode_identity(ftvb, ftree, payload_off, n, group)
                        else
                            handled = false
                        end

                        if not handled then
                            local vo = payload_off + math.max(cmd_len, 0)
                            local vl = n - math.max(cmd_len, 0)
                            if vl > 0 then
                                local vi = ftree:add(pf.cfg_data, ftvb(vo, vl))
                                if vl <= 4 then
                                    local be = ftvb(vo, vl):uint()
                                    vi:append_text(string.format("  (BE = %d)", be))
                                end
                            end
                        end
                    end

                    -- Info column entry
                    local entry
                    if bg == 0x00 and n == 0 then
                        entry = string.format("HB>%s", dev_s)
                    elseif bg == 0x43 and n == 1 then
                        entry = string.format("KA>%s", dev_s)
                    elseif cmd_name then
                        entry = string.format("0x%02X>%s [%s]", group, dev_s, cmd_name)
                    else
                        entry = string.format("0x%02X>%s", group, dev_s)
                    end
                    info_items[#info_items + 1] = entry

                    frame_count = frame_count + 1
                    offset = offset + wire_len
                end
            end
        end
    end

    if frame_count > 0 then
        pinfo.cols.protocol:set("MOZA")
        pinfo.cols.info:set(table.concat(info_items, " | "))
    end
    return frame_count
end

-- ─── Hook: post-dissector on the usbcom (CDC bulk data) layer ───────────────
--
-- usbcom.data.out_payload / in_payload are FT_BYTES fields; FieldInfo.value
-- yields a ByteArray, which we promote to a Tvb for parsing.
--
-- Both fields are extracted (a single URB carries only one direction, but the
-- field extractor can return several instances when a capture stacks them).
-- Payloads are scanned rather than requiring byte 0 == 0x7E, so a transfer that
-- begins mid-frame still surfaces whatever complete frames it contains.

local fi_out = Field.new("usbcom.data.out_payload")
local fi_in  = Field.new("usbcom.data.in_payload")

function moza.dissector(tvb, pinfo, tree)
    local total = 0
    for _, extractor in ipairs({ fi_out, fi_in }) do
        for _, fi in ipairs({ extractor() }) do
            local ba = fi.value
            if ba ~= nil and ba:len() > 0 then
                total = total + parse_frames(ba:tvb("Moza Protocol"), pinfo, tree)
            end
        end
    end
    return total
end

register_postdissector(moza)
