using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using BA63Driver;
using BA63Driver.Interfaces;
using BA63Driver.Mapper;
using SerialDash;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device.MotorsWithFrequency;
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
