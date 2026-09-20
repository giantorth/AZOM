using System;
using System.Collections.Generic;
using System.Linq;
using GameReaderCommon.Enums;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device.MotorsWithFrequency;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers;
using SimHub.Plugins.DataPlugins.ShakeItV3.Settings;
using SimHub.Plugins.Devices;
using MozaPlugin.Protocol;

namespace MozaPlugin.Integration
{
    /// <summary>
    /// ShakeIt Motors channels provider for <b>one pedal</b> of an S12 module.
    /// Each pedal port is its own SimHub device, so each gets its own provider
    /// instance and its own ShakeIt profile — grouping all three into a single
    /// device made the channel list long and forced unrelated pedals to share
    /// one set of effect defaults.
    ///
    /// The channels are numbered, not named after the firmware's slot labels.
    /// Those labels (ABS, Lockup, Gear Shift…) describe nothing the hardware
    /// actually does — every slot produces the same vibration — and with
    /// round-robin allocation a channel does not own a fixed slot anyway. More
    /// channels are offered than the module has oscillators, because the user
    /// can build as many ShakeIt effects as they like;
    /// <see cref="Devices.PedalHaptics.PedalHapticsEffectWorker"/> packs whichever
    /// are live into the eight real slots and shares them past that.
    ///
    /// Road Texture is the exception and keeps its own named channel: it drives a
    /// suspension position rather than a tone, so it cannot take part in the pool.
    ///
    /// Constructed by the bridge — MUST stay public and constructible with no
    /// arguments (the pedal parameter is optional for exactly that reason), and
    /// MUST NOT touch plugin state at construction time.
    /// </summary>
    public sealed class MozaPedalHapticsChannelsProvider : IShakeItChannelsInfoProvider
    {
        private readonly byte _pedal;
        private readonly int _pedalIndex;
        private readonly List<ChannelInformation> _channels;

        public MozaPedalHapticsChannelsProvider(
            byte pedal = (byte)PedalHapticsPedal.Throttle)
        {
            _pedal = pedal;
            _pedalIndex = pedal - MozaPedalHapticsProtocol.MinPedal;
            _channels = BuildChannels();
        }

        private static List<ChannelInformation> BuildChannels()
        {
            var list = new List<ChannelInformation>(MozaPedalHapticsProtocol.ChannelsPerPedal);
            for (int i = 0; i < MozaPedalHapticsProtocol.ChannelsPerPedal; i++)
                list.Add(new ChannelInformation { Name = MozaPedalHapticsProtocol.ChannelName(i) });
            return list;
        }

        /// <summary>Which pedal this instance drives — lets the bridge spot a provider
        /// left behind by a different pedal's device and replace it.</summary>
        public byte PedalId => _pedal;

        /// <summary>
        /// Per-pedal so each device keeps its own effect defaults; a shared key
        /// would have all three pedals overwrite each other's.
        /// </summary>
        public string DefaultSettingsKey
            => "MozaPedalHaptics" + MozaPedalHapticsProtocol.PedalLabel(_pedal);

        public bool IsConnected => MozaPlugin.Instance?.IsPedalHapticsReady == true;

        public List<ChannelInformation> GetChannels(MotorsWithFrequencyOutputManagerBase manager) => _channels;

        // Channels are allocated to hardware on demand, so leaving a new effect
        // enabled on all of them would burn the whole pool on one effect.
        public ChannelActivation CreateDefaultActivationFor(FFBPlacement placement, MotorsWithFrequencyOutputManagerBase manager)
            => new ChannelActivation { IsEnabled = false };

        public void LoadDefaultPlatformSettings(EffectsContainerBase effectsContainerBase, ShakeItProfile shakeItProfile)
        {
            // Corner placements mean nothing on a single pedal motor — collapse to
            // mono where the effect allows it, as SimHub's own pedal providers do.
            if (effectsContainerBase.EffectsAggregates.Any(i => i.Key == "Mono"))
                effectsContainerBase.AggregationMode = "Mono";

            // Seed one enabled channel so a new effect does something immediately
            // without claiming every slot. The hook has no channel index, so the
            // activations are written per placement here.
            var activation = effectsContainerBase.SettingsStore.GetSettings<DeviceChannelActivationSettings>();
            foreach (FFBPlacement placement in Enum.GetValues(typeof(FFBPlacement)))
            {
                if (!activation.Channels.TryGetValue(placement, out var pca))
                {
                    pca = new PlacementChannelsActivation();
                    activation.Channels[placement] = pca;
                }
                for (int ch = 0; ch < _channels.Count; ch++)
                    pca.Channels[ch] = new ChannelActivation { IsEnabled = ch == 0 };
            }
        }

        public void UpdateOutput(Dictionary<int, ChannelValue> values)
        {
            var plugin = MozaPlugin.Instance;
            if (plugin == null) return;

            for (int i = 0; i < _channels.Count; i++)
            {
                double gain = 0, freq = 0;
                if (values != null && values.TryGetValue(i, out var c) && c != null)
                {
                    gain = c.Gain;
                    freq = c.Frequency;
                }
                plugin.PostShakeItPedalHapticsChannel(_pedalIndex, i, gain, freq);
            }
        }

        public void Stop() => MozaPlugin.Instance?.ClearShakeItPedalHaptics(_pedalIndex);

        /// <summary>
        /// Band advertised to the ShakeIt tone mixer. The unit accepts 10-100 Hz
        /// and clamps outside it, so there is no point offering the user more.
        /// </summary>
        public FrequencyRange HardwareFrequencyRange()
            => new FrequencyRange(MozaPedalHapticsProtocol.MinFrequencyHz, MozaPedalHapticsProtocol.MaxFrequencyHz);

        public void SetSettings(ShakeItSettings shakeItSettings) { }

        public IEnumerable<DeviceSettingControl> GetSettingsControls() { yield break; }
    }
}
