using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using BA63Driver;
using BA63Driver.Interfaces;
using BA63Driver.Mapper;
using GameReaderCommon.Enums;
using SerialDash;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device.MotorsWithFrequency;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers;
using SimHub.Plugins.DataPlugins.ShakeItV3.Settings;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;
using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;
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
    /// <see cref="MozaPedalHapticsConnectionManager"/> is swapped in for it; its
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

        // StandardProtocolMotorsDeviceExtension keeps its hosted ShakeIt plugin
        // in a private field; everything past it is public.
        private const string HostedPluginField = "shakeITV3PluginBase";

        private static readonly ConcurrentDictionary<Type, FieldInfo?> FieldCache =
            new ConcurrentDictionary<Type, FieldInfo?>();

        /// <summary>Build the replacement manager lazily, inside a caller's guard.</summary>
        public static object CreateConnectionManager(byte pedal) => new MozaPedalHapticsConnectionManager(pedal);

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
        {
            if (!IsSupported) return;

            try
            {
                var field = FieldCache.GetOrAdd(motorsDeviceExtension.GetType(),
                    t => t.GetField(HostedPluginField, BindingFlags.NonPublic | BindingFlags.Instance));
                var hosted = field?.GetValue(motorsDeviceExtension);
                // GetProp handles the two traps the wheelbase bridge documents:
                // ShakeItSettings<T> re-declares OutputManager with `new`, and
                // AbstractSettingsStore.Settings is a field, not a property.
                var settings = MozaBaseHapticsBridge.GetProp(hosted, "Settings");
                if (settings == null) return;   // not constructed yet; retried next tick

                Install(MozaBaseHapticsBridge.GetProp(settings, "OutputManager"), settings, pedal);
                Install(MozaBaseHapticsBridge.GetProp(settings, "CurrentOutputManager"), settings, pedal);
                Install(MozaBaseHapticsBridge.GetProp(MozaBaseHapticsBridge.GetProp(settings, "CurrentProfile"), "OutputManager"), settings, pedal);
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] Could not install the pedal-haptics channels provider: {ex.Message}");
            }
        }

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

                    // Count what valid assignments already hold before placing newcomers.
                    var load = new int[MozaPedalHapticsProtocol.ChannelsPerPedal];
                    var unassigned = new List<DeviceChannelActivationSettings>();
                    foreach (var (activation, on) in leaves)
                    {
                        int current = AssignedChannel(activation);
                        if (!on)
                        {
                            if (ClearChannels(activation)) released++;
                        }
                        else if (current >= 0) load[current]++;
                        else unassigned.Add(activation);
                    }

                    foreach (var activation in unassigned)
                    {
                        int channel = 0;
                        for (int ch = 1; ch < load.Length; ch++)
                            if (load[ch] < load[channel]) channel = ch;
                        SetSingleChannel(activation, channel);
                        load[channel]++;
                        assigned++;
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
        /// The one oscillator this effect drives, or -1 when it is not exactly one
        /// oscillator on every placement — none, several (SimHub's stock defaults
        /// enable all of them), or placements that disagree.
        /// </summary>
        private static int AssignedChannel(DeviceChannelActivationSettings activation)
        {
            int found = -1;
            foreach (FFBPlacement placement in Enum.GetValues(typeof(FFBPlacement)))
            {
                if (!activation.Channels.TryGetValue(placement, out var pca) || pca == null) return -1;

                int single = -1;
                for (int ch = 0; ch < MozaPedalHapticsProtocol.ChannelsPerPedal; ch++)
                {
                    if (!pca.Channels.TryGetValue(ch, out var a) || a == null || !a.IsEnabled) continue;
                    if (single >= 0) return -1;
                    single = ch;
                }
                if (single < 0 || (found >= 0 && single != found)) return -1;
                found = single;
            }
            return found;
        }

        private static void SetSingleChannel(DeviceChannelActivationSettings activation, int channel)
        {
            foreach (FFBPlacement placement in Enum.GetValues(typeof(FFBPlacement)))
            {
                if (!activation.Channels.TryGetValue(placement, out var pca) || pca == null)
                {
                    pca = new PlacementChannelsActivation();
                    activation.Channels[placement] = pca;
                }
                for (int ch = 0; ch < MozaPedalHapticsProtocol.ChannelsPerPedal; ch++)
                    SetChannel(pca, ch, ch == channel);
            }
        }

        /// <returns>True when an enabled oscillator was actually switched off.</returns>
        private static bool ClearChannels(DeviceChannelActivationSettings activation)
        {
            bool changed = false;
            foreach (var pca in activation.Channels.Values)
            {
                if (pca == null) continue;
                for (int ch = 0; ch < MozaPedalHapticsProtocol.ChannelsPerPedal; ch++)
                    if (pca.Channels.TryGetValue(ch, out var a) && a != null && a.IsEnabled)
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
    }

    /// <summary>
    /// Stand-in for SimHub's StandardProtocolConnectionDevice manager. Reports
    /// whether a pedal-haptics unit has answered, never touches HID, and hands
    /// out the motors driver.
    ///
    /// Implements <see cref="IConnectableLedDeviceManager"/> deliberately:
    /// without it, StandardProtocolConnectionDevice.DataUpdate calls Display()
    /// with six empty colour arrays every tick.
    /// </summary>
    internal sealed class MozaPedalHapticsConnectionManager : ILedDeviceManager, IConnectableLedDeviceManager
    {
        private readonly MozaPedalHapticsMotorsDriver _motors;

        internal MozaPedalHapticsConnectionManager(byte pedal)
        {
            _motors = new MozaPedalHapticsMotorsDriver(pedal);
        }
        private bool _lastConnected;

        public LedModuleSettings? LedModuleSettings { get; set; }
        public LedDeviceState? LastState { get; private set; }

#pragma warning disable CS0067 // Required by ILedDeviceManager; this device renders nothing
        public event EventHandler? BeforeDisplay;
        public event EventHandler? AfterDisplay;
        public event EventHandler? OnError;
#pragma warning restore CS0067
        public event EventHandler? OnConnect;
        public event EventHandler? OnDisconnect;

        // This device has no LED sub-device to keep alive, so unlike the
        // wheelbase there is no wide-gate/narrow-gate split — the connection and
        // the motors driver can both use the one real condition.
        public bool IsConnected() => MozaPlugin.Instance?.IsPedalHapticsReady == true;

        /// <summary>Raise SimHub's connect/disconnect events when the state flips. Called from the device extension's DataUpdate.</summary>
        public void UpdateConnectionState()
        {
            bool now = IsConnected();
            if (now == _lastConnected) return;
            _lastConnected = now;
            if (now) OnConnect?.Invoke(this, EventArgs.Empty);
            else OnDisconnect?.Invoke(this, EventArgs.Empty);
        }

        public void EnsureConnected() { }

        public void Display(Func<Color[]> leds, Func<Color[]> buttons, Func<Color[]> encoders,
            Func<Color[]> matrix, Func<Color[]> rawState, Func<Color[]> overrideState, bool forceRefresh,
            Func<object>? extraData = null, double rpmBrightness = 1.0, double buttonsBrightness = 1.0,
            double encodersBrightness = 1.0, double matrixBrightness = 1.0)
        {
            // The unit owns no pixels.
        }

        // The capture contains no identity traffic for device 0x1F at all — no
        // serial, no firmware version, no model name — so there is nothing
        // honest to report here. See docs/protocol/devices/pedal-haptics.md.
        public string GetSerialNumber() => "";
        public string GetFirmwareVersion() => "";
        public object GetDriverInstance() => _motors;
        public void Close() => _motors.Clear();
        public void ResetDetection() { }
        public void SerialPortCanBeScanned(object sender, SerialDashController.ScanArgs e) { }
        public IPhysicalMapper GetPhysicalMapper() => new NeutralLedsMapper();
        public ILedDriverBase? GetLedDriver() => null;
    }

    /// <summary>
    /// Sink for SimHub's ShakeIt motors mixer. The three MotorStates slots map
    /// onto one pedal motor's channel list; values go to the
    /// effect worker through the plugin so the worker stays the single wire
    /// owner.
    /// </summary>
    internal sealed class MozaPedalHapticsMotorsDriver : IMotorsDriver
    {
        private readonly byte _pedal;
        private readonly int _pedalIndex;

        internal MozaPedalHapticsMotorsDriver(byte pedal)
        {
            _pedal = pedal;
            _pedalIndex = pedal - MozaPedalHapticsProtocol.MinPedal;
        }

        public bool IsConnected => MozaPlugin.Instance?.IsPedalHapticsReady == true;

        public string SerialNumber => "";
        public string FirmwareVersion => "";

        public bool SendMotors(MotorStates states, bool forceRefresh)
        {
            var plugin = MozaPlugin.Instance;
            if (plugin == null) return false;

            var s = states?.States;
            if (s == null)
            {
                plugin.ClearShakeItPedalHaptics(_pedalIndex);
                return true;
            }

            // MotorStates is a fixed-size array whose length SimHub sets from the
            // definition; do not assume it reaches ChannelsPerPedal, because a
            // stale definition on disk would hand back a shorter one and indexing
            // past it would throw on the data thread every tick.
            int n = Math.Min(s.Length, MozaPedalHapticsProtocol.ChannelsPerPedal);
            for (int i = 0; i < n; i++)
                plugin.PostShakeItPedalHapticsChannel(_pedalIndex, i, s[i].Gain, s[i].Frequency);
            return true;
        }

        public void Clear() => MozaPlugin.Instance?.ClearShakeItPedalHaptics(_pedalIndex);

        public void Dispose() => Clear();
    }
}
