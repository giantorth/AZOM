using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using MozaPlugin.Devices;
using MozaPlugin.Protocol;
using MozaPlugin.Devices.MBooster;
using MozaPlugin.Devices.Led;

namespace MozaPlugin.UI
{
    /// <summary>Pure-function text builders for the Diagnostics tab panels.</summary>
    internal static class DiagnosticsTextBuilder
    {
        // ── Field formatters ────────────────────────────────────────────

        public static string Blank(string s) => string.IsNullOrEmpty(s) ? "—" : s;
        public static string Redact(string s) => MozaLog.RedactId(s);
        public static string RedactBytes(byte[] b) => MozaLog.RedactBytesHex(b);
        public static string Hex(byte[] b) => b == null || b.Length == 0 ? "—" : BitConverter.ToString(b);
        public static string HexRaw(byte[] b) => b == null || b.Length == 0 ? "—" : BitConverter.ToString(b).Replace("-", "");
        public static string JoinList(IReadOnlyList<string> l)
            => l == null || l.Count == 0 ? "(empty)" : string.Join(", ", l);
        public static string TruncateId(string id)
            => string.IsNullOrEmpty(id) ? "—" : (id.Length > 40 ? id.Substring(0, 40) + "…" : id);

        /// <summary>Plugin assembly version (AssemblyInformationalVersion, +sha stripped).</summary>
        public static string GetPluginVersion()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var info = (AssemblyInformationalVersionAttribute?)Attribute
                    .GetCustomAttribute(asm, typeof(AssemblyInformationalVersionAttribute));
                var s = info?.InformationalVersion;
                if (!string.IsNullOrEmpty(s))
                {
                    int plus = s!.IndexOf('+');
                    return plus >= 0 ? s.Substring(0, plus) : s;
                }
                return asm.GetName().Version?.ToString() ?? "unknown";
            }
            catch { return "unknown"; }
        }

        // ── Per-panel builders ──────────────────────────────────────────

        public static string BuildPluginInfo() => $"Version:        {GetPluginVersion()}";

        public static string BuildUsbDetection(MozaPlugin plugin)
        {
            var sb = new StringBuilder();
            var ports = MozaPortDiscovery.Instance.Enumerate();
            var source = MozaPortDiscovery.Instance.Source;
            string fallbackState;
            if (plugin.Settings.DisableSerialProbeFallback)
                fallbackState = "DISABLED";
            else if (source != MozaDiscoverySource.None)
                fallbackState = "not used (enumeration is authoritative)";
            else
                fallbackState = "armed (active — no device source)";
            sb.AppendLine($"Source:         {source}  (probe fallback: {fallbackState})");
            sb.AppendLine($"Platform:       {Protocol.WineHost.Describe()}");
            if (Protocol.WineHost.IsWine)
                sb.AppendLine($"Native exec:    {(Protocol.WineNativeExec.Available ? "available" : "UNAVAILABLE (no cold-start warm-up)")}  last: {Protocol.WineNativeExec.LastRun}");

            if (ports.Count == 0)
            {
                sb.AppendLine($"Discovered:     (no MOZA devices — source {source})");
            }
            else
            {
                sb.AppendLine($"Discovered:     {ports.Count} device(s)");
                for (int i = 0; i < ports.Count; i++)
                {
                    var p = ports[i];
                    sb.AppendLine($"  {p.PortName,-8} VID 0x{p.Vid:X4}  PID 0x{p.Pid:X4}  {p.FriendlyName}");
                    // Second line only carries what the sysfs source adds; on
                    // Windows every field below is empty and the line is skipped.
                    if (p.DevicePath.Length > 0 || p.Serial.Length > 0)
                    {
                        string com = Protocol.WineComNameResolver.ResolveComName(p.PortName) ?? "(unresolved)";
                        sb.AppendLine($"           dev {p.DevicePath}  serial {p.Serial}  bus {p.InstanceId}  wine COM {com}");
                    }
                }
            }

            string wheelbasePort = plugin.Connection?.LastPortName ?? "";
            sb.Append("Assignments:    Wheelbase ");
            sb.Append(string.IsNullOrEmpty(wheelbasePort) ? "(disconnected)" : "→ " + wheelbasePort);
            // AB9/AB6 share one lane. LastPortName survives Disconnect, so gate on
            // IsConnected like the Hub / Base(aux) lines below — otherwise this
            // prints a port for a shifter that was unplugged. A user-disabled lane
            // reads as such rather than as "disconnected": that setting is the
            // single most common reason an active shifter never appears.
            var ab9Conn = plugin.Ab9Manager?.Connection;
            bool ab9Connected = ab9Conn?.IsConnected == true;
            string ab9Port = ab9Connected ? ab9Conn!.LastPortName ?? "" : "";
            sb.Append("  |  ");
            sb.Append(ab9Connected
                ? Protocol.MozaUsbIds.ActiveShifterShortName(ab9Conn!.DiscoveredPid)
                : "AB9/AB6");
            sb.Append(' ');
            sb.Append(!string.IsNullOrEmpty(ab9Port) ? "→ " + ab9Port
                      : plugin.Settings?.DisableAb9Detection == true ? "(detection disabled)"
                      : "(disconnected)");
            string hubPort = plugin.HubConnection?.IsConnected == true
                ? plugin.HubConnection.LastPortName ?? "" : "";
            sb.Append("  |  Hub ");
            sb.Append(string.IsNullOrEmpty(hubPort) ? "(disconnected)" : "→ " + hubPort);
            sb.AppendLine();
            // Base-aux pipe only exists after a base→hub migration (broken base,
            // wheel on hub); omit the line entirely in the common case.
            string baseAuxPort = plugin.BaseAuxConnection?.IsConnected == true
                ? plugin.BaseAuxConnection.LastPortName ?? "" : "";
            if (!string.IsNullOrEmpty(baseAuxPort))
                sb.AppendLine($"                Base(aux) → {baseAuxPort}  (wheel driven via hub)");

            // Directly-USB-attached peripherals get their own lanes, which the three
            // fixed slots above don't cover — without this a pedals-only rig reads as
            // "everything disconnected" while its CRP2 is happily answering on COM7.
            var lanes = plugin.PeripheralRegistry?.Snapshot();
            if (lanes != null && lanes.Count > 0)
            {
                var live = new List<string>();
                for (int i = 0; i < lanes.Count; i++)
                {
                    var c = lanes[i];
                    if (!c.IsConnected) continue;
                    live.Add($"{LaneName(c)} → {c.Connection.LastPortName ?? c.PortName}");
                }
                if (live.Count > 0)
                    sb.AppendLine($"                Standalone: {string.Join("  |  ", live)}");
            }

            // Classified open-failure surface. AccessDenied here is the
            // "port held by another app" footgun (PitHouse etc.); a stuck
            // ConsecutiveOpenFails count with PortVanished points at hot-
            // unplug or Wine pty teardown rather than user misconfig.
            var conn = plugin.Connection;
            if (conn != null)
            {
                var f = conn.LastFailure;
                sb.AppendLine(
                    $"LastFailure:    kind={f.Kind} port={f.PortName ?? "-"} " +
                    $"consecutive={conn.ConsecutiveOpenFailures}");
            }
            return sb.ToString();
        }

        /// <summary>Display name for a standalone lane — the shifter lanes share a
        /// category, so the model (HGP / SGP) is what identifies them.</summary>
        private static string LaneName(StandalonePeripheralController c)
        {
            if (c.Category == MozaDeviceCategory.Shifter)
                return c.ShifterModel == ShifterModelKind.Sgp ? "SGP"
                     : c.ShifterModel == ShifterModelKind.Hgp ? "HGP"
                     : "Shifter";
            return c.Category.ToString();
        }

        /// <summary>
        /// The dedicated lanes for peripherals plugged straight into the PC (pedals /
        /// handbrake / HGP / SGP). Each is its own connection with its own detection
        /// ownership, so none of them appear in the wheelbase/AB9/hub assignment slots
        /// — a pedals-only rig otherwise reads as a total detection failure.
        ///
        /// <para><c>binary=</c> is the presence-probe latch that gates the settings
        /// reads, and <c>read=</c> whether those reads actually came back: a lane that
        /// is connected with <c>read=no</c> means the tab is showing MozaData defaults,
        /// not the device's stored calibration.</para>
        /// </summary>
        public static string BuildStandalonePeripherals(MozaPlugin plugin, MozaData data)
        {
            var lanes = plugin.PeripheralRegistry?.Snapshot();
            if (lanes == null || lanes.Count == 0)
                return "(no directly-USB-attached peripherals — this lane claims the pedals / handbrake / " +
                       "HGP / SGP PIDs from the registry; a unit behind a wheelbase or hub is a relayed " +
                       "sub-device on that pipe instead)";

            var sb = new StringBuilder();
            sb.AppendLine($"Discovered:     {lanes.Count} dedicated lane(s)");
            for (int i = 0; i < lanes.Count; i++)
            {
                var c = lanes[i];
                string port = c.Connection.LastPortName ?? c.PortName;
                string state =
                    !c.IsConnected     ? "disconnected"
                    : c.BinaryConfirmed ? "connected"
                                        : "connected (probing)";
                sb.AppendLine(
                    $"  [{i}] {LaneName(c),-9} {port,-6}  state={state}  " +
                    $"pid={Blank(c.Connection.DiscoveredPid ?? "")}  binary={(c.BinaryConfirmed ? "yes" : "no")}");
                // SharedFlagSet drives the UI tab; OwnsPeripheral says writes route
                // here. They diverge when a base/hub pipe answered for the same
                // peripheral first, which is what makes a "my tab is empty" report
                // diagnosable from the bundle alone.
                string read = c.Category == MozaDeviceCategory.Pedals
                        ? (data.PedalsSettingsRead ? "yes" : "no")
                    : c.Category == MozaDeviceCategory.Handbrake
                        ? (data.HandbrakeSettingsRead ? "yes" : "no")
                        : "n/a";
                // capture= is this lane's CaptureLabel, i.e. the exact "source" column
                // its frames carry in serial-capture-*.txt — ties a row to its traffic.
                sb.AppendLine(
                    $"        tabFlag={(c.SharedFlagSet ? "set" : "clear")}  " +
                    $"ownsWrites={(c.OwnsPeripheral ? "yes" : "no")}  settingsRead={read}  " +
                    $"pendingReads={c.PendingResponses.PendingCount}  capture={c.Connection.CaptureLabel}");
                var f = c.Connection.LastFailure;
                if (f.Kind != ConnectionFailureKind.None)
                    sb.AppendLine($"        lastFailure={f.Kind} port={Blank(f.PortName ?? "")} '{f.Message}'");
            }
            return sb.ToString().TrimEnd();
        }

        public static string BuildMBoosterDevices(MozaPlugin plugin, MozaData data)
        {
            var registry = plugin.MBoosterRegistry;
            if (registry == null || registry.Devices.Count == 0)
                return "(no mBooster pedals detected — USB discovery needs VID 0x346E PID 0x0008 in the Windows USB enum; " +
                       "a unit on a wheelbase/hub pedal port registers as a routed lane once dev 0x19 answers the model probe)";

            var sb = new StringBuilder();
            var devs = registry.Devices;
            sb.AppendLine($"Discovered:     {devs.Count} mBooster device(s)");
            // Merged (post role-merge) positions — what the trace graph and the
            // game-facing properties actually receive. "max seen" proves whether
            // pedal input ever flowed through the merge this session, settling
            // "the graph never moved" vs "nobody pressed during capture".
            var (maxT, maxB, maxC) = registry.MaxMergedPositionsSeen;
            sb.AppendLine(
                $"Merged pos:     T={data.ThrottlePosition} B={data.BrakePosition} C={data.ClutchPosition}" +
                $"  (max seen this session: T={maxT} B={maxB} C={maxC})");
            for (int i = 0; i < devs.Count; i++)
            {
                var d = devs[i];
                string id = MBoosterDeviceController.ShortIdentity(d.Identity);
                string state =
                    d.Detected      ? "detected"
                    : d.IsConnected ? "connected (probing)"
                                    : "disconnected";
                string roleStr;
                string dispNameStr;
                var s = d.CurrentSettings;
                if (s != null)
                {
                    roleStr     = s.Role.ToString();
                    dispNameStr = string.IsNullOrEmpty(s.DisplayName) ? "—" : s.DisplayName;
                }
                else
                {
                    roleStr = "(no settings row)";
                    dispNameStr = "—";
                }
                string livePort = d.Connection?.LastPortName ?? "";
                string port = string.IsNullOrEmpty(livePort) ? d.PortName : livePort;
                sb.AppendLine(
                    $"  [{i}] {port,-6}  role={roleStr,-8}  state={state}  " +
                    $"hidPos={d.LastHidPosition.ToString("F3", CultureInfo.InvariantCulture)}  " +
                    $"name='{dispNameStr}'  id={id}");
                // Device-reported identity (learned over the Moza wire) — confirms
                // the serial-interrogation path on real hardware + shows the chain size.
                string serialStr = string.IsNullOrEmpty(d.Serial) ? "—" : Redact(d.Serial!);
                sb.AppendLine(
                    $"        serial={serialStr}  " +
                    $"model='{(string.IsNullOrEmpty(d.ModelName) ? "—" : d.ModelName)}'  " +
                    $"subDevs={d.SubDeviceCount}  container={(string.IsNullOrEmpty(d.ContainerId) ? "—" : d.ContainerId)}");
                // Per-axis role resolution for a chained lane — the actual
                // routing (which HID axis drives throttle/brake/clutch), so a
                // mis-mapping is visible straight from the bundle.
                if (d.AxisCount > 1)
                {
                    // ax<i>[+/-/?] = role — + connected, - not connected, ? unknown
                    // (device hasn't streamed a "PD Linked" diagnostic this session).
                    var connected = d.ConnectedAxes;
                    var roleParts = new System.Collections.Generic.List<string>();
                    for (int a = 0; a < d.AxisCount && a < MBoosterDeviceController.MaxAxes; a++)
                    {
                        string flag = connected == null ? "?" : (a < connected.Length && connected[a] ? "+" : "-");
                        roleParts.Add($"ax{a}[{flag}]={MozaMBoosterRegistry.ResolveAxisRole(s, a, d.AxisCount)}");
                    }
                    sb.AppendLine($"        axes={d.AxisCount}  roles=[{string.Join(", ", roleParts)}]");
                }
                AppendMBoosterPedalConfig(sb, d, s);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Per-pedal type, resolved motor/config device id, and the calibration +
        /// Pedal Feel values for one lane.
        ///
        /// The type (active/passive) and the device id are the pair that decides
        /// whether config writes reach hardware at all: only ACTIVE pedals count
        /// toward chain-ness, and a lane with one active pedal must address the
        /// host for everything (bundle KY3HK4QP shipped a full capture of the
        /// plugin writing to a phantom 0x1d, and neither of these lines existed
        /// to show it). The settings values live in MozaProfile, NOT in
        /// MozaPluginSettings, so plugin-settings.json carries none of them —
        /// this is the only place a bundle records what the user configured.
        /// </summary>
        private static void AppendMBoosterPedalConfig(
            StringBuilder sb, MBoosterDeviceController d, Devices.MBooster.MBoosterDeviceSettings? s)
        {
            var types = d.AxisTypes;
            sb.AppendLine(
                $"        active pedals={(d.ActiveAxisCount < 0 ? "? (type diagnostic not streamed yet)" : d.ActiveAxisCount.ToString())}" +
                $"  deviceReportedMaxThreshold={FmtKg(d.DeviceReportedMaxThresholdKg)}");
            foreach (int a in d.ConnectedAxisIndices())
            {
                byte dev = d.MotorDeviceForCurrentAxis(a);
                string type = types == null || a >= types.Length ? "?"
                            : types[a] == 1 ? "active"
                            : types[a] == 2 ? "passive" : "unknown";
                var role = MozaMBoosterRegistry.ResolveAxisRole(s, a, Math.Max(1, d.AxisCount));
                sb.AppendLine($"        ax{a} {role}/{type} → dev 0x{dev:x2}");
                var cfg = MozaMBoosterRegistry.PeekPedalConfig(s, a, d.SoleConnectedAxis());
                if (cfg == null) { sb.AppendLine("             (no config row)"); continue; }
                sb.AppendLine(
                    $"             simInput: ratio={FmtPct(cfg.SensorOutputRatioPct)} " +
                    $"maxThreshold={FmtKg(cfg.MaxThresholdKg)} " +
                    $"dir={(cfg.Direction < 0 ? "—" : cfg.Direction.ToString())} " +
                    $"min={(cfg.Min < 0 ? "—" : cfg.Min.ToString())} " +
                    $"max={(cfg.Max < 0 ? "—" : cfg.Max.ToString())} " +
                    $"outCurve={(cfg.CurveY != null ? "set" : "—")}");
                sb.AppendLine(
                    $"             pedalFeel: deadzone={cfg.DeadzoneKg.ToString("F1", CultureInfo.InvariantCulture)}kg " +
                    $"maxForce={cfg.MaxForceKg.ToString("F0", CultureInfo.InvariantCulture)}kg " +
                    $"travel={FmtMm(cfg.TravelStartMm)}..{FmtMm(cfg.TravelEndMm)} " +
                    $"endstop={FmtRaw(cfg.EndstopFrontStiffness)}/{FmtRaw(cfg.EndstopEndStiffness)} " +
                    $"friction={FmtPct(cfg.NaturalFrictionPct)} " +
                    $"inCurve={(cfg.InputCurveY != null ? "set" : "—")}");
            }
        }

        // -1 is the shared "not set / no override" sentinel across every mBooster
        // calibration field — render it as such rather than as a real value.
        private static string FmtRaw(float v) =>
            v < 0 ? "—" : v.ToString("0.#", CultureInfo.InvariantCulture);
        private static string FmtKg(float v) => v < 0 ? "—" : FmtRaw(v) + "kg";
        private static string FmtMm(float v) => v < 0 ? "—" : FmtRaw(v) + "mm";
        private static string FmtPct(float v) => v < 0 ? "—" : FmtRaw(v) + "%";

        /// <summary>Multi-Function Stalks state + the truck-sim button map. The map is
        /// what turns a "stalk behaves wrong in ETS2" report into a diagnosis, and the
        /// seen-index list shows which physical lever positions the device reports.</summary>
        public static string BuildStalks(MozaPlugin plugin, MozaData d)
        {
            if (!d.IsStalksConnected && d.StalksButtonCount == 0)
                return "(no MOZA Stalks detected — HID-only device, VID 0x346E PID 0x0024)";

            var sb = new StringBuilder();
            sb.AppendLine($"Connected:      {d.IsStalksConnected}");
            sb.AppendLine($"Buttons seen:   {d.StalksButtonCount} (highest index reported + 1)");

            var pressed = new System.Collections.Generic.List<string>();
            var states = d.StalksButtonStates;
            for (int i = 0; i < states.Length; i++)
                if (states[i]) pressed.Add((i + 1).ToString());
            sb.AppendLine($"Pressed now:    {(pressed.Count == 0 ? "(none)" : string.Join(", ", pressed))}  (btn numbers)");

            var s = plugin?.Settings;
            var cfg = s?.StalksTruckSim;
            sb.AppendLine($"Mode:           {(s == null ? "—" : s.StalksMode.ToString())}");
            if (cfg == null) return sb.ToString().TrimEnd();

            sb.AppendLine(
                $"Keys:           wiperFwd='{cfg.WiperForwardKey}' wiperBack='{cfg.WiperBackKey}' " +
                $"lightCycle='{cfg.LightCycleKey}' indL='{cfg.IndicatorLeftKey}' indR='{cfg.IndicatorRightKey}'");
            sb.AppendLine(
                $"Stages:         wipers={cfg.WiperStageCount} (wrap={cfg.WiperForwardWraps}) " +
                $"lights={cfg.LightStageCount}  minBlink={cfg.IndicatorMinBlinkSeconds}s  " +
                $"keyHold={cfg.KeyHoldMs}ms gap={cfg.KeyGapMs}ms");

            var map = cfg.ButtonActions;
            if (map == null || map.Count == 0)
            {
                sb.AppendLine("Map:            (no buttons mapped)");
                return sb.ToString().TrimEnd();
            }
            sb.AppendLine($"Map:            {map.Count} button(s)");
            var indices = new System.Collections.Generic.List<int>(map.Keys);
            indices.Sort();
            foreach (int i in indices)
            {
                var a = map[i];
                if (a == null) continue;
                string extra =
                    a.Kind == Devices.StalksTruckSim.StalkActionKind.WiperStage ||
                    a.Kind == Devices.StalksTruckSim.StalkActionKind.LightStage ? $" stage={a.Stage}"
                    : string.IsNullOrEmpty(a.Key) ? "" : $" key='{a.Key}'";
                sb.AppendLine($"  btn{i + 1,-3} (idx {i,2})  {a.Kind}{extra}");
            }
            return sb.ToString().TrimEnd();
        }

        public static string BuildWheelIdentity(MozaData d, Devices.DeviceDetectionState? detection = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Model:          {Blank(d.WheelModelName)}");
            sb.AppendLine($"FW (sw):        {Blank(d.WheelSwVersion)}");
            sb.AppendLine($"HW version:     {Blank(d.WheelHwVersion)}");
            sb.AppendLine($"HW sub:         {Blank(d.WheelHwSubVersion)}");
            sb.AppendLine($"Serial:         {Redact(d.WheelSerialNumber)}");
            sb.AppendLine($"Sub-devices:    {d.WheelSubDeviceCount}");
            sb.AppendLine($"Device presence:0x{d.WheelDevicePresence:X2}");
            sb.AppendLine($"Device type:    {Hex(d.WheelDeviceType)}");
            sb.AppendLine($"Capabilities:   {Hex(d.WheelCapabilities)}");
            sb.AppendLine($"MCU UID:        {RedactBytes(d.WheelMcuUid)}");
            sb.Append    ($"Identity-11:    {Hex(d.WheelIdentity11)}");
            if (detection?.NewWheelActingOldProtocol == true)
            {
                sb.AppendLine();
                sb.Append("FW advisory:    new-protocol wheel answered as old-protocol — firmware update recommended");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Wheel LED groups: firmware mode + brightness per zone, next to what SimHub's
        /// "Brightness limiter and balance" sliders asked for and what the change gate
        /// believes is on the wheel.
        ///
        /// Why this section exists: bundle GY9RWKMR ("button + knob brightness sliders do
        /// nothing" on a CS Pro) needed a hand-decode of the raw wire capture to find that
        /// the two zones were in Static mode with their registers at 5 % and 10 % while the
        /// plugin's cache said 100. Every number needed for that verdict is here.
        /// </summary>
        public static string BuildWheelLedZones(MozaPlugin plugin, MozaData d)
        {
            if (plugin == null || d == null) return "(no plugin instance)";

            var model = plugin.WheelModelInfo;
            if (model == null)
                return "(wheel model not resolved yet — LED zone layout unknown)";

            string Bri(int v) => v < 0 ? "—" : v.ToString(CultureInfo.InvariantCulture);
            string Cfg(string key)
            {
                var (cached, desired) = plugin.HardwareApplier?.WheelCfgDiag(key) ?? (null, null);
                string c = cached.HasValue ? cached.Value.ToString(CultureInfo.InvariantCulture) : "—";
                string w = desired.HasValue ? desired.Value.ToString(CultureInfo.InvariantCulture) : "—";
                return $"{c}/{w}";
            }
            // 0=Off, 1=SimHub, 2=Static; -1 = not read back from the wheel yet.
            string Mode(int v) => v switch
            {
                0 => "0 off",
                1 => "1 simhub",
                2 => "2 static",
                _ => "—",
            };

            var sb = new StringBuilder();
            sb.AppendLine($"Layout:         rpm={model.RpmLedCount} buttons={model.ButtonLedCount} "
                          + $"knobs={model.KnobCount} ring={model.KnobRingLedTotal} "
                          + $"flags={(model.HasFlagLeds ? "yes" : "no")}");
            // Rotary encoders are a separate capability from knob LEDs (knobs= above is
            // the LED-ring count and is 0 on most rims that do have encoders). Both
            // numbers are printed because they disagree by design: the catalogued count
            // is the truth, while the swept one over-reports — firmware answers every
            // wheel-knob-signal-mode index whether or not the encoder exists. A
            // "wrong number of BUTTON/KNOB selectors" report is triaged from this line:
            // catalog=— means the model still needs its real count recorded.
            int knobSigMask = d.WheelKnobSignalModeMask;
            int swept = 0;
            for (int k = 0; k < MozaData.WheelKnobMax; k++)
                if ((knobSigMask & (1 << k)) != 0) swept = k + 1;
            string catEnc = model.KnobEncoderCount >= 0
                ? model.KnobEncoderCount.ToString(CultureInfo.InvariantCulture) : "—";
            sb.AppendLine($"Knob encoders:  catalog={catEnc} swept={swept} mask=0x{knobSigMask:X2} "
                          + $"knob-mode={(d.WheelKnobModeSupported ? "yes" : "no")}");
            int mask = plugin.DetectionState?.WheelLedGroupMask ?? 0;
            var present = new List<string>();
            for (int g = 2; g <= 4; g++) if ((mask & (1 << g)) != 0) present.Add(g.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine($"Extended groups:0x{mask:X2}  present={JoinList(present)}  (2 single, 3 rotary, 4 ambient)");
            sb.AppendLine($"Master slider:  {Bri(plugin.WheelLedMasterBrightness)}"
                          + $"   ES raw: {Bri(plugin.WheelLedMasterBrightnessRaw)}");
            // The per-wheel-page overlay is where the LED modes and per-zone brightness
            // actually live. If it doesn't resolve, ApplyWheelToHardware sees -1 for all
            // of them and writes nothing — indistinguishable, without this line, from a
            // wheel that simply already matched.
            var pageGuid = plugin.GetCurrentWheelPageGuid();
            var overlay = plugin.GetCurrentWheelOverlay(plugin.Settings?.ProfileStore?.CurrentProfile);
            sb.AppendLine($"Wheel page:     {(pageGuid.HasValue ? pageGuid.Value.ToString().Substring(0, 8) : "unresolved")}"
                          + $"  overlay={(overlay != null ? "yes" : "no")}"
                          + (overlay != null
                             ? $"  want modes rpm={Bri(overlay.WheelTelemetryMode)} "
                               + $"btn={Bri(overlay.WheelButtonsLedMode)} knob={Bri(overlay.WheelKnobLedMode)}"
                               + $"  want bri rpm={Bri(overlay.WheelRpmBrightness)} "
                               + $"btn={Bri(overlay.WheelButtonsBrightness)} knob={Bri(overlay.WheelKnobRingBrightness)}"
                             : ""));
            // Header and rows share one width table so the columns line up. mode next to
            // mode-cache/want is the load-bearing pair: a wheel reporting Static while the
            // plugin wants SimHub means the mode write never landed, and the firmware is
            // discarding that group's live colour stream.
            string Row(string zone, string mode, string modeCfg, string idle,
                       string wheelBri, string slider, string applied, string briCfg)
                => $"  {zone,-21}{mode,-10}{modeCfg,-12}{idle,5}{wheelBri,8}{slider,8}{applied,9}  {briCfg}";

            sb.AppendLine(Row("zone", "mode", "mode c/w", "idle", "wheel", "slider", "applied", "bri c/w"));
            sb.AppendLine(Row("0 rpm      1B 00 FF", Mode(d.WheelTelemetryMode),
                Cfg("wheel-telemetry-mode"), Bri(d.WheelTelemetryIdleEffect),
                Bri(d.WheelRpmBrightness), Bri(plugin.WheelLedBrightnessRpm),
                Bri(plugin.WheelLedAppliedBrightnessRpm), Cfg("wheel-rpm-brightness")));
            sb.AppendLine(Row("1 buttons  1B 01 FF", Mode(d.WheelButtonsLedMode),
                Cfg("wheel-buttons-led-mode"), Bri(d.WheelButtonsIdleEffect),
                Bri(d.WheelButtonsBrightness), Bri(plugin.WheelLedBrightnessButtons),
                Bri(plugin.WheelLedAppliedBrightnessButtons), Cfg("wheel-buttons-brightness")));
            sb.AppendLine(Row("3 knob     1B 03 FF", Mode(d.WheelKnobLedMode),
                Cfg("wheel-knob-led-mode"), Bri(d.WheelKnobIdleEffect),
                Bri(d.KnobRingBrightness), Bri(plugin.WheelLedBrightnessKnob),
                Bri(plugin.WheelLedAppliedBrightnessKnob), Cfg("wheel-knob-brightness")));
            sb.Append($"Flags (meter):  wheel={Bri(d.WheelFlagsBrightness)} "
                      + $"cache/want={Cfg("dash-flags-brightness")}  (dev 0x14, not a wheel LED group)");
            return sb.ToString();
        }

        /// <summary>
        /// Base (wheelbase MCU) identity + the firmware-gated capability state.
        /// The numeric base firmware is the SOLE gate for the wheelbase LFE effects
        /// and the 10-band FFB equalizer (<see cref="MozaData.BaseSupportsLfe"/>),
        /// and nothing else in a bundle reports it — diagnosing bundle 65HZBQJT
        /// (an R12 that never answers the dev-0x12 group-0x04 probe) needed hex
        /// archaeology on the wire capture. <c>FW (numeric)</c> plus its source is
        /// what tells a SILENT base apart from a genuinely old one; both read
        /// "LFE: no", and only one of them is a bug.
        /// </summary>
        public static string BuildBaseIdentity(MozaPlugin plugin, MozaData d)
        {
            // No base on the bus (standalone CM2 dash, hub-only rig, wheel-only
            // bench). Say so rather than printing a page of blanks — but keep the
            // stale numeric version visible, since it is static-backed and a
            // leftover value is itself worth seeing.
            if (plugin?.DetectionState?.BaseDetected != true
                && string.IsNullOrEmpty(d.BaseModelName) && string.IsNullOrEmpty(d.BaseSwVersion))
                return $"(no wheelbase detected)  FW (numeric): {d.BaseFwVersionText} via {d.BaseFwVersionSource}";

            var sb = new StringBuilder();
            sb.AppendLine($"Model:          {Blank(d.BaseModelName)}");
            sb.AppendLine($"FW (sw):        {Blank(d.BaseSwVersion)}");
            sb.AppendLine($"HW version:     {Blank(d.BaseHwVersion)}");
            sb.AppendLine($"HW sub:         {Blank(d.BaseHwSubVersion)}");
            sb.AppendLine($"MCU UID:        {RedactBytes(d.BaseMcuUid)}");
            sb.AppendLine($"Identity-11:    {Hex(d.BaseIdentity11)}");
            sb.AppendLine($"FW (numeric):   {d.BaseFwVersionText}  via {d.BaseFwVersionSource}");

            var detection = plugin?.DetectionState;
            if (detection != null)
                sb.AppendLine($"FW re-probes:   {detection.BaseFwVersionProbeRetries} retry round(s) spent");

            sb.AppendLine($"LFE support:    {(d.BaseSupportsLfe ? "yes" : "no")}  (needs >= 1.2.10.10)");
            sb.AppendLine($"EQ bands:       {(d.BaseSupportsEq10 ? 10 : 6)}  (same firmware gate as LFE)");

            if (plugin != null)
            {
                var p = plugin.BaseLfeHapticsReadyParts;
                sb.AppendLine(
                    $"LFE haptics:    {(plugin.IsBaseLfeHapticsReady ? "ready" : "not ready")} " +
                    $"(worker={YesNo(p.Worker)} fw={YesNo(p.Firmware)} " +
                    $"baseDetected={YesNo(p.BaseDetected)} pipe={YesNo(p.PipeConnected)})");
                // Cached on the UI thread — the getter enumerates SimHub's device
                // collection and can't run from the bundle writer's thread. When it
                // is deployed the plugin's own LFE tab hides, so a "where did my LFE
                // tab go" report is answered by this line alone.
                bool? deployed = plugin.ShakeItLfeDeviceDeployedCached;
                sb.Append($"ShakeIt device: {(deployed == null ? "unknown (settings pane never opened)" : deployed.Value ? "deployed (plugin LFE tab hidden)" : "not deployed")}");
            }
            return sb.ToString();
        }

        private static string YesNo(bool v) => v ? "yes" : "no";

        /// <summary>Diagnostics block for the CM2 dashboard. Reports the wheelbase PID,
        /// the standalone-USB dashboard connection, whether a CM2 is present (and its
        /// wire dev_id), the MAIN sender's target dev_id, and the dedicated _cm2Sender
        /// lane when one exists. Returns "(no MOZA serial connection)" when no Moza
        /// serial connection is open.</summary>
        public static string BuildStandaloneDashboardState(MozaPlugin plugin)
        {
            var conn = plugin?.Connection;
            if (conn == null) return "(no MOZA serial connection)";
            string pid = conn.DiscoveredPid ?? "(unknown)";
            string pidDesc = Protocol.MozaUsbIds.Describe(conn.DiscoveredPid);
            bool cm2Present = plugin?.IsCm2Present ?? false;
            byte target = plugin?.TelemetrySender?.TargetDeviceId ?? Protocol.MozaProtocol.DeviceWheel;
            string targetDesc = plugin?.TelemetrySender?.TargetDescription ?? $"0x{target:X2}";

            // Dedicated standalone-USB dashboard connection (CM2 0x0025 on its own port).
            var dashConn = plugin?.DashboardConnection;
            bool dashUsb = plugin?.DashboardUsbConnected ?? false;
            string dashLine = dashConn != null && dashConn.IsConnected
                ? $"{dashConn.LastPortName} {dashConn.DiscoveredPid} ({Protocol.MozaUsbIds.Describe(dashConn.DiscoveredPid)})"
                : "(not connected)";

            var sb = new StringBuilder();
            sb.AppendLine($"Wheelbase USB PID: {pid} ({pidDesc})");
            sb.AppendLine($"Dashboard conn:    {dashLine}");
            sb.AppendLine($"Dashboard USB:     {(dashUsb ? "yes" : "no")}");
            sb.AppendLine($"DashDetected:      {plugin?.IsDashDetected ?? false}");
            sb.AppendLine($"CM2 present:       {cm2Present}{(cm2Present ? $" (dev 0x{plugin!.Cm2TargetDeviceId:X2})" : "")}");
            sb.Append    ($"Main target dev:   {targetDesc}");

            // The dash pipeline's own enable gate + whether a wheel page backs it.
            // An unstarted lane below is almost always this line reading false.
            sb.AppendLine();
            sb.Append(
                $"Dash telem enable: {plugin?.ActiveDashTelemetryEnabled ?? false} " +
                $"(wheel page {(plugin?.GetCurrentWheelPageGuid().HasValue == true ? "resolved" : "unresolved")})");

            // CM1-vs-CM2 classification of a BRIDGED dash (a USB 0x0025 dash is always a
            // real CM2, so the line is omitted there). Reports the evidence, not a guess:
            // only the CM1-exclusive 0x8E param-read answer latches CM1, and only a
            // tier-def catalog proves CM2 — a dash showing neither stays undecided and is
            // re-probed. "undecided" with probes climbing and ans=no is the normal
            // steady state for a real CM2 whose catalog hasn't arrived yet.
            if (plugin != null && plugin.IsCm2Present && !dashUsb)
            {
                var dd = plugin.DualDisplay;
                int catalog = plugin._cm2Sender?.CatalogCount ?? 0;
                string cls;
                if (plugin.DashIsCm1)
                    cls = $"CM1 (latched, driver {(plugin.IsCm1DriverRunning ? "running" : "stopped")})";
                else if (catalog > 0)
                    cls = $"CM2 (catalog={catalog})";
                else if (dd == null)
                    cls = "undecided (coordinator not wired yet)";
                else
                {
                    var forSpan = dd.DiscriminatingFor;
                    cls = $"undecided (0x8E ans={(dd.DashParamReadAnswered ? "yes" : "no")}, " +
                          $"probes={dd.Cm1ProbeCount}, " +
                          $"deciding {(forSpan.HasValue ? $"{forSpan.Value.TotalSeconds:F0}s" : "not started")}, " +
                          $"catalog=0)";
                }
                sb.AppendLine();
                sb.Append($"Dash class:        {cls}");
            }

            // Dedicated CM2 lane (the _cm2Sender). DECOUPLED: present whenever a CM2
            // is attached (bus or USB), regardless of the wheel — the CM2 is ALWAYS
            // driven by this dedicated sender now. The MAIN line above stays on the
            // wheel (0x17); this line drives the CM2 (0x12 USB / 0x14 bus). Omitted
            // when no CM2 is attached.
            var cm2 = plugin?._cm2Sender;
            if (cm2 != null)
            {
                sb.AppendLine();
                sb.Append(
                    $"CM2 dash lane:     {cm2.TargetDescription} on {cm2.ConnectionRef?.CaptureLabel} pipe " +
                    $"(frames={cm2.FramesSent}, {cm2.Phase})");
            }
            else if (cm2Present)
            {
                sb.AppendLine();
                sb.Append("CM2 dash lane:     (not started)");
            }

            // Dash LED driver (SimHub effects → CM2 bitmask/colour bridge).
            // Separates "SimHub feeds black" (everLit=no, sends=0) from
            // "colors produced but writes dropped" (everLit=yes, sends>0).
            var led = MozaDashLedDeviceManager.Latest;
            if (led != null)
            {
                var snap = led.DiagSnapshot;
                long nowTicks = DateTime.UtcNow.Ticks;
                string Age(long ticks) => ticks == 0
                    ? "never"
                    : $"{TimeSpan.FromTicks(nowTicks - ticks).TotalSeconds:F1}s ago";
                string mask = snap.LastBitmask < 0 ? "(none)" : $"0x{snap.LastBitmask:X3}";
                string fw = (plugin?.Settings?.Cm2NewLedFirmware ?? false) ? "indicator" : "legacy";
                sb.AppendLine();
                sb.Append(
                    $"CM2 LED driver:    fw={fw} engaged={(snap.Engaged ? "yes" : "no")} everLit={(snap.EverLit ? "yes" : "no")} " +
                    $"lastNonBlack={Age(snap.LastNonBlackTicks)} lastBitmask={mask} sentAgo={Age(snap.LastBitmaskSendTicks)} " +
                    $"sends: bitmask={snap.BitmaskSends} rpmColor={snap.RpmColorSends} flag={snap.FlagSends}");
            }
            return sb.ToString();
        }

        public static string BuildDisplayIdentity(MozaData d)
        {
            if (string.IsNullOrEmpty(d.DisplayModelName) && d.DisplayMcuUid.Length == 0)
                return "(display sub-device not probed or not present)";
            var sb = new StringBuilder();
            sb.AppendLine($"Model:          {Blank(d.DisplayModelName)}");
            sb.AppendLine($"FW (sw):        {Blank(d.DisplaySwVersion)}");
            sb.AppendLine($"HW version:     {Blank(d.DisplayHwVersion)}");
            sb.AppendLine($"Serial:         {Redact(d.DisplaySerialNumber)}");
            sb.AppendLine($"Sub-devices:    {d.DisplaySubDeviceCount}");
            sb.AppendLine($"Device presence:0x{d.DisplayDevicePresence:X2}");
            sb.AppendLine($"Device type:    {Hex(d.DisplayDeviceType)}");
            sb.AppendLine($"Capabilities:   {Hex(d.DisplayCapabilities)}");
            sb.AppendLine($"MCU UID:        {RedactBytes(d.DisplayMcuUid)}");
            sb.Append    ($"Identity-11:    {Hex(d.DisplayIdentity11)}");
            return sb.ToString();
        }

        public static string BuildDashboardState(MozaPlugin plugin)
        {
            var state = plugin.WheelStateForDiagnostics;
            if (state == null) return "(no configJson state received yet)";
            var sb = new StringBuilder();
            sb.AppendLine($"TitleId:        {state.TitleId}");
            sb.AppendLine($"displayVersion: {state.DisplayVersion}");
            sb.AppendLine($"resetVersion:   {state.ResetVersion}");
            sb.AppendLine($"sortTag:        {state.SortTag}");
            sb.AppendLine($"rootDirPath:    {Blank(state.RootDirPath)}");
            sb.AppendLine($"rootPath:       {Blank(state.RootPath)}");
            sb.AppendLine($"configJsonList ({state.ConfigJsonList.Count}): {JoinList(state.ConfigJsonList)}");
            sb.AppendLine($"imageRefMap:    {state.ImageRefMap.Count} entries");
            sb.AppendLine($"fontRefMap:     {state.FontRefMap.Count} entries");
            sb.AppendLine($"imagePath:      {state.ImagePath.Count} entries");
            sb.AppendLine($"captured at:    {state.CapturedAt:HH:mm:ss}");
            sb.AppendLine(Build28xRawLine(plugin));
            sb.AppendLine();
            sb.AppendLine($"-- Enabled dashboards ({state.EnabledDashboards.Count}) --");
            foreach (var d in state.EnabledDashboards)
            {
                sb.AppendLine($"  • {d.Title} / dirName={d.DirName} / id={TruncateId(d.Id)}");
                if (!string.IsNullOrEmpty(d.LastModified))
                    sb.AppendLine($"      lastModified: {d.LastModified}");
                if (d.IdealDeviceInfos.Count > 0)
                {
                    foreach (var info in d.IdealDeviceInfos)
                        sb.AppendLine($"      device: id={info.DeviceId} hw={info.HardwareVersion} product={info.ProductType}");
                }
            }
            sb.Append($"-- Disabled dashboards ({state.DisabledDashboards.Count}) --");
            foreach (var d in state.DisabledDashboards)
                sb.Append($"\n  • {d.Title} / {d.DirName}");
            return sb.ToString();
        }

        /// <summary>
        /// Wheel's most-recent 28:00 / 28:01 reply bytes raw, with age in ms.
        /// Semantics not decoded — captured for offline correlation.
        /// </summary>
        public static string Build28xRawLine(MozaPlugin plugin)
        {
            var d = plugin.Data;
            if (d == null) return "wheel 28:xx raw: (no data)";
            string b00 = d.Last28x00ByteValid
                ? $"0x{d.Last28x00Byte5:X2}" : "(none)";
            string b01 = d.Last28x01BytesValid
                ? $"0x{d.Last28x01Byte4:X2} 0x{d.Last28x01Byte5:X2}"
                : "(none)";
            string age;
            if (d.Last28xReplyTickMs == 0)
                age = "never";
            else
            {
                int dt = unchecked(Environment.TickCount - d.Last28xReplyTickMs);
                age = dt < 0 ? "?" : $"{dt} ms";
            }
            return $"wheel 28:xx raw: 28:00=[{b00}]  28:01=[{b01}]  age={age}";
        }

        public static string BuildTileServer(MozaPlugin plugin)
        {
            var tile = plugin.TileServerStateForDiagnostics;
            if (tile == null)
                return "(no inbound tile-server blob received — plugin PUSHES empty state on 0x03; wheel doesn't push back in current captures)";
            var sb = new StringBuilder();
            sb.AppendLine($"root:          {Blank(tile.Root)}");
            sb.AppendLine($"version:       {tile.Version}");
            sb.AppendLine($"any populated: {tile.AnyPopulated}");
            foreach (var kv in tile.Games)
            {
                var g = kv.Value;
                sb.Append($"\n[{kv.Key}] populated={g.Populated} map_version={g.MapVersion} " +
                          $"tile_size={g.TileSize} layers={g.LayersCount} name={Blank(g.Name)}");
            }
            return sb.ToString();
        }

        public static string BuildSessionState(MozaPlugin plugin)
        {
            var ts = plugin.TelemetrySender;
            if (ts == null && !plugin.TelemetryEnabledForDiagnostics)
                return "(telemetry not running)";
            var sb = new StringBuilder();
            sb.AppendLine($"Enabled:            {plugin.TelemetryEnabledForDiagnostics}");
            sb.AppendLine($"FramesSent:         {plugin.FramesSentForDiagnostics}");
            sb.AppendLine($"Phase:              {(ts?.Phase ?? global::MozaPlugin.Telemetry.PipelinePhase.Idle)}");
            var rec = ts?.Recovery;
            if (rec != null)
            {
                sb.AppendLine($"  IsParked:         {rec.IsParked}");
                sb.AppendLine($"  RecoveryInFlight: {rec.IsRecoveryInFlight}");
                if (rec.IsParked && !string.IsNullOrEmpty(rec.ParkReason))
                    sb.AppendLine($"  ParkReason:       {rec.ParkReason}");
            }
            var conn = plugin.Connection;
            if (conn != null)
            {
                var lf = conn.LastFailure;
                sb.AppendLine($"LastFailure:        {lf.Kind}"
                    + (lf.Kind == ConnectionFailureKind.None ? "" : $" — {lf.Message}"));
                sb.AppendLine($"ConsecOpenFails:    {conn.ConsecutiveOpenFailures}");
            }
            var budget = plugin.SerialBudgetForDiagnostics;
            var errs = plugin.SerialWireErrorsForDiagnostics;
            int budgetTargetBytes = WriteBudget.TargetBytesPerWindow;
            sb.AppendLine(
                $"Bandwidth:          out={budget.BytesLastSec,5} B/s ({budget.PercentBudget,3}% of {budgetTargetBytes}B target, peak={budget.PeakBurstBytes})");
            sb.AppendLine(
                $"WireErrors:         drops={errs.FramesDropped} cksumFail={errs.ChecksumFailures} frameErr={errs.FrameErrors} resync={errs.FrameStartScanResyncs}");
            sb.AppendLine(
                $"  FrameStartScan:   lenReject={errs.LengthRejects} stuffedPairSkip={errs.StuffedPairSkips}");
            sb.AppendLine(
                $"  ReadCadence:      maxReadBytes={errs.MaxBytesToRead} saturatedReads={errs.FullReads} "
                + $"maxReadGap={errs.MaxReadGapMs}ms dispatchQueuePeak={errs.RxQueueHighWater} "
                + $"dispatchDrops={errs.RxQueueDrops}");
            // Resync skip-size distribution. Helps tell single-byte stray
            // padding (USB / driver idle bytes — harmless) from multi-byte
            // gaps (wire corruption — worth investigating). drops=0
            // cksumFail=0 with a 1B-dominated histogram means the wire is
            // healthy and the resync count is just inter-frame noise.
            if (errs.FrameStartScanResyncs > 0 && errs.ResyncSkipHistogram != null)
            {
                var h = errs.ResyncSkipHistogram;
                string[] labels = { "1B", "2B", "3-4B", "5-8B", "9-16B", "17-32B", "33-64B", ">64B" };
                var hsb = new StringBuilder();
                bool first = true;
                for (int i = 0; i < h.Length; i++)
                {
                    if (h[i] == 0) continue;
                    if (!first) hsb.Append("  ");
                    hsb.Append(labels[i]).Append('=').Append(h[i]);
                    first = false;
                }
                sb.AppendLine($"  ResyncSkipDist:   {hsb}");
                if (errs.RecentResyncSamples != null && errs.RecentResyncSamples.Length > 0)
                {
                    sb.AppendLine($"  RecentResyncs:    (last {errs.RecentResyncSamples.Length}, newest first)");
                    for (int i = errs.RecentResyncSamples.Length - 1; i >= 0; i--)
                        sb.AppendLine($"    {errs.RecentResyncSamples[i]}");
                }
            }
            // Both, unmasked. The sender flag only latches on session traffic
            // (TelemetryInboundDispatcher), so a sender that never started prints
            // false and the old `??` hid the probe result behind it — a wheel whose
            // display had answered identity fine still read "DisplayDetected: False".
            sb.AppendLine($"DisplayDetected:    sender={ts?.DisplayDetected.ToString() ?? "n/a"}  probe={plugin.IsDisplayDetected}");
            sb.AppendLine($"DisplayModelName:   {Blank(ts?.DisplayModelName ?? plugin.DisplayModelName)}");
            sb.AppendLine($"WheelEra:           {plugin.ActiveTelemetryWheelEra}");
            if (ts != null)
            {
                sb.AppendLine($"WheelReportedSlot:  {ts.WheelReportedSlot}");
                sb.AppendLine($"LastEmittedKind4:   {ts.LastEmittedKind4Slot}");
                sb.AppendLine($"DisplayEngaged:     {ts.Watchdog?.DisplayEngagementText() ?? "(n/a)"}");
                var p = ts.Policy;
                sb.AppendLine($"PolicyEra:          {p.Era}{(p.IsAuto ? " (auto)" : "")}");
                sb.AppendLine($"ResolvedTierDefSes: 0x{ts.ResolveTierDefSession():X2}");
                sb.AppendLine($"Encoding:           {p.Encoding}");
                sb.AppendLine($"BlindRetransmit:    {p.BlindRetransmitTierDef}");
                sb.AppendLine($"UploadWireFormat:   {p.UploadWireFormat}");
                sb.AppendLine($"FlagByte:           0x{ts.FlagByte:X2}");
                sb.AppendLine($"UploadDashboard:    {ts.UploadDashboard}");
                sb.Append    ($"Profile:            {ts.Profile?.Name ?? "(none)"}");
            }

            var counts = plugin.SessionCountsForDiagnostics;
            if (counts != null && counts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("Session traffic (in/out chunks):");
                var keys = new List<byte>(counts.Keys);
                keys.Sort();
                foreach (var k in keys)
                {
                    var v = counts[k];
                    sb.AppendLine($"  0x{k:X2}:  in={v.In,-5} out={v.Out}");
                }
            }
            return sb.ToString();
        }

        public static string BuildWheelCatalog(MozaPlugin plugin)
        {
            var sb = new StringBuilder();
            var pd = plugin.CatalogParserDiagnostics;
            string activity = pd.LastActivityMsAgo < 0
                ? "never"
                : $"{pd.LastActivityMsAgo} ms ago";
            sb.AppendLine(
                $"Parser: buf={pd.BufferBytes}B (last parsed {pd.LastParsedBufferBytes}B) " +
                $"crcRejects={pd.CrcRejects} lastActivity={activity}");
            // The list below is the CURRENT generation (LiveCatalog). Name the
            // union size too, so a shrinking catalog after a dash switch reads
            // as "new generation is smaller" rather than "we lost channels".
            if (pd.MergedCatalogCount > 0 && pd.MergedCatalogCount != pd.LiveCatalogCount)
            {
                sb.AppendLine(
                    $"  (showing current generation: {pd.LiveCatalogCount} of "
                    + $"{pd.MergedCatalogCount} URLs seen across all generations this connection)");
            }

            var catalog = plugin.WheelChannelCatalogForDiagnostics;
            if (catalog != null && catalog.Count > 0)
            {
                sb.AppendLine($"{catalog.Count} channels advertised by wheel:");
                for (int i = 0; i < catalog.Count; i++)
                {
                    string url = catalog[i] ?? "";
                    sb.AppendLine($"  [{i + 1,2}]  {url}");
                }
                return sb.ToString().TrimEnd();
            }

            // Fallback: derive from active subscription. The subscription was
            // built with the wheel's catalog, so URLs reflect what we sent.
            // Diag.Channels uses sequential idx (1..N across tiers/buckets) —
            // a URL appears multiple times when channels duplicate across
            // page-broadcast buckets. Dedup by URL preserving first-seen.
            var sub = plugin.SubscriptionForDiagnostics;
            if (sub != null && sub.Channels != null && sub.Channels.Count > 0)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ordered = new List<string>();
                foreach (var ch in sub.Channels)
                {
                    if (string.IsNullOrEmpty(ch.Url)) continue;
                    if (seen.Add(ch.Url)) ordered.Add(ch.Url);
                }
                if (ordered.Count > 0)
                {
                    sb.AppendLine($"{ordered.Count} channels (from last subscription — catalog parser empty):");
                    for (int i = 0; i < ordered.Count; i++)
                        sb.AppendLine($"  [{i + 1,2}]  {ordered[i]}");
                    return sb.ToString().TrimEnd();
                }
            }

            return "(no channel catalog received from wheel yet)";
        }

        public static string BuildSubscription(MozaPlugin plugin)
        {
            var sub = plugin.SubscriptionForDiagnostics;
            if (sub == null) return "(no subscription sent yet)";
            var sb = new StringBuilder();
            sb.AppendLine($"Sent on session {sub.SessionByte} format={sub.Format}  at {sub.CapturedAt:HH:mm:ss}");
            if (sub.PreambleBytes.Length > 0)
                sb.AppendLine($"Preamble ({sub.PreambleBytes.Length}B): {BitConverter.ToString(sub.PreambleBytes).Replace('-', ' ')}");
            sb.AppendLine($"Body ({sub.BodyBytes.Length}B): {BitConverter.ToString(sub.BodyBytes).Replace('-', ' ')}");
            if (sub.Channels.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Channels ({sub.Channels.Count}):");
                foreach (var ch in sub.Channels)
                    sb.AppendLine($"  idx={ch.Idx,2}  comp=0x{ch.Comp:X2}  width={ch.Width,3}  {ch.Url}");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Render the most recent unsolicited firmware-debug frames
        /// (raw wire group 0x0E, subtype 0x05). These are ASCII log lines the
        /// wheel-bus firmware emits during normal operation — parameter
        /// writes, init traces, occasional warnings — captured by
        /// <see cref="FirmwareDebugLog"/> for visibility. Empty by default
        /// because nothing else in the plugin acts on these; when present
        /// they're useful for understanding what the firmware is doing
        /// across init / dashboard switches / setting writes.</summary>
        public static string BuildFirmwareDebug(MozaPlugin plugin)
        {
            var log = plugin.FirmwareDebugLogForDiagnostics;
            var entries = log.Snapshot();
            if (entries.Length == 0)
                return $"(no firmware-debug frames captured; total received={log.TotalReceived})";

            var sb = new StringBuilder();
            sb.AppendLine($"Recent frames: {entries.Length} shown / {log.TotalReceived} total received");
            // Param-store fault line: ≥5 failures = the wedge signature (a healthy
            // wheel logs none; one-off lines during init are below the threshold).
            var (wf, rf, firstUtc, lastUtc) = log.ParamFaultSnapshot();
            if (wf + rf >= 5)
                sb.AppendLine(
                    $"PARAM-STORE FAULT: {wf} failed writes / {rf} failed reads " +
                    $"({firstUtc.ToLocalTime():HH:mm:ss}–{lastUtc.ToLocalTime():HH:mm:ss}) — " +
                    "wheel parameter storage is wedged; power-cycle the wheel to recover the display");
            // Render newest first so the most recent activity is at the top
            // of the section (and the oldest, least relevant lines slide off
            // the visible area first on long scrolls). Limit to last 64 so a
            // burst doesn't dominate the diagnostics view.
            int limit = Math.Min(entries.Length, 64);
            for (int i = entries.Length - 1; i >= entries.Length - limit; i--)
            {
                var e = entries[i];
                // Local-time stamp keeps the format consistent with the rest
                // of the diagnostics tab (manifest already shows UTC).
                string ts = e.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff");
                // Empty lines are continuation fragments (the firmware
                // sometimes splits a single log line across two 0x0E
                // frames); skip rendering them to keep the section readable
                // — they're still in the bundle's moza-log.txt for full
                // forensic context.
                if (e.Text.Length == 0) continue;
                sb.AppendLine($"  {ts} [{e.SourceName,-7}] {e.Text}");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Render the wheel display's own application log, pulled over
        /// the session layer (FF kind=14 request / kind=15 receipt) and held in
        /// <see cref="Diagnostics.DeviceLogStore"/>. Distinct from
        /// <see cref="BuildFirmwareDebug"/>, which shows the base/wheel MCU
        /// group-0x0E chatter. Lines carry their own device-side timestamp, so
        /// the host receive time is only shown to order them.</summary>
        public static string BuildDeviceLog(MozaPlugin plugin)
        {
            var log = plugin.DeviceLogForDiagnostics;
            var entries = log.Snapshot();
            bool enabled = plugin.Settings?.EnableDeviceLogPull ?? false;

            var sb = new StringBuilder();
            if (!enabled)
                return "(device log pull disabled via EnableDeviceLogPull)";

            // Pull counters first, so "no lines" is diagnosable without a wire
            // trace: requests=0 means we never asked, requests>0 payloads=0
            // means the display isn't answering.
            var pull = plugin.TelemetrySender?.DeviceLogPullStatus;
            if (pull != null) sb.AppendLine($"Pull: {pull}");
            var cm2 = plugin._cm2Sender?.DeviceLogPullStatus;
            if (cm2 != null) sb.AppendLine($"Pull: {cm2}");

            if (entries.Length == 0)
            {
                sb.Append("(no device log lines yet; the display is polled at connect and every 60 s)");
                return sb.ToString();
            }

            sb.AppendLine($"Lines: {entries.Length} shown / {log.TotalRecorded} recorded");
            // Newest first, matching the firmware-debug section.
            // Redacted here as well as in the bundle's own device-display-log.txt:
            // this text is written to the bundle as diagnostics.txt and uploaded
            // on the bug-report path, so masking only the dedicated entry would
            // leak the same identifiers out of the other one.
            var data = plugin.Data;
            int limit = Math.Min(entries.Length, 200);
            for (int i = entries.Length - 1; i >= entries.Length - limit; i--)
            {
                var e = entries[i];
                string ts = e.ReceivedUtc.ToLocalTime().ToString("HH:mm:ss");
                sb.AppendLine($"  {ts} [{e.Source,-5}] {Diagnostics.CaptureRedactor.RedactText(e.Text, data)}");
            }
            return sb.ToString().TrimEnd();
        }

        public static string BuildSubscriptionResponse(MozaPlugin plugin)
        {
            var chunks = plugin.SubscriptionResponseForDiagnostics;
            if (chunks == null || chunks.Count == 0)
                return "(no inbound chunks captured on session 0x02 in 5s window after subscription)";
            var sb = new StringBuilder();
            sb.AppendLine($"{chunks.Count} chunks captured on session 0x02 after most-recent subscription:");
            int total = 0;
            for (int i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                total += c.Length;
                int show = Math.Min(c.Length, 80);
                string hex = BitConverter.ToString(c, 0, show).Replace('-', ' ');
                string ellip = c.Length > show ? " …" : "";
                sb.AppendLine($"  [{i,2}] {c.Length,3}B: {hex}{ellip}");
            }
            sb.AppendLine();
            sb.AppendLine($"Concat ({total}B): {BuildConcatHex(chunks, 200)}");
            return sb.ToString().TrimEnd();
        }

        public static string BuildConcatHex(IReadOnlyList<byte[]> chunks, int max)
        {
            var sb = new StringBuilder();
            int n = 0;
            foreach (var c in chunks)
            {
                foreach (var b in c)
                {
                    if (n++ >= max) { sb.Append(" …"); return sb.ToString(); }
                    sb.Append(b.ToString("X2"));
                    sb.Append(' ');
                }
            }
            return sb.ToString().TrimEnd();
        }
    }
}
