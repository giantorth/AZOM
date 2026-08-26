using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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

namespace MozaPlugin.Devices.Haptics
{
    /// <summary>
    /// The wheelbase side of a device definition that declares BOTH LedsFeature and
    /// HapticsFeature (SimHub 9.12+).
    ///
    /// Declaring haptics restructures the composite device: SimHub calls
    /// <c>LedModuleDevice.DisablePrimary()</c> and adds a
    /// <c>StandardProtocolConnectionDevice</c>, which becomes the composite's only
    /// primary. When a primary is not Connected, CompositeDeviceInstance sets
    /// PrimaryDeviceMissing on every other sub-device, ShouldBeRunning() goes false
    /// and the LED module stops being driven — so a haptics definition would go
    /// dark unless the plugin makes that connection report connected.
    ///
    /// <see cref="MozaBaseConnectionManager"/> is what gets swapped in for it. Its
    /// GetDriverInstance() also answers the motors extension's lazily-resolved
    /// <see cref="IMotorsDriver"/>, so one swap covers both connection state and
    /// value delivery — no HID report ever leaves SimHub.
    ///
    /// These types bind 9.12-era SimHub/BA63 members. They live in their own file
    /// and are only touched from inside a try/catch behind
    /// <see cref="MozaBaseHapticsBridge.IsSupported"/>, so an older SimHub degrades
    /// to "no haptics" rather than a TypeLoadException.
    /// </summary>
    internal static class MozaBaseHapticsBridge
    {
        private static bool? _supported;

        /// <summary>
        /// True when the running SimHub has the declarative haptics feature
        /// (Device Builder HapticsFeature + the motors device extension). Probed by
        /// type name, not by parsing a version string.
        /// </summary>
        public static bool IsSupported
        {
            get
            {
                if (_supported.HasValue) return _supported.Value;
                bool ok;
                try
                {
                    var asm = typeof(SimHub.Plugins.Devices.DeviceDescriptor).Assembly;
                    ok = asm.GetType(
                             "SimHub.Plugins.DataPlugins.ShakeItV3.Device.StandardProtocolMotorsDeviceExtension") != null
                         && asm.GetType(
                             "SimHub.Plugins.OutputPlugins.CommonDevices.Devices.StandardProtocolConnectionDevice") != null;
                }
                catch (Exception ex)
                {
                    MozaLog.Debug($"[AZOM] Device haptics capability probe failed: {ex.Message}");
                    ok = false;
                }

                if (!ok)
                    MozaLog.Info("[AZOM] This SimHub build has no device haptics feature; wheelbase LFE stays on the plugin's LFE tab");
                _supported = ok;
                return ok;
            }
        }

        /// <summary>The hosted ShakeIt settings object for a motors sub-device, or null before SimHub constructs it.</summary>
        private static object? GetHostedSettings(object motorsDeviceExtension)
        {
            var field = FieldCache.GetOrAdd(motorsDeviceExtension.GetType(),
                t => t.GetField(HostedPluginField, BindingFlags.NonPublic | BindingFlags.Instance));
            return GetProp(field?.GetValue(motorsDeviceExtension), "Settings");
        }

        /// <summary>
        /// Apply the plugin's shipped effect defaults (<see cref="MozaLfeEffectDefaults"/>)
        /// to the profile SimHub seeds when the device is created.
        ///
        /// That profile is built inside the motors sub-device's own LoadDefaultSettings,
        /// before any hook this plugin owns can install its channels provider — so it is
        /// keyed to SimHub's stock "SimagicReactors" defaults, which enable every channel
        /// on every effect. On a base that SUMS its three oscillators into one actuator
        /// that is a silent 3x, and the EffectsDefaults folder this plugin writes is never
        /// consulted for those 24. Hence this pass.
        ///
        /// Effects the user adds later need none of it: by then the provider is installed,
        /// so LoadDefaultPlatformSettings reads the shipped files directly.
        /// </summary>
        /// <returns>(containers seen, containers populated). The caller latches its
        /// one-shot flag on containers &gt; 0.</returns>
        public static (int Containers, int Applied) ApplyShippedEffectDefaults(object motorsDeviceExtension)
        {
            if (!IsSupported) return (0, 0);

            int containers = 0;
            int applied = 0;
            try
            {
                var settings = GetHostedSettings(motorsDeviceExtension);
                if (settings == null) return (0, 0);

                foreach (var profile in ProfilesToWalk(settings))
                {
                    if (!(GetProp(profile, "EffectsContainers") is System.Collections.IEnumerable list))
                        continue;

                    foreach (var container in list)
                    {
                        containers++;
                        if (MozaLfeEffectDefaults.ApplyTo(container, GetProp(container, "ContainerType") as string ?? ""))
                            applied++;
                    }
                }

                if (applied > 0)
                    MozaLog.Info(
                        $"[AZOM] Applied MOZA wheelbase LFE defaults to {applied} of {containers} stock effect(s) "
                        + "— all effects off, oscillator 1 only");
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Could not apply wheelbase LFE effect defaults: {ex.Message}");
            }
            return (containers, applied);
        }

        /// <summary>Every distinct profile object reachable from the settings — the Profiles list plus CurrentProfile.</summary>

        /// <summary>Every distinct profile object reachable from the settings — the Profiles list plus CurrentProfile.</summary>
        internal static IEnumerable<object> ProfilesToWalk(object settings)
        {
            var seen = new List<object>();

            void Add(object? profile)
            {
                if (profile == null) return;
                foreach (var existing in seen)
                    if (ReferenceEquals(existing, profile)) return;
                seen.Add(profile);
            }

            if (GetProp(settings, "Profiles") is System.Collections.IEnumerable list)
                foreach (var profile in list) Add(profile);

            Add(GetProp(settings, "CurrentProfile"));
            return seen;
        }

        /// <summary>Build the replacement manager. Separate from the type so callers can construct it lazily inside a guard.</summary>
        public static object CreateConnectionManager() => new MozaBaseConnectionManager();

        // StandardProtocolMotorsDeviceExtension keeps its hosted ShakeIt plugin in
        // a private field; everything past it is public.
        private const string HostedPluginField = "shakeITV3PluginBase";

        private static readonly ConcurrentDictionary<Type, FieldInfo?> FieldCache =
            new ConcurrentDictionary<Type, FieldInfo?>();


        /// <summary>
        /// True when <paramref name="instance"/> is SimHub's motors (Haptics)
        /// sub-device. Matched by type name so this file stays loadable on a
        /// SimHub without it.
        /// </summary>
        public static bool IsMotorsDeviceExtension(object? instance) =>
            string.Equals(instance?.GetType().FullName,
                "SimHub.Plugins.DataPlugins.ShakeItV3.Device.StandardProtocolMotorsDeviceExtension",
                StringComparison.Ordinal);

        /// <summary>
        /// Replace SimHub's <c>StandardProtocolMotorsChannelsSettingsProvider</c>
        /// with <see cref="MozaWheelbaseLfeChannelsProvider"/> on every output-manager
        /// slot the Haptics section uses.
        ///
        /// The declarative HapticsFeature path gives no way to name a provider, and
        /// SimHub's stock one returns <c>IsEnabled = true</c> from
        /// <c>CreateDefaultActivationFor</c> — so a new effect drives all three
        /// oscillators, which the base SUMS into one actuator (a silent 3×). Ours
        /// seeds a single enabled oscillator per effect and names them properly.
        ///
        /// Re-asserted every tick, like SimHub's own <c>ConfigureSharedDriver</c>:
        /// a profile switch or a settings reload runs <c>CreateOutputManager</c>
        /// again, which stamps a fresh stock provider over ours.
        /// </summary>
        public static void TryInstallChannelsProvider(object motorsDeviceExtension)
        {
            if (!IsSupported) return;

            try
            {
                var field = FieldCache.GetOrAdd(motorsDeviceExtension.GetType(),
                    t => t.GetField(HostedPluginField, BindingFlags.NonPublic | BindingFlags.Instance));
                var hosted = field?.GetValue(motorsDeviceExtension);
                var settings = GetProp(hosted, "Settings");
                if (settings == null) return;   // not constructed yet; retried next tick

                Install(GetProp(settings, "OutputManager"), settings);
                Install(GetProp(settings, "CurrentOutputManager"), settings);
                Install(GetProp(GetProp(settings, "CurrentProfile"), "OutputManager"), settings);
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] Could not install the LFE channels provider: {ex.Message}");
            }
        }

        // Resolved accessors, keyed by (declaring type, member name). The install
        // runs on the data thread every tick, so the hierarchy walk below is done
        // once per shape rather than per frame.
        private static readonly ConcurrentDictionary<(Type, string), Func<object, object?>?> MemberCache =
            new ConcurrentDictionary<(Type, string), Func<object, object?>?>();

        /// <summary>
        /// Read a public instance member by name — property OR field, most-derived
        /// declaration first.
        ///
        /// Two traps this exists for. A plain <c>GetProperty(name)</c> is not usable:
        /// <c>ShakeItSettings&lt;T&gt;</c> re-declares <c>OutputManager</c> with
        /// <c>new</c>, so the lookup finds two and throws AmbiguousMatchException on
        /// the real closed generic type. And properties alone are not enough:
        /// <c>AbstractSettingsStore.Settings</c> — the list holding every effect's
        /// <c>DeviceChannelActivationSettings</c> — is a public FIELD, so a
        /// property-only walk silently returns null and the activations look like
        /// they do not exist.
        /// </summary>
        internal static object? GetProp(object? target, string name)
        {
            if (target == null) return null;
            var accessor = MemberCache.GetOrAdd((target.GetType(), name), key =>
            {
                const BindingFlags flags =
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

                for (var t = key.Item1; t != null; t = t.BaseType)
                {
                    var p = t.GetProperty(key.Item2, flags);
                    if (p?.GetMethod != null)
                        return o => p.GetValue(o);

                    var f = t.GetField(key.Item2, flags);
                    if (f != null)
                        return o => f.GetValue(o);
                }
                return null;
            });
            return accessor?.Invoke(target);
        }

        private static void Install(object? outputManager, object settings)
        {
            // MotorsOutputManagerBase is public and exposes a public setter, so
            // only reaching the manager needs reflection.
            if (!(outputManager is MotorsOutputManagerBase manager)) return;
            if (manager.ShakeItChannelsInfoProvider is MozaWheelbaseLfeChannelsProvider) return;

            var provider = new MozaWheelbaseLfeChannelsProvider();
            manager.ShakeItChannelsInfoProvider = provider;

            // CreateOutputManager does this straight after constructing the stock
            // provider; mirror it so ours sees the same settings object.
            if (settings is SimHub.Plugins.DataPlugins.ShakeItV3.Settings.ShakeItSettings shakeItSettings)
                provider.SetSettings(shakeItSettings);

            MozaLog.Info("[AZOM] Installed the MOZA LFE channels provider on the wheelbase Haptics section "
                       + "(3 named oscillators, one enabled per new effect)");
        }
    }

    /// <summary>
    /// Stand-in for SimHub's StandardProtocolConnectionDevice manager. Reports the
    /// wheelbase pipe's real state (which gates the LED sub-device), never touches
    /// HID, and hands out the motors driver.
    ///
    /// Implements <see cref="IConnectableLedDeviceManager"/> deliberately: without
    /// it, StandardProtocolConnectionDevice.DataUpdate calls Display() with six
    /// empty colour arrays every tick.
    /// </summary>
    internal sealed class MozaBaseConnectionManager : ILedDeviceManager, IConnectableLedDeviceManager
    {
        private readonly MozaBaseMotorsDriver _motors = new MozaBaseMotorsDriver();
        private bool _lastConnected;

        public LedModuleSettings? LedModuleSettings { get; set; }
        public LedDeviceState? LastState { get; private set; }

#pragma warning disable CS0067 // Required by ILedDeviceManager; this sub-device renders nothing
        public event EventHandler? BeforeDisplay;
        public event EventHandler? AfterDisplay;
        public event EventHandler? OnError;
#pragma warning restore CS0067
        public event EventHandler? OnConnect;
        public event EventHandler? OnDisconnect;

        // Base pipe state, NOT LFE readiness: this value gates the LED sub-device
        // through PrimaryDeviceMissing, so an LFE-less base must still report
        // connected or its ambient strip stops updating. The motors driver carries
        // the narrower LFE gate.
        public bool IsConnected()
        {
            var plugin = MozaPlugin.Instance;
            return plugin != null
                && plugin.DetectionState.BaseDetected
                && plugin.DeviceManager?.IsConnected == true;
        }

        /// <summary>Raise SimHub's connect/disconnect events when the pipe state flips. Called from the device extension's DataUpdate.</summary>
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
            // The connection sub-device owns no pixels — the LED module's own
            // injected driver does. Nothing to do.
        }

        public string GetSerialNumber() => BaseSerialNumber();
        public string GetFirmwareVersion() => MozaPlugin.Instance?.Data?.BaseFwVersionText ?? "";
        public object GetDriverInstance() => _motors;
        public void Close() => _motors.Clear();
        public void ResetDetection() { }
        public void SerialPortCanBeScanned(object sender, SerialDashController.ScanArgs e) { }
        public IPhysicalMapper GetPhysicalMapper() => new NeutralLedsMapper();

        /// <summary>Hex MCU UID — the closest thing the base has to a serial number.</summary>
        internal static string BaseSerialNumber()
        {
            var uid = MozaPlugin.Instance?.Data?.BaseMcuUid;
            if (uid == null || uid.Length == 0) return "";
            return BitConverter.ToString(uid).Replace("-", "");
        }
        public ILedDriverBase? GetLedDriver() => null;
    }

    /// <summary>
    /// Sink for SimHub's ShakeIt motors mixer. The three MotorStates slots map onto
    /// the base's three LFE oscillators (wire ids 1/2/0, summed in firmware); the
    /// values go to <see cref="BaseLfeEffectWorker"/> through the plugin so the
    /// worker stays the single wire owner.
    /// </summary>
    internal sealed class MozaBaseMotorsDriver : IMotorsDriver
    {
        // Narrower than the connection manager's gate: this one drives the Haptics
        // section's connected state and the ShakeIt mixer's, and LFE genuinely
        // needs firmware >= 1.2.10.10.
        public bool IsConnected => MozaPlugin.Instance?.IsBaseLfeHapticsReady == true;

        public string SerialNumber => MozaBaseConnectionManager.BaseSerialNumber();
        public string FirmwareVersion => MozaPlugin.Instance?.Data?.BaseFwVersionText ?? "";

        public bool SendMotors(MotorStates states, bool forceRefresh)
        {
            var plugin = MozaPlugin.Instance;
            if (plugin == null) return false;

            var s = states?.States;
            if (s == null || s.Length < 3)
            {
                plugin.ClearShakeItLfeChannels();
                return true;
            }

            // Slots 3..7 exist in the fixed-size array but the base has only three
            // oscillators, and the definition advertises MotorsCount = 3.
            plugin.PostShakeItLfeChannels(
                s[0].Gain, s[0].Frequency,
                s[1].Gain, s[1].Frequency,
                s[2].Gain, s[2].Frequency);
            return true;
        }

        public void Clear() => MozaPlugin.Instance?.ClearShakeItLfeChannels();

        public void Dispose() => Clear();
    }
}
