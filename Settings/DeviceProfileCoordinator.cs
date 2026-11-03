using System;
using System.Collections.Generic;
using System.Windows;
using MozaPlugin.Devices;

namespace MozaPlugin.Settings
{
    /// <summary>
    /// Runtime side of the per-device profile stores (<see cref="DeviceProfileRegistry"/>):
    /// wires each store into SimHub's profile system, resolves the connected device's
    /// active profile, seeds a store the first time a device is seen, and applies a
    /// device's profile when its selection changes (manual pick or game switch).
    /// </summary>
    internal sealed class DeviceProfileCoordinator
    {
        private readonly MozaPlugin _plugin;
        private readonly ProfileCoordinator _profiles;
        private readonly object _lock = new object();

        // Store instance → its ActiveProfileChanged handler, for detach.
        private readonly Dictionary<object, Action> _detachers = new Dictionary<object, Action>();
        // Late seeds queued to the UI thread, so a burst of lookups queues one each.
        private readonly HashSet<(DeviceProfileKind, Guid)> _pendingSeeds = new HashSet<(DeviceProfileKind, Guid)>();

        // The base page the connected base resolved to last. BaseModelName is blanked
        // on every rim swap / transient reconnect while the base itself stays put.
        private Guid? _lastBasePageGuid;

        internal DeviceProfileCoordinator(MozaPlugin plugin, ProfileCoordinator profiles)
        {
            _plugin = plugin;
            _profiles = profiles;
        }

        private DeviceProfileRegistry Registry
        {
            get
            {
                var s = _plugin.Settings;
                return s.DeviceProfiles ??= new DeviceProfileRegistry();
            }
        }

        /// <summary>Raised (on the thread that changed it) when any device store's
        /// active profile changes, or a store is created. Pages re-render on it.</summary>
        internal event EventHandler? DeviceProfilesChanged;

        // ===== Lifecycle =====

        /// <summary>Wire every persisted store. Call from InitProfileSystem, before the
        /// initial apply, on the thread that deserialized the settings.</summary>
        internal void InitAll()
        {
            var reg = Registry;
            foreach (var kv in reg.Wheels) Wire(DeviceProfileKind.Wheel, kv.Key, kv.Value);
            foreach (var kv in reg.Bases) Wire(DeviceProfileKind.Base, kv.Key, kv.Value);
            foreach (var kv in reg.Dashes) Wire(DeviceProfileKind.Dash, kv.Key, kv.Value);
        }

        /// <summary>Detach every store subscription (teardown / ClearSettings).</summary>
        internal void DetachAll()
        {
            List<Action> detach;
            lock (_lock)
            {
                detach = new List<Action>(_detachers.Values);
                _detachers.Clear();
                _pendingSeeds.Clear();
                _lastBasePageGuid = null;
            }
            foreach (var d in detach) { try { d(); } catch { } }
        }

        private void Wire<TProfile, TStore>(DeviceProfileKind kind, Guid guid,
            ActiveProfileTrackingStore<TProfile, TStore> store, Func<TProfile> newDefault)
            where TProfile : class, SimHub.Plugins.ProfilesCommon.IProfile,
                SimHub.Plugins.ProfilesCommon.IProfile<TProfile, TStore>, new()
            where TStore : class, SimHub.Plugins.ProfilesCommon.IProfileSettings<TProfile>
        {
            if (store == null) return;
            lock (_lock) { if (_detachers.ContainsKey(store)) return; }

            if (store.Profiles.Count == 0)
                store.Profiles.Add(newDefault());
            store.Init();
            store.ResetActiveProfileBaseline();

            EventHandler handler = (s, e) => OnActiveProfileChanged(kind, guid);
            store.ActiveProfileChanged += handler;
            lock (_lock) { _detachers[store] = () => store.ActiveProfileChanged -= handler; }
        }

        private void Wire(DeviceProfileKind kind, Guid guid, object store)
        {
            switch (store)
            {
                case MozaWheelProfileStore w:
                    Wire<MozaWheelProfile, MozaWheelProfileStore>(kind, guid, w,
                        () => new MozaWheelProfile { Name = "Default" });
                    break;
                case MozaBaseDeviceProfileStore b:
                    Wire<MozaBaseDeviceProfile, MozaBaseDeviceProfileStore>(kind, guid, b, () =>
                    {
                        var p = new MozaBaseDeviceProfile { Name = "Default" };
                        p.SeedBaselineFromFlatFields(_plugin.Settings);
                        return p;
                    });
                    break;
                case MozaDashProfileStore d:
                    Wire<MozaDashProfile, MozaDashProfileStore>(kind, guid, d, () =>
                    {
                        var p = new MozaDashProfile { Name = "Default" };
                        p.SeedBaselineFromFlatFields(_plugin.Settings);
                        return p;
                    });
                    break;
            }
        }

        // ===== Store lookup / seeding =====

        internal MozaWheelProfileStore? WheelStore(Guid guid, bool seed) =>
            GetOrSeed(DeviceProfileKind.Wheel, guid, seed, r => r.Wheels, (r, d) => r.Wheels = d,
                () => DeviceProfileMigration.BuildWheelStore(_plugin.Settings.LegacyDeviceProfileSnapshot, guid));

        internal MozaBaseDeviceProfileStore? BaseStore(Guid guid, bool seed) =>
            GetOrSeed(DeviceProfileKind.Base, guid, seed, r => r.Bases, (r, d) => r.Bases = d,
                () => DeviceProfileMigration.BuildBaseStore(_plugin.Settings.LegacyDeviceProfileSnapshot, guid));

        internal MozaDashProfileStore? DashStore(Guid guid, bool seed) =>
            GetOrSeed(DeviceProfileKind.Dash, guid, seed, r => r.Dashes, (r, d) => r.Dashes = d,
                () => DeviceProfileMigration.BuildDashStore(_plugin.Settings.LegacyDeviceProfileSnapshot, guid));

        /// <summary>
        /// Return the store for <paramref name="guid"/>, creating it from the split
        /// snapshot (or a single Default) when <paramref name="seed"/> and it is missing.
        /// Stores are built on the UI thread — SimHub's profile store hooks WPF weak
        /// events in its constructor — so a miss off that thread queues the seed,
        /// returns null, and applies the device once the store exists.
        /// </summary>
        private TStore? GetOrSeed<TStore>(DeviceProfileKind kind, Guid guid, bool seed,
            Func<DeviceProfileRegistry, Dictionary<Guid, TStore>?> get,
            Action<DeviceProfileRegistry, Dictionary<Guid, TStore>> set,
            Func<TStore> build) where TStore : class
        {
            var reg = Registry;
            var dict = get(reg);
            if (dict != null && dict.TryGetValue(guid, out var existing) && existing != null)
                return existing;
            // Never ahead of the split: it builds every store it can from the
            // legacy profiles, and a store seeded first would block that.
            if (!seed || guid == Guid.Empty || !_plugin.Settings.DeviceProfilesSplit) return null;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                lock (_lock)
                {
                    if (!_pendingSeeds.Add((kind, guid))) return null;
                }
                dispatcher.BeginInvoke(new Action(() =>
                {
                    lock (_lock) { _pendingSeeds.Remove((kind, guid)); }
                    if (GetOrSeed(kind, guid, true, get, set, build) != null)
                        ApplyDevice(kind, guid);
                }));
                return null;
            }

            TStore store;
            lock (_lock)
            {
                dict = get(reg);
                if (dict != null && dict.TryGetValue(guid, out existing) && existing != null)
                    return existing;
                store = build();
                // COW: the save debounce serializes these dicts off-thread.
                var next = dict != null ? new Dictionary<Guid, TStore>(dict) : new Dictionary<Guid, TStore>();
                next[guid] = store;
                set(reg, next);
            }
            Wire(kind, guid, store);
            MozaLog.Info($"[AZOM] Created {kind} profile store for page {guid:N} "
                         + (_plugin.Settings.LegacyDeviceProfileSnapshot != null ? "(cloned from the pre-split profiles)" : "(Default)"));
            _plugin.PersistSettings();
            RaiseChanged();
            return store;
        }

        // ===== Page → store resolution =====

        /// <summary>A wheel page's store GUID from its model prefix. The generic and
        /// old-protocol pages own no store: they follow the attached wheel.</summary>
        internal Guid? WheelPageGuidFor(string? modelPrefix)
        {
            if (modelPrefix == null) return null;
            if (modelPrefix.Length == 0 || modelPrefix == MozaDeviceConstants.OldProtocolMarker)
                return _plugin.GetCurrentWheelPageGuid();
            return Guid.TryParse(MozaDeviceConstants.ResolveWheelGuid(modelPrefix), out var g) ? g : (Guid?)null;
        }

        /// <summary>A wheelbase page's store GUID from its model prefix; the legacy
        /// shared page ("") follows the attached base.</summary>
        internal Guid? BasePageGuidFor(string? modelPrefix)
        {
            if (string.IsNullOrEmpty(modelPrefix)) return CurrentBasePageGuid;
            return Guid.TryParse(MozaDeviceConstants.ResolveBaseGuid(modelPrefix!), out var g) ? g : (Guid?)null;
        }

        /// <summary>A dash device's store GUID from its SimHub DeviceTypeID.</summary>
        internal static Guid DashPageGuidFor(string? deviceTypeId) =>
            !string.IsNullOrEmpty(deviceTypeId)
            && (deviceTypeId!.Equals(MozaDeviceConstants.DashCm1Guid, StringComparison.OrdinalIgnoreCase)
                || deviceTypeId.StartsWith(MozaDeviceConstants.DashCm1Guid + "_", StringComparison.OrdinalIgnoreCase))
                ? MozaPlugin.Cm1PageGuid
                : MozaPlugin.Cm2PageGuid;

        // ===== Active device resolution =====

        internal Guid? CurrentBasePageGuid
        {
            get
            {
                var prefix = BaseModelInfo.ExtractPrefix(_plugin.Data?.BaseModelName);
                if (!string.IsNullOrEmpty(prefix)
                    && Guid.TryParse(MozaDeviceConstants.ResolveBaseGuid(prefix), out var g))
                {
                    _lastBasePageGuid = g;
                    return g;
                }
                return _plugin.DetectionState.BaseDetected ? _lastBasePageGuid : null;
            }
        }

        /// <summary>The dash page the attached dash belongs to (CM1 once the
        /// discriminator says so, else CM2). Null when no dash is present.</summary>
        internal Guid? CurrentDashPageGuid =>
            _plugin.IsDashDetected
                ? (_plugin.DashIsCm1 ? MozaPlugin.Cm1PageGuid : MozaPlugin.Cm2PageGuid)
                : (Guid?)null;

        internal MozaWheelProfile? ActiveWheelProfile
        {
            get
            {
                var g = _plugin.GetCurrentWheelPageGuid();
                return g.HasValue ? WheelStore(g.Value, seed: true)?.CurrentProfile : null;
            }
        }

        internal MozaBaseDeviceProfile? ActiveBaseProfile
        {
            get
            {
                var g = CurrentBasePageGuid;
                return g.HasValue ? BaseStore(g.Value, seed: true)?.CurrentProfile : null;
            }
        }

        internal MozaDashProfile? ActiveDashProfile
        {
            get
            {
                var g = CurrentDashPageGuid;
                return g.HasValue ? DashStore(g.Value, seed: true)?.CurrentProfile : null;
            }
        }

        /// <summary>The dash's profile for an explicit dash page (CM2 / CM1 page UI),
        /// whether or not that dash is attached right now.</summary>
        internal MozaDashProfile? DashProfileFor(Guid pageGuid, bool seed = true) =>
            DashStore(pageGuid, seed)?.CurrentProfile;

        /// <summary>
        /// The profile that owns the dashboard pick for the lane
        /// <c>DashboardBindingCoordinator.ApplyTelemetryDashboardFromProfile</c> drives:
        /// the wheel's screen, or the CM2 when the wheel can't show a dashboard.
        /// </summary>
        internal IDashboardKeyProfile? ActiveDashboardKeyProfile
        {
            get
            {
                // Same lane test as ApplyTelemetryDashboardFromProfile.
                if (!_plugin.ShouldDriveDashboard()
                    && (_plugin.ActiveCm2Sender != null || _plugin.IsCm2Present))
                    return DashProfileFor(MozaPlugin.Cm2PageGuid);
                return ActiveWheelProfile;
            }
        }

        // ===== Wheel LED tabs: global vs per-profile =====

        /// <summary>A wheel page's global LED settings, or null when none of its tabs
        /// has ever gone global.</summary>
        internal WheelGlobalLedSettings? WheelGlobalLed(Guid? pageGuid)
        {
            if (!pageGuid.HasValue) return null;
            var dict = _plugin.Settings?.WheelGlobalLedByPageGuid;
            return dict != null && dict.TryGetValue(pageGuid.Value, out var g) ? g : null;
        }

        internal bool IsWheelLedGlobal(Guid? pageGuid, WheelLedGroup group) =>
            WheelGlobalLed(pageGuid)?.IsGlobal(group) == true;

        /// <summary>
        /// Switch one LED tab of a wheel page between global and per-profile storage.
        /// The first time a tab goes global it is seeded from that wheel's active
        /// profile, so the LEDs don't change; after that the global values persist
        /// across off/on. Re-applies the wheel when it is the attached one.
        /// </summary>
        internal void SetWheelLedGlobal(Guid pageGuid, WheelLedGroup group, bool on)
        {
            var settings = _plugin.Settings;
            if (settings == null) return;
            lock (_lock)
            {
                var dict = settings.WheelGlobalLedByPageGuid;
                if (dict == null || !dict.TryGetValue(pageGuid, out var g) || g == null)
                {
                    if (!on) return;
                    g = new WheelGlobalLedSettings();
                    // COW: the save debounce serializes this dict off-thread.
                    var next = dict != null
                        ? new Dictionary<Guid, WheelGlobalLedSettings>(dict)
                        : new Dictionary<Guid, WheelGlobalLedSettings>();
                    next[pageGuid] = g;
                    settings.WheelGlobalLedByPageGuid = next;
                }
                if (g.IsGlobal(group) == on) return;
                if (on && !g.IsSeeded(group))
                {
                    var source = WheelStore(pageGuid, seed: true)?.CurrentProfile;
                    if (source != null) WheelGlobalLedSettings.CopyGroup(source, g, group);
                    g.MarkSeeded(group);
                }
                g.SetGlobal(group, on);
            }
            MozaLog.Info($"[AZOM] Wheel {group} LEDs ({pageGuid:N}) now stored "
                         + (on ? "globally" : "in the device profile"));
            _plugin.PersistSettings();
            if (_plugin.GetCurrentWheelPageGuid() == pageGuid)
                _plugin.HardwareApplier.ApplyWheelToHardware();
            RaiseChanged();
        }

        /// <summary>Where the attached wheel's <paramref name="group"/> LED edits go:
        /// the wheel's global settings when that tab is global, else its current profile.</summary>
        internal IWheelLedFields? WheelLedTarget(WheelLedGroup group)
        {
            var page = _plugin.GetCurrentWheelPageGuid();
            var global = WheelGlobalLed(page);
            if (global != null && global.IsGlobal(group)) return global;
            return ActiveWheelProfile;
        }

        /// <summary>
        /// <paramref name="profile"/> with the page's global LED tabs laid over it — what
        /// the wheel actually shows. Returns the profile itself when no tab is global;
        /// otherwise a copy, so the stored profile is never touched.
        /// </summary>
        internal MozaWheelProfile? EffectiveWheelProfile(MozaWheelProfile? profile, Guid? pageGuid)
        {
            var global = WheelGlobalLed(pageGuid);
            if (global == null || !global.AnyGlobal) return profile;
            var merged = new MozaWheelProfile();
            if (profile != null) merged.CopyProfilePropertiesFrom(profile);
            foreach (WheelLedGroup g in Enum.GetValues(typeof(WheelLedGroup)))
                if (global.IsGlobal(g)) WheelGlobalLedSettings.CopyGroup(global, merged, g);
            return merged;
        }

        /// <summary>The attached wheel's effective LED settings (see <see cref="EffectiveWheelProfile"/>).</summary>
        internal MozaWheelProfile? ActiveWheelLedProfile =>
            EffectiveWheelProfile(ActiveWheelProfile, _plugin.GetCurrentWheelPageGuid());

        // ===== Apply on change =====

        private void OnActiveProfileChanged(DeviceProfileKind kind, Guid guid)
        {
            object? profile = kind switch
            {
                DeviceProfileKind.Wheel => WheelStore(guid, false)?.CurrentProfile,
                DeviceProfileKind.Base => BaseStore(guid, false)?.CurrentProfile,
                _ => DashStore(guid, false)?.CurrentProfile,
            };
            MozaLog.Info($"[AZOM] {kind} profile changed (page {guid:N}): "
                         + ((profile as SimHub.Plugins.ProfilesCommon.IProfile)?.Name ?? "<none>"));
            // A selection change is a deliberate apply, same as the main selector.
            _profiles.MarkProfileApplied();
            ApplyDevice(kind, guid);
            _plugin.PersistSettings();
            RaiseChanged();
        }

        /// <summary>Write a device's current profile to hardware if that device is the
        /// one attached. Each Apply*ToHardware honours the launch-apply gate itself.</summary>
        internal void ApplyDevice(DeviceProfileKind kind, Guid guid)
        {
            try
            {
                switch (kind)
                {
                    case DeviceProfileKind.Wheel:
                        if (_plugin.GetCurrentWheelPageGuid() != guid) return;
                        _plugin.HardwareApplier.ApplyWheelToHardware();
                        if (_plugin.ShouldDriveDashboard())
                            _profiles.ApplySavedDashboardKey(WheelStore(guid, false)?.CurrentProfile);
                        _profiles.SyncTelemetryAfterApply();
                        break;
                    case DeviceProfileKind.Base:
                        if (CurrentBasePageGuid != guid) return;
                        _plugin.HardwareApplier.ApplyBaseAmbientToHardware();
                        break;
                    case DeviceProfileKind.Dash:
                        if (CurrentDashPageGuid != guid) return;
                        _plugin.HardwareApplier.ApplyDashToHardware();
                        if (guid == MozaPlugin.Cm2PageGuid && !_plugin.ShouldDriveDashboard())
                            _profiles.ApplySavedDashboardKey(DashStore(guid, false)?.CurrentProfile);
                        _profiles.SyncTelemetryAfterApply();
                        break;
                }
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] {kind} profile apply failed: {ex.Message}");
            }
        }

        private void RaiseChanged()
        {
            try { DeviceProfilesChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { MozaLog.Warn("[AZOM] DeviceProfilesChanged subscriber threw: " + ex.Message); }
        }

        // ===== Diagnostics =====

        internal IEnumerable<string> DescribeStores()
        {
            var reg = Registry;
            foreach (var kv in reg.Wheels)
                yield return $"wheel {Describe(kv.Key)}: {kv.Value?.CurrentProfile?.Name ?? "<none>"} ({kv.Value?.Profiles.Count ?? 0} profiles)";
            foreach (var kv in reg.Bases)
                yield return $"base  {Describe(kv.Key)}: {kv.Value?.CurrentProfile?.Name ?? "<none>"} ({kv.Value?.Profiles.Count ?? 0} profiles)";
            foreach (var kv in reg.Dashes)
                yield return $"dash  {Describe(kv.Key)}: {kv.Value?.CurrentProfile?.Name ?? "<none>"} ({kv.Value?.Profiles.Count ?? 0} profiles)";
        }

        private static string Describe(Guid g)
        {
            if (g == MozaPlugin.Cm2PageGuid) return "CM2";
            if (g == MozaPlugin.Cm1PageGuid) return "CM1";
            var s = g.ToString();
            var wheel = MozaDeviceConstants.GetWheelModelPrefix(s);
            if (!string.IsNullOrEmpty(wheel)) return wheel!;
            var b = MozaDeviceConstants.GetBaseModelPrefix(s);
            if (!string.IsNullOrEmpty(b)) return b!;
            return g.ToString("N").Substring(0, 8);
        }
    }
}
