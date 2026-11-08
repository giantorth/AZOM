using System;
using System.Collections.Generic;
using BA63Driver;
using BA63Driver.Interfaces;
using GameReaderCommon.Enums;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device.MotorsWithFrequency;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers;
using SimHub.Plugins.DataPlugins.ShakeItV3.Settings;
using MozaPlugin.Integration;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices.Haptics
{
    /// <summary>
    /// The pedal-haptics side of a device definition that declares
    /// HapticsFeature (SimHub 9.12+) and nothing else — no LEDs, unlike the
    /// wheelbase.
    ///
    /// The restructuring problem is the same one
    /// <see cref="MozaBaseHapticsBridge"/> documents: declaring haptics makes
    /// SimHub call <c>LedModuleDevice.DisablePrimary()</c> and add a
    /// <c>StandardProtocolConnectionDevice</c> that becomes the composite's only
    /// primary. If that never reports Connected, CompositeDeviceInstance sets
    /// PrimaryDeviceMissing and the whole device stops being driven.
    /// <see cref="MozaHapticsConnectionManager"/> is swapped in for it; its
    /// GetDriverInstance() also answers the motors extension's lazily-resolved
    /// <see cref="IMotorsDriver"/>, so one swap covers both connection state and
    /// value delivery. No HID report ever leaves SimHub — the frames go out the
    /// serial pipe.
    ///
    /// These types bind 9.12-era SimHub/BA63 members, and are only touched from
    /// inside a guard on <see cref="MozaBaseHapticsBridge.IsSupported"/>, so an
    /// older SimHub degrades to "no haptics" rather than a TypeLoadException.
    /// </summary>
    internal static class MozaPedalHapticsBridge
    {
        /// <summary>Same probe as the wheelbase bridge — one SimHub feature, one gate.</summary>
        public static bool IsSupported => MozaBaseHapticsBridge.IsSupported;

        // Enum.GetValues allocates and boxes per call; the sweep walks placements per
        // leaf. Nested so FFBPlacement only loads with the guarded sweep methods.
        private static class Placements
        {
            public static readonly FFBPlacement[] All = (FFBPlacement[])Enum.GetValues(typeof(FFBPlacement));
        }

        /// <summary>Build the replacement manager lazily, inside a caller's guard.</summary>
        public static object CreateConnectionManager(byte pedal) => CreateManager(pedal);

        /// <summary>Same, for the combined device.</summary>
        public static object CreateCombinedConnectionManager() => CreateManager(pedal: 0);

        // The unselected layout reads disconnected, so SimHub shows it as idle rather than live.
        private static MozaHapticsConnectionManager CreateManager(byte pedal)
        {
            bool combined = pedal == 0;
            return new MozaHapticsConnectionManager(
                () => MozaPlugin.Instance?.IsPedalHapticsActive(combined) == true,
                new MozaPedalHapticsMotorsDriver(pedal));
        }

        /// <summary>
        /// Replace SimHub's <c>StandardProtocolMotorsChannelsSettingsProvider</c>
        /// with <see cref="MozaPedalHapticsChannelsProvider"/> on every
        /// output-manager slot the Haptics section uses, so the three channels
        /// read as Throttle / Brake / Clutch instead of Motor 1/2/3.
        ///
        /// Re-asserted every tick, like SimHub's own <c>ConfigureSharedDriver</c>:
        /// a profile switch or settings reload runs <c>CreateOutputManager</c>
        /// again and stamps a fresh stock provider over ours.
        /// </summary>
        public static void TryInstallChannelsProvider(object motorsDeviceExtension, byte pedal)
            => TryInstall(motorsDeviceExtension, (manager, settings) => Install(manager, settings, pedal));

        /// <summary>Same, with the combined device's pedal-per-channel provider.</summary>
        public static void TryInstallCombinedChannelsProvider(object motorsDeviceExtension)
            => TryInstall(motorsDeviceExtension, InstallCombined);

        private static void TryInstall(object motorsDeviceExtension, Action<object?, object> install)
            => MozaBaseHapticsBridge.InstallOnOutputManagers(motorsDeviceExtension, install, "pedal-haptics");

        /// <summary>
        /// Give every switched-on effect exactly one oscillator, and release the
        /// oscillator of every switched-off one. The channel grid is hidden from the
        /// UI, so the plugin owns this assignment outright.
        ///
        /// A sweep rather than a hook: SimHub has no callback for an effect being
        /// switched on — <c>LoadDefaultPlatformSettings</c> only runs on Add/Reset
        /// effect, never for the stock profile — and <c>CreateDefaultActivationFor</c>
        /// has no channel index. Runs on the data thread, the same thread as the tone
        /// mixer, so the mixer never sees a half-written activation.
        ///
        /// A newcomer gets the oscillator fewest switched-on effects already hold, so
        /// effects spread across the module's mixer instead of summing into one tone.
        /// Only leaf effects are assigned; a group carries no tone of its own, and a
        /// switched-off group silences its children.
        /// </summary>
        /// <returns>(assigned, released) counts, for logging.</returns>
        public static (int Assigned, int Released) SyncOscillatorAssignments(object motorsDeviceExtension)
            => Sweep(motorsDeviceExtension, combined: false);

        /// <summary>
        /// The combined device's version of <see cref="SyncOscillatorAssignments"/>:
        /// the same assignment run once per pedal, over that pedal's oscillator
        /// block, for the effects whose pedal tick is on. A placement's ticks are
        /// seeded all-on the first time the sweep sees it, as SimHub's stock
        /// providers default every channel on.
        /// </summary>
        public static (int Assigned, int Released) SyncCombinedOscillatorAssignments(object motorsDeviceExtension)
            => Sweep(motorsDeviceExtension, combined: true);

        private static (int Assigned, int Released) Sweep(object motorsDeviceExtension, bool combined)
        {
            if (!IsSupported) return (0, 0);

            int assigned = 0, released = 0;
            try
            {
                var settings = MozaBaseHapticsBridge.GetHostedSettings(motorsDeviceExtension);
                if (settings == null) return (0, 0);

                foreach (var profile in MozaBaseHapticsBridge.ProfilesToWalk(settings))
                {
                    if (!(profile is ShakeItProfile shakeItProfile)) continue;
                    var leaves = new List<(DeviceChannelActivationSettings Activation, bool On)>();
                    CollectLeaves(shakeItProfile.EffectsContainers, true, 0, leaves);

                    if (!combined)
                    {
                        SweepBlock(leaves, 0, _ => true, ref assigned, ref released);
                        continue;
                    }

                    foreach (var (activation, _) in leaves)
                        SeedPedalTicks(activation);
                    for (int p = 0; p < MozaPedalHapticsCombinedChannelsProvider.PedalChannels; p++)
                    {
                        int pedalChannel = p;
                        SweepBlock(leaves, MozaPedalHapticsCombinedChannelsProvider.OscillatorChannel(p, 0),
                            pca => IsOn(pca, pedalChannel), ref assigned, ref released);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentOutOfRangeException)
            {
                // The UI thread edited the effect list mid-walk; the next sweep picks it up.
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] Pedal-haptics oscillator sweep failed: {ex.Message}");
            }
            return (assigned, released);
        }

        /// <summary>
        /// Assign within one oscillator block. <paramref name="wants"/> says
        /// whether a placement should sound on this block at all — always for a
        /// per-pedal device, only where the pedal is ticked on the combined one.
        /// </summary>
        private static void SweepBlock(List<(DeviceChannelActivationSettings Activation, bool On)> leaves,
            int blockBase, Func<PlacementChannelsActivation?, bool> wants, ref int assigned, ref int released)
        {
            // Count what valid assignments already hold before placing newcomers.
            var load = new int[MozaPedalHapticsProtocol.ChannelsPerPedal];
            var unassigned = new List<DeviceChannelActivationSettings>();
            foreach (var (activation, on) in leaves)
            {
                if (!on || !WantsAny(activation, wants))
                {
                    if (ClearChannels(activation, blockBase)) released++;
                    continue;
                }
                int current = AssignedChannel(activation, blockBase, wants);
                if (current >= 0) load[current]++;
                else unassigned.Add(activation);
            }

            foreach (var activation in unassigned)
            {
                int channel = 0;
                for (int ch = 1; ch < load.Length; ch++)
                    if (load[ch] < load[channel]) channel = ch;
                SetSingleChannel(activation, blockBase, channel, wants);
                load[channel]++;
                assigned++;
            }
        }

        private static bool WantsAny(DeviceChannelActivationSettings activation,
            Func<PlacementChannelsActivation?, bool> wants)
        {
            foreach (var placement in Placements.All)
            {
                activation.Channels.TryGetValue(placement, out var pca);
                if (wants(pca)) return true;
            }
            return false;
        }

        private static bool IsOn(PlacementChannelsActivation? pca, int channel)
            => pca != null && pca.Channels.TryGetValue(channel, out var a) && a != null && a.IsEnabled;

        /// <summary>
        /// Tick every pedal on each placement not seen before. Only placements
        /// that exist are seeded — the mixer creates the ones an effect plays on.
        /// </summary>
        private static void SeedPedalTicks(DeviceChannelActivationSettings activation)
        {
            foreach (var pca in activation.Channels.Values)
            {
                if (pca == null || pca.Channels.ContainsKey(MozaPedalHapticsCombinedChannelsProvider.PedalsSeededMarker))
                    continue;
                for (int p = 0; p < MozaPedalHapticsCombinedChannelsProvider.PedalChannels; p++)
                    SetChannel(pca, p, true);
                pca.Channels[MozaPedalHapticsCombinedChannelsProvider.PedalsSeededMarker] =
                    new ChannelActivation { IsEnabled = false };
            }
        }

        // "On" matches ProcessEffects: own IsEnabled AND every enclosing group's.
        // Depth-capped because this walks a user-editable tree.
        private static void CollectLeaves(IList<EffectsContainerBase>? containers, bool parentOn, int depth,
            List<(DeviceChannelActivationSettings, bool)> leaves)
        {
            if (depth > 8 || containers == null) return;

            for (int i = 0; i < containers.Count; i++)
            {
                var container = containers[i];
                if (container == null) continue;
                bool on = parentOn && container.IsEnabled;

                if (container is GroupContainer group)
                    CollectLeaves(group.EffectsContainers, on, depth + 1, leaves);
                else
                    leaves.Add((container.SettingsStore.GetSettings<DeviceChannelActivationSettings>(), on));
            }
        }

        /// <summary>
        /// The one oscillator in the block this effect drives, or -1 when that is
        /// not exactly one oscillator on every wanted placement and none on the
        /// rest — none, several (SimHub's stock defaults enable all of them), or
        /// placements that disagree.
        /// </summary>
        private static int AssignedChannel(DeviceChannelActivationSettings activation, int blockBase,
            Func<PlacementChannelsActivation?, bool> wants)
        {
            int found = -1;
            foreach (var placement in Placements.All)
            {
                activation.Channels.TryGetValue(placement, out var pca);
                bool want = wants(pca);
                if (pca == null)
                {
                    if (want) return -1;
                    continue;
                }

                int single = -1;
                for (int ch = 0; ch < MozaPedalHapticsProtocol.ChannelsPerPedal; ch++)
                {
                    if (!IsOn(pca, blockBase + ch)) continue;
                    if (single >= 0) return -1;
                    single = ch;
                }
                if (!want)
                {
                    if (single >= 0) return -1;
                    continue;
                }
                if (single < 0 || (found >= 0 && single != found)) return -1;
                found = single;
            }
            return found;
        }

        private static void SetSingleChannel(DeviceChannelActivationSettings activation, int blockBase, int channel,
            Func<PlacementChannelsActivation?, bool> wants)
        {
            foreach (var placement in Placements.All)
            {
                if (!activation.Channels.TryGetValue(placement, out var pca) || pca == null)
                {
                    pca = new PlacementChannelsActivation();
                    activation.Channels[placement] = pca;
                }
                bool want = wants(pca);
                for (int ch = 0; ch < MozaPedalHapticsProtocol.ChannelsPerPedal; ch++)
                    SetChannel(pca, blockBase + ch, want && ch == channel);
            }
        }

        /// <returns>True when an enabled oscillator was actually switched off.</returns>
        private static bool ClearChannels(DeviceChannelActivationSettings activation, int blockBase)
        {
            bool changed = false;
            foreach (var pca in activation.Channels.Values)
            {
                if (pca == null) continue;
                for (int ch = 0; ch < MozaPedalHapticsProtocol.ChannelsPerPedal; ch++)
                    if (pca.Channels.TryGetValue(blockBase + ch, out var a) && a != null && a.IsEnabled)
                    {
                        a.IsEnabled = false;
                        changed = true;
                    }
            }
            return changed;
        }

        // In place where the entry exists, so nothing bound to it is orphaned.
        private static void SetChannel(PlacementChannelsActivation pca, int channel, bool enabled)
        {
            if (pca.Channels.TryGetValue(channel, out var existing) && existing != null)
                existing.IsEnabled = enabled;
            else
                pca.Channels[channel] = new ChannelActivation { IsEnabled = enabled };
        }

        private static void Install(object? outputManager, object settings, byte pedal)
        {
            if (!(outputManager is MotorsOutputManagerBase manager)) return;
            if (manager.ShakeItChannelsInfoProvider is MozaPedalHapticsChannelsProvider existing
                && existing.PedalId == pedal) return;

            var provider = new MozaPedalHapticsChannelsProvider(pedal);
            manager.ShakeItChannelsInfoProvider = provider;

            if (settings is SimHub.Plugins.DataPlugins.ShakeItV3.Settings.ShakeItSettings shakeItSettings)
                provider.SetSettings(shakeItSettings);

            MozaLog.Info($"[AZOM] Installed the MOZA pedal-haptics channels provider for the "
                       + $"{MozaPedalHapticsProtocol.PedalLabel(pedal).ToLowerInvariant()} pedal "
                       + $"({MozaPedalHapticsProtocol.ChannelsPerPedal} channels)");
        }

        private static void InstallCombined(object? outputManager, object settings)
        {
            if (!(outputManager is MotorsOutputManagerBase manager)) return;
            if (manager.ShakeItChannelsInfoProvider is MozaPedalHapticsCombinedChannelsProvider) return;

            var provider = new MozaPedalHapticsCombinedChannelsProvider();
            manager.ShakeItChannelsInfoProvider = provider;

            if (settings is SimHub.Plugins.DataPlugins.ShakeItV3.Settings.ShakeItSettings shakeItSettings)
                provider.SetSettings(shakeItSettings);

            MozaLog.Info("[AZOM] Installed the MOZA pedal-haptics combined channels provider "
                       + $"({MozaPedalHapticsCombinedChannelsProvider.PedalChannels} pedals)");
        }
    }

    /// <summary>
    /// Sink for SimHub's ShakeIt motors mixer. The MotorStates slots map onto
    /// one pedal motor's channel list — or, on the combined device, one slot per
    /// pedal; values go to the effect worker through the plugin so the worker
    /// stays the single wire owner.
    ///
    /// Only reached before our channels provider is installed, whose
    /// UpdateOutput posts directly.
    /// </summary>
    internal sealed class MozaPedalHapticsMotorsDriver : IMotorsDriver
    {
        private readonly int _pedalIndex;
        private readonly bool _combined;

        /// <param name="pedal">Motor port, or 0 for the combined device.</param>
        internal MozaPedalHapticsMotorsDriver(byte pedal)
        {
            _combined = pedal == 0;
            _pedalIndex = pedal - MozaPedalHapticsProtocol.MinPedal;
        }

        public bool IsConnected => MozaPlugin.Instance?.IsPedalHapticsActive(_combined) == true;

        public string SerialNumber => "";
        public string FirmwareVersion => "";

        public bool SendMotors(MotorStates states, bool forceRefresh)
        {
            var plugin = MozaPlugin.Instance;
            if (plugin == null) return false;
            if (plugin.PedalHapticsCombined != _combined) return true;

            var s = states?.States;
            if (s == null)
            {
                Clear();
                return true;
            }

            // MotorStates is a fixed-size array whose length SimHub sets from the
            // definition; do not assume it reaches the expected count, because a
            // stale definition on disk would hand back a shorter one and indexing
            // past it would throw on the data thread every tick.
            if (_combined)
            {
                // One mixed tone per pedal, on that pedal's first oscillator.
                int pedals = Math.Min(s.Length, MozaPedalHapticsProtocol.PedalCount);
                for (int p = 0; p < pedals; p++)
                    plugin.PostShakeItPedalHapticsChannel(p, 0, s[p].Gain, s[p].Frequency);
                return true;
            }

            int n = Math.Min(s.Length, MozaPedalHapticsProtocol.ChannelsPerPedal);
            for (int i = 0; i < n; i++)
                plugin.PostShakeItPedalHapticsChannel(_pedalIndex, i, s[i].Gain, s[i].Frequency);
            return true;
        }

        // Inactive layout must not clear what the active one is driving.
        public void Clear()
        {
            var plugin = MozaPlugin.Instance;
            if (plugin == null || plugin.PedalHapticsCombined != _combined) return;

            if (!_combined)
            {
                plugin.ClearShakeItPedalHaptics(_pedalIndex);
                return;
            }
            for (int p = 0; p < MozaPedalHapticsProtocol.PedalCount; p++)
                plugin.ClearShakeItPedalHaptics(p);
        }

        public void Dispose() => Clear();
    }
}
