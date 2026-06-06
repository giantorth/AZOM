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
                    // CM2: LEDs are FIRMWARE-DRIVEN. A real-CM2 wire-trace
                    // (prueba1.pcapng) shows PitHouse never sends the wheel
                    // RPM-bar per-LED commands (group 0x3F) to dev=0x12 — the CM2
                    // ignores them. It drives its LEDs from the stored group-0x32
                    // config (colours + RPM thresholds + cm2-rpm-group-mode=1,
                    // sent by HardwareApplier on apply) fed by the tier-def
                    // telemetry session (Rpm value frames). So the per-frame LED
                    // push here is a NO-OP for the CM2 — the disproven
                    // SendCm2LiveLedFrame path is retired.
                    //
                    // The capture ALSO carries a group-0x24 → dev 0x12 keyed
                    // value stream (`<id> <BE float32>`) that likely feeds the
                    // CM2 screen widgets; its key→channel map is NOT derivable
                    // from that capture (it was a static 20/40/60/80/100 test
                    // pattern), so wiring it blind is deferred until an in-game
                    // CM2 capture pins the keys. See
                    // docs/protocol/devices/main-hub-0x12.md.
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
        /// Per-frame live LED emission for standalone CM2 via the wheel
        /// RPM-bar command family retargeted to dev=0x12 (CM2 bridge/main).
        ///
        /// Emits:
        ///  • <c>wheel-telemetry-rpm-colors</c> chunks when at least one of
        ///    the 16 logical color slots changed since the last frame
        ///    (per-LED [idx, R, G, B] in 5-LED 20-byte chunks);
        ///  • <c>wheel-send-rpm-telemetry</c> 4-byte bitmask per frame
        ///    whenever the bitmask differs from the last sent value (or
        ///    on every frame if <c>AlwaysResendBitmask</c> is set).
        ///
        /// Color slot mapping: RPM colors 1–10 → LED positions 1–10; flag
        /// colors 1–6 → LED positions 11–16. Matches the CM2 physical
        /// layout (logical 1–3 left, 4–13 top, 14–16 right) in the same
        /// order used by the cm2-stored-color writes in HardwareApplier.
        /// </summary>
        private void SendCm2LiveLedFrame(MozaPlugin plugin,
            Color[] ledColors, Color[] buttonColors,
            int bitmask, bool alwaysResendBitmask,
            int rpmCount, int flagCount)
        {
            // Build the 16-slot color array from the SimHub RPM + button arrays.
            var current = new Color[Cm2LedCount];
            for (int i = 0; i < Cm2LedCount; i++)
            {
                if (i < rpmCount)
                    current[i] = ledColors[i];
                else
                {
                    int flagIdx = i - rpmCount;
                    if (flagIdx < flagCount)
                        current[i] = buttonColors[flagIdx];
                    else
                        current[i] = Color.FromArgb(0, 0, 0);
                }
            }

            // Resend colors only when at least one slot changed (or first frame).
            bool colorsChanged = !_cm2ColorsInitialised;
            if (!colorsChanged)
            {
                for (int i = 0; i < Cm2LedCount; i++)
                {
                    if (current[i].R != _lastCm2Colors[i].R
                        || current[i].G != _lastCm2Colors[i].G
                        || current[i].B != _lastCm2Colors[i].B)
                    {
                        colorsChanged = true;
                        break;
                    }
                }
            }

            if (colorsChanged)
            {
                // 4 bytes per LED, padded to 20-byte chunks. 16 LEDs → 64 bytes
                // → 4 chunks of 20 (last chunk has 4 padding entries marked
                // 0xFF index to avoid being interpreted as "LED 0 off").
                int dataLen = Cm2LedCount * 4;
                int bufferLen = ((dataLen + 19) / 20) * 20;
                var colorData = new byte[bufferLen];
                for (int i = 0; i < Cm2LedCount; i++)
                {
                    int o = i * 4;
                    colorData[o] = (byte)i;
                    colorData[o + 1] = current[i].R;
                    colorData[o + 2] = current[i].G;
                    colorData[o + 3] = current[i].B;
                }
                for (int pos = dataLen; pos < bufferLen; pos += 4)
                    colorData[pos] = 0xFF;

                var chunk = new byte[20];
                for (int pos = 0; pos < bufferLen; pos += 20)
                {
                    Array.Copy(colorData, pos, chunk, 0, 20);
                    plugin.DeviceManager.WriteArrayForDevice(
                        "wheel-telemetry-rpm-colors",
                        MozaProtocol.DeviceMain,
                        chunk);
                }

                for (int i = 0; i < Cm2LedCount; i++)
                    _lastCm2Colors[i] = current[i];
                _cm2ColorsInitialised = true;
            }

            if (alwaysResendBitmask || bitmask != _lastBitmask)
            {
                _lastBitmask = bitmask;
                // CM2 (dev 0x12) RPM LEDs: 4-byte active-mask form (CM2 has 16 LEDs).
                // This is a separate device from the wheel (dev 0x17) and is left
                // unchanged by the wheel-LED 8-byte active+window work.
                var bitmaskBytes = new byte[]
                {
                    (byte)(bitmask & 0xFF),
                    (byte)((bitmask >> 8) & 0xFF),
                    (byte)((bitmask >> 16) & 0xFF),
                    (byte)((bitmask >> 24) & 0xFF),
                };
                plugin.DeviceManager.WriteArrayForDevice(
                    "wheel-send-rpm-telemetry",
                    MozaProtocol.DeviceMain,
                    bitmaskBytes);
            }
        }
    }
}
