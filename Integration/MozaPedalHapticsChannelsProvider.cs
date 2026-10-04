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
    /// actually does — every slot produces the same vibration. One channel per
    /// oscillator (slots 0-7); each switched-on effect is given the least-used
    /// one, and effects share once all eight are taken. Road Texture (slot 8)
    /// takes a suspension position rather than a tone, so it is not a channel.
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
        /// decided by <c>MozaPedalHapticsBridge.SyncOscillatorAssignments</c> — but the user is
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
        // Never enabled by default: oscillators are handed out only to switched-on
        // effects, by MozaPedalHapticsBridge.SyncOscillatorAssignments. Returning
        // true here would put every effect on every oscillator.
        public ChannelActivation CreateDefaultActivationFor(FFBPlacement placement, MotorsWithFrequencyOutputManagerBase manager)
            => new ChannelActivation { IsEnabled = false };

        /// <summary>
        /// Called by SimHub on Add effect and Reset effect. No oscillator is
        /// assigned here — a new effect may never be switched on, and the sweep
        /// assigns one once it is.
        /// </summary>
        public void LoadDefaultPlatformSettings(EffectsContainerBase effectsContainerBase, ShakeItProfile shakeItProfile)
        {
            // Corner placements mean nothing on a single pedal motor — collapse to
            // mono where the effect allows it, as SimHub's own pedal providers do.
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
