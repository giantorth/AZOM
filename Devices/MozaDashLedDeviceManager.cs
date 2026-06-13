using System;
using System.Drawing;
using BA63Driver.Interfaces;
using BA63Driver.Mapper;
using MozaPlugin.Protocol;
using SerialDash;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;
using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// A virtual ILedDeviceManager for the MOZA CM2/CM1 dashboard.
    /// Always reports as connected to enable SimHub's LED effects UI.
    ///
    /// The rim is a wheel-style 16-LED strip in physical order
    /// [flag 1-3][RPM 1-10][flag 4-6]. SimHub feeds a single telemetry array in
    /// that order; we drive it with two firmware paths:
    ///   RPM  → 10-bit on/off bitmask (dash-send-telemetry, 41 FD DE) + live
    ///          per-LED indicator colours (cm2-indicator-color / dash-rpm-color,
    ///          0B 00). PitHouse drives the bitmask per frame (cm2.pcapng).
    ///   Flag → live 6×RGB colour array (dash-flag-colors, 32 08 00), black = off
    ///          (cm2t.pcapng: flags lit while the bitmask stayed 0).
    ///
    /// Both SimHub LED modes are supported like the wheel manager: combined
    /// (telemetry effects on the `leds` channel) and individual-exclusive (effects
    /// on `rawState`, merged over the array via ApplyOverrides).
    /// </summary>
    internal class MozaDashLedDeviceManager : ILedDeviceManager
    {
        private LedDeviceState _lastState = new LedDeviceState(
            Array.Empty<Color>(), Array.Empty<Color>(), Array.Empty<Color>(),
            Array.Empty<Color>(), Array.Empty<Color>(), 1.0, 1.0, 1.0, 1.0);

        private int _lastBitmask = -1;

        // LED-bitmask keepalive: the dash firmware blanks its LEDs if it doesn't
        // get a fresh dash-send-telemetry frame within a few seconds, even when the
        // value is unchanged. PitHouse re-sends the bitmask every telemetry frame
        // (cm2.pcapng: ~21/s, including FD DE 00000000 when static); we re-send the
        // last bitmask at 1 Hz when nothing changes — same pattern the wheel uses
        // (MozaLedDeviceManager.KeepaliveIntervalSeconds).
        private DateTime _lastSendTime = DateTime.MinValue;
        private const double KeepaliveIntervalSeconds = 1.0;

        // Flag-LED keepalive: the 6 CM2 flag LEDs are driven by the live
        // dash-flag-colors array (group 0x32 cmd 08 00, 6×RGB, black = off), NOT
        // the RPM bitmask — verified cm2t.pcapng, where the flags lit green while
        // the 41 14 FD DE bitmask stayed 0. Unlike the RPM bitmask (a latched
        // on/off state), a flag colour is a momentary push that the firmware blanks
        // sub-second if not refreshed — so a 1 Hz keepalive made solid flags blink.
        // PitHouse streams the array at ~12.5 Hz (cm2t.pcapng); we match that while
        // any flag is lit.
        // CM2 rim, wheel-style: 16 LEDs in physical order [flag 1-3][RPM 1-10][flag 4-6].
        private const int RpmLedCount = 10;
        private const int FlagLedCount = 6;
        private const int FlagLeftCount = 3;
        private const int TotalLedCount = RpmLedCount + FlagLedCount; // 16
        private const double FlagKeepaliveIntervalSeconds = 0.08; // ~12.5 Hz, matches PitHouse
        private readonly byte[] _lastFlagRgb = new byte[FlagLedCount * 3];
        private bool _lastFlagPrimed;
        private DateTime _lastFlagSendTime = DateTime.MinValue;

        // RPM LED colour sync: the bitmask only toggles each RPM LED on/off; the
        // colour comes from a device register. We push SimHub's computed gradient
        // to the live indicator register (cm2-indicator-color / dash-rpm-color,
        // 0B 00) so the bar matches SimHub. This is the LIVE register — no throttle,
        // change-gated only. All 10 indices (incl. redline) come from the pipeline.
        // Last-written colour per index, change detection.
        private readonly Color[] _lastRpmColors = new Color[RpmLedCount];
        private bool _rpmColorsPrimed;

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
                _lastBitmask = -1;
                _lastSendTime = DateTime.MinValue;
                _lastFlagPrimed = false;
                _lastFlagSendTime = DateTime.MinValue;
                _rpmColorsPrimed = false;
                OnDisconnect?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool IsConnected() => MozaPlugin.Instance?.IsDashDetected ?? false;

        public string GetSerialNumber() => "MOZA-DASH-VIRTUAL";

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

                // CM2 rim is a wheel-style 16-LED strip in physical order
                // [flag 1-3][RPM 1-10][flag 4-6]. SimHub feeds one telemetry array
                // in that order, driven by two firmware paths:
                //   RPM  → 10-bit on/off bitmask (41 FD DE) + live indicator colours
                //          (0B 00); the bitmask lights each set bit in its colour.
                //   Flag → live 6×RGB colour array (32 08 00), black = off; flags do
                //          NOT use the bitmask (cm2t.pcapng: flags lit, bitmask 0).
                //
                // Combined AND individual modes are handled like the wheel LED
                // manager: in SimHub's "Individual LEDs (exclusive)" mode the `leds`
                // channel is empty and only `rawState` carries colour, so we merge
                // rawState over the telemetry array first (ApplyOverrides — a no-op
                // in combined mode) and then process the single merged array by
                // position.

                var plugin = MozaPlugin.Instance;
                if (plugin == null || !plugin.Data.IsConnected || !plugin.IsDashDetected)
                    return;

                if (rawColors.Length > 0)
                    ledColors = MozaLedDeviceManager.ApplyOverrides(ledColors, rawColors, 0, TotalLedCount);

                if (ledColors.Length == 0)
                    return;

                bool alwaysResend = plugin.Settings.AlwaysResendBitmask;
                var now = DateTime.UtcNow;

                // Split flag/RPM/flag by position. Full 16-LED array → flags at
                // [0,1,2] + [13,14,15], RPM at [3..12]. A short array (pre-detection
                // / transition) → no flags, RPM from index 0.
                bool hasFlags = ledColors.Length >= TotalLedCount;
                int flagLeft = hasFlags ? FlagLeftCount : 0;

                var rpmColors = new Color[RpmLedCount];
                int rpmAvail = Math.Max(0, Math.Min(RpmLedCount, ledColors.Length - flagLeft));
                for (int i = 0; i < rpmAvail; i++)
                    rpmColors[i] = ledColors[flagLeft + i];

                // ── RPM colours → live indicator register (0B 00), before the bitmask
                // so an LED is never lit a frame before its colour lands. Change-gated;
                // live register, no throttle. ──
                SyncRpmColors(plugin, rpmColors);

                // ── RPM bar: 10-bit on/off bitmask. PitHouse re-sends per frame
                // (cm2.pcapng); we send on change + 1 Hz keepalive so a held value
                // doesn't blank. ──
                int bitmask = 0;
                for (int i = 0; i < RpmLedCount; i++)
                {
                    if (rpmColors[i].R > 0 || rpmColors[i].G > 0 || rpmColors[i].B > 0)
                        bitmask |= (1 << i);
                }
                bool keepaliveDue = (now - _lastSendTime).TotalSeconds >= KeepaliveIntervalSeconds;
                if (alwaysResend || bitmask != _lastBitmask || keepaliveDue)
                {
                    _lastBitmask = bitmask;
                    _lastSendTime = now;
                    plugin.WriteDashLedBitmask(bitmask);
                }

                // ── Flag LEDs: live 6×RGB colour array (32 08 00), black = off.
                // Flags 0-2 are the left block (LEDs 1-3), 3-5 the right (LEDs 14-16).
                // Send on change + ~12.5 Hz keepalive while any flag is lit (matches
                // PitHouse; a slower 1 Hz refresh made solid flags blink). ──
                var rgb = new byte[FlagLedCount * 3];
                bool anyFlagOn = false;
                if (hasFlags)
                {
                    for (int i = 0; i < FlagLedCount; i++)
                    {
                        int srcIdx = i < FlagLeftCount ? i : RpmLedCount + i; // 0,1,2, 13,14,15
                        var c = ledColors[srcIdx];
                        rgb[i * 3] = c.R;
                        rgb[i * 3 + 1] = c.G;
                        rgb[i * 3 + 2] = c.B;
                        if (c.R > 0 || c.G > 0 || c.B > 0) anyFlagOn = true;
                    }
                }

                bool flagsChanged = !_lastFlagPrimed;
                if (!flagsChanged)
                {
                    for (int i = 0; i < rgb.Length; i++)
                        if (rgb[i] != _lastFlagRgb[i]) { flagsChanged = true; break; }
                }
                bool flagKeepaliveDue = anyFlagOn
                    && (now - _lastFlagSendTime).TotalSeconds >= FlagKeepaliveIntervalSeconds;
                if (hasFlags && (alwaysResend || flagsChanged || flagKeepaliveDue))
                {
                    Array.Copy(rgb, _lastFlagRgb, rgb.Length);
                    _lastFlagPrimed = true;
                    _lastFlagSendTime = now;
                    plugin.WriteDashFlagColors(rgb);
                }

                // Dashboard brightness is stored config (set via plugin UI slider →
                // ApplySavedDashSettings on connect). Don't forward SimHub's per-frame
                // rpmBrightness here — SimHub passes 0 during scene transitions / no-game
                // states, which would blank the dashboard. SimHub brightness applies to
                // wheel RPM + button LEDs only.
            }
            finally
            {
                AfterDisplay?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Push the 10 RPM colours (already extracted from the flag/RPM/flag array)
        /// to the dash's live indicator-colour register (cm2-indicator-color /
        /// dash-rpm-color, wire 0B 00), change-gated. Each index takes the latest
        /// non-black colour — an off LED keeps its last colour, since the bitmask
        /// handles on/off. No throttle: this is the live, non-persistent register.
        /// </summary>
        private void SyncRpmColors(MozaPlugin plugin, Color[] rpmColors)
        {
            for (int i = 0; i < RpmLedCount && i < rpmColors.Length; i++)
            {
                var c = rpmColors[i];
                // Off this frame — keep the last known indicator colour.
                if (c.R == 0 && c.G == 0 && c.B == 0) continue;

                if (!_rpmColorsPrimed
                    || c.R != _lastRpmColors[i].R
                    || c.G != _lastRpmColors[i].G
                    || c.B != _lastRpmColors[i].B)
                {
                    _lastRpmColors[i] = c;
                    plugin.WriteDashRpmColor(i, c.R, c.G, c.B);
                }
            }
            _rpmColorsPrimed = true;
        }
    }
}
