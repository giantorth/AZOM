using System;
using System.Collections.Generic;
using System.Linq;
using GameReaderCommon.Enums;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device.MotorsWithFrequency;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers;
using SimHub.Plugins.DataPlugins.ShakeItV3.Settings;
using SimHub.Plugins.Devices;

namespace MozaPlugin.Integration
{
    /// <summary>
    /// ShakeIt Motors channels provider for the wheelbase LFE (cmd 0x2D/0x77,
    /// fw >= 1.2.10.10). The base runs three identical concurrent oscillators
    /// (wire ids 1/2/0) and sums them in firmware — "engine / ABS / gearshift" are
    /// just how the plugin's own LFE modes fill them, not distinct hardware. All
    /// three are exposed as generic ShakeIt channels the user can route effects to.
    /// SimHub's tone mixer calls <see cref="UpdateOutput"/> every data tick with the
    /// mixed per-channel (gain 0..1, frequency Hz); values are forwarded to
    /// <see cref="Devices.BaseLfeEffectWorker"/> through
    /// <see cref="MozaPlugin.Instance"/> so the worker stays the single wire owner.
    ///
    /// A NEW effect defaults to ONE enabled output (Oscillator 1). The base sums
    /// all three slots into one physical actuator, so enabling all three by
    /// default multiplied the output; the user opts into the others per effect.
    /// SimHub's <see cref="CreateDefaultActivationFor"/> hook has no channel index,
    /// so the default is seeded explicitly in <see cref="LoadDefaultPlatformSettings"/>.
    ///
    /// Installed over SimHub's own <c>StandardProtocolMotorsChannelsSettingsProvider</c>
    /// on the wheelbase device's Haptics section — see
    /// <see cref="Devices.Haptics.MozaBaseHapticsBridge.TryInstallChannelsProvider"/>.
    /// The declarative HapticsFeature path has no way to name a provider, and
    /// SimHub's default one enables EVERY channel on every new effect, which on a
    /// summing base is a silent 3× — so the swap is what keeps the defaults sane.
    ///
    /// Constructed by the bridge (and previously by SimHub via a generic new()
    /// constraint) — MUST stay public with a parameterless ctor, and MUST NOT
    /// touch plugin state at construction time (may precede plugin Init).
    /// </summary>
    public sealed class MozaWheelbaseLfeChannelsProvider : IShakeItChannelsInfoProvider
    {
        // Index order is the wire mapping the worker applies:
        // 0 → wire id 1, 1 → wire id 2, 2 → wire id 0. Three identical oscillators.
        private readonly List<ChannelInformation> _channels = new List<ChannelInformation>
        {
            new ChannelInformation { Name = "Oscillator 1" },
            new ChannelInformation { Name = "Oscillator 2" },
            new ChannelInformation { Name = "Oscillator 3" },
        };

        public string DefaultSettingsKey => "MozaWheelbaseLfe";

        public bool IsConnected => MozaPlugin.Instance?.IsBaseLfeHapticsReady == true;

        public List<ChannelInformation> GetChannels(MotorsWithFrequencyOutputManagerBase manager) => _channels;

        // Fallback default is OFF; the enabled channel(s) are seeded per new effect
        // in LoadDefaultPlatformSettings (this hook can't tell which channel it is).
        public ChannelActivation CreateDefaultActivationFor(FFBPlacement placement, MotorsWithFrequencyOutputManagerBase manager)
            => new ChannelActivation { IsEnabled = false };

        public void LoadDefaultPlatformSettings(EffectsContainerBase effectsContainerBase, ShakeItProfile shakeItProfile)
        {
            // Corner placements are meaningless on a single wheelbase — collapse
            // to mono when the effect supports it (mirrors SimHub's pedal providers).
            if (effectsContainerBase.EffectsAggregates.Any(i => i.Key == "Mono"))
                effectsContainerBase.AggregationMode = "Mono";

            // Seed the per-placement channel activations so a new effect drives
            // exactly Oscillator 1. GetSettings<T> returns the same persisted
            // instance the tone mixer reads, so this write sticks; the dicts are
            // public so no internal Get() is needed. Seeding every placement keeps
            // the default correct regardless of which placement the effect emits.
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
