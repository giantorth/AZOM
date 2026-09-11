using System;
using System.Drawing;
using BA63Driver.Interfaces;
using BA63Driver.Mapper;
using SerialDash;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;
using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;

namespace MozaPlugin.Devices.Led
{
    /// <summary>
    /// Virtual ILedDeviceManager for the wheel-base ambient LED strips (two
    /// physical strips on the base body). Strip length is per base model —
    /// 6 LEDs on an R16 Ultra, 9 on R21/R25/R27 — resolved at send time from
    /// <see cref="BaseModelInfo"/>. Receives TotalLeds colors from SimHub's
    /// Display() pipeline; splits the first half onto strip 0 and the second
    /// onto strip 1; sends per-LED color chunks (cmd 0x1A, 4-byte-per-LED
    /// [idx, R, G, B], up to 5 LEDs / 20 bytes per chunk) and a per-strip
    /// bitmask (cmd 0x1B, 4-byte LE u32). Group 0x20 device 0x12.
    ///
    /// Per-frame brightness scaling uses SimHub's rpmBrightness (0..1);
    /// the firmware also applies its own stored brightness setting on top.
    ///
    /// Idle handoff: when SimHub stops feeding telemetry colors (game
    /// exits / scene transitions deliver an empty array), the manager
    /// sends one final bitmask=0 frame to clear the strip and then goes
    /// quiet, allowing the firmware's standby animation (rainbow / breath
    /// / flow / etc.) to resume.
    ///
    /// See docs/protocol/leds/base-ambient-0x20-0x22.md.
    /// </summary>
    internal class MozaBaseLedDeviceManager : ILedDeviceManager
    {
        // Two physical strips, addressed independently. Length is per base
        // model (BaseModelInfo) — 6 on R16 Ultra, 9 on R21/R25/R27 — so it is
        // resolved per frame from the detected model name rather than fixed.
        // Falls back to the 9-LED layout while the model name is unknown,
        // which is the pre-existing behaviour.
        // Via MozaData's latch, NOT BaseModelInfo(BaseModelName) — that string is
        // blanked by ClearWheelIdentity on rim swaps and transient reconnects,
        // which silently reverted this emitter to the 9-LED layout mid-session.
        private static int CurrentLedsPerStrip
            => MozaPlugin.Instance?.Data?.ResolvedAmbientLedsPerStrip
               ?? BaseModelInfo.DefaultLedsPerStrip;

        /// <summary>Total LEDs SimHub is asked to render for this base.</summary>
        internal static int CurrentTotalLeds => CurrentLedsPerStrip * 2;

        private LedDeviceState _lastState = SimHubLedCompat.CreateState(
            Array.Empty<Color>(), Array.Empty<Color>(), Array.Empty<Color>(),
            Array.Empty<Color>(), Array.Empty<Color>(), Array.Empty<Color>(),
            1.0, 1.0, 1.0, 1.0);

        // Per-strip cached state. Bitmask = -1 means "nothing sent yet";
        // colorHash = 0 means "no palette captured yet" (zero is a safe
        // sentinel since any non-empty palette including all-black has at
        // least the leading length byte folded in).
        private readonly int[] _lastBitmask = new int[] { -1, -1 };
        private readonly long[] _lastColorHash = new long[] { 0, 0 };

        // Whether we last sent live telemetry. Used to fire a single
        // bitmask=0 release frame on the active→idle transition so the
        // device-side standby animation can take back over.
        private bool _wasActive;

        // Latched while this pipeline is standing down for a dashboard upload,
        // so the resume edge can drop the per-strip change detection.
        private bool _uploadPaused;

        // LED-bitmask keepalive: the base firmware blanks its strip LEDs if the
        // bitmask isn't refreshed within a few seconds, even when unchanged — the
        // R25 capture sends the bitmask every frame (colors only on change). Re-send
        // the last bitmask at 1 Hz when the value is static, matching the dash/wheel
        // keepalive. Active path only — the idle-release path below stays quiet so
        // the firmware standby animation resumes.
        private DateTime _lastSendTime = DateTime.MinValue;
        private const double KeepaliveIntervalSeconds = 1.0;

        public LedModuleSettings LedModuleSettings { get; set; } = null!;

        public LedDeviceState LastState => _lastState;

        private bool _wasConnected;

        public event EventHandler? BeforeDisplay;
        public event EventHandler? AfterDisplay;
        public event EventHandler? OnConnect;
#pragma warning disable CS0067 // Required by ILedDeviceManager interface
        public event EventHandler? OnError;
#pragma warning restore CS0067
        public event EventHandler? OnDisconnect;

        /// <summary>
        /// Check current detection state and fire OnConnect/OnDisconnect if it changed.
        /// Called from device extension's DataUpdate() every frame.
        /// </summary>
        internal void UpdateConnectionState()
        {
            bool connected = IsConnected();
            if (connected == _wasConnected) return;
            _wasConnected = connected;

            if (connected)
            {
                OnConnect?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _lastBitmask[0] = -1;
                _lastBitmask[1] = -1;
                _lastColorHash[0] = 0;
                _lastColorHash[1] = 0;
                _wasActive = false;
                _lastSendTime = DateTime.MinValue;
                OnDisconnect?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Model token the owning device definition was written for ("R16"), or
        /// empty for the legacy shared definition. Definitions are per-model now,
        /// so a leftover one for a base the user no longer runs must not drive the
        /// attached base's strip with the wrong geometry.
        /// </summary>
        internal string ExpectedModelPrefix { get; set; } = "";

        public bool IsConnected()
        {
            var plugin = MozaPlugin.Instance;
            if (plugin == null || !plugin.IsBaseAmbientLedSupported) return false;
            if (ExpectedModelPrefix.Length == 0) return true;   // legacy shared definition

            // Empty while the base identity has not arrived yet — stay connected
            // rather than blinking the strip off during a reconnect.
            var attached = BaseModelInfo.ExtractPrefix(plugin.Data?.BaseModelName);
            return attached.Length == 0
                || string.Equals(attached, ExpectedModelPrefix, StringComparison.OrdinalIgnoreCase);
        }

        // Surfaced as "Serial number" on the LEDs tab's connection status. The base
        // has no serial number as such, so its MCU UID is the closest real identity
        // — better than a placeholder string, and it distinguishes two bases.
        public string GetSerialNumber()
        {
            var uid = MozaPlugin.Instance?.Data?.BaseMcuUid;
            return uid == null || uid.Length == 0 ? "" : BitConverter.ToString(uid).Replace("-", "");
        }

        // The base's real firmware, as read at group 0x04 — not the driver's version.
        public string GetFirmwareVersion() => MozaPlugin.Instance?.Data?.BaseFwVersionText ?? "";

        public object GetDriverInstance() => this;

        public void Close() { }

        public void ResetDetection() { }

        public void SerialPortCanBeScanned(object sender, SerialDashController.ScanArgs e) { }

        public IPhysicalMapper GetPhysicalMapper() => new NeutralLedsMapper();

        public ILedDriverBase? GetLedDriver() => null;

        /// <summary>
        /// SimHub &lt;= 9.11.x <c>ILedDeviceManager.Display</c>, which has no
        /// <c>overrideState</c> channel. Declared alongside the current overload so one
        /// DLL serves both host generations — the CLR binds an implicitly-implemented
        /// interface method by name and signature at type-load time, so each SimHub
        /// build picks the overload its own interface declares. Dead code on 9.12+.
        /// Drop this once 9.11.x is no longer supported (see SimHubLedCompat).
        ///
        /// <c>virtual</c> is load-bearing, not style: the CLR fills an interface slot only
        /// from a public *virtual* method (ECMA-335 II.12.2). Roslyn marks the overload
        /// matching the compile-time interface virtual automatically, but this one matches
        /// no interface we compile against, so without the keyword it stays non-virtual and
        /// 9.11.x still fails type load with the error this whole shim exists to avoid.
        /// </summary>
        public virtual void Display(
            Func<Color[]> leds,
            Func<Color[]> buttons,
            Func<Color[]> encoders,
            Func<Color[]> matrix,
            Func<Color[]> rawState,
            bool forceRefresh,
            Func<object>? extraData = null,
            double rpmBrightness = 1.0,
            double buttonsBrightness = 1.0,
            double encodersBrightness = 1.0,
            double matrixBrightness = 1.0)
            => Display(leds, buttons, encoders, matrix, rawState, SimHubLedCompat.NoOverrides,
                forceRefresh, extraData,
                rpmBrightness, buttonsBrightness, encodersBrightness, matrixBrightness);

        public void Display(
            Func<Color[]> leds,
            Func<Color[]> buttons,
            Func<Color[]> encoders,
            Func<Color[]> matrix,
            Func<Color[]> rawState,
            Func<Color[]> overrideState,
            bool forceRefresh,
            Func<object>? extraData = null,
            double rpmBrightness = 1.0,
            double buttonsBrightness = 1.0,
            double encodersBrightness = 1.0,
            double matrixBrightness = 1.0)
        {
            BeforeDisplay?.Invoke(this, EventArgs.Empty);

            try
            {
                var ledColors = leds?.Invoke() ?? Array.Empty<Color>();
                var buttonColors = buttons?.Invoke() ?? Array.Empty<Color>();
                var encoderColors = encoders?.Invoke() ?? Array.Empty<Color>();
                var matrixColors = matrix?.Invoke() ?? Array.Empty<Color>();
                var rawColors = rawState?.Invoke() ?? Array.Empty<Color>();
                var overrideColors = overrideState?.Invoke() ?? Array.Empty<Color>();

                _lastState = SimHubLedCompat.CreateState(
                    ledColors, buttonColors, encoderColors, matrixColors, rawColors, overrideColors,
                    rpmBrightness, buttonsBrightness, encodersBrightness, matrixBrightness);

                int ledsPerStrip = CurrentLedsPerStrip;
                int totalLeds = ledsPerStrip * 2;

                // Merge SimHub's physical-index colour layers (Individual LEDs on
                // rawState, dashboard "Device LEDs override" components on
                // overrideState) over the contiguous telemetry strip — same
                // ApplyOverrides pattern used by wheel + dashboard managers, raw
                // first then override on top (PhysicalMapper.GetColor blend order).
                if (rawColors.Length > 0)
                {
                    ledColors = MozaLedDeviceManager.ApplyOverrides(
                        ledColors, rawColors, 0, totalLeds);
                }
                if (overrideColors.Length > 0)
                {
                    ledColors = MozaLedDeviceManager.ApplyOverrides(
                        ledColors, overrideColors, 0, totalLeds);
                }

                var plugin = MozaPlugin.Instance;
                if (plugin == null || !plugin.Data.IsConnected || !plugin.IsBaseAmbientLedSupported)
                    return;

                // Dashboard upload standing the pipeline down (see the same
                // guard, and why it is not the raw in-flight flag, in
                // MozaLedDeviceManager). Both strips ride the same wheelbase
                // link the transfer needs. On resume, drop the per-strip change
                // detection: the firmware's standby animation reclaimed the
                // strips once the 1 Hz bitmask stopped, so an unchanged frame
                // must still be re-sent.
                if (UploadProgressLedBar.IsStandDownActive)
                {
                    _uploadPaused = true;
                    return;
                }
                if (_uploadPaused)
                {
                    _uploadPaused = false;
                    _lastBitmask[0] = _lastBitmask[1] = -1;
                    _lastColorHash[0] = _lastColorHash[1] = 0;
                    _lastSendTime = DateTime.MinValue;
                    _wasActive = false;
                }

                // No telemetry colors this frame — issue a single release
                // (bitmask=0 to both strips) on the active→idle transition,
                // then stay quiet so the firmware's standby animation
                // resumes. Without the release some firmware revisions
                // continue showing whatever telemetry colors were last lit.
                if (ledColors.Length == 0)
                {
                    if (_wasActive)
                    {
                        SendBitmask(plugin, 0, 0);
                        SendBitmask(plugin, 1, 0);
                        _lastBitmask[0] = 0;
                        _lastBitmask[1] = 0;
                        _wasActive = false;
                    }
                    return;
                }

                _wasActive = true;

                // Per-frame brightness from SimHub's pipeline. Clamped to
                // [0..1] — values >1 would over-saturate (firmware brightness
                // applies on top, so we're already in 0..255 before its
                // multiplier).
                double brightness = rpmBrightness;
                if (brightness < 0) brightness = 0;
                if (brightness > 1) brightness = 1;

                // Walk both physical strips in parallel — same pattern, just
                // different SimHub source slice and target command suffix.
                // keepaliveDue is computed once and shared so both strips refresh
                // on the same 1 Hz tick; _lastSendTime advances only when a bitmask
                // actually went out (change or keepalive), so a continuously moving
                // value never triggers a redundant keepalive frame.
                var now = DateTime.UtcNow;
                bool keepaliveDue = (now - _lastSendTime).TotalSeconds >= KeepaliveIntervalSeconds;
                bool sent0 = ProcessStrip(plugin, ledColors, brightness, stripIndex: 0, sourceOffset: 0,
                    ledsPerStrip: ledsPerStrip, keepaliveDue: keepaliveDue);
                bool sent1 = ProcessStrip(plugin, ledColors, brightness, stripIndex: 1, sourceOffset: ledsPerStrip,
                    ledsPerStrip: ledsPerStrip, keepaliveDue: keepaliveDue);
                if (sent0 || sent1)
                    _lastSendTime = now;
            }
            finally
            {
                AfterDisplay?.Invoke(this, EventArgs.Empty);
            }
        }

        // Returns true if a bitmask frame was sent for this strip (change or
        // keepalive) so the caller can advance the shared keepalive timer.
        private bool ProcessStrip(MozaPlugin plugin, Color[] ledColors, double brightness,
            int stripIndex, int sourceOffset, int ledsPerStrip, bool keepaliveDue)
        {
            // Materialise this strip's colors with brightness applied. Source
            // array may be shorter than expected — pad with black so the
            // bitmask + chunk shape is always strip-complete.
            var stripColors = new Color[ledsPerStrip];
            int available = Math.Max(0, Math.Min(ledsPerStrip, ledColors.Length - sourceOffset));
            for (int i = 0; i < available; i++)
            {
                var c = ledColors[sourceOffset + i];
                byte r = (byte)Math.Round(c.R * brightness);
                byte g = (byte)Math.Round(c.G * brightness);
                byte b = (byte)Math.Round(c.B * brightness);
                stripColors[i] = Color.FromArgb(r, g, b);
            }

            // Build bitmask: bit N set = LED N is non-black.
            int bitmask = 0;
            for (int i = 0; i < ledsPerStrip; i++)
            {
                var c = stripColors[i];
                if (c.R > 0 || c.G > 0 || c.B > 0)
                    bitmask |= (1 << i);
            }

            // Hash the post-brightness palette so we re-send colors only on
            // a meaningful change (matches PitHouse capture: "Colors are only
            // re-sent when the palette changes — not every frame").
            long colorHash = HashColors(stripColors);
            bool colorsChanged = colorHash != _lastColorHash[stripIndex];
            bool bitmaskChanged = bitmask != _lastBitmask[stripIndex];

            if (colorsChanged)
            {
                SendColorChunks(plugin, stripColors, stripIndex);
                _lastColorHash[stripIndex] = colorHash;
            }

            if (bitmaskChanged || keepaliveDue)
            {
                SendBitmask(plugin, stripIndex, bitmask);
                _lastBitmask[stripIndex] = bitmask;
                return true;
            }
            return false;
        }

        // Send a strip's colors as cmd-0x1A chunks of at most 5 entries
        // ([idx, R, G, B] each, so 20 bytes of LED data per chunk). Chunk 1
        // carries LEDs 0..4; chunk 2 carries whatever remains, so its shape
        // follows strip length: 4 entries / wire N=18 on a 9-LED strip,
        // 1 entry / wire N=6 on the R16 Ultra's 6-LED strip. A strip of 5 or
        // fewer LEDs sends no chunk 2 at all.
        //
        // Chunk 2 must NOT be padded to 20 bytes. The wheel-LED command
        // (0x19) needs a [0xFF, 0, 0, 0] trailing entry to hide zero-pad
        // bytes from the wheel firmware's "interpret-as-set-LED-0-black" bug.
        // The base firmware behaves differently: with that padding entry
        // present, bitmask=0x01 (light only LED 0) silently produced no LEDs
        // lit; 2+ active bits worked normally. Both the R25 (2026-05-05) and
        // R16 Ultra (2026-08-22) captures send chunk 2 at exactly the
        // remaining LED count with no padding.
        private static void SendColorChunks(MozaPlugin plugin, Color[] strip, int stripIndex)
        {
            string command = stripIndex == 0
                ? "base-ambient-rpm-colors-strip0"
                : "base-ambient-rpm-colors-strip1";

            const int MaxEntriesPerChunk = 5;
            int total = strip.Length;

            for (int first = 0; first < total; first += MaxEntriesPerChunk)
            {
                int count = Math.Min(MaxEntriesPerChunk, total - first);
                var chunk = new byte[count * 4];
                for (int i = 0; i < count; i++)
                {
                    int led = first + i;
                    int o = i * 4;
                    chunk[o]     = (byte)led;
                    chunk[o + 1] = strip[led].R;
                    chunk[o + 2] = strip[led].G;
                    chunk[o + 3] = strip[led].B;
                }
                plugin.DeviceManager.WriteArray(command, chunk);
            }
        }

        // Send a strip's bitmask as a 4-byte LE u32 (high bits zero). Payload
        // width is always 4 bytes regardless of strip length; the used width
        // is the LED count (0x3F max on 6 LEDs, 0x1FF on 9).
        // Per docs/protocol/leds/base-ambient-0x20-0x22.md.
        private static void SendBitmask(MozaPlugin plugin, int stripIndex, int bitmask)
        {
            string command = stripIndex == 0
                ? "base-ambient-send-rpm-strip0"
                : "base-ambient-send-rpm-strip1";
            var payload = new byte[]
            {
                (byte)(bitmask & 0xFF),
                (byte)((bitmask >> 8) & 0xFF),
                (byte)((bitmask >> 16) & 0xFF),
                (byte)((bitmask >> 24) & 0xFF),
            };
            plugin.DeviceManager.WriteArray(command, payload);
        }

        // Cheap palette change-detector. Fold each color's RGB into a 64-bit
        // accumulator. Collisions are theoretically possible but irrelevant
        // in practice — worst case is a missed re-send for one frame, which
        // self-corrects on the next palette change.
        private static long HashColors(Color[] strip)
        {
            unchecked
            {
                long h = 1469598103934665603L; // FNV-1a 64-bit basis
                for (int i = 0; i < strip.Length; i++)
                {
                    var c = strip[i];
                    h ^= c.R; h *= 1099511628211L;
                    h ^= c.G; h *= 1099511628211L;
                    h ^= c.B; h *= 1099511628211L;
                }
                return h;
            }
        }
    }
}
