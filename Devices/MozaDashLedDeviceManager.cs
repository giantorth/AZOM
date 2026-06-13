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
    /// Receives computed RPM LED colors from Display() and sends a 16-bit on/off
    /// bitmask to the dash via dash-send-telemetry. Colors are stored on the
    /// device firmware — only the bitmask is sent per frame.
    ///
    /// PitHouse drives the bus CM2's RPM LEDs the same way: a per-frame
    /// dash-send-telemetry bitmask (group 0x41 cmd FD DE) that the firmware
    /// lights in its configured colours — verified cm2.pcapng 2026-06-08. An
    /// earlier build streamed per-LED colour on group 0x32 sub 0x0B to dev 0x12
    /// instead; the firmware ignores that, so CM2 LEDs never lit.
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
        // the 41 14 FD DE bitmask stayed 0. Like RPM, the firmware blanks a held
        // flag if the colour isn't refreshed, so re-send at 1 Hz while any flag
        // is lit. Flags surface on SimHub's `buttons` channel (the profile's
        // 6-LED LogicalButtonsSection).
        private const int RpmLedCount = 10;
        private const int FlagLedCount = 6;
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

                // CM2/CM1 dashboards have 10 RPM LEDs (SimHub `leds` telemetry
                // stream) + 6 flag LEDs (SimHub `buttons` section), driven by two
                // distinct firmware paths:
                //   RPM  → 10-bit on/off bitmask (dash-send-telemetry, 41 FD DE);
                //          the firmware lights each set bit in its stored colour.
                //   Flag → live 6×RGB colour array (dash-flag-colors, 32 08 00);
                //          the firmware lights each non-black flag. The flag LEDs
                //          do NOT use the bitmask (cm2t.pcapng: flags lit while the
                //          bitmask stayed 0).

                var plugin = MozaPlugin.Instance;
                if (plugin == null || !plugin.Data.IsConnected || !plugin.IsDashDetected)
                    return;

                bool alwaysResend = plugin.Settings.AlwaysResendBitmask;
                var now = DateTime.UtcNow;

                // ── RPM bar: 10-bit on/off bitmask ──────────────────────────────
                int rpmCount = Math.Min(ledColors.Length, RpmLedCount);
                // Merge SimHub Individual-LED overrides into the RPM range (no-op on
                // CM2 — the individual section is disabled, so rawColors is empty).
                if (rawColors.Length > 0)
                    ledColors = MozaLedDeviceManager.ApplyOverrides(ledColors, rawColors, 0, rpmCount);

                // ── RPM colours → live indicator register (0B 00) ───────────────
                // Push SimHub's per-LED colour so the bar shows the user's gradient
                // (the bitmask below only lights LEDs in whatever colour the device
                // last stored). Written BEFORE the bitmask so an LED is never lit a
                // frame before its colour lands. All 10 indices from the pipeline
                // (latest non-black — an off LED keeps its last colour).
                // Change-gated; live register, no throttle. If the firmware ignores
                // 0B 00 this is a silent no-op
                // (hardware-test gate — see plan; fallback is the 1B 00 FF stored
                // register with a flash-write throttle).
                SyncRpmColors(plugin, ledColors);

                if (rpmCount > 0)
                {
                    int bitmask = 0;
                    for (int i = 0; i < rpmCount; i++)
                    {
                        if (ledColors[i].R > 0 || ledColors[i].G > 0 || ledColors[i].B > 0)
                            bitmask |= (1 << i);
                    }

                    // PitHouse re-sends the bitmask per frame (cm2.pcapng); we send
                    // on change + a 1 Hz keepalive so a held value doesn't blank.
                    // WriteDashLedBitmask routes to the active sink (standalone-USB
                    // CM2 → 0x12 on the dedicated pipe, behind-base CM2 → 0x14).
                    bool keepaliveDue = (now - _lastSendTime).TotalSeconds >= KeepaliveIntervalSeconds;
                    if (alwaysResend || bitmask != _lastBitmask || keepaliveDue)
                    {
                        _lastBitmask = bitmask;
                        _lastSendTime = now;
                        plugin.WriteDashLedBitmask(bitmask);
                    }
                }

                // ── Flag LEDs: live 6×RGB colour array ──────────────────────────
                // Each flag is its own RGB triplet; black = that flag off. PitHouse
                // streams the array per frame (cm2t.pcapng: 32 14 08 00 <6×RGB>);
                // we send on change + a 1 Hz keepalive while any flag is lit so a
                // held flag doesn't blank. (Off flags stay off without a refresh,
                // so we don't stream black indefinitely when no flag is active.)
                var rgb = new byte[FlagLedCount * 3];
                bool anyFlagOn = false;
                int flagCount = Math.Min(buttonColors.Length, FlagLedCount);
                for (int i = 0; i < flagCount; i++)
                {
                    var c = buttonColors[i];
                    rgb[i * 3] = c.R;
                    rgb[i * 3 + 1] = c.G;
                    rgb[i * 3 + 2] = c.B;
                    if (c.R > 0 || c.G > 0 || c.B > 0) anyFlagOn = true;
                }

                bool flagsChanged = !_lastFlagPrimed;
                if (!flagsChanged)
                {
                    for (int i = 0; i < rgb.Length; i++)
                        if (rgb[i] != _lastFlagRgb[i]) { flagsChanged = true; break; }
                }
                bool flagKeepaliveDue = anyFlagOn
                    && (now - _lastFlagSendTime).TotalSeconds >= KeepaliveIntervalSeconds;
                if (alwaysResend || flagsChanged || flagKeepaliveDue)
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
        /// Push SimHub's RPM gradient to the dash's live indicator-colour register
        /// (cm2-indicator-color / dash-rpm-color, wire 0B 00), change-gated. All 10
        /// indices (incl. redline) take the latest non-black pipeline colour — an
        /// off LED keeps its last colour, since the bitmask handles on/off. No
        /// throttle: this is the live, non-persistent register.
        /// </summary>
        private void SyncRpmColors(MozaPlugin plugin, Color[] ledColors)
        {
            int count = Math.Min(ledColors.Length, RpmLedCount);
            for (int i = 0; i < count; i++)
            {
                var c = ledColors[i];
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
