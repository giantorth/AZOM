using System;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices.Led
{
    /// <summary>
    /// Wheel-base ambient LED strips (two physical strips on the base body). Strip
    /// length is per base model — 6 LEDs on an R16 Ultra, 9 on R21/R25/R27 — resolved
    /// at send time. SimHub's telemetry run is split: first half onto strip 0, second
    /// onto strip 1; per-LED colour chunks (cmd 0x1A) and a per-strip bitmask (cmd
    /// 0x1B, 4-byte LE u32), group 0x20 dev 0x12. Per-frame brightness uses SimHub's
    /// rpmBrightness; the firmware's stored brightness applies on top.
    /// See docs/protocol/leds/base-ambient-0x20-0x22.md.
    /// </summary>
    internal sealed class MozaBaseLedDeviceManager : MozaMaskLedDeviceManager
    {
        public MozaBaseLedDeviceManager() : base(2) { }

        // Via MozaData's latch, NOT BaseModelInfo(BaseModelName) — that string is
        // blanked by ClearWheelIdentity on rim swaps and transient reconnects, which
        // silently reverted this emitter to the 9-LED layout mid-session.
        protected override int LedsPerStrip
            => MozaPlugin.Instance?.Data?.ResolvedAmbientLedsPerStrip
               ?? BaseModelInfo.DefaultLedsPerStrip;

        protected override int StripCount => 2;

        protected override LedZone StripZone(int strip)
            => strip == 0 ? LedZone.BaseStrip0 : LedZone.BaseStrip1;

        protected override MozaDeviceManager? Pipe(MozaPlugin plugin) => plugin.DeviceManager;

        protected override bool LaneReady(MozaPlugin plugin)
            => plugin.Data.IsConnected && plugin.IsBaseAmbientLedSupported;

        /// <summary>
        /// Model token the owning device definition was written for ("R16"), or empty
        /// for the legacy shared definition. A leftover definition for a base the user
        /// no longer runs must not drive the attached base with the wrong geometry.
        /// </summary>
        internal string ExpectedModelPrefix { get; set; } = "";

        public override bool IsConnected()
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

        // The base has no serial number as such; its MCU UID is the closest real
        // identity and distinguishes two bases.
        public override string GetSerialNumber()
            => MozaPlugin.ScreenId(Haptics.MozaBaseConnectionManager.BaseSerialNumber());

        // The base's real firmware, as read at group 0x04 — not the driver's version.
        public override string GetFirmwareVersion() => MozaPlugin.Instance?.Data?.BaseFwVersionText ?? "";

        private static readonly string[] s_strip0Mask = { "base-ambient-send-rpm-strip0" };
        private static readonly string[] s_strip1Mask = { "base-ambient-send-rpm-strip1" };
        private static readonly LedMaskEncoding[] s_le4 = { LedMaskEncoding.ActiveLe4 };

        // Colours are cmd-0x1A chunks of at most 5 [idx, R, G, B] entries: chunk 1
        // carries LEDs 0..4, chunk 2 whatever remains (4 entries on a 9-LED strip,
        // 1 on the R16 Ultra's 6-LED strip). Chunk 2 must NOT be padded: with a
        // [0xFF, 0, 0, 0] filler present, bitmask=0x01 lit nothing on the base. The
        // R25 (2026-05-05) and R16 Ultra (2026-08-22) captures send it at exactly the
        // remaining LED count. Whole-strip rewrites (sparseColors: false) keep that
        // captured shape. The bitmask is a 4-byte LE u32 whatever the strip length.
        protected override LedZoneLayout? StripLayout(MozaDeviceManager dm, int strip, int ledsPerStrip)
        {
            var idx = new byte[ledsPerStrip];
            for (int i = 0; i < ledsPerStrip; i++) idx[i] = (byte)i;
            return dm.BuildLedLayout(
                strip == 0 ? "base-ambient-rpm-colors-strip0" : "base-ambient-rpm-colors-strip1",
                idx, strip == 0 ? s_strip0Mask : s_strip1Mask, s_le4,
                offViaMask: false, maskWithColors: false, sparseColors: false, lapseMs: 0);
        }
    }
}
