using System.Collections.Generic;
using System.Linq;
using GameReaderCommon.Enums;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device.MotorsWithFrequency;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers;
using SimHub.Plugins.DataPlugins.ShakeItV3.Settings;
using SimHub.Plugins.Devices;

namespace MozaPlugin.ShakeIt
{
    /// <summary>
    /// ShakeIt Motors channels provider for the wheelbase LFE: exposes the base's
    /// three summable oscillator slots (cmd 0x2D/0x77, fw >= 1.2.10.10) as ShakeIt
    /// channels. SimHub's tone mixer calls <see cref="UpdateOutput"/> every data
    /// tick with the mixed per-channel (gain 0..1, frequency Hz); values are
    /// forwarded to <see cref="Devices.BaseLfeEffectWorker"/> through
    /// <see cref="MozaPlugin.Instance"/> so the worker stays the single wire owner.
    ///
    /// Instantiated by SimHub via the generic new() constraint inside the
    /// reflection-constructed device instance (see <see cref="MozaShakeItDeviceRegistry"/>)
    /// — MUST stay public with a parameterless ctor, and MUST NOT touch plugin
    /// state at construction time (may precede plugin Init).
    /// </summary>
    public sealed class MozaWheelbaseLfeChannelsProvider : IShakeItChannelsInfoProvider
    {
        // Index order is the wire mapping the worker applies:
        // 0 → engine slot (id 1), 1 → ABS slot (id 2), 2 → gearshift slot (id 0).
        private readonly List<ChannelInformation> _channels = new List<ChannelInformation>
        {
            new ChannelInformation { Name = "Oscillator 1" },
            new ChannelInformation { Name = "Oscillator 2" },
            new ChannelInformation { Name = "Oscillator 3" },
        };

        public string DefaultSettingsKey => "MozaWheelbaseLfe";

        public bool IsConnected => MozaPlugin.Instance?.IsBaseLfeHapticsReady == true;

        public List<ChannelInformation> GetChannels(MotorsWithFrequencyOutputManagerBase manager) => _channels;

        public ChannelActivation CreateDefaultActivationFor(FFBPlacement placement, MotorsWithFrequencyOutputManagerBase manager)
            => new ChannelActivation { IsEnabled = true };

        public void LoadDefaultPlatformSettings(EffectsContainerBase effectsContainerBase, ShakeItProfile shakeItProfile)
        {
            // Corner placements are meaningless on a single wheelbase — collapse
            // to mono when the effect supports it (mirrors SimHub's pedal providers).
            if (effectsContainerBase.EffectsAggregates.Any(i => i.Key == "Mono"))
                effectsContainerBase.AggregationMode = "Mono";
        }

        public void UpdateOutput(Dictionary<int, ChannelValue> values)
        {
            var plugin = MozaPlugin.Instance;
            if (plugin == null) return;
            double g0 = 0, f0 = 0, g1 = 0, f1 = 0, g2 = 0, f2 = 0;
            if (values != null)
            {
                if (values.TryGetValue(0, out var c0) && c0 != null) { g0 = c0.Gain; f0 = c0.Frequency; }
                if (values.TryGetValue(1, out var c1) && c1 != null) { g1 = c1.Gain; f1 = c1.Frequency; }
                if (values.TryGetValue(2, out var c2) && c2 != null) { g2 = c2.Gain; f2 = c2.Frequency; }
            }
            plugin.PostShakeItLfeChannels(g0, f0, g1, f1, g2, f2);
        }

        public void Stop() => MozaPlugin.Instance?.ClearShakeItLfeChannels();

        // Capture-verified oscillator band: ABS runs down to 5 Hz; the wire freq
        // field saturates at 200 Hz.
        public FrequencyRange HardwareFrequencyRange() => new FrequencyRange(5, 200);

        public void SetSettings(ShakeItSettings shakeItSettings) { }

        public IEnumerable<DeviceSettingControl> GetSettingsControls() { yield break; }
    }
}
