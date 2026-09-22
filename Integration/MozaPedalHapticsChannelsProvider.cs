using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

        // Rotation cursor per pedal. Static because SimHub rebuilds the provider
        // whenever it re-creates an output manager, and the rotation has to
        // survive that or every reload starts assigning at oscillator 1 again.
        private static readonly int[] _nextChannel = new int[MozaPedalHapticsProtocol.PedalCount];


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

        /// <summary>
        /// The channel list SimHub sees — and deliberately not the same list in
        /// both directions.
        ///
        /// Only two things in SimHub call this, and they are cleanly separated by
        /// thread: the tone mixer (<c>MotorsWithFrequencyOutputManager.UpdateOutput</c>,
        /// data thread, every tick) and the per-effect checkbox list
        /// (<c>MotorsWithFrequencyOutputManagerEffectsChannelsModel.BuildOrUpdateModel</c>,
        /// WPF thread, once when an effect's settings control is built). The 10 Hz
        /// preview timer only calls <c>UpdateEffectsPreview</c> and never reaches
        /// the mixer, so nothing drives output from the UI thread.
        ///
        /// So the mixer is handed all eight oscillators, and the UI is handed
        /// none. Effects still get spread across the hardware — the routing is
        /// decided in <see cref="LoadDefaultPlatformSettings"/> — but the user is
        /// never shown a channel grid to fill in, which is the whole point: the
        /// oscillator an effect lands on is an implementation detail.
        ///
        /// Internal code must use <c>_channels</c> directly, never this: it runs
        /// on both threads and would otherwise see an empty list.
        /// </summary>
        public List<ChannelInformation> GetChannels(MotorsWithFrequencyOutputManagerBase manager)
            => IsUiThread ? HiddenFromUi : _channels;

        private static readonly List<ChannelInformation> HiddenFromUi = new List<ChannelInformation>();

        private static bool IsUiThread
        {
            get
            {
                try { return System.Windows.Application.Current?.Dispatcher?.CheckAccess() == true; }
                catch { return false; }   // no WPF app (tests, headless) — treat as the mixer
            }
        }
        // Never enabled by default: LoadDefaultPlatformSettings picks the one
        // channel a new effect lands on, and this hook has no channel index to
        // decide with. Returning true here is what makes SimHub tick every box
        // on every effect and put them all on one oscillator.
        public ChannelActivation CreateDefaultActivationFor(FFBPlacement placement, MotorsWithFrequencyOutputManagerBase manager)
            => new ChannelActivation { IsEnabled = false };

        /// <summary>
        /// Called by SimHub when an effect is created — which is where the
        /// round-robin lives. Each new effect is assigned the next oscillator and
        /// wraps once all eight are spoken for, so effects spread across the
        /// hardware's own mixer at their own frequencies instead of collapsing
        /// into one tone, and the user never opens the channel list to do it.
        ///
        /// Sharing after a wrap is harmless: ShakeIt sums the effects on a
        /// channel before the value reaches us, and the oscillators are
        /// interchangeable.
        /// </summary>
        public void LoadDefaultPlatformSettings(EffectsContainerBase effectsContainerBase, ShakeItProfile shakeItProfile)
        {
            // Corner placements mean nothing on a single pedal motor — collapse to
            // mono where the effect allows it, as SimHub's own pedal providers do.
            if (effectsContainerBase.EffectsAggregates.Any(i => i.Key == "Mono"))
                effectsContainerBase.AggregationMode = "Mono";

            int channel = MozaPedalHapticsProtocol.RotateChannel(
                Interlocked.Increment(ref _nextChannel[_pedalIndex]) - 1);

            var activation = effectsContainerBase.SettingsStore.GetSettings<DeviceChannelActivationSettings>();
            foreach (FFBPlacement placement in Enum.GetValues(typeof(FFBPlacement)))
            {
                if (!activation.Channels.TryGetValue(placement, out var pca))
                {
                    pca = new PlacementChannelsActivation();
                    activation.Channels[placement] = pca;
                }
                for (int ch = 0; ch < _channels.Count; ch++)
                    pca.Channels[ch] = new ChannelActivation { IsEnabled = ch == channel };
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
