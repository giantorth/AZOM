using System;
using System.Drawing;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices.Led
{
    /// <summary>
    /// FCS button LEDs (six, SimHub's buttons channel) driven live while the panel's
    /// light mode is Telemetry. Wire: colours <c>4E 01 08 [idx R G B]…</c> and mask
    /// <c>4E 01 09 [lit] [owned]</c> on the FCS's own pipe; owned = all six while
    /// driving, released with owned = 0. See docs/protocol/devices/fcs.md.
    /// </summary>
    internal sealed class MozaFcsLedDeviceManager : MozaMaskLedDeviceManager
    {
        private const int AllLeds = (1 << MozaData.FcsState.LedCount) - 1;

        public MozaFcsLedDeviceManager() : base(1) { }

        protected override int StripCount => 1;
        protected override int LedsPerStrip => MozaData.FcsState.LedCount;
        protected override LedZone StripZone(int strip) => LedZone.Fcs;
        protected override int DriveWindow(int strip) => AllLeds;
        // Own USB link: a wheel dashboard upload doesn't contend with it.
        protected override bool HonorsUploadStandDown => false;

        protected override Color[] SourceColors(Color[] leds, Color[] buttons) => buttons;
        protected override double SourceBrightness(double rpmBrightness, double buttonsBrightness) => buttonsBrightness;

        protected override MozaDeviceManager? Pipe(MozaPlugin plugin) => plugin.DetectionState.FcsOwner;

        // Live frames only show in Telemetry light mode (2).
        protected override bool LaneReady(MozaPlugin plugin)
            => plugin.DetectionState.FcsDetected && plugin.Data.Fcs.LightMode == 2;

        public override bool IsConnected() => MozaPlugin.Instance?.DetectionState.FcsDetected ?? false;

        public override string GetSerialNumber()
        {
            var uid = MozaPlugin.Instance?.Data?.Fcs.McuUid;
            return uid == null || uid.Length == 0 ? "" : MozaPlugin.ScreenId(BitConverter.ToString(uid).Replace("-", ""));
        }

        public override string GetFirmwareVersion() => MozaPlugin.Instance?.Data?.Fcs.FwVersionText ?? "";

        private static readonly byte[] s_index = { 0, 1, 2, 3, 4, 5 };
        private static readonly string[] s_mask = { "fcs-live-mask" };
        private static readonly LedMaskEncoding[] s_u8 = { LedMaskEncoding.ActiveWindowU8 };

        // Five [idx R G B] entries per chunk gives Pit House's 5 + 1 framing; unlit
        // LEDs keep their colour entry (offViaMask false), as Pit House sends them.
        protected override LedZoneLayout? StripLayout(MozaDeviceManager dm, int strip, int ledsPerStrip)
            => dm.BuildLedLayout("fcs-live-colors", s_index, s_mask, s_u8,
                offViaMask: false, maskWithColors: false, sparseColors: true, lapseMs: 0);
    }
}
