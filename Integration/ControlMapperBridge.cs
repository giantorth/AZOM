using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using SimHub.Plugins;

namespace MozaPlugin.Integration
{
    /// <summary>
    /// Reflection-based plumbing that registers a <see cref="MozaVariantProvider"/>
    /// into SimHub's Control Mapper <c>VariantHelper.VariantProviders</c> private
    /// list and keeps it there. SimHub exposes no public registration API for
    /// variant providers (Simucube, Fanatec and Simagic are bundled inside
    /// <c>SimHub.Plugins.dll</c> and created by <c>VariantHelper.Start()</c>).
    /// <c>docs/controlmapper.md</c> holds the decompiled behaviour this relies on;
    /// two facts shape the bridge:
    /// <list type="bullet">
    /// <item><c>Start()</c> subscribes <c>VariantChanged</c> only for the providers
    /// it creates and early-returns once the list exists, so a provider added later
    /// is never subscribed. The bridge therefore asks SimHub to re-enumerate
    /// controllers itself, through the public
    /// <c>ControlMapperPluginSettings.UpdateControllerList()</c>, whenever the wheel
    /// variant changes.</item>
    /// <item><c>Stop()</c> disposes the providers and nulls the list.
    /// <c>RemapperWorker</c> calls <c>Start()</c>/<c>Stop()</c> every tick according
    /// to the "Recognize supported wheels as individual controllers" toggle and the
    /// output mode, so the list — and our entry in it — can vanish and reappear at
    /// any time. <see cref="Poll"/> re-inserts the provider when SimHub rebuilds the
    /// list.</item>
    /// </list>
    /// Every reflection step is defensive — if a future SimHub assembly renames any
    /// field or method, the bridge logs a single warning and leaves the rest of the
    /// plugin untouched.
    /// </summary>
    internal class ControlMapperBridge
    {
        private const string ControlMapperPluginTypeName =
            "SimHub.Plugins.OutputPlugins.ControlRemapper.ControlMapperPlugin";
        private const string RecognizeWheelsLabel =
            "Recognize supported wheels as individual controllers";
        private const int ListRecheckIntervalMs = 1000;

        // Replaced by the entry a prior plugin instance left in VariantProviders
        // (see TryRegister) — Poll() must drive the instance SimHub actually holds.
        private MozaVariantProvider _provider = new MozaVariantProvider();
        private MozaVariantProvider? _hookedProvider;

        private object? _variantHelper;
        private FieldInfo? _providersField;
        // Live list we last saw our provider in; null while SimHub has no list
        // (toggle off or Control Mapper output disabled).
        private IList? _providers;
        private bool _registered;
        private bool _giveUpLogged;
        private bool _updateRequestUnavailableLogged;
        private int _lastListRecheckTick;

        // ControlMapperPluginSettings (public type) and the members read through it.
        private object? _controlMapperSettings;
        private PropertyInfo? _settingsControllerMappingsProp;
        private PropertyInfo? _settingsRecognizeWheelsProp;
        private MethodInfo? _settingsUpdateControllerListMethod;
        private PropertyInfo? _csmDescriptionProp;
        private PropertyInfo? _csmStateProp;
        private PropertyInfo? _csmIsEnabledProp;
        private PropertyInfo? _descControllerIDProp;
        private PropertyInfo? _descVendorIDProp;
        private PropertyInfo? _descProductIdProp;
        private PropertyInfo? _descVariantProp;
        private PropertyInfo? _stateAvailableProp;
        private PropertyInfo? _stateStatusProp;
        private bool _settingsResolveAttempted;
        private string? _lastDiagVariant;
        // Cached so Unregister can detach the CollectionChanged handler: the
        // publisher (SimHub's ControllerMappings) outlives a plugin reload, and
        // RemoveEventHandler needs the exact Delegate instance AddEventHandler used.
        private EventInfo? _mappingsCollChangedEvent;
        private Delegate? _mappingsCollChangedHandler;
        private object? _mappingsCollChangedTarget;

        public bool IsRegistered => _registered;

        /// <summary>
        /// True once <see cref="LogGiveUp"/> has fired, meaning a specific
        /// reflection step failed and the bridge will not retry. Callers
        /// should suppress their generic "never became available" timeout
        /// warning when this is true — <see cref="LogGiveUp"/> has already
        /// logged the actual reason.
        /// </summary>
        public bool IsGivenUp => _giveUpLogged;

        /// <summary>
        /// Attempt to register the MOZA variant provider with Control
        /// Mapper. Idempotent — repeated calls after success return true
        /// without re-walking the reflection chain. Returns false when
        /// Control Mapper isn't loaded yet (caller should retry from
        /// <c>DataUpdate</c> up to a cap) OR when a SimHub assembly change
        /// has invalidated the lookup (in which case <see cref="LogGiveUp"/>
        /// has been called and the caller should stop retrying).
        /// Registration succeeds even while SimHub has no provider list (toggle
        /// off); <see cref="Poll"/> inserts the provider once the list exists.
        /// </summary>
        public bool TryRegister(PluginManager pm)
        {
            if (_registered) return true;
            if (pm == null) return false;
            if (_giveUpLogged) return false;

            try
            {
                Assembly simhubPluginsAsm = pm.GetType().Assembly;
                Type? cmType = simhubPluginsAsm.GetType(ControlMapperPluginTypeName, throwOnError: false);
                if (cmType == null)
                {
                    LogGiveUp("ControlMapperPlugin type not found in SimHub.Plugins");
                    return false;
                }

                MethodInfo? getPluginMethod = pm.GetType().GetMethod(
                    "GetPlugin",
                    BindingFlags.Public | BindingFlags.Instance,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);
                if (getPluginMethod == null || !getPluginMethod.IsGenericMethodDefinition)
                {
                    LogGiveUp("PluginManager.GetPlugin<T>() not found");
                    return false;
                }

                object? cmInstance;
                try { cmInstance = getPluginMethod.MakeGenericMethod(cmType).Invoke(pm, null); }
                catch (Exception ex)
                {
                    LogGiveUp($"GetPlugin<ControlMapperPlugin> threw: {ex.GetBaseException().Message}");
                    return false;
                }
                if (cmInstance == null)
                {
                    // ControlMapperPlugin may not be loaded yet (plugin load
                    // ordering). Quiet retry; caller polls again next tick.
                    return false;
                }

                FieldInfo? rwField = cmType.GetField(
                    "remapperWorker", BindingFlags.NonPublic | BindingFlags.Instance);
                if (rwField == null)
                {
                    LogGiveUp("ControlMapperPlugin.remapperWorker field not found");
                    return false;
                }
                object? rw = rwField.GetValue(cmInstance);
                if (rw == null) return false;

                FieldInfo? vhField = rw.GetType().GetField(
                    "variantHelper", BindingFlags.NonPublic | BindingFlags.Instance);
                if (vhField == null)
                {
                    LogGiveUp("RemapperWorker.variantHelper field not found");
                    return false;
                }
                object? vh = vhField.GetValue(rw);
                if (vh == null) return false;

                FieldInfo? providersField = vh.GetType().GetField(
                    "VariantProviders", BindingFlags.NonPublic | BindingFlags.Instance);
                if (providersField == null)
                {
                    LogGiveUp("VariantHelper.VariantProviders field not found");
                    return false;
                }

                // Settings first: the toggle, the async re-enumeration entry point
                // and the mapping dump all hang off ControlMapperPluginSettings.
                ResolveSettingsReflection(cmType, cmInstance);
                HookMappingsCollectionChanged();

                _variantHelper = vh;
                _providersField = providersField;

                bool listPresent;
                int count;
                bool adopted;
                string? listError;
                lock (vh.GetType())
                {
                    listError = SyncProviderIntoList(out listPresent, out count, out adopted, out _);
                }
                if (listError != null)
                {
                    LogGiveUp(listError);
                    return false;
                }

                HookProviderEvent();
                _registered = true;
                _lastListRecheckTick = Environment.TickCount;

                if (listPresent)
                {
                    MozaLog.Info(
                        $"[AZOM] ControlMapper bridge: {(adopted ? "reusing existing" : "registered")} MozaVariantProvider " +
                        $"({count} providers total; \"{RecognizeWheelsLabel}\": {DescribeToggle()})");
                }
                else
                {
                    MozaLog.Warn(
                        $"[AZOM] ControlMapper bridge: SimHub has no variant-provider list — \"{RecognizeWheelsLabel}\" " +
                        $"is {DescribeToggle()} or Control Mapper output is disabled. MOZA per-wheel mappings stay inactive " +
                        "until it is enabled; the provider is inserted automatically when SimHub creates the list.");
                }

                // Controllers already plugged in were enumerated before our provider
                // existed; re-key them now through SimHub's own async path.
                RequestControllerListUpdate("registration");
                return true;
            }
            catch (Exception ex)
            {
                LogGiveUp($"unexpected exception: {ex.GetBaseException().Message}");
                return false;
            }
        }

        // Caller holds lock(_variantHelper.GetType()) — the same Type object
        // VariantHelper.Start()/Stop() lock on while they build or drop the list.
        // Adopts an existing MozaVariantProvider entry or appends ours. Returns a
        // give-up reason when the field holds something other than a list.
        private string? SyncProviderIntoList(out bool listPresent, out int count, out bool adopted, out bool inserted)
        {
            listPresent = false;
            count = 0;
            adopted = false;
            inserted = false;

            object? raw = _providersField!.GetValue(_variantHelper);
            if (raw == null)
            {
                _providers = null;
                return null;
            }
            if (raw is not IList list)
            {
                return "VariantHelper.VariantProviders is not an IList (declared type: "
                    + _providersField.FieldType.FullName + ")";
            }

            listPresent = true;
            MozaVariantProvider? existing = null;
            foreach (object? entry in list)
            {
                if (entry is MozaVariantProvider p)
                {
                    existing = p;
                    break;
                }
            }
            if (existing != null)
            {
                if (!ReferenceEquals(existing, _provider))
                {
                    _provider = existing;
                    adopted = true;
                }
            }
            else
            {
                list.Add(_provider);
                inserted = true;
            }
            _providers = list;
            count = list.Count;
            return null;
        }

        private void HookProviderEvent()
        {
            if (ReferenceEquals(_hookedProvider, _provider)) return;
            UnhookProviderEvent();
            _provider.VariantChanged += OnProviderVariantChanged;
            _hookedProvider = _provider;
        }

        private void UnhookProviderEvent()
        {
            if (_hookedProvider == null) return;
            try { _hookedProvider.VariantChanged -= OnProviderVariantChanged; } catch { }
            _hookedProvider = null;
        }

        // SimHub's VariantHelper never subscribed to this provider (see class
        // summary), so the re-enumeration its bundled providers get for free is
        // requested here.
        private void OnProviderVariantChanged(object? sender, EventArgs e)
            => RequestControllerListUpdate("variant changed");

        // ControlMapperPluginSettings.UpdateControllerList() — public, Task.Run
        // inside SimHub, ends in RemapperWorker.UpdateControllerList. Same path the
        // Control Mapper UI and the bundled providers use.
        private void RequestControllerListUpdate(string reason)
        {
            if (_controlMapperSettings == null || _settingsUpdateControllerListMethod == null)
            {
                if (!_updateRequestUnavailableLogged)
                {
                    _updateRequestUnavailableLogged = true;
                    MozaLog.Warn(
                        "[AZOM] ControlMapper bridge: ControlMapperPluginSettings.UpdateControllerList not found — " +
                        "controllers keep the variant they were enumerated with until SimHub rescans " +
                        "(USB change or manual rescan).");
                }
                return;
            }
            try
            {
                _settingsUpdateControllerListMethod.Invoke(_controlMapperSettings, null);
                MozaLog.Debug($"[AZOM] ControlMapper bridge: requested controller re-enumeration ({reason})");
            }
            catch (Exception ex)
            {
                MozaLog.Debug(
                    $"[AZOM] ControlMapper bridge: UpdateControllerList threw — {ex.GetBaseException().Message}");
            }
        }

        /// <summary>
        /// Drive the provider's wheel-change detection and keep the provider in
        /// SimHub's list. Called once per <c>MozaPlugin.DataUpdate</c> tick. Cheap —
        /// the provider compares a single string against its cache and only fires
        /// <see cref="MozaVariantProvider.VariantChanged"/> on transition; the list
        /// check runs once a second.
        ///
        /// We deliberately do NOT clone mappings or override <c>Available</c>
        /// here. SimHub handles per-variant mappings natively: every match
        /// predicate in <c>RemapperWorker.UpdateControllerList</c> includes
        /// Variant, so each variant gets its own slot in ControllerMappings and a
        /// device whose current variant has no matching mapping shows up in
        /// UnmappedControllers (the "Add Source Controller" dropdown), and
        /// <c>SharpHelper.AquireController</c> compares the live variant against
        /// <c>Description.Variant</c> on every acquire attempt, so only the mapping
        /// whose stored Variant matches the attached wheel forwards input.
        /// </summary>
        public void Poll()
        {
            if (!_registered) return;
            try { _provider.Poll(); }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] ControlMapper bridge poll: {ex.Message}");
            }

            RecheckProviderList();

            // We deliberately do NOT auto-create a ControllerSourceMapping for a
            // newly-attached wheel. The user adds each MOZA source controller via
            // SimHub's "Add Source Controller" flow; the provider supplies the
            // Variant string and AquireController's per-variant gate dispatches
            // input to the matching mapping. (A previous build synthesized a
            // per-variant mapping here, but it produced an unwanted extra mapping
            // that SimHub never marked Available — see docs/controlmapper.md.)
            string? currentVariant = ComputeCurrentVariant();

            // Diagnostic: when the wheel-side variant changes, dump every MOZA
            // mapping's stored Variant + Status + Available + IsEnabled + ControllerID.
            try
            {
                if (!string.Equals(currentVariant, _lastDiagVariant, StringComparison.Ordinal))
                {
                    DumpMappingsState(currentVariant ?? "<none>");
                    _lastDiagVariant = currentVariant;
                }
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] CM diag: {ex.Message}");
            }
        }

        // SimHub drops the provider list whenever the toggle is off or Control
        // Mapper output is disabled, and rebuilds it without us when either comes
        // back. Re-insert and re-key the controllers when that happens.
        private void RecheckProviderList()
        {
            int now = Environment.TickCount;
            if (unchecked(now - _lastListRecheckTick) < ListRecheckIntervalMs) return;
            _lastListRecheckTick = now;
            if (_variantHelper == null || _providersField == null) return;

            bool hadList = _providers != null;
            bool listPresent;
            int count;
            bool adopted;
            bool inserted;
            string? error;
            try
            {
                lock (_variantHelper.GetType())
                {
                    error = SyncProviderIntoList(out listPresent, out count, out adopted, out inserted);
                }
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] ControlMapper bridge: provider list recheck threw — {ex.Message}");
                return;
            }
            if (error != null)
            {
                MozaLog.Debug($"[AZOM] ControlMapper bridge: {error}");
                return;
            }

            if (!listPresent)
            {
                if (hadList)
                {
                    MozaLog.Info(
                        $"[AZOM] ControlMapper bridge: SimHub dropped its variant-provider list — \"{RecognizeWheelsLabel}\" " +
                        "turned off or Control Mapper output disabled; MOZA per-wheel mappings inactive until it is back");
                }
                return;
            }

            if (inserted || adopted || !hadList)
            {
                HookProviderEvent();
                MozaLog.Info(
                    $"[AZOM] ControlMapper bridge: {(inserted ? "re-inserted" : "found")} MozaVariantProvider after SimHub " +
                    $"rebuilt its list ({count} providers total; \"{RecognizeWheelsLabel}\": {DescribeToggle()})");
                RequestControllerListUpdate("provider list rebuilt");
            }
        }

        private bool? TryGetRecognizeIndividualWheels()
        {
            if (_controlMapperSettings == null || _settingsRecognizeWheelsProp == null) return null;
            try { return _settingsRecognizeWheelsProp.GetValue(_controlMapperSettings) as bool?; }
            catch { return null; }
        }

        private string DescribeToggle() => TryGetRecognizeIndividualWheels() switch
        {
            true => "on",
            false => "off",
            _ => "unknown",
        };

        /// <summary>
        /// Diagnostics-tab text: bridge state, SimHub's toggle, the live variant and
        /// every MOZA wheelbase/hub mapping with its acquire status.
        /// </summary>
        public string BuildDiagnostics()
        {
            var sb = new StringBuilder();
            bool listPresent = false;
            bool inList = false;
            int count = 0;
            if (_variantHelper != null && _providersField != null)
            {
                try
                {
                    lock (_variantHelper.GetType())
                    {
                        if (_providersField.GetValue(_variantHelper) is IList list)
                        {
                            listPresent = true;
                            count = list.Count;
                            foreach (object? entry in list)
                            {
                                if (ReferenceEquals(entry, _provider)) { inList = true; break; }
                            }
                        }
                    }
                }
                catch { }
            }
            sb.AppendLine(
                $"Bridge:         registered={(_registered ? "yes" : "no")}  givenUp={(_giveUpLogged ? "yes" : "no")}  " +
                $"simhubList={(listPresent ? count + " provider(s)" : "none")}  providerInList={(inList ? "yes" : "no")}");
            sb.AppendLine($"Toggle:         {DescribeToggle()}  ({RecognizeWheelsLabel})");
            sb.AppendLine($"Variant:        {ComputeCurrentVariant() ?? "—"}");

            List<string>? lines = CollectMozaMappingLines(out int total);
            if (lines == null)
            {
                sb.AppendLine("Mappings:       (settings reflection unavailable)");
            }
            else
            {
                sb.AppendLine($"Mappings:       {lines.Count} MOZA wheelbase/hub of {total} total");
                foreach (string line in lines) sb.AppendLine("  " + line);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Log the current state (Variant / ControllerID / Status / Available /
        /// IsEnabled) of every MOZA wheelbase mapping. Called once per detected
        /// variant transition and on every ControllerMappings change.
        /// </summary>
        private void DumpMappingsState(string currentVariantLabel)
        {
            List<string>? lines = CollectMozaMappingLines(out int total);
            if (lines == null)
            {
                MozaLog.Debug("[AZOM] CM diag: reflection unavailable, skipping dump");
                return;
            }
            MozaLog.Debug(
                $"[AZOM] CM diag (variant=\"{currentVariantLabel}\", toggle={DescribeToggle()}): "
                + $"ControllerMappings.Count={total}");
            foreach (string line in lines) MozaLog.Debug("[AZOM] CM diag   " + line);
            MozaLog.Debug($"[AZOM] CM diag: {lines.Count} MOZA mapping(s) total");
        }

        // One line per MOZA wheelbase/hub ControllerSourceMapping; null when the
        // settings reflection didn't resolve.
        private List<string>? CollectMozaMappingLines(out int totalMappings)
        {
            totalMappings = 0;
            if (_controlMapperSettings == null
                || _settingsControllerMappingsProp == null
                || _csmDescriptionProp == null
                || _csmStateProp == null
                || _descVendorIDProp == null
                || _descVariantProp == null
                || _descControllerIDProp == null
                || _stateAvailableProp == null)
                return null;

            object? mappingsObj;
            try { mappingsObj = _settingsControllerMappingsProp.GetValue(_controlMapperSettings); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] CM diag: get mappings: {ex.Message}"); return null; }
            if (mappingsObj is not IList mappings) return null;

            var lines = new List<string>();
            int idx = 0;
            object?[] snapshot;
            try
            {
                snapshot = new object?[mappings.Count];
                mappings.CopyTo(snapshot, 0);
            }
            catch { return null; }
            totalMappings = snapshot.Length;
            foreach (object? entry in snapshot)
            {
                idx++;
                if (entry == null) continue;
                object? desc;
                try { desc = _csmDescriptionProp.GetValue(entry); }
                catch { continue; }
                if (desc == null) continue;
                if (!IsMozaWheelbaseOrHubDesc(desc)) continue;

                string variant = (_descVariantProp.GetValue(desc) as string) ?? "<null>";
                object? cidObj = null;
                try { cidObj = _descControllerIDProp.GetValue(desc); } catch { }
                string cidShort = cidObj?.ToString() ?? "<null>";
                if (cidShort.Length > 8) cidShort = cidShort.Substring(0, 8);
                object? state = null;
                try { state = _csmStateProp.GetValue(entry); } catch { }
                string availStr = "<n/a>";
                string statusStr = "<n/a>";
                if (state != null)
                {
                    try { availStr = _stateAvailableProp.GetValue(state) is bool b ? b.ToString() : "<?>"; }
                    catch { }
                    if (_stateStatusProp != null)
                    {
                        try { statusStr = _stateStatusProp.GetValue(state)?.ToString() ?? "<null>"; }
                        catch { }
                    }
                }
                string enabledStr = "<n/a>";
                if (_csmIsEnabledProp != null)
                {
                    try { enabledStr = _csmIsEnabledProp.GetValue(entry) is bool eb ? eb.ToString() : "<?>"; }
                    catch { }
                }
                lines.Add(
                    $"#{idx}: Variant=\"{variant}\" CtrlID={cidShort}.. Status={statusStr} "
                    + $"Available={availStr} IsEnabled={enabledStr} DescObj={GetHash(desc):X}");
            }
            return lines;
        }

        private void ResolveSettingsReflection(Type cmType, object cmInstance)
        {
            if (_settingsResolveAttempted) return;
            _settingsResolveAttempted = true;
            try
            {
                FieldInfo? settingsField = cmType.GetField(
                    "controlMapperPluginSettings",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _controlMapperSettings = settingsField?.GetValue(cmInstance);
                if (_controlMapperSettings == null)
                {
                    MozaLog.Debug("[AZOM] CM diag: controlMapperPluginSettings unavailable");
                    return;
                }

                Type settingsType = _controlMapperSettings.GetType();
                _settingsControllerMappingsProp = settingsType.GetProperty(
                    "ControllerMappings", BindingFlags.Public | BindingFlags.Instance);
                // SimHub's own spelling.
                _settingsRecognizeWheelsProp = settingsType.GetProperty(
                    "RecognizeIndiviualWheels", BindingFlags.Public | BindingFlags.Instance);
                _settingsUpdateControllerListMethod = settingsType.GetMethod(
                    "UpdateControllerList",
                    BindingFlags.Public | BindingFlags.Instance,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);

                Assembly asm = settingsType.Assembly;
                Type? csmType = asm.GetType(
                    "SimHub.Plugins.OutputPlugins.ControlRemapper.Models.ControllerSourceMapping", throwOnError: false);
                Type? descType = asm.GetType(
                    "SimHub.Plugins.OutputPlugins.ControlRemapper.Models.ControllerDescription", throwOnError: false);
                Type? stateType = asm.GetType(
                    "SimHub.Plugins.OutputPlugins.ControlRemapper.Models.ControllerState", throwOnError: false);
                if (csmType == null || descType == null || stateType == null) return;

                _csmDescriptionProp = csmType.GetProperty("ControllerDescription");
                _csmStateProp = csmType.GetProperty("ControllerState");
                _csmIsEnabledProp = csmType.GetProperty("IsEnabled");
                _descControllerIDProp = descType.GetProperty("ControllerID");
                _descVendorIDProp = descType.GetProperty("VendorID");
                _descProductIdProp = descType.GetProperty("ProductId");
                _descVariantProp = descType.GetProperty("Variant");
                _stateAvailableProp = stateType.GetProperty("Available");
                _stateStatusProp = stateType.GetProperty("ControllerStatus");
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] CM diag: reflection resolve: {ex.Message}");
            }
        }

        private static int GetHash(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);

        /// <summary>
        /// Returns true iff the given <c>ControllerDescription</c> represents a
        /// MOZA wheelbase or hub — i.e. a USB endpoint that can carry a
        /// swappable wheel. Keeps the bridge's per-mapping bookkeeping (clone-
        /// description on Add, diag dump) narrowed to devices that actually have
        /// a wheel variant, so MOZA pedals / shifters / handbrakes / mBoosters /
        /// dashboards under VID 0x346E are left alone. Matches
        /// <see cref="MozaVariantProvider.GetVariant"/>'s own filter so the
        /// variant pipeline and the bridge agree on scope.
        /// </summary>
        private bool IsMozaWheelbaseOrHubDesc(object? desc)
        {
            if (desc == null) return false;
            if (_descVendorIDProp == null || _descProductIdProp == null) return false;
            int vid;
            try { vid = Convert.ToInt32(_descVendorIDProp.GetValue(desc)); }
            catch { return false; }
            if (vid != Protocol.MozaProtocol.VendorId) return false;
            int pid;
            try { pid = Convert.ToInt32(_descProductIdProp.GetValue(desc)); }
            catch { return false; }
            ushort pidU = unchecked((ushort)pid);
            return Protocol.MozaUsbIds.IsWheelbasePid(pidU)
                || Protocol.MozaUsbIds.IsHubPid(pidU);
        }

        // Resolve the current wheel variant friendly-name from live plugin state.
        // Delegates to the canonical resolver so the old-protocol ("ES") rule
        // and any future variant logic stay in one place.
        private static string? ComputeCurrentVariant() => MozaVariantProvider.ComputeCurrentVariant();

        /// <summary>
        /// Subscribe to <c>ControllerMappings.CollectionChanged</c> so the
        /// bridge sees every "Add Source Controller" (and dumps the mapping state
        /// on any change). Reflection-based since the concrete event type
        /// (<c>NotifyCollectionChangedEventHandler</c>) lives in
        /// <c>System.Collections.Specialized</c> and is wired via
        /// <c>ObservableCollection&lt;T&gt;</c>.
        /// </summary>
        private void HookMappingsCollectionChanged()
        {
            if (_controlMapperSettings == null || _settingsControllerMappingsProp == null) return;
            if (_mappingsCollChangedHandler != null) return;
            object? mappingsObj;
            try { mappingsObj = _settingsControllerMappingsProp.GetValue(_controlMapperSettings); }
            catch { return; }
            if (mappingsObj == null) return;

            // ObservableCollection<T> implements INotifyCollectionChanged
            EventInfo? evt = mappingsObj.GetType().GetEvent(
                "CollectionChanged",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (evt == null)
            {
                // Walk interfaces
                foreach (var i in mappingsObj.GetType().GetInterfaces())
                {
                    evt = i.GetEvent("CollectionChanged");
                    if (evt != null) break;
                }
            }
            if (evt == null)
            {
                MozaLog.Debug("[AZOM] CM diag: CollectionChanged event not found on ControllerMappings");
                return;
            }
            try
            {
                Delegate handler = Delegate.CreateDelegate(
                    evt.EventHandlerType!, this,
                    typeof(ControlMapperBridge).GetMethod(
                        nameof(OnControllerMappingsChanged),
                        BindingFlags.NonPublic | BindingFlags.Instance)!);
                evt.AddEventHandler(mappingsObj, handler);
                _mappingsCollChangedEvent = evt;
                _mappingsCollChangedHandler = handler;
                _mappingsCollChangedTarget = mappingsObj;
                MozaLog.Debug("[AZOM] CM diag: subscribed to ControllerMappings.CollectionChanged");
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] CM diag: subscribe failed: {ex.Message}");
            }
        }

        // Detach the CollectionChanged handler subscribed in
        // HookMappingsCollectionChanged. Must run on every teardown — the
        // publisher lives in SimHub and would otherwise keep this bridge alive.
        private void UnhookMappingsCollectionChanged()
        {
            if (_mappingsCollChangedEvent == null
                || _mappingsCollChangedHandler == null
                || _mappingsCollChangedTarget == null)
                return;
            try { _mappingsCollChangedEvent.RemoveEventHandler(_mappingsCollChangedTarget, _mappingsCollChangedHandler); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] CM diag: unsubscribe failed: {ex.Message}"); }
            finally
            {
                _mappingsCollChangedEvent = null;
                _mappingsCollChangedHandler = null;
                _mappingsCollChangedTarget = null;
            }
        }

        private void OnControllerMappingsChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            try
            {
                MozaLog.Debug(
                    $"[AZOM] CM diag: ControllerMappings changed (action={e.Action}, "
                    + $"newCount={(e.NewItems?.Count ?? 0)}, oldCount={(e.OldItems?.Count ?? 0)})");
                string? currentVariant = ComputeCurrentVariant();

                // Detach shared Description references on newly-added MOZA
                // wheelbase mappings. SimHub's "Add Source Controller" passes
                // the SAME ControllerDescription object reference into multiple
                // ControllerSourceMappings (verified via DescObj hash in the
                // diag dump), and UpdateOrAdd into UnmappedControllers uses a
                // ControllerID-only predicate whose updater (CopyFrom) mutates
                // the shared description on every UpdateControllerList tick.
                // The result is that one MOZA mapping's Variant can mutate
                // through the shared reference and "infect" the others.
                //
                // Fix: deep-clone the new CSM's Description into an independent
                // object and, while SimHub is recognising individual wheels,
                // stamp its Variant with the currently-attached wheel — that's
                // what the user meant to add. AquireController's variant gating
                // then works correctly per-mapping.
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add
                    && e.NewItems != null)
                {
                    foreach (object? added in e.NewItems)
                    {
                        if (added == null) continue;
                        // Stamp the variant first, then dedupe — the dedupe key
                        // reads the freshly-stamped variant.
                        DetachMozaDescription(added, currentVariant);
                        DeduplicateMozaMapping(added);
                    }
                }

                DumpMappingsState(currentVariant ?? "<none>");
            }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] CM diag: collchanged handler: {ex.Message}"); }
        }

        /// <summary>
        /// If <paramref name="csm"/> is a MOZA wheelbase
        /// ControllerSourceMapping that shares its Description object with
        /// anything else, replace its Description with an independent clone.
        /// The clone's <c>Variant</c> is set to the currently-attached wheel only
        /// while SimHub's "Recognize supported wheels as individual controllers"
        /// toggle is on: with it off, <c>VariantHelper.GetVariant</c> is null for
        /// every device and a stamped mapping could never acquire.
        /// </summary>
        private void DetachMozaDescription(object csm, string? currentVariant)
        {
            if (_csmDescriptionProp == null || _descVendorIDProp == null
                || _descProductIdProp == null || _descVariantProp == null) return;

            object? desc;
            try { desc = _csmDescriptionProp.GetValue(csm); } catch { return; }
            if (desc == null) return;
            if (!IsMozaWheelbaseOrHubDesc(desc)) return;

            // Build an independent clone of the description.
            Type descType = desc.GetType();
            MethodInfo? copyFrom = descType.GetMethod("CopyFrom");
            if (copyFrom == null) { MozaLog.Debug("[AZOM] CM diag: CopyFrom not found on Description"); return; }
            object? clone;
            try { clone = Activator.CreateInstance(descType); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] CM diag: clone ctor: {ex.Message}"); return; }
            if (clone == null) return;
            try { copyFrom.Invoke(clone, new[] { desc }); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] CM diag: clone CopyFrom: {ex.Message}"); return; }

            // Toggle on and a wheel detected: stamp the live wheel. Otherwise keep
            // whatever SimHub enumerated (null with the toggle off), so the mapping
            // matches what AquireController will compute.
            int oldHash = GetHash(desc);
            string? originalVariant;
            try { originalVariant = _descVariantProp.GetValue(desc) as string; }
            catch { originalVariant = null; }
            bool? toggle = TryGetRecognizeIndividualWheels();
            string targetVariant = (toggle == true ? currentVariant : null) ?? originalVariant ?? string.Empty;
            try { _descVariantProp.SetValue(clone, targetVariant); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] CM diag: clone set Variant: {ex.Message}"); }

            // Replace the CSM's Description with the independent clone.
            try { _csmDescriptionProp.SetValue(csm, clone); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] CM diag: set CSM Description: {ex.Message}"); return; }

            MozaLog.Debug(
                $"[AZOM] CM diag: detached shared Description on new MOZA mapping "
                + $"(oldDescObj={oldHash:X}, newDescObj={GetHash(clone):X}, "
                + $"originalVariant=\"{originalVariant ?? "<null>"}\", "
                + $"setVariant=\"{targetVariant}\", toggle={DescribeToggle()})");
        }

        /// <summary>
        /// Prevent double-adding the same MOZA wheelbase. The base can enumerate
        /// under two DirectInput interface paths (one with a USB serial, one
        /// synthesized — observed under Wine), so SimHub's "Add Source Controller"
        /// lists it twice and lets the user map the same physical device for the
        /// same wheel more than once; both mappings then acquire the same device
        /// and double-process its input. When a freshly-added MOZA wheelbase/hub
        /// mapping matches an EXISTING one on VID+PID+Variant (same wheelbase,
        /// same wheel), the just-added one is redundant and gets removed. Distinct
        /// variants on the same base (e.g. CS Pro + KS) are intentional per-wheel
        /// mappings and are kept.
        /// </summary>
        private void DeduplicateMozaMapping(object addedCsm)
        {
            if (_settingsControllerMappingsProp == null || _controlMapperSettings == null
                || _csmDescriptionProp == null || _descVendorIDProp == null
                || _descProductIdProp == null || _descVariantProp == null) return;

            object? addedDesc;
            try { addedDesc = _csmDescriptionProp.GetValue(addedCsm); } catch { return; }
            if (addedDesc == null || !IsMozaWheelbaseOrHubDesc(addedDesc)) return;

            int vid, pid;
            try
            {
                vid = Convert.ToInt32(_descVendorIDProp.GetValue(addedDesc));
                pid = Convert.ToInt32(_descProductIdProp.GetValue(addedDesc));
            }
            catch { return; }
            string variant = (_descVariantProp.GetValue(addedDesc) as string) ?? string.Empty;

            object? mappingsObj;
            try { mappingsObj = _settingsControllerMappingsProp.GetValue(_controlMapperSettings); }
            catch { return; }
            if (mappingsObj is not IList mappings) return;

            // Is there ANOTHER MOZA wheelbase/hub mapping with the same
            // VID+PID+Variant? If so the just-added one is a duplicate.
            bool duplicate = false;
            foreach (object? entry in mappings)
            {
                if (entry == null || ReferenceEquals(entry, addedCsm)) continue;
                object? d;
                try { d = _csmDescriptionProp.GetValue(entry); } catch { continue; }
                if (d == null || !IsMozaWheelbaseOrHubDesc(d)) continue;
                int v2, p2;
                try
                {
                    v2 = Convert.ToInt32(_descVendorIDProp.GetValue(d));
                    p2 = Convert.ToInt32(_descProductIdProp.GetValue(d));
                }
                catch { continue; }
                if (v2 != vid || p2 != pid) continue;
                string var2 = (_descVariantProp.GetValue(d) as string) ?? string.Empty;
                if (string.Equals(var2, variant, StringComparison.OrdinalIgnoreCase))
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate) return;

            // Remove the just-added duplicate. Must defer: we're inside the
            // collection's own CollectionChanged dispatch, and ObservableCollection
            // throws on re-entrant mutation. BeginInvoke runs after this event
            // unwinds, on the UI thread the collection requires.
            System.Windows.Threading.Dispatcher? dispatcher = null;
            try { dispatcher = System.Windows.Application.Current?.Dispatcher; }
            catch { }
            if (dispatcher == null)
            {
                MozaLog.Debug("[AZOM] CM dedupe: no dispatcher; cannot remove duplicate mapping");
                return;
            }

            var capturedMappings = mappings;
            var capturedCsm = addedCsm;
            var capturedVariant = variant;
            try
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (capturedMappings.Contains(capturedCsm))
                        {
                            capturedMappings.Remove(capturedCsm);
                            MozaLog.Info(
                                $"[AZOM] CM: removed duplicate MOZA wheelbase mapping for variant "
                                + $"\"{capturedVariant}\" — that wheelbase + wheel is already mapped");
                        }
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Warn($"[AZOM] CM dedupe: remove threw: {ex.GetBaseException().Message}");
                    }
                }));
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] CM dedupe: BeginInvoke threw: {ex.GetBaseException().Message}");
            }
        }

        /// <summary>
        /// Remove the provider from Control Mapper's list so a plugin
        /// reload without SimHub restart doesn't leave a dead provider
        /// hanging in <c>VariantHelper.VariantProviders</c>. Called from
        /// <c>MozaPlugin.End</c>.
        /// </summary>
        public void Unregister()
        {
            // Detach handlers first — they can be subscribed even when provider
            // registration never completed (_registered == false).
            UnhookMappingsCollectionChanged();
            UnhookProviderEvent();
            if (!_registered) return;
            try
            {
                if (_variantHelper != null && _providersField != null)
                {
                    lock (_variantHelper.GetType())
                    {
                        // The live list — the cached one may be an orphan SimHub
                        // already dropped.
                        if (_providersField.GetValue(_variantHelper) is IList live)
                        {
                            for (int i = live.Count - 1; i >= 0; i--)
                            {
                                if (live[i] is MozaVariantProvider)
                                    live.RemoveAt(i);
                            }
                        }
                    }
                }
                MozaLog.Info("[AZOM] ControlMapper bridge: MozaVariantProvider removed");
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] ControlMapper bridge unregister: {ex.Message}");
            }
            finally
            {
                _providers = null;
                _variantHelper = null;
                _providersField = null;
                _registered = false;
            }
        }

        private void LogGiveUp(string reason)
        {
            if (_giveUpLogged) return;
            _giveUpLogged = true;
            MozaLog.Warn(
                $"[AZOM] ControlMapper bridge: {reason} — Control Mapper variant integration disabled " +
                "for this session. The wheelbase will still appear in Control Mapper without a variant.");
        }
    }
}
