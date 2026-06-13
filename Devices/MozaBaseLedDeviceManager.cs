using System;
using System.Drawing;
using BA63Driver.Interfaces;
using BA63Driver.Mapper;
using SerialDash;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;
using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Virtual ILedDeviceManager for the wheel-base ambient LED strips
    /// (R21 / R25 / R27 family — 18 LEDs total across two physical 9-LED
    /// strips on the base body). Receives 18 colors from SimHub's Display()
    /// pipeline; splits LEDs 0–8 onto strip 0 and 9–17 onto strip 1; sends
    /// per-LED color chunks (cmd 0x1A, 4-byte-per-LED [idx, R, G, B], up to
    /// 5 LEDs / 20 bytes per chunk) and a 9-bit-per-strip bitmask
    /// (cmd 0x1B, 4-byte LE u32). Group 0x20 device 0x12.
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
        // Two physical strips of 9 LEDs each, addressed independently.
        public const int LedsPerStrip = 9;
        public const int TotalLeds = LedsPerStrip * 2;

        private LedDeviceState _lastState = new LedDeviceState(
            Array.Empty<Color>(), Array.Empty<Color>(), Array.Empty<Color>(),
            Array.Empty<Color>(), Array.Empty<Color>(), 1.0, 1.0, 1.0, 1.0);

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

        public bool IsConnected() => MozaPlugin.Instance?.IsBaseAmbientLedSupported ?? false;

        public string GetSerialNumber() => "MOZA-BASE-AMBIENT-VIRTUAL";

        public string GetFirmwareVersion() => "1.0";

        public object GetDriverInstance() => this;

        public void Close() { }

        public void ResetDetection() { }

        public void SerialPortCanBeScanned(object sender, SerialDashController.ScanArgs e) { }

        public IPhysicalMapper GetPhysicalMapper() => new NeutralLedsMapper();

        public ILedDriverBase? GetLedDriver() => null;

        public void Display(
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
        {
            BeforeDisplay?.Invoke(this, EventArgs.Empty);

            try
            {
                var ledColors = leds?.Invoke() ?? Array.Empty<Color>();
                var buttonColors = buttons?.Invoke() ?? Array.Empty<Color>();
                var encoderColors = encoders?.Invoke() ?? Array.Empty<Color>();
                var matrixColors = matrix?.Invoke() ?? Array.Empty<Color>();
                var rawColors = rawState?.Invoke() ?? Array.Empty<Color>();

                _lastState = new LedDeviceState(
                    ledColors, buttonColors, encoderColors, matrixColors, rawColors,
                    rpmBrightness, buttonsBrightness, encodersBrightness, matrixBrightness);

                // Merge SimHub Individual-LED overrides over the contiguous
                // 18-LED telemetry strip (same ApplyOverrides pattern used by
                // wheel + dashboard managers).
                if (rawColors.Length > 0)
                {
                    ledColors = MozaLedDeviceManager.ApplyOverrides(
                        ledColors, rawColors, 0, TotalLeds);
                }

                var plugin = MozaPlugin.Instance;
                if (plugin == null || !plugin.Data.IsConnected || !plugin.IsBaseAmbientLedSupported)
                    return;

                bool alwaysResendBitmask = plugin.Settings.AlwaysResendBitmask;

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
                    alwaysResendBitmask: alwaysResendBitmask, keepaliveDue: keepaliveDue);
                bool sent1 = ProcessStrip(plugin, ledColors, brightness, stripIndex: 1, sourceOffset: LedsPerStrip,
                    alwaysResendBitmask: alwaysResendBitmask, keepaliveDue: keepaliveDue);
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
            int stripIndex, int sourceOffset, bool alwaysResendBitmask, bool keepaliveDue)
        {
            // Materialise the 9 colors for this strip with brightness applied.
            // Source array may be shorter than expected — pad with black so
            // the bitmask + chunk shape is always strip-complete.
            var stripColors = new Color[LedsPerStrip];
            int available = Math.Max(0, Math.Min(LedsPerStrip, ledColors.Length - sourceOffset));
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
            for (int i = 0; i < LedsPerStrip; i++)
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

            if (alwaysResendBitmask || bitmaskChanged || keepaliveDue)
            {
                SendBitmask(plugin, stripIndex, bitmask);
                _lastBitmask[stripIndex] = bitmask;
                return true;
            }
            return false;
        }

        // Send a strip's 9 colors as two cmd-0x1A chunks: LEDs 0..4 (5
        // entries = 20-byte payload, wire N=22) then LEDs 5..8 (4 entries =
        // 16-byte payload, wire N=18). No trailing padding entry on chunk 2.
        //
        // Earlier revisions appended a [0xFF, 0, 0, 0] padding entry to pad
        // chunk 2 to 20 bytes, mirroring the wheel-side trick that hides
        // zero-pad bytes from the wheel firmware's "interpret-as-set-LED-0-
        // black" bug. The base firmware behaves differently: with that
        // padding entry present, bitmask=0x01 (light only LED 0) silently
        // produced no LEDs lit; 2+ active bits worked normally. PitHouse's
        // R25 capture (2026-05-05) sends chunk 2 at exactly N=18 with no
        // padding, so we match that wire format precisely.
        private static void SendColorChunks(MozaPlugin plugin, Color[] strip, int stripIndex)
        {
            string command = stripIndex == 0
                ? "base-ambient-rpm-colors-strip0"
                : "base-ambient-rpm-colors-strip1";

            var chunk1 = new byte[20];
            for (int i = 0; i < 5; i++)
            {
                int o = i * 4;
                chunk1[o]     = (byte)i;
                chunk1[o + 1] = strip[i].R;
                chunk1[o + 2] = strip[i].G;
                chunk1[o + 3] = strip[i].B;
            }
            plugin.DeviceManager.WriteArray(command, chunk1);

            var chunk2 = new byte[16];
            for (int i = 0; i < 4; i++)
            {
                int led = 5 + i;
                int o = i * 4;
                chunk2[o]     = (byte)led;
                chunk2[o + 1] = strip[led].R;
                chunk2[o + 2] = strip[led].G;
                chunk2[o + 3] = strip[led].B;
            }
            plugin.DeviceManager.WriteArray(command, chunk2);
        }

        // Send a strip's 9-bit bitmask as a 4-byte LE u32 (high bits zero).
        // 4-byte form per docs/protocol/leds/base-ambient-0x20-0x22.md.
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
