using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SimHubInterfaceTest
{
    /// <summary>
    /// Every SimHub member the plugin reaches by name, checked against the loaded host
    /// with the same binding flags the plugin uses. Each check lists the plugin literals
    /// it covers; <see cref="Reconcile"/> then compares those against every reflection
    /// literal actually in the plugin's IL, so adding, renaming or dropping a lookup in
    /// the plugin without touching this file fails the run.
    ///
    /// Where the plugin keeps the lookup in a reusable helper or constant, the check calls
    /// that helper or reads that constant instead of restating it.
    /// </summary>
    internal sealed class Contract
    {
        private const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        /// <summary>
        /// Reflection literals in the plugin that deliberately target no SimHub type
        /// (the plugin's own members, BCL types), with the reason.
        /// </summary>
        private static readonly Dictionary<string, string> NotSimHub = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_CreateDelegate"] = "XAML compiler's GeneratedInternalTypeHelper, on System.Delegate",
        };

        private readonly Assembly _plugin;
        private readonly Type[] _types;
        private readonly Result _r;
        private readonly HashSet<string> _covered = new HashSet<string>(StringComparer.Ordinal);

        public Contract(Assembly plugin, Type[] types, Result result)
        {
            _plugin = plugin;
            _types = types;
            _r = result;
        }

        public void Run()
        {
            ControlMapper();
            Leds();
            Haptics();
            TrackMap();
            AcRawData();
            PropertyNames();
            Reconcile();
        }

        // ── Integration/ControlMapperBridge.cs ────────────────────────────────────
        private void ControlMapper()
        {
            const string A = "ControlMapper";
            var bridge = PluginType("MozaPlugin.Integration.ControlMapperBridge");
            var provider = PluginType("MozaPlugin.Integration.MozaVariantProvider");
            string cmName = Const(bridge, "ControlMapperPluginTypeName");

            var pm = HostType("SimHub.Plugins.PluginManager", "SimHub.Plugins");
            var cm = cmName == null ? null : pm?.Assembly.GetType(cmName, false);
            Check(A, cm != null, $"{cmName} exists in SimHub.Plugins", null, cmName);

            var getPlugin = Try(() => pm?.GetMethod("GetPlugin", PublicInstance, null, Type.EmptyTypes, null));
            Check(A, getPlugin != null && getPlugin.IsGenericMethodDefinition
                     && getPlugin.GetGenericArguments().Length == 1
                     && cm != null && Try(() => getPlugin.MakeGenericMethod(cm)) != null,
                "PluginManager.GetPlugin<T>() exists and accepts ControlMapperPlugin", null, "GetPlugin");

            var rw = Try(() => cm?.GetField("remapperWorker", NonPublicInstance));
            Check(A, rw != null, "ControlMapperPlugin.remapperWorker (non-public instance field)", null, "remapperWorker");

            var vh = Try(() => rw?.FieldType.GetField("variantHelper", NonPublicInstance));
            Check(A, vh != null, "RemapperWorker.variantHelper (non-public instance field)", null, "variantHelper");

            var vp = Try(() => vh?.FieldType.GetField("VariantProviders", NonPublicInstance));
            Check(A, vp != null, "VariantHelper.VariantProviders (non-public instance field)", null, "VariantProviders");
            if (vp != null)
            {
                var element = ElementOf(vp.FieldType);
                Check(A, typeof(IList).IsAssignableFrom(vp.FieldType) && provider != null
                         && element != null && element.IsAssignableFrom(provider),
                    "VariantHelper.VariantProviders is an IList that accepts MozaVariantProvider",
                    $"declared {vp.FieldType}", "VariantProviders");
            }

            // The bridge mutates the list under lock(variantHelper.GetType()), on the
            // assumption that SimHub's own Start/Stop take that same lock.
            var vhType = vh?.FieldType;
            var start = Try(() => vhType?.GetMethod("Start", PublicInstance, null, Type.EmptyTypes, null));
            var stop = Try(() => vhType?.GetMethod("Stop", PublicInstance, null, Type.EmptyTypes, null));
            Check(A, start != null && stop != null && Il.LocksOnOwnType(start) && Il.LocksOnOwnType(stop),
                "VariantHelper.Start()/Stop() lock on the VariantHelper type, the lock the bridge shares");

            var settingsField = Try(() => cm?.GetField("controlMapperPluginSettings", AnyInstance));
            Check(A, settingsField != null, "ControlMapperPlugin.controlMapperPluginSettings (instance field)",
                null, "controlMapperPluginSettings");
            var st = settingsField?.FieldType;

            var mappings = Prop(st, "ControllerMappings", PublicInstance, out string err);
            Check(A, mappings?.GetMethod != null && typeof(IList).IsAssignableFrom(mappings.PropertyType),
                "ControlMapperPluginSettings.ControllerMappings is a readable IList", err, "ControllerMappings");

            var mappingsType = mappings?.PropertyType;
            var collChanged = Try(() => mappingsType?.GetEvent("CollectionChanged", AnyInstance))
                              ?? mappingsType?.GetInterfaces().Select(i => Try(() => i.GetEvent("CollectionChanged")))
                                  .FirstOrDefault(e => e != null);
            var handler = Try(() => bridge?.GetMethod("OnControllerMappingsChanged", NonPublicInstance));
            Check(A, collChanged != null && handler != null && DelegateCompatible(collChanged.EventHandlerType, handler),
                "ControllerMappings.CollectionChanged binds ControlMapperBridge.OnControllerMappingsChanged",
                collChanged == null ? "event not found" : handler == null ? "bridge handler not found" : $"event type {collChanged.EventHandlerType}",
                "CollectionChanged", "OnControllerMappingsChanged");

            var recognize = Prop(st, "RecognizeIndiviualWheels", PublicInstance, out err);
            Check(A, recognize?.GetMethod != null && recognize.PropertyType == typeof(bool),
                "ControlMapperPluginSettings.RecognizeIndiviualWheels is a readable bool", err, "RecognizeIndiviualWheels");

            var update = Try(() => st?.GetMethod("UpdateControllerList", PublicInstance, null, Type.EmptyTypes, null));
            Check(A, update != null, "ControlMapperPluginSettings.UpdateControllerList() (public, no parameters)",
                null, "UpdateControllerList");

            const string Models = "SimHub.Plugins.OutputPlugins.ControlRemapper.Models.";
            var csm = st?.Assembly.GetType(Models + "ControllerSourceMapping", false);
            var desc = st?.Assembly.GetType(Models + "ControllerDescription", false);
            var state = st?.Assembly.GetType(Models + "ControllerState", false);
            Check(A, csm != null, $"{Models}ControllerSourceMapping exists", null, Models + "ControllerSourceMapping");
            Check(A, desc != null, $"{Models}ControllerDescription exists", null, Models + "ControllerDescription");
            Check(A, state != null, $"{Models}ControllerState exists", null, Models + "ControllerState");

            var csmDesc = Prop(csm, "ControllerDescription", null, out err);
            Check(A, csmDesc?.GetMethod != null && csmDesc.GetSetMethod(true) != null && desc != null
                     && csmDesc.PropertyType.IsAssignableFrom(desc),
                "ControllerSourceMapping.ControllerDescription is read/write and takes a ControllerDescription", err,
                "ControllerDescription");
            var csmState = Prop(csm, "ControllerState", null, out err);
            Check(A, csmState?.GetMethod != null && state != null && state.IsAssignableFrom(csmState.PropertyType),
                "ControllerSourceMapping.ControllerState is a readable ControllerState", err, "ControllerState");
            var csmEnabled = Prop(csm, "IsEnabled", null, out err);
            Check(A, csmEnabled?.GetMethod != null && csmEnabled.PropertyType == typeof(bool),
                "ControllerSourceMapping.IsEnabled is a readable bool", err, "IsEnabled");

            var id = Prop(desc, "ControllerID", null, out err);
            Check(A, id?.GetMethod != null, "ControllerDescription.ControllerID is readable", err, "ControllerID");
            foreach (var n in new[] { "VendorID", "ProductId" })
            {
                var p = Prop(desc, n, null, out err);
                Check(A, p?.GetMethod != null && typeof(IConvertible).IsAssignableFrom(p.PropertyType),
                    $"ControllerDescription.{n} is a readable number", err ?? (p == null ? null : $"declared {p.PropertyType}"), n);
            }
            var variant = Prop(desc, "Variant", null, out err);
            Check(A, variant?.GetMethod != null && variant.GetSetMethod(true) != null && variant.PropertyType == typeof(string),
                "ControllerDescription.Variant is a read/write string", err, "Variant");
            MethodInfo copyFrom = null;
            err = null;
            try { copyFrom = desc?.GetMethod("CopyFrom"); }
            catch (AmbiguousMatchException) { err = "ambiguous: more than one CopyFrom overload"; }
            Check(A, copyFrom != null && copyFrom.GetParameters().Length == 1
                     && copyFrom.GetParameters()[0].ParameterType.IsAssignableFrom(desc),
                "ControllerDescription.CopyFrom(ControllerDescription) is the single CopyFrom", err, "CopyFrom");
            Check(A, desc != null && !desc.IsAbstract && desc.GetConstructor(Type.EmptyTypes) != null,
                "ControllerDescription has a public parameterless constructor (cloned via Activator)");

            var available = Prop(state, "Available", null, out err);
            Check(A, available?.GetMethod != null && available.PropertyType == typeof(bool),
                "ControllerState.Available is a readable bool", err, "Available");
            var status = Prop(state, "ControllerStatus", null, out err);
            Check(A, status?.GetMethod != null, "ControllerState.ControllerStatus is readable", err, "ControllerStatus");
        }

        // ── Devices/Led ──────────────────────────────────────────────────────────
        private void Leds()
        {
            const string A = "LEDs";

            // LedDriverInjection resolves the DeviceDriver setter in its static
            // initialiser; read its verdict rather than repeating the lookup.
            var injection = PluginType("MozaPlugin.Devices.Led.LedDriverInjection");
            object canInject = null;
            string err = null;
            try { canInject = injection?.GetProperty("CanInject", AnyStatic)?.GetValue(null); }
            catch (Exception e) { err = Runner.Describe(e); }
            Check(A, canInject is bool b && b, "LedDriverInjection.CanInject: LedModuleSettings.DeviceDriver has a setter",
                err, "DeviceDriver");

            string backing = Const(injection, "ManagerBackingField");
            var connection = HostType("SimHub.Plugins.OutputPlugins.CommonDevices.Devices.StandardProtocolConnectionDevice", "SimHub.Plugins");
            var ourManager = PluginType("MozaPlugin.Devices.Haptics.MozaBaseConnectionManager");
            var managerField = backing == null ? null : Try(() => connection?.GetField(backing, NonPublicInstance));
            Check(A, managerField != null && ourManager != null && managerField.FieldType.IsAssignableFrom(ourManager),
                $"StandardProtocolConnectionDevice.{backing} exists and takes MozaBaseConnectionManager",
                managerField == null ? null : $"declared {managerField.FieldType}", backing);

            var settings = HostType("SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules.LedModuleSettings", "SimHub.Plugins");
            var encoders = Prop(settings, "EncodersCount", AnyInstance, out err);
            Check(A, encoders?.GetSetMethod(true) != null && encoders.PropertyType == typeof(int),
                "LedModuleSettings.EncodersCount is a writable int", err, "EncodersCount");

            // SimHubLedCompat binds LedDeviceState's widest constructor by parameter name
            // in its static constructor. Run it, then read back what it bound.
            var compat = PluginType("MozaPlugin.Devices.Led.SimHubLedCompat");
            if (compat == null) return;
            try
            {
                RuntimeHelpers.RunClassConstructor(compat.TypeHandle);
                var ctor = (ConstructorInfo)compat.GetField("s_ctor", AnyStatic)?.GetValue(null);
                var map = (int[])compat.GetField("s_argMap", AnyStatic)?.GetValue(null);
                var ours = (string[])compat.GetField("OurArgNames", AnyStatic)?.GetValue(null);
                if (ctor == null || map == null || ours == null)
                {
                    HarnessStale(A, "SimHubLedCompat.s_ctor / s_argMap / OurArgNames");
                    return;
                }
                var pars = ctor.GetParameters();
                Check(A, true, $"SimHubLedCompat bound LedDeviceState({pars.Length} parameters)");
                for (int i = 0; i < pars.Length; i++)
                    if (map[i] < 0)
                        _r.Add("WARN", A, $"LedDeviceState parameter '{pars[i].Name}' ({pars[i].ParameterType.Name}) " +
                                          "is not one SimHubLedCompat fills — that LED channel stays at its default");
                foreach (var name in ours.Where(n => pars.All(p => p.Name != n)))
                    _r.Add("WARN", A, $"LedDeviceState no longer takes '{name}' — the plugin's {name} channel is dropped");
            }
            catch (Exception e)
            {
                Check(A, false, "SimHubLedCompat binds LedDeviceState's constructor", Runner.Describe(e));
            }
        }

        // ── Devices/Haptics/MozaBaseHapticsBridge.cs, MozaLfeEffectDefaults.cs ─────
        private void Haptics()
        {
            const string A = "Haptics";
            var bridge = PluginType("MozaPlugin.Devices.Haptics.MozaBaseHapticsBridge");
            var descriptor = HostType("SimHub.Plugins.Devices.DeviceDescriptor", "SimHub.Plugins");

            const string MotorsName = "SimHub.Plugins.DataPlugins.ShakeItV3.Device.StandardProtocolMotorsDeviceExtension";
            const string ConnectionName = "SimHub.Plugins.OutputPlugins.CommonDevices.Devices.StandardProtocolConnectionDevice";
            var motors = descriptor?.Assembly.GetType(MotorsName, false);
            Check(A, motors != null, $"{MotorsName} exists (IsSupported)", null, MotorsName);
            Check(A, descriptor?.Assembly.GetType(ConnectionName, false) != null, $"{ConnectionName} exists (IsSupported)",
                null, ConnectionName);

            string hostedName = Const(bridge, "HostedPluginField");
            var hosted = hostedName == null ? null : Try(() => motors?.GetField(hostedName, NonPublicInstance));
            Check(A, hosted != null, $"StandardProtocolMotorsDeviceExtension.{hostedName} (non-public instance field)",
                null, hostedName);

            // Walk the hosted ShakeIt object graph with the bridge's own GetProp resolver.
            var find = Try(() => bridge?.GetMethod("FindPublicMember", AnyStatic, null, new[] { typeof(Type), typeof(string) }, null));
            if (find == null)
            {
                HarnessStale(A, "MozaBaseHapticsBridge.FindPublicMember(Type, string)");
                return;
            }
            Type Member(Type on, string name, out string why)
            {
                why = null;
                if (on == null) { why = "owner unresolved"; return null; }
                MemberInfo m;
                try { m = (MemberInfo)find.Invoke(null, new object[] { on, name }); }
                catch (Exception e) { why = Runner.Describe(e); return null; }
                if (m == null) { why = $"not found on {on}"; return null; }
                return m is PropertyInfo p ? p.PropertyType : ((FieldInfo)m).FieldType;
            }
            Type CheckMember(Type on, string name, string what)
            {
                var t = Member(on, name, out string why);
                Check(A, t != null, what, why, name);
                return t;
            }

            var settings = CheckMember(hosted?.FieldType, "Settings", "hosted ShakeIt plugin exposes Settings");
            var profilesList = CheckMember(settings, "Profiles", "ShakeIt settings expose Profiles");
            var profile = ElementOf(profilesList);
            Check(A, profile != null, "Profiles is a typed collection", profilesList == null ? null : $"declared {profilesList}", "Profiles");
            var current = CheckMember(settings, "CurrentProfile", "ShakeIt settings expose CurrentProfile");
            CheckMember(settings, "OutputManager", "ShakeIt settings expose OutputManager");
            CheckMember(settings, "CurrentOutputManager", "ShakeIt settings expose CurrentOutputManager");

            Type container = null;
            foreach (var p in new[] { profile, current }.Where(t => t != null).Distinct())
            {
                var list = CheckMember(p, "EffectsContainers", $"{p.Name} exposes EffectsContainers");
                container ??= ElementOf(list);
                CheckMember(p, "OutputManager", $"{p.Name} exposes OutputManager");
            }
            Check(A, container != null, "EffectsContainers is a typed collection", null, "EffectsContainers");

            var containerType = Member(container, "ContainerType", out string why);
            Check(A, containerType == typeof(string), "effect containers expose ContainerType (string)", why, "ContainerType");
            var enabled = Member(container, "IsEnabled", out why);
            Check(A, enabled == typeof(bool), "effect containers expose IsEnabled (bool)", why, "IsEnabled");

            var concrete = container == null
                ? new List<Type>()
                : Types(container.Assembly).Where(t => container.IsAssignableFrom(t) && !t.IsAbstract && !t.ContainsGenericParameters).ToList();
            Check(A, concrete.Any(g => Member(g, "EffectsContainers", out _) != null),
                "a group effect container exposes EffectsContainers (nested walk)", null, "EffectsContainers");

            // MozaLfeEffectDefaults: container.GetType().GetProperty("SettingsStore") on every
            // concrete container, then `is AbstractSettingsStore`.
            var store = HostType("SimHub.Plugins.DataPlugins.ShakeItV3.Settings.AbstractSettingsStore", "SimHub.Plugins");
            var broken = new List<string>();
            foreach (var t in concrete)
            {
                var p = Prop(t, "SettingsStore", null, out string e);
                if (p?.GetMethod == null || store == null || !store.IsAssignableFrom(p.PropertyType))
                    broken.Add($"{t.Name}{(e == null ? "" : $" ({e})")}");
            }
            Check(A, concrete.Count > 0 && broken.Count == 0,
                $"SettingsStore resolves to an AbstractSettingsStore on all {concrete.Count} concrete effect containers",
                broken.Count == 0 ? null : string.Join(", ", broken), "SettingsStore");

            // Install(): `outputManager is MotorsOutputManagerBase`.
            var motorsBase = HostType("SimHub.Plugins.DataPlugins.ShakeItV3.Device.MotorsOutputManagerBase", "SimHub.Plugins");
            Check(A, motorsBase != null && hosted != null && GenericArgumentsDeep(hosted.FieldType).Any(motorsBase.IsAssignableFrom),
                "the motors sub-device's output manager is a MotorsOutputManagerBase (channels provider swap)",
                hosted == null ? null : $"hosted {hosted.FieldType}");
        }

        // ── Telemetry/Frames/GameDataSnapshot.cs: recorded track map ───────────────
        private void TrackMap()
        {
            const string A = "TrackMap";
            const string Tracker = "SimHub.Plugins.DataPlugins.PersistantTracker.PersistantTrackerPlugin, SimHub.Plugins";
            var tracker = Type.GetType(Tracker, false);
            Check(A, tracker != null, $"Type.GetType(\"{Tracker}\") resolves", null, Tracker);

            var getMap = Try(() => tracker?.GetMethod("GetMap", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null));
            var record = HostType("GameReaderCommon.DataRecordBase", "GameReaderCommon");
            Check(A, getMap != null && record != null && record.IsAssignableFrom(getMap.ReturnType),
                "PersistantTrackerPlugin.GetMap() (public static, no parameters) returns a DataRecordBase",
                getMap == null ? null : $"returns {getMap.ReturnType}", "GetMap");
        }

        // ── Telemetry/Frames/GameDataSnapshot.cs: AC raw data ──────────────────────
        // GetRawDataObject() returns TRaw of the game's GameManagerBase<TRaw, ...>; the
        // plugin reads it only when it is the type named by GameDataSnapshot.AcRawDataType.
        private void AcRawData()
        {
            const string A = "AC raw";
            string rawName = Const(PluginType("MozaPlugin.Telemetry.Frames.GameDataSnapshot"), "AcRawDataType");
            Assembly ac = null;
            string err = null;
            try { ac = Assembly.Load("ACSharedMemory"); }
            catch (Exception e) { err = Runner.Describe(e); }
            var raw = rawName == null ? null : ac?.GetType(rawName, false);
            var managers = raw == null
                ? new List<string>()
                : Types(ac).Where(t => !t.IsAbstract && RawDataType(t) == raw).Select(t => t.Name).ToList();
            Check(A, managers.Count > 0, $"{rawName} is the raw data type of a SimHub AC game manager",
                err ?? (raw == null ? "type not found in ACSharedMemory" : "no GameManagerBase<TRaw> uses it"));
            if (managers.Count > 0) _r.Add("INFO", A, $"{raw.Name} feeds {string.Join(", ", managers)}");

            FieldInfo Field(Type owner, string name, Type type, string what)
            {
                var f = Try(() => owner?.GetField(name));
                Check(A, f != null && (type == null || f.FieldType == type), what,
                    owner == null ? "owner unresolved" : f == null ? Nearest(owner, name) : $"declared {f.FieldType}", name);
                return f;
            }

            var graphics = Field(raw, "Graphics", null, $"{raw?.Name}.Graphics (public field)");
            Field(graphics?.FieldType, "CarCoordinates", typeof(float[]), "Graphics.CarCoordinates (float[] field)");
            var info = Field(raw, "StaticInfo", null, $"{raw?.Name}.StaticInfo (public field)");
            Field(info?.FieldType, "Track", typeof(string), "StaticInfo.Track (string field)");
            Field(info?.FieldType, "TrackConfiguration", typeof(string), "StaticInfo.TrackConfiguration (string field)");
        }

        // ── Telemetry/SimHubPropertyResolver.cs ─────────────────────────────────────
        private void PropertyNames()
        {
            const string A = "Properties";
            var pm = HostType("SimHub.Plugins.PluginManager", "SimHub.Plugins");
            MethodInfo m = null;
            string err = null;
            try { m = pm?.GetMethod("GetAllPropertiesNames", AnyInstance); }
            catch (AmbiguousMatchException) { err = "ambiguous: more than one overload"; }
            Check(A, m != null && m.GetParameters().Length == 0 && ElementOf(m.ReturnType) == typeof(string),
                "PluginManager.GetAllPropertiesNames() takes no parameters and returns strings",
                err ?? (m == null ? null : $"returns {m.ReturnType}"), "GetAllPropertiesNames");
        }

        // ── Coverage ─────────────────────────────────────────────────────────────
        private void Reconcile()
        {
            const string A = "Coverage";
            foreach (var (type, method) in ReflectionLiterals.PluginSinks)
                if (PluginType(type)?.GetMethods(AnyStatic | AnyInstance).Any(m => m.Name == method) != true)
                    HarnessStale(A, $"IL scan helper {type}.{method} (ReflectionLiterals.PluginSinks)");

            var used = ReflectionLiterals.Collect(_types);
            foreach (var kv in used.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (_covered.Contains(kv.Key) || NotSimHub.ContainsKey(kv.Key)) continue;
                _r.Add("FAIL", A, $"\"{kv.Key}\" is looked up by reflection in {string.Join(", ", kv.Value)} " +
                                  "but no contract check covers it — add one to Contract.cs (or NotSimHub if it is not a SimHub member)");
            }
            foreach (var lit in _covered.Where(l => !used.ContainsKey(l)).OrderBy(l => l, StringComparer.Ordinal))
                _r.Add("FAIL", A, $"Contract.cs checks \"{lit}\" but the plugin no longer looks it up — update or drop the check");
            foreach (var lit in NotSimHub.Keys.Where(l => !used.ContainsKey(l)))
                _r.Add("FAIL", A, $"NotSimHub lists \"{lit}\" but the plugin no longer looks it up — drop the entry");
            _r.Add("INFO", A, $"{used.Count} reflection literals in the plugin, {_covered.Count} covered by checks, {NotSimHub.Count} exempt");
        }

        // ── helpers ──────────────────────────────────────────────────────────────
        private void Check(string area, bool ok, string what, string why = null, params string[] literals)
        {
            _r.ContractChecks++;
            foreach (var l in literals.Where(l => l != null)) _covered.Add(l);
            _r.Add(ok ? "PASS" : "FAIL", area, ok || why == null ? what : $"{what} — {why}");
        }

        private void HarnessStale(string area, string what)
            => _r.Add("FAIL", area, $"{what} not found in the plugin — tests/simhub-interface is out of date");

        private Type PluginType(string name)
        {
            var t = _plugin.GetType(name, false);
            if (t == null) HarnessStale("Harness", $"type {name}");
            return t;
        }

        private string Const(Type owner, string name)
        {
            if (owner == null) return null;
            var value = owner.GetField(name, AnyStatic)?.GetRawConstantValue() as string;
            if (value == null) HarnessStale("Harness", $"constant {owner.Name}.{name}");
            return value;
        }

        private static Type HostType(string fullName, string assembly) => Try(() => Type.GetType(fullName + ", " + assembly, false));

        private static PropertyInfo Prop(Type t, string name, BindingFlags? flags, out string error)
        {
            error = null;
            if (t == null) { error = "owner unresolved"; return null; }
            try { return flags.HasValue ? t.GetProperty(name, flags.Value) : t.GetProperty(name); }
            catch (AmbiguousMatchException) { error = "ambiguous: declared more than once"; return null; }
        }

        private static T Try<T>(Func<T> f) where T : class
        {
            try { return f(); } catch { return null; }
        }

        private static Type ElementOf(Type t)
        {
            if (t == null) return null;
            if (t.IsArray) return t.GetElementType();
            var e = t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? t
                : t.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return e?.GetGenericArguments()[0];
        }

        private static IEnumerable<Type> GenericArgumentsDeep(Type t)
        {
            if (t == null || !t.IsGenericType) yield break;
            foreach (var a in t.GetGenericArguments())
            {
                yield return a;
                foreach (var inner in GenericArgumentsDeep(a)) yield return inner;
            }
        }

        private static IEnumerable<Type> Types(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }

        // TRaw of GameReaderCommon.GameManagerBase<TRaw, ...>.
        private static Type RawDataType(Type manager)
        {
            for (var b = manager?.BaseType; b != null; b = b.BaseType)
                if (b.IsGenericType && b.GetGenericTypeDefinition().FullName?.StartsWith("GameReaderCommon.GameManagerBase`") == true)
                    return b.GetGenericArguments()[0];
            return null;
        }

        private static bool DelegateCompatible(Type delegateType, MethodInfo method)
        {
            var invoke = delegateType?.GetMethod("Invoke");
            if (invoke == null || invoke.ReturnType != method.ReturnType) return false;
            var want = invoke.GetParameters();
            var have = method.GetParameters();
            return want.Length == have.Length
                   && want.Zip(have, (w, h) => h.ParameterType.IsAssignableFrom(w.ParameterType)).All(x => x);
        }

        // What the owner declares instead, to make a failure self-explaining.
        private static string Nearest(Type owner, string name)
        {
            if (owner == null) return "owner unresolved";
            var near = owner.GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => (m is FieldInfo || m is PropertyInfo)
                            && (m.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                                || name.StartsWith(m.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(m => $"{m.Name} ({(m is FieldInfo ? "field" : "property")})")
                .ToList();
            return near.Count == 0 ? $"not on {owner}" : $"{owner.Name} has {string.Join(", ", near)}";
        }
    }
}
