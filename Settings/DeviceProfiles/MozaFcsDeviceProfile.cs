using System.Windows.Controls;
using Newtonsoft.Json;
using SimHub.Plugins.ProfilesCommon;

namespace MozaPlugin.Settings
{
    /// <summary>
    /// FCS button-LED settings per game, in the FCS page's own profile store
    /// (<see cref="DeviceProfileRegistry.Fcs"/>, keyed by <c>MozaDeviceConstants.FcsGuid</c>).
    /// Axis modes / thresholds are device-stored and not part of the profile.
    /// -1 / null = leave the device's stored value alone.
    /// </summary>
    public class MozaFcsDeviceProfile
        : ProfileBase<MozaFcsDeviceProfile, MozaFcsDeviceProfileStore>,
          IProfile, IProfile<MozaFcsDeviceProfile, MozaFcsDeviceProfileStore>
    {
        [JsonIgnore]
        public override Control ProfileContentControl => null!;

        public int LightMode { get; set; } = -1;     // 0 off, 1 non-telemetry, 2 telemetry
        public int Brightness { get; set; } = -1;    // 0..100
        public int StandbyMode { get; set; } = -1;   // 0..6
        // Six packed RGB colours (sparse, -1 = unset).
        public int[]? Colors { get; set; }
        // Interval in ms indexed by standby effect 0..6 (2..6 used).
        public int[]? StandbyIntervals { get; set; }

        public override void CopyProfilePropertiesFrom(MozaFcsDeviceProfile p)
        {
            if (p == null) return;
            LightMode = p.LightMode;
            Brightness = p.Brightness;
            StandbyMode = p.StandbyMode;
            Colors = DeviceProfileCopy.Arr(p.Colors);
            StandbyIntervals = DeviceProfileCopy.Arr(p.StandbyIntervals);
        }

        /// <summary>Set one slot of a sparse packed array, growing it as needed.</summary>
        internal static int[] SetSlot(int[]? arr, int length, int index, int value)
        {
            if (arr == null || arr.Length < length)
            {
                var grown = new int[length];
                for (int i = 0; i < length; i++) grown[i] = arr != null && i < arr.Length ? arr[i] : -1;
                arr = grown;
            }
            arr[index] = value;
            return arr;
        }
    }

    public class MozaFcsDeviceProfileStore
        : ActiveProfileTrackingStore<MozaFcsDeviceProfile, MozaFcsDeviceProfileStore>,
          IProfileSettings<MozaFcsDeviceProfile>, IProfileSettings
    {
        public override string FileFilter => "Moza FCS profile (*.shmozafcs)|*.shmozafcs";

        public override void InitProfile(MozaFcsDeviceProfile p) { }
    }
}
