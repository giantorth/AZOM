using MozaPlugin.Settings;

namespace MozaPlugin.Devices.Extensions
{
    /// <summary>
    /// FCS button-LED settings stored in SimHub device profiles (per game), via the
    /// extension's GetSettings()/SetSettings(). -1 / null = not included.
    /// Colours packed R&lt;&lt;16 | G&lt;&lt;8 | B.
    /// </summary>
    public class MozaFcsExtensionSettings
    {
        public int LightMode { get; set; } = -1;
        public int Brightness { get; set; } = -1;
        public int StandbyMode { get; set; } = -1;
        public int[]? Colors { get; set; }
        public int[]? StandbyIntervals { get; set; }

        /// <summary>Capture from the FCS profile, falling back to what the device reported.</summary>
        public void CaptureFromCurrent(MozaData data, MozaFcsDeviceProfile? profile)
        {
            var d = data.Fcs;
            int Pick(int p, int dev) => p >= 0 ? p : dev;
            LightMode = Pick(profile?.LightMode ?? -1, d.LightMode);
            Brightness = Pick(profile?.Brightness ?? -1, d.Brightness);
            StandbyMode = Pick(profile?.StandbyMode ?? -1, d.StandbyMode);
            Colors = Merge(profile?.Colors, d.Colors);
            StandbyIntervals = Merge(profile?.StandbyIntervals, d.StandbyIntervals);
        }

        /// <summary>Write these values into the profile and mirror them into
        /// <paramref name="data"/>. Does not touch hardware.</summary>
        public void ApplyTo(MozaData data, MozaFcsDeviceProfile? profile)
        {
            var d = data.Fcs;
            if (LightMode >= 0) d.LightMode = LightMode;
            if (Brightness >= 0) d.Brightness = Brightness;
            if (StandbyMode >= 0) d.StandbyMode = StandbyMode;
            CopySparse(Colors, d.Colors);
            CopySparse(StandbyIntervals, d.StandbyIntervals);

            if (profile == null) return;
            if (LightMode >= 0) profile.LightMode = LightMode;
            if (Brightness >= 0) profile.Brightness = Brightness;
            if (StandbyMode >= 0) profile.StandbyMode = StandbyMode;
            if (Colors != null) profile.Colors = (int[])Colors.Clone();
            if (StandbyIntervals != null) profile.StandbyIntervals = (int[])StandbyIntervals.Clone();
        }

        private static int[] Merge(int[]? profile, int[] device)
        {
            var r = new int[device.Length];
            for (int i = 0; i < r.Length; i++)
                r[i] = profile != null && i < profile.Length && profile[i] >= 0 ? profile[i] : device[i];
            return r;
        }

        private static void CopySparse(int[]? src, int[] dst)
        {
            if (src == null) return;
            for (int i = 0; i < src.Length && i < dst.Length; i++)
                if (src[i] >= 0) dst[i] = src[i];
        }
    }
}
