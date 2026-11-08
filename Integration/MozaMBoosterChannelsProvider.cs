using System.Collections.Generic;
using System.Linq;
using GameReaderCommon.Enums;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device.MotorsWithFrequency;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers;
using SimHub.Plugins.DataPlugins.ShakeItV3.Settings;
using SimHub.Plugins.Devices;
using MozaPlugin.Devices.MBooster;
using MozaPlugin.Protocol;

namespace MozaPlugin.Integration
{
    /// <summary>
    /// ShakeIt Motors channels provider for the mBooster pedals device: one
    /// channel per pedal role (Throttle / Brake / Clutch), each one oscillator on
    /// whichever pedal holds that role. The tone mixer sums an effect set into one
    /// tone per channel, which suits the motor: it plays one waveform at a time.
    ///
    /// Constructed by the bridge — MUST stay public, parameterless, and MUST NOT
    /// touch plugin state at construction time.
    /// </summary>
    public sealed class MozaMBoosterChannelsProvider : IShakeItChannelsInfoProvider
    {
        internal const int ChannelCount = 3;

        private static readonly string[] ChannelNames = { "Throttle", "Brake", "Clutch" };

        private readonly List<ChannelInformation> _channels;

        public MozaMBoosterChannelsProvider()
        {
            _channels = new List<ChannelInformation>(ChannelCount);
            foreach (var name in ChannelNames)
                _channels.Add(new ChannelInformation { Name = name });
        }

        public string DefaultSettingsKey => "MozaMBooster";

        public bool IsConnected => MozaPlugin.Instance?.IsMBoosterShakeItActive == true;

        public List<ChannelInformation> GetChannels(MotorsWithFrequencyOutputManagerBase manager) => _channels;

        // Every pedal on by default, as SimHub's stock providers do; the user
        // unticks the pedals an effect should not play on.
        public ChannelActivation CreateDefaultActivationFor(FFBPlacement placement, MotorsWithFrequencyOutputManagerBase manager)
            => new ChannelActivation { IsEnabled = true };

        public void LoadDefaultPlatformSettings(EffectsContainerBase effectsContainerBase, ShakeItProfile shakeItProfile)
        {
            // Corner placements mean nothing on a pedal motor.
            if (effectsContainerBase.EffectsAggregates.Any(i => i.Key == "Mono"))
                effectsContainerBase.AggregationMode = "Mono";

            // Bite Point is a clutch effect, as on the plugin tab.
            if (effectsContainerBase is MozaClutchBitePointContainer)
                SeedSingleChannel(effectsContainerBase, MBoosterDeviceController.RoleIndexOf(MBoosterRole.Clutch));
        }

        private static void SeedSingleChannel(EffectsContainerBase container, int channel)
        {
            var activation = container.SettingsStore.GetSettings<DeviceChannelActivationSettings>();
            foreach (var effect in container.Effects)
            {
                if (!activation.Channels.TryGetValue(effect.Placement, out var placement))
                    activation.Channels[effect.Placement] = placement = new PlacementChannelsActivation();
                for (int ch = 0; ch < ChannelCount; ch++)
                    placement.Channels[ch] = new ChannelActivation { IsEnabled = ch == channel };
            }
        }

        public void UpdateOutput(Dictionary<int, ChannelValue> values)
        {
            var plugin = MozaPlugin.Instance;
            if (plugin == null) return;

            for (int role = 0; role < ChannelCount; role++)
            {
                double gain = 0, freq = 0;
                if (values != null && values.TryGetValue(role, out var c) && c != null)
                {
                    gain = c.Gain;
                    freq = c.Frequency;
                }
                plugin.PostShakeItMBooster(role, gain, freq);
            }
        }

        public void Stop() => MozaPlugin.Instance?.ClearShakeItMBooster();

        public FrequencyRange HardwareFrequencyRange()
            => new FrequencyRange(MozaMBoosterProtocol.ShakeItMinFrequencyHz, MozaMBoosterProtocol.ShakeItMaxFrequencyHz);

        public void SetSettings(ShakeItSettings shakeItSettings) { }

        public IEnumerable<DeviceSettingControl> GetSettingsControls() { yield break; }
    }
}
