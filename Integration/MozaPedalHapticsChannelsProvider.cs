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
    /// ShakeIt Motors channels provider for the S12 pedal vibration module —
    /// three independent motors, one per pedal, selected by the pedal byte of
    /// the group-0x4D vibration command. SimHub's tone mixer calls
    /// <see cref="UpdateOutput"/> each data tick with the mixed per-pedal
    /// (gain 0..1, frequency Hz); the values are forwarded through
    /// <see cref="MozaPlugin.Instance"/> so
    /// <see cref="Devices.PedalHaptics.PedalHapticsEffectWorker"/> stays the
    /// single wire owner.
    ///
    /// Compare <see cref="MozaWheelbaseLfeChannelsProvider"/>, which this is
    /// otherwise modelled on. That one forces a new effect to a single enabled
    /// channel, because the wheelbase SUMS its three oscillators into one
    /// physical actuator and enabling all three is a silent 3×. These three
    /// channels are separate motors on separate pedals, so there is nothing to
    /// sum and the stock all-enabled default is left alone — the user turns off
    /// the pedals an effect should not reach.
    ///
    /// Installed over SimHub's own <c>StandardProtocolMotorsChannelsSettingsProvider</c>
    /// by <see cref="Devices.Haptics.MozaPedalHapticsBridge.TryInstallChannelsProvider"/>;
    /// the declarative HapticsFeature path has no way to name a provider, and
    /// without the swap the three channels show up as "Motor 1/2/3" with no clue
    /// which pedal is which.
    ///
    /// Constructed by the bridge — MUST stay public with a parameterless ctor,
    /// and MUST NOT touch plugin state at construction time (it may be built
    /// before plugin Init).
    /// </summary>
    public sealed class MozaPedalHapticsChannelsProvider : IShakeItChannelsInfoProvider
    {
        // Index order IS the wire mapping and is a user-visible contract:
        // MozaPedalHapticsProtocol maps channel i to a (pedal, slot) pair, and
        // reordering this list silently moves every effect a user already
        // assigned. Pedal-major, so a pedal's nine slots sit together in the
        // ShakeIt list: 0-8 throttle, 9-17 brake, 18-26 clutch.
        private readonly List<ChannelInformation> _channels = BuildChannels();

        private static List<ChannelInformation> BuildChannels()
        {
            var list = new List<ChannelInformation>(MozaPedalHapticsProtocol.ChannelCount);
            for (int i = 0; i < MozaPedalHapticsProtocol.ChannelCount; i++)
                list.Add(new ChannelInformation { Name = MozaPedalHapticsProtocol.ChannelName(i) });
            return list;
        }

        public string DefaultSettingsKey => "MozaPedalHaptics";

        public bool IsConnected => MozaPlugin.Instance?.IsPedalHapticsReady == true;

        public List<ChannelInformation> GetChannels(MotorsWithFrequencyOutputManagerBase manager) => _channels;

        // Separate actuators, so the stock all-enabled default is correct here.
        public ChannelActivation CreateDefaultActivationFor(FFBPlacement placement, MotorsWithFrequencyOutputManagerBase manager)
            => new ChannelActivation { IsEnabled = true };

        public void LoadDefaultPlatformSettings(EffectsContainerBase effectsContainerBase, ShakeItProfile shakeItProfile)
        {
            // Corner placements mean nothing on a pedal set — three pedals are not
            // four wheels — so collapse to mono where the effect allows it, the
            // same as SimHub's own pedal providers. Channel activation is left at
            // SimHub's defaults on purpose; see the class remarks.
            if (effectsContainerBase.EffectsAggregates.Any(i => i.Key == "Mono"))
                effectsContainerBase.AggregationMode = "Mono";
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
                plugin.PostShakeItPedalHapticsChannel(i, gain, freq);
            }
        }

        public void Stop() => MozaPlugin.Instance?.ClearShakeItPedalHaptics();

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
