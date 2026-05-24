using System.Collections.Generic;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Pre-built command definitions from docs/protocol/devices/.
    /// </summary>
    public static class MozaCommandDatabase
    {
        private static readonly Dictionary<string, MozaCommand> _commands = new Dictionary<string, MozaCommand>();
        public static IReadOnlyDictionary<string, MozaCommand> Commands => _commands;

        // Group-indexed lookup built once at static-constructor end. Each command
        // appears under both its ReadGroup and WriteGroup keys (skipping 0xFF =
        // not-applicable). Lets MozaResponseParser scan only the commands matching
        // the inbound group instead of iterating all ~200+ entries per message.
        private static readonly Dictionary<byte, List<MozaCommand>> _byGroup
            = new Dictionary<byte, List<MozaCommand>>();
        public static IReadOnlyList<MozaCommand> CommandsForGroup(byte group)
        {
            return _byGroup.TryGetValue(group, out var list)
                ? (IReadOnlyList<MozaCommand>)list
                : System.Array.Empty<MozaCommand>();
        }

        static MozaCommandDatabase()
        {
            // ===== WHEELBASE (device: base, read group 40, write group 41) =====
            AddCommand("base-limit",              "base", 40, 41, new byte[] { 1 },      2, "int");
            AddCommand("base-ffb-strength",       "base", 40, 41, new byte[] { 2 },      2, "int");
            AddCommand("base-inertia",            "base", 40, 41, new byte[] { 4 },      2, "int");
            AddCommand("base-damper",             "base", 40, 41, new byte[] { 7 },      2, "int");
            AddCommand("base-friction",           "base", 40, 41, new byte[] { 8 },      2, "int");
            AddCommand("base-spring",             "base", 40, 41, new byte[] { 9 },      2, "int");
            AddCommand("base-speed",              "base", 40, 41, new byte[] { 10 },     2, "int");
            AddCommand("base-road-sensitivity",   "base", 40, 41, new byte[] { 12 },     2, "int");
            AddCommand("base-protection",         "base", 40, 41, new byte[] { 13 },     2, "int");
            AddCommand("base-equalizer1",         "base", 40, 41, new byte[] { 14 },     2, "int");
            AddCommand("base-equalizer2",         "base", 40, 41, new byte[] { 15 },     2, "int");
            AddCommand("base-equalizer3",         "base", 40, 41, new byte[] { 16 },     2, "int");
            AddCommand("base-equalizer4",         "base", 40, 41, new byte[] { 17 },     2, "int");
            AddCommand("base-torque",             "base", 40, 41, new byte[] { 18 },     2, "int");
            AddCommand("base-natural-inertia",    "base", 40, 41, new byte[] { 19 },     2, "int");
            AddCommand("base-equalizer5",         "base", 40, 41, new byte[] { 20 },     2, "int");
            AddCommand("base-natural-inertia-enable", "base", 40, 41, new byte[] { 22 }, 2, "int");
            AddCommand("base-max-angle",          "base", 40, 41, new byte[] { 23 },     2, "int");
            AddCommand("base-ffb-reverse",        "base", 40, 41, new byte[] { 24 },     2, "int");
            AddCommand("base-speed-damping",      "base", 40, 41, new byte[] { 25 },     2, "int");
            AddCommand("base-speed-damping-point","base", 40, 41, new byte[] { 26 },     2, "int");
            AddCommand("base-soft-limit-strength","base", 40, 41, new byte[] { 27 },     2, "int");
            AddCommand("base-soft-limit-retain",  "base", 40, 41, new byte[] { 28 },     2, "int");
            // cmd 0x1E "Performance output" (legacy keep-name "temp-strategy"): 0=Reserved, 1=Full.
            AddCommand("base-temp-strategy",      "base", 40, 41, new byte[] { 30 },     2, "int");
            AddCommand("base-soft-limit-stiffness","base",40, 41, new byte[] { 31 },     2, "int");
            AddCommand("base-equalizer6",         "base", 40, 41, new byte[] { 44 },     2, "int");
            AddCommand("base-protection-mode",    "base", 40, 41, new byte[] { 45 },     2, "int");
            // cmd 0x2E base "Gearshift vibration intensity" slider, range 0..5.
            // Companion fire-and-forget event = `gearshift-event` (cmd 0x76 grp 0x2D).
            AddCommand("base-gearshift-vibration","base", 40, 41, new byte[] { 46 },     2, "int");
            // cmd 0x76 grp 0x2D fire-and-forget gearshift event. Body `76 00 01` always.
            // Wheel uses persisted base-gearshift-vibration intensity; no echo on 0xAD.
            AddCommand("base-gearshift-event",    "base", 0xFF, 45, new byte[] { 0x76 }, 2, "int");
            AddCommand("base-ffb-disable",        "base", 40, 41, new byte[] { 254 },    2, "int");

            // FFB curve (read group 40, write group 41)
            AddCommand("base-ffb-curve-x1", "base", 40, 41, new byte[] { 34, 1 }, 1, "int");
            AddCommand("base-ffb-curve-x2", "base", 40, 41, new byte[] { 34, 2 }, 1, "int");
            AddCommand("base-ffb-curve-x3", "base", 40, 41, new byte[] { 34, 3 }, 1, "int");
            AddCommand("base-ffb-curve-x4", "base", 40, 41, new byte[] { 34, 4 }, 1, "int");
            AddCommand("base-ffb-curve-y1", "base", 40, 41, new byte[] { 34, 5 }, 1, "int");
            AddCommand("base-ffb-curve-y2", "base", 40, 41, new byte[] { 34, 6 }, 1, "int");
            AddCommand("base-ffb-curve-y3", "base", 40, 41, new byte[] { 34, 7 }, 1, "int");
            AddCommand("base-ffb-curve-y4", "base", 40, 41, new byte[] { 34, 8 }, 1, "int");
            AddCommand("base-ffb-curve-y5", "base", 40, 41, new byte[] { 34, 9 }, 1, "int");

            // Wheelbase telemetry (read group 43, read-only)
            AddCommand("base-state",       "base", 43, 0xFF, new byte[] { 1 }, 2, "int");
            AddCommand("base-state-err",   "base", 43, 0xFF, new byte[] { 2 }, 2, "int");
            AddCommand("base-mcu-temp",    "base", 43, 0xFF, new byte[] { 4 }, 2, "int");
            AddCommand("base-mosfet-temp", "base", 43, 0xFF, new byte[] { 5 }, 2, "int");
            AddCommand("base-motor-temp",  "base", 43, 0xFF, new byte[] { 6 }, 2, "int");

            // Wheelbase calibration (write group 42)
            AddCommand("base-calibration", "base", 0xFF, 42, new byte[] { 1 }, 2, "int");

            // Partner-SDK (iRacing-MOZA bridge). iRacing POSTs each of these
            // CoAP URIs exactly once per session as a capability probe; the
            // wheel firmware persists the value to EEPROM (Tables 11 / 5 — see
            // [INFO]param_manage.c echoes on group 0x0E). Wire format observed:
            // CoAP LE int32 -> serial BE16 in the last 2 payload bytes (high
            // 16 bits never populated). Verified 2026-05-23 via paired UDP +
            // USB-CDC captures (tools/correlate_coap_serial.py).
            AddCommand("base-feedforward",      "base", 0xFF, 0x2A, new byte[] { 0x40 }, 2, "int");
            AddCommand("base-high-freq-torque", "base", 0xFF, 0x2A, new byte[] { 0x41 }, 2, "int");
            AddCommand("base-motor-run-state",  "base", 0xFF, 0x2C, new byte[] { 0x01 }, 2, "int");

            // ===== MAIN DEVICE (device: main, read/write group 31) =====
            AddCommand("main-set-compat-mode",   "main", 0xFF, 31, new byte[] { 19 }, 1, "int");
            AddCommand("main-get-compat-mode",   "main", 31, 0xFF, new byte[] { 23 }, 1, "int");
            AddCommand("main-set-ble-mode",      "main", 0xFF, 31, new byte[] { 71 }, 1, "int");
            AddCommand("main-get-ble-mode",      "main", 31, 0xFF, new byte[] { 70 }, 1, "int");
            AddCommand("main-set-led-status",    "main", 0xFF, 31, new byte[] { 9 },  1, "int");
            AddCommand("main-get-led-status",    "main", 31, 0xFF, new byte[] { 8 },  1, "int");
            AddCommand("main-set-work-mode",     "main", 0xFF, 31, new byte[] { 51 }, 1, "int");
            AddCommand("main-get-work-mode",     "main", 31, 0xFF, new byte[] { 52 }, 1, "int");
            AddCommand("main-set-interpolation", "main", 0xFF, 31, new byte[] { 76 }, 1, "int");
            AddCommand("main-get-interpolation", "main", 31, 0xFF, new byte[] { 77 }, 1, "int");
            AddCommand("main-set-spring-gain",   "main", 0xFF, 31, new byte[] { 78, 8 },  1, "int");
            AddCommand("main-set-damper-gain",   "main", 0xFF, 31, new byte[] { 78, 9 },  1, "int");
            AddCommand("main-set-inertia-gain",  "main", 0xFF, 31, new byte[] { 78, 10 }, 1, "int");
            AddCommand("main-set-friction-gain", "main", 0xFF, 31, new byte[] { 78, 11 }, 1, "int");
            AddCommand("main-get-spring-gain",   "main", 31, 0xFF, new byte[] { 79, 8 },  1, "int");
            AddCommand("main-get-damper-gain",   "main", 31, 0xFF, new byte[] { 79, 9 },  1, "int");
            AddCommand("main-get-inertia-gain",  "main", 31, 0xFF, new byte[] { 79, 10 }, 1, "int");
            AddCommand("main-get-friction-gain", "main", 31, 0xFF, new byte[] { 79, 11 }, 1, "int");

            // ===== PEDALS (read group 35, write group 36) =====
            AddCommand("pedals-throttle-dir", "pedals", 35, 36, new byte[] { 1 }, 2, "int");
            AddCommand("pedals-throttle-min", "pedals", 35, 36, new byte[] { 2 }, 2, "int");
            AddCommand("pedals-throttle-max", "pedals", 35, 36, new byte[] { 3 }, 2, "int");
            AddCommand("pedals-brake-dir",    "pedals", 35, 36, new byte[] { 4 }, 2, "int");
            AddCommand("pedals-brake-min",    "pedals", 35, 36, new byte[] { 5 }, 2, "int");
            AddCommand("pedals-brake-max",    "pedals", 35, 36, new byte[] { 6 }, 2, "int");
            AddCommand("pedals-brake-angle-ratio", "pedals", 35, 36, new byte[] { 26 }, 4, "float");
            AddCommand("pedals-clutch-dir",   "pedals", 35, 36, new byte[] { 7 }, 2, "int");
            AddCommand("pedals-clutch-min",   "pedals", 35, 36, new byte[] { 8 }, 2, "int");
            AddCommand("pedals-clutch-max",   "pedals", 35, 36, new byte[] { 9 }, 2, "int");

            // Pedal output curves (4-byte float, read 35 / write 36)
            AddCommand("pedals-throttle-y1", "pedals", 35, 36, new byte[] { 14 }, 4, "float");
            AddCommand("pedals-throttle-y2", "pedals", 35, 36, new byte[] { 15 }, 4, "float");
            AddCommand("pedals-throttle-y3", "pedals", 35, 36, new byte[] { 16 }, 4, "float");
            AddCommand("pedals-throttle-y4", "pedals", 35, 36, new byte[] { 17 }, 4, "float");
            AddCommand("pedals-throttle-y5", "pedals", 35, 36, new byte[] { 27 }, 4, "float");
            AddCommand("pedals-brake-y1",    "pedals", 35, 36, new byte[] { 18 }, 4, "float");
            AddCommand("pedals-brake-y2",    "pedals", 35, 36, new byte[] { 19 }, 4, "float");
            AddCommand("pedals-brake-y3",    "pedals", 35, 36, new byte[] { 20 }, 4, "float");
            AddCommand("pedals-brake-y4",    "pedals", 35, 36, new byte[] { 21 }, 4, "float");
            AddCommand("pedals-brake-y5",    "pedals", 35, 36, new byte[] { 28 }, 4, "float");
            AddCommand("pedals-clutch-y1",   "pedals", 35, 36, new byte[] { 22 }, 4, "float");
            AddCommand("pedals-clutch-y2",   "pedals", 35, 36, new byte[] { 23 }, 4, "float");
            AddCommand("pedals-clutch-y3",   "pedals", 35, 36, new byte[] { 24 }, 4, "float");
            AddCommand("pedals-clutch-y4",   "pedals", 35, 36, new byte[] { 25 }, 4, "float");
            AddCommand("pedals-clutch-y5",   "pedals", 35, 36, new byte[] { 29 }, 4, "float");

            // Pedal calibration (write-only, group 38)
            AddCommand("pedals-throttle-cal-start", "pedals", 0xFF, 38, new byte[] { 12 }, 2, "int");
            AddCommand("pedals-throttle-cal-stop",  "pedals", 0xFF, 38, new byte[] { 16 }, 2, "int");
            AddCommand("pedals-brake-cal-start",    "pedals", 0xFF, 38, new byte[] { 13 }, 2, "int");
            AddCommand("pedals-brake-cal-stop",     "pedals", 0xFF, 38, new byte[] { 17 }, 2, "int");
            AddCommand("pedals-clutch-cal-start",   "pedals", 0xFF, 38, new byte[] { 14 }, 2, "int");
            AddCommand("pedals-clutch-cal-stop",    "pedals", 0xFF, 38, new byte[] { 18 }, 2, "int");

            // Pedal outputs (read group 37, read-only)
            AddCommand("pedals-throttle-output", "pedals", 37, 0xFF, new byte[] { 1 }, 2, "int");
            AddCommand("pedals-brake-output",    "pedals", 37, 0xFF, new byte[] { 2 }, 2, "int");
            AddCommand("pedals-clutch-output",   "pedals", 37, 0xFF, new byte[] { 3 }, 2, "int");

            // ===== HUB (read group 100, read-only) =====
            // Command IDs match foxblat/boxflat serial.yml
            AddCommand("hub-base-power",    "hub", 100, 0xFF, new byte[] { 2 },    2, "int");
            AddCommand("hub-port1-power",   "hub", 100, 0xFF, new byte[] { 3 },    2, "int");
            AddCommand("hub-port2-power",   "hub", 100, 0xFF, new byte[] { 4 },    2, "int");
            AddCommand("hub-port3-power",   "hub", 100, 0xFF, new byte[] { 5, 1 }, 1, "int");
            AddCommand("hub-pedals1-power", "hub", 100, 0xFF, new byte[] { 6 },    2, "int");
            AddCommand("hub-pedals2-power", "hub", 100, 0xFF, new byte[] { 7 },    2, "int");
            AddCommand("hub-pedals3-power", "hub", 100, 0xFF, new byte[] { 8 },    2, "int");

            // ===== TELEMETRY OUTPUT =====
            AddCommand("dash-send-telemetry",           "dash",  0xFF, 65, new byte[] { 253, 222 }, 4, "int");
            AddCommand("wheel-send-rpm-telemetry",      "wheel", 0xFF, 63, new byte[] { 26, 0 },    4, "array");
            AddCommand("wheel-send-buttons-telemetry",  "wheel", 0xFF, 63, new byte[] { 26, 1 },    8, "array");
            AddCommand("wheel-telemetry-rpm-colors",    "wheel", 0xFF, 63, new byte[] { 25, 0 },   20, "array");
            AddCommand("wheel-telemetry-button-colors", "wheel", 0xFF, 63, new byte[] { 25, 1 },   20, "array");
            AddCommand("wheel-send-knob-telemetry",     "wheel", 0xFF, 63, new byte[] { 26, 3 },    8, "array");
            AddCommand("wheel-telemetry-knob-colors",   "wheel", 0xFF, 63, new byte[] { 25, 3 },   20, "array");
            AddCommand("wheel-old-send-telemetry",      "wheel", 0xFF, 65, new byte[] { 253, 222 }, 4, "int");

            // ===== WHEEL IDENTITY (read-only, bytes=0 → request sends cmd ID only) =====
            AddCommand("wheel-model-name",  "wheel",  7, 0xFF, new byte[] { 1 }, 0, "array");
            AddCommand("wheel-sw-version",  "wheel", 15, 0xFF, new byte[] { 1 }, 0, "array");
            AddCommand("wheel-hw-version",  "wheel",  8, 0xFF, new byte[] { 1 }, 0, "array");
            AddCommand("wheel-hw-sub",      "wheel",  8, 0xFF, new byte[] { 2 }, 0, "array");
            AddCommand("wheel-serial-a",    "wheel", 16, 0xFF, new byte[] { 0 }, 0, "array");
            AddCommand("wheel-serial-b",    "wheel", 16, 0xFF, new byte[] { 1 }, 0, "array");
            // PitHouse-style extended identity probes (groups 0x02/0x04/0x05/0x06/0x09/0x11).
            // Empty-cmd requests (cmd_id=[], bytes=0) hit the request-as-probe path;
            // non-empty cmds use standard cmd_id prefix. Read-only, write_group=0xFF.
            AddCommand("wheel-presence",       "wheel",  9, 0xFF, new byte[] { },                 0, "array"); // 0x09 → reply `00 01` (1 sub-device)
            AddCommand("wheel-device-presence","wheel",  2, 0xFF, new byte[] { },                 0, "array"); // 0x02 → reply `02` (protocol ver?)
            AddCommand("wheel-device-type",    "wheel",  4, 0xFF, new byte[] { },                 0, "array"); // 0x04 → reply `01 02 04 06` (no cmd echo in reply)
            AddCommand("wheel-capabilities",   "wheel",  5, 0xFF, new byte[] { },                 0, "array"); // 0x05 → reply `01 02 1f 01` (no cmd echo in reply)
            AddCommand("wheel-mcu-uid",        "wheel",  6, 0xFF, new byte[] { },                 0, "array"); // 0x06 → 12-byte STM32 UID
            AddCommand("wheel-identity-11",    "wheel", 17, 0xFF, new byte[] { 4 },               0, "array"); // 0x11 cmd=04 → reply `04 01`

            // ===== BASE IDENTITY (device 0x13) =====
            // Same probe shapes as the wheel identity block above, just
            // re-targeted at device "base" (0x13). PitHouse capture
            // 2026-05-23 (iracing-pithouse-serial.pcapng) shows all five
            // probes issued cold-start: groups 0x06 / 0x07 / 0x08 / 0x0F /
            // 0x11 to device 0x13. Responses follow the standard
            // group | 0x80 / nibble-swap-device convention; the parser's
            // device-hint logic maps 0x31 → "base" so these don't collide
            // with the wheel-* lookups in the shared response groups.
            AddCommand("base-model-name",    "base",  7, 0xFF, new byte[] { 1 }, 0, "array");
            AddCommand("base-sw-version",    "base", 15, 0xFF, new byte[] { 1 }, 0, "array");
            AddCommand("base-hw-version",    "base",  8, 0xFF, new byte[] { 1 }, 0, "array");
            AddCommand("base-hw-sub",        "base",  8, 0xFF, new byte[] { 2 }, 0, "array");
            AddCommand("base-mcu-uid",       "base",  6, 0xFF, new byte[] { },   0, "array");
            AddCommand("base-identity-11",   "base", 17, 0xFF, new byte[] { 4 }, 0, "array");

            // ===== WHEEL SETTINGS (read group 64, write group 63) =====
            AddCommand("wheel-brightness",         "wheel", 64, 63, new byte[] { 1 },          1, "int");
            AddCommand("wheel-rpm-timings",        "wheel", 64, 63, new byte[] { 2 },         10, "array");
            AddCommand("wheel-paddles-mode",       "wheel", 64, 63, new byte[] { 3 },          1, "int");
            AddCommand("wheel-rpm-indicator-mode", "wheel", 64, 63, new byte[] { 4 },          1, "int");
            AddCommand("wheel-stick-mode",         "wheel", 64, 63, new byte[] { 5 },          2, "int");
            AddCommand("wheel-stick-mode-new",     "wheel", 0xFF, 63, new byte[] { 5 },        1, "int");
            AddCommand("wheel-set-rpm-display-mode","wheel",0xFF,63, new byte[] { 7 },         1, "int");
            AddCommand("wheel-get-rpm-display-mode","wheel", 64, 0xFF, new byte[] { 8 },       1, "int");
            AddCommand("wheel-clutch-point",       "wheel", 64, 63, new byte[] { 9 },          1, "int");
            AddCommand("wheel-knob-mode",          "wheel", 64, 63, new byte[] { 10 },         1, "int");
            // Per-encoder rotary signal mode (newer firmware). Each knob independently: 0=Buttons, 1=Knob.
            for (byte i = 0; i < 5; i++)
                AddCommand($"wheel-knob-signal-mode{i}", "wheel", 64, 63, new byte[] { 42, i }, 1, "int");
            AddCommand("wheel-paddle-adaptive-mode","wheel",64, 63, new byte[] { 11 },         1, "int");
            AddCommand("wheel-paddle-button-mode", "wheel", 64, 63, new byte[] { 13 },         1, "int");
            AddCommand("wheel-rpm-interval",       "wheel", 64, 63, new byte[] { 22 },         4, "int");
            AddCommand("wheel-rpm-mode",           "wheel", 64, 63, new byte[] { 23 },         1, "int");

            // Wheel RPM threshold values (read group 64, write group 63)
            for (byte i = 0; i < 10; i++)
                AddCommand($"wheel-rpm-value{i + 1}", "wheel", 64, 63, new byte[] { 24, i }, 2, "int");

            // Wheel RPM LED colors (read group 64, write group 63, id [31, 0, 0xFF, index]).
            // Group 0 spec max = 25 LEDs. 11..25 beyond any shipping wheel, exposed for diagnostics.
            for (byte i = 0; i < 25; i++)
                AddCommand($"wheel-rpm-color{i + 1}", "wheel", 64, 63, new byte[] { 31, 0, 0xFF, i }, 3, "array");

            // Wheel button colors (id [31, 1, 0xFF, index]). Group 1 spec max = 16 LEDs.
            for (byte i = 0; i < 16; i++)
                AddCommand($"wheel-button-color{i + 1}", "wheel", 64, 63, new byte[] { 31, 1, 0xFF, i }, 3, "array");

            // Per-knob Active LED color. Wire: [0x27, <knob 0..4>, <role>] + RGB.
            // role=0: WRITE persistent Active color / READ same. role=1: READ-only
            // live color at current rotation position (PitHouse never writes role=1).
            // See docs/protocol/findings/2026-05-10-knob-led-cmd27.md.
            for (byte k = 1; k <= 5; k++)
            {
                AddCommand($"wheel-knob{k}-active-color", "wheel", 64, 63, new byte[] { 0x27, (byte)(k - 1), 0 }, 3, "array");
                // Read-only live-position color (no WRITE — wheel ignores ROLE1 writes).
                AddCommand($"wheel-knob{k}-live-color",   "wheel", 64, 0xFF, new byte[] { 0x27, (byte)(k - 1), 1 }, 3, "array");
            }

            // Extended LED groups: G2 Single/28 LEDs, G3 Rotary/56 LEDs, G4 Ambient/12 LEDs.
            // Color [31,G,0xFF,idx], brightness [27,G,0xFF], mode [28,G], idle [29,G].
            foreach (var (prefix, g, n) in new[] {
                ("wheel-single",  (byte)2, 28),
                ("wheel-ambient", (byte)4, 12),
            })
            {
                AddCommand($"{prefix}-brightness",  "wheel", 64, 63, new byte[] { 27, g, 0xFF }, 1, "int");
                AddCommand($"{prefix}-mode",        "wheel", 64, 63, new byte[] { 28, g },       1, "int");
                AddCommand($"{prefix}-idle-effect", "wheel", 64, 63, new byte[] { 29, g },       1, "int");
                for (byte i = 0; i < n; i++)
                    AddCommand($"{prefix}-color{i + 1}", "wheel", 64, 63, new byte[] { 31, g, 0xFF, i }, 3, "array");
            }

            // Group 3 knob rings (knob-* prefix). bg-color sub-byte is 0x01
            // (PitHouse persistent/Inactive write); 0xFF was wrong for this group.
            // wheel-knob-led-mode is distinct from wheel-knob-mode (cmd 10 = encoder signal).
            AddCommand("wheel-knob-brightness",  "wheel", 64, 63, new byte[] { 27, 3, 0xFF }, 1, "int");
            AddCommand("wheel-knob-led-mode",    "wheel", 64, 63, new byte[] { 28, 3 },       1, "int");
            AddCommand("wheel-knob-idle-effect", "wheel", 64, 63, new byte[] { 29, 3 },       1, "int");
            for (byte i = 0; i < 56; i++)
                AddCommand($"wheel-knob-bg-color{i + 1}", "wheel", 64, 63, new byte[] { 31, 3, 0x01, i }, 3, "array");

            // LEGACY wheel-flag-color: flag LEDs live on the Meter sub-device
            // (dev 0x14 / grp 50). Kept for rollback on very old firmware.
            // for (byte i = 0; i < 6; i++)
            //     AddCommand($"wheel-flag-color{i + 1}", "wheel", 64, 63, new byte[] { 21, 2, i }, 3, "array");

            // Wheel RPM blink colors (write-only, id [15, index])
            for (byte i = 0; i < 10; i++)
                AddCommand($"wheel-rpm-blink-color{i + 1}", "wheel", 0xFF, 63, new byte[] { 15, i }, 3, "array");

            // Wheel brightness (by zone: 0=rpm, 1=buttons, 2=flags)
            AddCommand("wheel-rpm-brightness",     "wheel", 64, 63, new byte[] { 27, 0, 0xFF }, 1, "int");
            AddCommand("wheel-buttons-brightness",  "wheel", 64, 63, new byte[] { 27, 1, 0xFF }, 1, "int");
            // LEGACY: wheel-flags-brightness on device 0x17. Replaced by dash-flags-brightness
            // (Meter sub-device, write group 50, id [10, 2] — MeterSetCfg_SetFlagGroupBrightness_o).
            // AddCommand("wheel-flags-brightness",    "wheel", 64, 63, new byte[] { 27, 2, 0xFF }, 1, "int");

            // Wheel telemetry mode and idle effects
            AddCommand("wheel-telemetry-mode",          "wheel", 64, 63, new byte[] { 28, 0 },  1, "int");
            AddCommand("wheel-buttons-led-mode",        "wheel", 64, 63, new byte[] { 28, 1 },  1, "int");
            AddCommand("wheel-telemetry-idle-effect",   "wheel", 64, 63, new byte[] { 29, 0 },  1, "int");
            AddCommand("wheel-buttons-idle-effect",     "wheel", 64, 63, new byte[] { 29, 1 },  1, "int");
            // Per-(group, effect) idle animation speed (cmd 0x1E). Payload =
            // [effect_id, BE u16 ms]. Callers must fill effect_id; 0=Off is no-op.
            AddCommand("wheel-telemetry-idle-interval", "wheel", 0xFF, 63, new byte[] { 30, 0 }, 3, "array");
            AddCommand("wheel-buttons-idle-interval",   "wheel", 0xFF, 63, new byte[] { 30, 1 }, 3, "array");
            AddCommand("wheel-knob-idle-interval",      "wheel", 0xFF, 63, new byte[] { 30, 3 }, 3, "array");

            // Wheel sleep-light settings: mode 0x20 [mode], timeout 0x21 [BE u16 min],
            // speed 0x22 [mode, BE u16 ms], color 0x24 0xFF 0x01 0xFF [RGB].
            AddCommand("wheel-idle-mode",    "wheel", 64, 63, new byte[] { 32 },       1, "int");
            AddCommand("wheel-idle-timeout", "wheel", 64, 63, new byte[] { 33 },       2, "int");
            // wheel-idle-speed payload is [mode, ms_msb, ms_lsb]. Type "array" so
            // callers must build all 3 bytes — the previous hardcoded mode=0 in
            // CommandId silently sent slider updates to the wrong sleep mode.
            AddCommand("wheel-idle-speed",   "wheel", 64, 63, new byte[] { 34 },       3, "array");
            AddCommand("wheel-idle-color",   "wheel", 64, 63, new byte[] { 36, 255, 1, 255 }, 3, "array");

            // Old wheel (ES) colors
            for (byte i = 0; i < 10; i++)
                AddCommand($"wheel-old-rpm-color{i + 1}", "wheel", 64, 63, new byte[] { 21, 0, i }, 3, "array");
            AddCommand("wheel-old-rpm-brightness", "wheel", 64, 63, new byte[] { 20, 0 }, 1, "int");

            // ===== DASHBOARD SETTINGS (read group 51, write group 50) =====
            AddCommand("dash-rpm-timings",         "dash", 51, 50, new byte[] { 5 },       10, "array");
            AddCommand("dash-rpm-display-mode",    "dash", 51, 50, new byte[] { 7 },        1, "int");
            AddCommand("dash-rpm-brightness",      "dash", 51, 50, new byte[] { 10, 0 },    1, "int");
            AddCommand("dash-flags-brightness",    "dash", 51, 50, new byte[] { 10, 2 },    1, "int");
            AddCommand("dash-rpm-interval",        "dash", 51, 50, new byte[] { 12 },       4, "int");
            AddCommand("dash-rpm-mode",            "dash", 51, 50, new byte[] { 13 },       1, "int");
            AddCommand("dash-rpm-indicator-mode",  "dash", 51, 50, new byte[] { 17, 0 },    1, "int");
            AddCommand("dash-flags-indicator-mode", "dash", 51, 50, new byte[] { 17, 2 },   1, "int");

            // Dash RPM threshold values (read group 51, write group 50, id [14, index])
            for (byte i = 0; i < 10; i++)
                AddCommand($"dash-rpm-value{i + 1}", "dash", 51, 50, new byte[] { 14, i }, 4, "int");

            // Dash RPM LED colors (id [11, 0, index])
            for (byte i = 0; i < 10; i++)
                AddCommand($"dash-rpm-color{i + 1}", "dash", 51, 50, new byte[] { 11, 0, i }, 3, "array");

            // Dash RPM blink colors (write-only, id [9, index])
            for (byte i = 0; i < 10; i++)
                AddCommand($"dash-rpm-blink-color{i + 1}", "dash", 0xFF, 50, new byte[] { 9, i }, 3, "array");

            // Dash flag colors (id [11, 2, index])
            for (byte i = 0; i < 6; i++)
                AddCommand($"dash-flag-color{i + 1}", "dash", 51, 50, new byte[] { 11, 2, i }, 3, "array");

            // Dash flag default colors (write-only, id [8, 0])
            AddCommand("dash-flag-colors", "dash", 0xFF, 50, new byte[] { 8, 0 }, 18, "array");

            // ===== CM2 STANDALONE DASHBOARD METER CONFIG (dev=0x12, write grp 0x32) =====
            // Verified working in usb-capture/CM2.md lab 2026-05-21: standalone CM2
            // receives meter-config writes on its bridge/main (dev=0x12) under group
            // 0x32. Distinct from legacy dash-* commands at dev=0x14 — those did
            // *not* drive the CM2's LEDs in lab tests. All commands are write-only
            // (read_group=0xFF) to avoid colliding with dash-* read paths at grp 51.
            //
            // device-type "cm2-main" routes via MozaDeviceManager.GetDeviceId → 0x12.
            //
            // - 17 00 FF + value : indicator brightness (lab-verified visible effect)
            // - 18 00 + value    : normal mode (0=off, 1=telemetry, 2=forced-on)
            // - 19 00 + value    : standby mode
            // - 11 00 + value    : RPM group mode (1 = SimHub/telemetry mode)
            // - 11 02 + value    : flag group mode (1 = SimHub/telemetry mode)
            // - 0D + value       : RPM regulation mode (encoding TBV — write both
            //                      percent and absolute thresholds until confirmed)
            // - 05 + 10 B        : RPM percent thresholds (10-byte ramp)
            // - 0E <i> + u32     : RPM absolute threshold per rung (10 thresholds)
            // - 1B 00 FF <i> + RGB : per-LED stored color (16 LEDs, persists across
            //                      replug — use only for profile apply, not per-frame)
            AddCommand("cm2-indicator-brightness", "cm2-main", 0xFF, 0x32, new byte[] { 0x17, 0x00, 0xFF }, 1, "int");
            AddCommand("cm2-normal-mode",          "cm2-main", 0xFF, 0x32, new byte[] { 0x18, 0x00 }, 1, "int");
            AddCommand("cm2-standby-mode",         "cm2-main", 0xFF, 0x32, new byte[] { 0x19, 0x00 }, 1, "int");
            AddCommand("cm2-rpm-group-mode",       "cm2-main", 0xFF, 0x32, new byte[] { 0x11, 0x00 }, 1, "int");
            AddCommand("cm2-flag-group-mode",      "cm2-main", 0xFF, 0x32, new byte[] { 0x11, 0x02 }, 1, "int");
            AddCommand("cm2-rpm-regulation-mode",  "cm2-main", 0xFF, 0x32, new byte[] { 0x0D }, 1, "int");
            // 10-byte percent ramp (one byte per RPM rung, 0..100).
            AddCommand("cm2-rpm-percent-thresholds", "cm2-main", 0xFF, 0x32, new byte[] { 0x05 }, 10, "array");
            // Per-rung absolute RPM thresholds (10 rungs, u32 each).
            for (byte i = 0; i < 10; i++)
                AddCommand($"cm2-rpm-absolute-threshold{i + 1}", "cm2-main", 0xFF, 0x32, new byte[] { 0x0E, i }, 4, "int");
            // Per-LED stored color (16 LEDs on CM2 — all 16 are RPM positions per
            // user-confirmed hardware layout). cmd `1B 00 FF <i>` + RGB.
            for (byte i = 0; i < 16; i++)
                AddCommand($"cm2-stored-color{i + 1}", "cm2-main", 0xFF, 0x32, new byte[] { 0x1B, 0x00, 0xFF, i }, 3, "array");

            // ===== HANDBRAKE (device: handbrake, read group 91, write group 92) =====
            AddCommand("handbrake-direction",        "handbrake", 91, 92, new byte[] { 1 },  2, "int");
            AddCommand("handbrake-min",              "handbrake", 91, 92, new byte[] { 2 },  2, "int");
            AddCommand("handbrake-max",              "handbrake", 91, 92, new byte[] { 3 },  2, "int");
            AddCommand("handbrake-mode",             "handbrake", 91, 92, new byte[] { 11 }, 2, "int");
            AddCommand("handbrake-button-threshold", "handbrake", 91, 92, new byte[] { 10 }, 2, "int");

            // Handbrake output curve (4-byte float, read 91 / write 92)
            AddCommand("handbrake-y1", "handbrake", 91, 92, new byte[] { 5 }, 4, "float");
            AddCommand("handbrake-y2", "handbrake", 91, 92, new byte[] { 6 }, 4, "float");
            AddCommand("handbrake-y3", "handbrake", 91, 92, new byte[] { 7 }, 4, "float");
            AddCommand("handbrake-y4", "handbrake", 91, 92, new byte[] { 8 }, 4, "float");
            AddCommand("handbrake-y5", "handbrake", 91, 92, new byte[] { 9 }, 4, "float");

            // Handbrake calibration (write-only, group 94)
            AddCommand("handbrake-cal-start", "handbrake", 0xFF, 94, new byte[] { 3 }, 2, "int");
            AddCommand("handbrake-cal-stop",  "handbrake", 0xFF, 94, new byte[] { 4 }, 2, "int");

            // ===== AB9 ACTIVE SHIFTER (dev id 0x12) =====
            // Reads (grp 0x1E) and writes (grp 0x1F) use different shapes:
            //   WRITE 7E 03 1F 12 <cmd> 00 <val>  →  echo on 0x9F
            //   READ  7E 01 1E 12 <cmd>           →  resp on 0x9E + BE u16
            // See docs/protocol/devices/ab9-shifter.md.
            AddCommand("ab9-mode",                 "ab9", 0xFF, 0x1F, new byte[] { 0xD3, 0x00 }, 1, "int");
            AddCommand("ab9-mech-resistance",      "ab9", 0xFF, 0x1F, new byte[] { 0xD6, 0x00 }, 1, "int");
            AddCommand("ab9-spring",               "ab9", 0xFF, 0x1F, new byte[] { 0xAF, 0x00 }, 1, "int");
            AddCommand("ab9-natural-damping",      "ab9", 0xFF, 0x1F, new byte[] { 0xB0, 0x00 }, 1, "int");
            AddCommand("ab9-natural-friction",     "ab9", 0xFF, 0x1F, new byte[] { 0xB2, 0x00 }, 1, "int");
            AddCommand("ab9-max-torque-limit",     "ab9", 0xFF, 0x1F, new byte[] { 0xA9, 0x00 }, 1, "int");

            // Read-side entries: single-byte CommandId, 2-byte BE response value.
            // Cover stored-setting reads PitHouse polls at ~66 Hz throughout a
            // session (see ab9-game-20260513.jsonl).
            AddCommand("ab9-mode-read",            "ab9", 0x1E, 0xFF, new byte[] { 0xD3 }, 2, "int");
            AddCommand("ab9-mech-resistance-read", "ab9", 0x1E, 0xFF, new byte[] { 0xD6 }, 2, "int");
            AddCommand("ab9-spring-read",          "ab9", 0x1E, 0xFF, new byte[] { 0xAF }, 2, "int");
            AddCommand("ab9-natural-damping-read", "ab9", 0x1E, 0xFF, new byte[] { 0xB0 }, 2, "int");
            AddCommand("ab9-natural-friction-read","ab9", 0x1E, 0xFF, new byte[] { 0xB2 }, 2, "int");
            AddCommand("ab9-max-torque-limit-read","ab9", 0x1E, 0xFF, new byte[] { 0xA9 }, 2, "int");
            AddCommand("ab9-shifter-x-read",       "ab9", 0x1E, 0xFF, new byte[] { 0xD7 }, 2, "int");
            AddCommand("ab9-shifter-y-read",       "ab9", 0x1E, 0xFF, new byte[] { 0xD8 }, 2, "int");
            AddCommand("ab9-status-d4-read",       "ab9", 0x1E, 0xFF, new byte[] { 0xD4 }, 2, "int");
            AddCommand("ab9-status-5d-read",       "ab9", 0x1E, 0xFF, new byte[] { 0x5D }, 2, "int");

            // Identity-probe responses. Empty CommandId = wildcard; the response
            // group alone disambiguates. Requires busHint="ab9" so dev 0x12 collisions
            // with the wheelbase main don't match base-* first.
            AddCommand("ab9-presence",  "ab9", 0x09, 0xFF, new byte[] { }, 2,  "array");
            AddCommand("ab9-id-02",     "ab9", 0x02, 0xFF, new byte[] { }, 4,  "array");
            AddCommand("ab9-id-04",     "ab9", 0x04, 0xFF, new byte[] { }, 4,  "array");
            AddCommand("ab9-id-05",     "ab9", 0x05, 0xFF, new byte[] { }, 4,  "array");
            AddCommand("ab9-id-06",     "ab9", 0x06, 0xFF, new byte[] { }, 12, "array");
            AddCommand("ab9-id-07",     "ab9", 0x07, 0xFF, new byte[] { }, 17, "array");
            AddCommand("ab9-id-08",     "ab9", 0x08, 0xFF, new byte[] { }, 17, "array");
            AddCommand("ab9-id-0f",     "ab9", 0x0F, 0xFF, new byte[] { }, 17, "array");
            AddCommand("ab9-id-10",     "ab9", 0x10, 0xFF, new byte[] { }, 17, "array");
            AddCommand("ab9-id-11",     "ab9", 0x11, 0xFF, new byte[] { }, 2,  "array");

            // ===== mBOOSTER PEDALS (dev id 0x12 on its own USB-CDC) =====
            // The mBooster vibration motor lives on its own PID 0x0008 composite
            // device; per protocol note § 6 the pedal-config surface (groups 35/36)
            // is "likely but unverified" on mBooster firmware. The plugin ships
            // the full surface because the user opted into the experimental path
            // — register here, send via MBoosterDeviceController (which targets
            // device 0x12 on the mBooster's own connection). Motor write (0xb1)
            // and the keepalive frame are NOT registered: they don't fit the
            // single-cmd-id-array convention and are built inline by
            // <see cref="MozaMBoosterProtocol"/>.
            //
            // The "mbooster" device-type string + "mbooster" bus hint are the
            // two routing keys the response parser uses to keep mBooster acks
            // from cross-matching against the wheelbase main / AB9 main
            // (all three share device id 0x12 on different USB endpoints).
            AddCommand("mbooster-throttle-dir", "mbooster", 35, 36, new byte[] { 1 }, 2, "int");
            AddCommand("mbooster-throttle-min", "mbooster", 35, 36, new byte[] { 2 }, 2, "int");
            AddCommand("mbooster-throttle-max", "mbooster", 35, 36, new byte[] { 3 }, 2, "int");
            AddCommand("mbooster-brake-dir",    "mbooster", 35, 36, new byte[] { 4 }, 2, "int");
            AddCommand("mbooster-brake-min",    "mbooster", 35, 36, new byte[] { 5 }, 2, "int");
            AddCommand("mbooster-brake-max",    "mbooster", 35, 36, new byte[] { 6 }, 2, "int");
            AddCommand("mbooster-clutch-dir",   "mbooster", 35, 36, new byte[] { 7 }, 2, "int");
            AddCommand("mbooster-clutch-min",   "mbooster", 35, 36, new byte[] { 8 }, 2, "int");
            AddCommand("mbooster-clutch-max",   "mbooster", 35, 36, new byte[] { 9 }, 2, "int");
            AddCommand("mbooster-brake-angle-ratio", "mbooster", 35, 36, new byte[] { 26 }, 4, "float");
            // 5-point output curves per pedal (4-byte float, read 35 / write 36)
            AddCommand("mbooster-throttle-y1", "mbooster", 35, 36, new byte[] { 14 }, 4, "float");
            AddCommand("mbooster-throttle-y2", "mbooster", 35, 36, new byte[] { 15 }, 4, "float");
            AddCommand("mbooster-throttle-y3", "mbooster", 35, 36, new byte[] { 16 }, 4, "float");
            AddCommand("mbooster-throttle-y4", "mbooster", 35, 36, new byte[] { 17 }, 4, "float");
            AddCommand("mbooster-throttle-y5", "mbooster", 35, 36, new byte[] { 27 }, 4, "float");
            AddCommand("mbooster-brake-y1",    "mbooster", 35, 36, new byte[] { 18 }, 4, "float");
            AddCommand("mbooster-brake-y2",    "mbooster", 35, 36, new byte[] { 19 }, 4, "float");
            AddCommand("mbooster-brake-y3",    "mbooster", 35, 36, new byte[] { 20 }, 4, "float");
            AddCommand("mbooster-brake-y4",    "mbooster", 35, 36, new byte[] { 21 }, 4, "float");
            AddCommand("mbooster-brake-y5",    "mbooster", 35, 36, new byte[] { 28 }, 4, "float");
            AddCommand("mbooster-clutch-y1",   "mbooster", 35, 36, new byte[] { 22 }, 4, "float");
            AddCommand("mbooster-clutch-y2",   "mbooster", 35, 36, new byte[] { 23 }, 4, "float");
            AddCommand("mbooster-clutch-y3",   "mbooster", 35, 36, new byte[] { 24 }, 4, "float");
            AddCommand("mbooster-clutch-y4",   "mbooster", 35, 36, new byte[] { 25 }, 4, "float");
            AddCommand("mbooster-clutch-y5",   "mbooster", 35, 36, new byte[] { 29 }, 4, "float");
            // Live outputs (read-only group 37) — fallback live-position source
            // if HID identity pairing fails on a particular unit.
            AddCommand("mbooster-throttle-output", "mbooster", 37, 0xFF, new byte[] { 1 }, 2, "int");
            AddCommand("mbooster-brake-output",    "mbooster", 37, 0xFF, new byte[] { 2 }, 2, "int");
            AddCommand("mbooster-clutch-output",   "mbooster", 37, 0xFF, new byte[] { 3 }, 2, "int");

            // ===== BASE AMBIENT LEDS (dev 0x12, write grp 0x20, read grp 0x22) =====
            // Two 9-LED strips on R21/R25/R27 bodies; R9/R12 silently drop the read.
            // Detection gates on a 0xA2 response to base-ambient-brightness.
            // See docs/protocol/leds/base-ambient-0x20-0x22.md.

            // Live RPM color chunks: up to 5 LEDs × [idx, R, G, B] = 20B per chunk.
            AddCommand("base-ambient-rpm-colors-strip0", "main", 0xFF, 0x20, new byte[] { 0x1A, 0x00 }, 20, "array");
            AddCommand("base-ambient-rpm-colors-strip1", "main", 0xFF, 0x20, new byte[] { 0x1A, 0x01 }, 20, "array");
            AddCommand("base-ambient-send-rpm-strip0",   "main", 0xFF, 0x20, new byte[] { 0x1B, 0x00 },  4, "array");
            AddCommand("base-ambient-send-rpm-strip1",   "main", 0xFF, 0x20, new byte[] { 0x1B, 0x01 },  4, "array");

            // Configuration (read group 0x22, write group 0x20).
            AddCommand("base-ambient-indicator-state", "main", 0x22, 0x20, new byte[] { 0x1C }, 1, "int");
            AddCommand("base-ambient-standby-mode",    "main", 0x22, 0x20, new byte[] { 0x1D }, 1, "int");

            // Per-mode standby interval (BE u16 ms). Each standby mode (0..5)
            // stores its own interval register independently.
            for (byte m = 0; m < 6; m++)
                AddCommand($"base-ambient-standby-interval-mode{m}", "main", 0x22, 0x20, new byte[] { 0x1E, m }, 2, "int");

            // Brightness 0..255. PitHouse uses cmd `1F FF` on the wire even
            // though rs21_parameter.db lists `1F 02` — the `0xFF` form is the
            // capture-verified one and the only one the firmware honours.
            AddCommand("base-ambient-brightness", "main", 0x22, 0x20, new byte[] { 0x1F, 0xFF }, 1, "int");

            // Per-LED static colors. cmd `0x20 [strip] [mode] [led]` + RGB.
            // strip = 0/1, mode = 1 (constant) / 2 (breath), led = 0..8.
            // Only LedDeviceManager + UI path that touches all 36 needs these
            // registered; we add them now so any future per-LED settings UI
            // can use them without revisiting the database.
            for (byte strip = 0; strip < 2; strip++)
                for (byte mode = 1; mode <= 2; mode++)
                    for (byte led = 0; led < 9; led++)
                        AddCommand(
                            $"base-ambient-led-color-strip{strip}-mode{mode}-led{led}",
                            "main", 0x22, 0x20,
                            new byte[] { 0x20, strip, mode, led }, 3, "array");

            AddCommand("base-ambient-sleep-mode",      "main", 0x22, 0x20, new byte[] { 0x21 }, 1, "int");
            AddCommand("base-ambient-sleep-timeout",   "main", 0x22, 0x20, new byte[] { 0x22 }, 2, "int");
            AddCommand("base-ambient-breath-interval", "main", 0x22, 0x20, new byte[] { 0x23, 0x01 }, 2, "int");

            // Per-LED sleep colors. cmd `0x25 [strip] 0x01 [led]` + RGB.
            for (byte strip = 0; strip < 2; strip++)
                for (byte led = 0; led < 9; led++)
                    AddCommand(
                        $"base-ambient-sleep-led-color-strip{strip}-led{led}",
                        "main", 0x22, 0x20,
                        new byte[] { 0x25, strip, 0x01, led }, 3, "array");

            AddCommand("base-ambient-startup-color",  "main", 0x22, 0x20, new byte[] { 0x26 }, 3, "array");
            AddCommand("base-ambient-shutdown-color", "main", 0x22, 0x20, new byte[] { 0x27 }, 3, "array");
        }

        private static void AddCommand(string name, string device, byte readGroup, byte writeGroup,
            byte[] commandId, int payloadBytes, string payloadType)
        {
            var cmd = new MozaCommand(name, device, readGroup, writeGroup,
                commandId, payloadBytes, payloadType);
            _commands[name] = cmd;
            // Index by both groups so the parser can fetch all commands matching
            // an inbound group in one lookup. 0xFF means "not applicable".
            if (readGroup != 0xFF)
            {
                if (!_byGroup.TryGetValue(readGroup, out var rl))
                    _byGroup[readGroup] = rl = new List<MozaCommand>();
                rl.Add(cmd);
            }
            if (writeGroup != 0xFF && writeGroup != readGroup)
            {
                if (!_byGroup.TryGetValue(writeGroup, out var wl))
                    _byGroup[writeGroup] = wl = new List<MozaCommand>();
                wl.Add(cmd);
            }
        }

        public static MozaCommand? Get(string name)
        {
            return _commands.TryGetValue(name, out var cmd) ? cmd : null;
        }
    }
}
