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
    /// A virtual ILedDeviceManager for the MOZA Dashboard.
    /// Always reports as connected to enable SimHub's LED effects UI.
    /// Receives computed LED colors from Display() and sends a bitmask
    /// to the dash via dash-send-telemetry. Colors are stored on the device
    /// firmware — only the on/off bitmask is sent per frame.
    ///
    /// Standalone CM2 path (when <see cref="MozaPlugin.ShouldUseStandaloneDashboardTarget"/>
    /// is true): the legacy <c>dash-send-telemetry</c> bitmask at dev=0x14 did
    /// not visibly drive CM2 LEDs in lab tests (usb-capture/CM2.md 2026-05-21),
    /// so swap to the wheel RPM-bar live commands (<c>wheel-telemetry-rpm-colors</c>
    /// for per-LED color, <c>wheel-send-rpm-telemetry</c> for the bitmask)
    /// re-targeted to dev=0x12 (CM2 bridge/main). All 16 LEDs are addressed as
    /// RPM positions; CM2 has no separate button strip.
    /// </summary>
    internal class MozaDashLedDeviceManager : ILedDeviceManager
    {
        // CM2 physical layout: 16 RPM LEDs (positions 1–16). Per CM2.md:
        // logical 1–3 left side bottom-to-top, 4–13 top row left-to-right,
        // 14–16 right side top-to-bottom. The dashboard profile model has
        // 10 RPM + 6 flag colors today; both feed into the same 16-LED strip.
        private const int Cm2LedCount = 16;

        private LedDeviceState _lastState = new LedDeviceState(
            Array.Empty<Color>(), Array.Empty<Color>(), Array.Empty<Color>(),
            Array.Empty<Color>(), Array.Empty<Color>(), 1.0, 1.0, 1.0, 1.0);

        private int _lastBitmask = -1;
        // Per-LED color cache for the CM2 live path. wheel-telemetry-rpm-colors
        // is a 5-LED chunked command; we resend only when at least one slot
        // changed. Initialised to a sentinel so the first frame always emits.
        private readonly Color[] _lastCm2Colors = new Color[Cm2LedCount];
        private bool _cm2ColorsInitialised;

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
                _cm2ColorsInitialised = false;
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

                // Merge SimHub Individual-LED overrides. Dashboard physical order:
                // [rpm 0..9][flag 0..5] — flags surface on the `buttons` channel.
                if (rawColors.Length > 0)
                {
                    ledColors = MozaLedDeviceManager.ApplyOverrides(
                        ledColors, rawColors, 0, MozaDeviceConstants.RpmLedCount);
                    buttonColors = MozaLedDeviceManager.ApplyOverrides(
                        buttonColors, rawColors, MozaDeviceConstants.RpmLedCount, MozaDeviceConstants.FlagLedCount);
                }

                if (ledColors.Length == 0 && buttonColors.Length == 0)
                    return;

                var plugin = MozaPlugin.Instance;
                if (plugin == null || !plugin.Data.IsConnected || !plugin.IsDashDetected)
                    return;

                bool alwaysResendBitmask = plugin.Settings.AlwaysResendBitmask;

                // Build bitmask: bits 0-9 = RPM LEDs (from telemetry), bits 10-15 = flag LEDs (from buttons)
                int bitmask = 0;
                int rpmCount = Math.Min(ledColors.Length, MozaDeviceConstants.RpmLedCount);
                for (int i = 0; i < rpmCount; i++)
                {
                    if (ledColors[i].R > 0 || ledColors[i].G > 0 || ledColors[i].B > 0)
                        bitmask |= (1 << i);
                }
                int flagCount = Math.Min(buttonColors.Length, MozaDeviceConstants.FlagLedCount);
                for (int i = 0; i < flagCount; i++)
                {
                    if (buttonColors[i].R > 0 || buttonColors[i].G > 0 || buttonColors[i].B > 0)
                        bitmask |= (1 << (MozaDeviceConstants.RpmLedCount + i));
                }

                bool useCm2Path = plugin.ShouldUseStandaloneDashboardTarget();

                if (useCm2Path)
                {
                    // CM2 LEDs are driven per-frame from SimHub's LED pipeline,
                    // exactly like the wheel — the live colours flow through this
                    // Display() callback, NOT from the one-time HardwareApplier
                    // config. Push each changed LED via the canonical per-LED
                    // colour commands (group 0x32 sub 0x0B, from rs21_parameter.db
                    // MeterSetCfg_SetIndicatorGroupColor / SetFlagGroupColor):
                    //   RPM  LED i → cm2-indicator-color{i+1}  ([0x0B,0x00,i] + RGB)
                    //   flag LED i → cm2-flag-color{i+1}        ([0x0B,0x02,i] + RGB)
                    // Off = black. SimHub already encodes on/off as colour in the
                    // ledColors/buttonColors arrays, so no separate bitmask is sent.
                    SendCm2LiveColors(plugin, ledColors, buttonColors, rpmCount, flagCount);
                }
                else
                {
                    // Legacy SHDP path: 16-bit on/off bitmask via dash-send-telemetry.
                    if (alwaysResendBitmask || bitmask != _lastBitmask)
                    {
                        _lastBitmask = bitmask;
                        plugin.DeviceManager.WriteSetting("dash-send-telemetry", bitmask);
                    }
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
        /// Per-frame live CM2 LED emission — the wheel-equivalent flow for the
        /// dash. Drives each of the 16 LEDs (10 RPM + 6 flag) from SimHub's
        /// computed colours using the canonical per-LED colour commands
        /// (group 0x32 sub 0x0B, rs21_parameter.db
        /// MeterSetCfg_SetIndicatorGroupColor / SetFlagGroupColor):
        ///   RPM  LED i → cm2-indicator-color{i+1}  ([0x0B,0x00,i] + RGB)
        ///   flag LED i → cm2-flag-color{i+1}        ([0x0B,0x02,i] + RGB)
        /// Only changed slots are sent (each command is a single-LED write).
        /// Off is encoded as black, so no separate on/off bitmask is needed.
        /// </summary>
        private void SendCm2LiveColors(MozaPlugin plugin,
            Color[] ledColors, Color[] buttonColors,
            int rpmCount, int flagCount)
        {
            for (int i = 0; i < Cm2LedCount; i++)
            {
                // Slots 0-9 = RPM (ledColors), 10-15 = flag (buttonColors).
                Color c;
                if (i < MozaDeviceConstants.RpmLedCount)
                    c = i < rpmCount ? ledColors[i] : Color.FromArgb(0, 0, 0);
                else
                {
                    int flagIdx = i - MozaDeviceConstants.RpmLedCount;
                    c = flagIdx < flagCount ? buttonColors[flagIdx] : Color.FromArgb(0, 0, 0);
                }

                if (_cm2ColorsInitialised
                    && _lastCm2Colors[i].R == c.R
                    && _lastCm2Colors[i].G == c.G
                    && _lastCm2Colors[i].B == c.B)
                    continue;

                string cmd = i < MozaDeviceConstants.RpmLedCount
                    ? $"cm2-indicator-color{i + 1}"
                    : $"cm2-flag-color{i - MozaDeviceConstants.RpmLedCount + 1}";
                plugin.DeviceManager.WriteColor(cmd, c.R, c.G, c.B);
                _lastCm2Colors[i] = c;
            }
            _cm2ColorsInitialised = true;
        }
    }
}
