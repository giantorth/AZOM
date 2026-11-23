using System;
using MozaPlugin.Devices;
using MozaPlugin.Devices.Extensions;
using MozaPlugin.Settings;

namespace MozaPlugin.Hardware
{
    /// <summary>
    /// FCS (farming control panel) writes. LED settings follow the FCS device profile
    /// and reach the device through the shared readback reconcile (every 0x4E write is
    /// an EEPROM commit); axis modes, thresholds and calibration are written on user
    /// action only. Wire format: docs/protocol/devices/fcs.md.
    /// </summary>
    internal sealed partial class HardwareApplier
    {
        private const string FcsTag = "fcs";

        // Profile-backed settings, by base command name (the -get/-set suffix dropped
        // so a readback and a write share one cache key).
        private static readonly string[] s_fcsProfileKeys =
        {
            "fcs-light-mode", "fcs-brightness", "fcs-standby-mode",
            "fcs-led-color0", "fcs-led-color1", "fcs-led-color2",
            "fcs-led-color3", "fcs-led-color4", "fcs-led-color5",
            "fcs-standby-interval2", "fcs-standby-interval3", "fcs-standby-interval4",
            "fcs-standby-interval5", "fcs-standby-interval6",
        };

        private MozaDeviceManager FcsManager => _detectionState.FcsOwner ?? _deviceManager;

        private static string FcsKey(string name) =>
            name.EndsWith("-get", StringComparison.Ordinal) || name.EndsWith("-set", StringComparison.Ordinal)
                ? name.Substring(0, name.Length - 4) : name;

        private static bool IsFcsProfileKey(string key) => Array.IndexOf(s_fcsProfileKeys, key) >= 0;

        /// <summary>Detect-time: profile LED settings land as their readbacks arrive,
        /// and only where they differ.</summary>
        internal void ArmFcsConnectApply() => s_readbackCfg.Arm(FcsTag, s_fcsProfileKeys);

        /// <summary>FCS readback (serial dispatch thread): adopt the device value and, if
        /// its connect-time reconcile is outstanding, push the profile's value.</summary>
        internal void PrimeFcsCfgFromDevice(string? name, int intValue, byte[]? arrayValue)
        {
            if (name == null) return;
            string key = FcsKey(name);
            if (!IsFcsProfileKey(key)) return;
            long deviceValue;
            if (arrayValue != null)
            {
                if (arrayValue.Length < 3) return;
                deviceValue = (arrayValue[0] << 16) | (arrayValue[1] << 8) | arrayValue[2];
            }
            else
            {
                if (intValue < 0) return;
                deviceValue = intValue;
            }
            if (!s_readbackCfg.Prime(FcsTag, key, deviceValue)) return;
            var profile = _plugin.ActiveFcsProfile;
            if (profile != null && ProfileWritesAllowed) ApplyFcsSetting(profile, key);
        }

        /// <summary>Write the active FCS LED profile (sentinel-guarded, change-gated).</summary>
        public void ApplyFcsToHardware()
        {
            var profile = _plugin.ActiveFcsProfile;
            if (profile == null || !ProfileWritesAllowed) return;
            foreach (var key in s_fcsProfileKeys)
                ApplyFcsSetting(profile, key);
        }

        private void ApplyFcsSetting(MozaFcsDeviceProfile profile, string key)
        {
            var d = _data.Fcs;
            long value;
            int idx;
            if (key.StartsWith("fcs-led-color", StringComparison.Ordinal))
            {
                idx = key[key.Length - 1] - '0';
                int c = profile.Colors != null && idx < profile.Colors.Length ? profile.Colors[idx] : -1;
                if (c < 0) return;
                d.Colors[idx] = c;
                value = c;
            }
            else if (key.StartsWith("fcs-standby-interval", StringComparison.Ordinal))
            {
                idx = key[key.Length - 1] - '0';
                int ms = profile.StandbyIntervals != null && idx < profile.StandbyIntervals.Length ? profile.StandbyIntervals[idx] : -1;
                if (ms < 0) return;
                d.StandbyIntervals[idx] = ms;
                value = ms;
            }
            else
            {
                int v = key == "fcs-light-mode" ? profile.LightMode
                      : key == "fcs-brightness" ? profile.Brightness
                      : profile.StandbyMode;
                if (v < 0) return;
                if (key == "fcs-light-mode") d.LightMode = v;
                else if (key == "fcs-brightness") d.Brightness = v;
                else d.StandbyMode = v;
                value = v;
            }

            if (!_detectionState.FcsDetected) return;
            if (s_readbackCfg.Pending(FcsTag, key, () =>
                    MozaLog.Info("[AZOM] FCS has not answered any settings read — holding back its profile writes")))
                return;
            if (s_readbackCfg.Changed(FcsTag, key, value)) WriteFcsRaw(key, value);
        }

        private void WriteFcsRaw(string key, long value)
        {
            if (key.StartsWith("fcs-led-color", StringComparison.Ordinal))
                FcsManager.WriteArray(key + "-set",
                    new[] { (byte)(value >> 16), (byte)(value >> 8), (byte)value });
            else
                FcsManager.WriteSetting(key + "-set", (int)value);
        }

        /// <summary>UI write of an int setting, by base name (e.g. <c>fcs-brightness</c>).</summary>
        public void WriteIfFcsDetected(string key, int value)
        {
            if (value < 0 || !_detectionState.FcsDetected) return;
            if (IsFcsProfileKey(key)) s_readbackCfg.NoteUserWrite(FcsTag, key, value);
            FcsManager.WriteSetting(key + "-set", value);
        }

        /// <summary>UI write of LED <paramref name="index"/>'s stored colour.</summary>
        public void WriteFcsColorIfDetected(int index, byte r, byte g, byte b)
        {
            if (!_detectionState.FcsDetected || index < 0 || index >= MozaData.FcsState.LedCount) return;
            string key = "fcs-led-color" + index;
            s_readbackCfg.NoteUserWrite(FcsTag, key, (r << 16) | (g << 8) | b);
            FcsManager.WriteArray(key + "-set", new[] { r, g, b });
        }

        /// <summary>Calibration control: <paramref name="control"/> 1 thumbstick, 2 joystick
        /// XY, 3 throttle, 4 joystick Z; <paramref name="action"/> 1 start, 0 stop, 2 cancel.</summary>
        public void FcsCalibrate(byte control, byte action)
        {
            if (!_detectionState.FcsDetected) return;
            FcsManager.WriteArray("fcs-calibrate", new[] { control, action });
        }

        /// <summary>Tracked read on the FCS pipe (calibration result checks).</summary>
        public void ReadFcs(string command)
        {
            if (_detectionState.FcsDetected) FcsManager.ReadSetting(command);
        }

        public void ApplyFcsExtensionSettings(MozaFcsExtensionSettings extSettings)
        {
            var profile = _plugin.ActiveFcsProfile;
            extSettings.ApplyTo(_data, profile);
            ApplyFcsToHardware();
            _plugin.PersistSettings();
        }
    }
}
