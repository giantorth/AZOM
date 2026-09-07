using System;
using System.Collections;
using System.Reflection;
using SimHub.Plugins;

namespace MozaPlugin.Integration
{
    /// <summary>
    /// Reflection-based plumbing that registers a
    /// <see cref="MozaVariantProvider"/> into SimHub's Control Mapper
    /// <c>VariantHelper.VariantProviders</c> private list, then drives the
    /// provider's wheel-change detection each tick.
    ///
    /// SimHub exposes no public registration API for variant providers
    /// (only Fanatec and Simucube, both bundled inside
    /// <c>SimHub.Plugins.dll</c>, are wired up by the helper at startup).
    /// See <c>docs/controlmapper.md</c> for the research that defined this
    /// approach. The bridge walks
    /// <c>ControlMapperPlugin → remapperWorker → variantHelper →
    /// VariantProviders</c> by field name and appends our provider. After
    /// appending, it calls <c>RemapperWorker.UpdateVariantProviders</c> so
    /// the helper re-subscribes to <see cref="MozaVariantProvider.VariantChanged"/>.
    ///
    /// Every reflection step is defensive — if a future SimHub assembly
    /// renames any field or method, the bridge logs a single warning and
    /// leaves the rest of the plugin untouched.
    /// </summary>
    internal class ControlMapperBridge
    {
        private const string ControlMapperPluginTypeName =
            "SimHub.Plugins.OutputPlugins.ControlRemapper.ControlMapperPlugin";

        // Replaced by the entry a prior plugin instance left in VariantProviders
        // (see Register) — Poll() must drive the instance SimHub actually holds.
        private MozaVariantProvider _provider = new MozaVariantProvider();

        private object? _remapperWorker;
        private IList? _providers;
        private MethodInfo? _updateProvidersMethod;
        // RemapperWorker.UpdateControllerList — invoked once at registration to
        // re-key a wheel already plugged in at SimHub launch with the MOZA variant.
        private MethodInfo? _updateControllerListMethod;
        private bool _registered;
        private bool _giveUpLogged;

        // Diagnostic: cached reflection for dumping ControllerMappings state
        // when our provider detects a variant change. Lets us see, in the
        // SimHub log, exactly what happens to each saved mapping's Variant
        // (and ControllerID + Available + IsEnabled) across a wheel swap.
        // Resolved lazily on first dump attempt; null entries make the dump
        // a no-op.
        private object? _controlMapperSettings;
        private PropertyInfo? _settingsControllerMappingsProp;
        private PropertyInfo? _csmDescriptionProp;
        private PropertyInfo? _csmStateProp;
        private PropertyInfo? _csmIsEnabledProp;
        private PropertyInfo? _descControllerIDProp;
        private PropertyInfo? _descVendorIDProp;
        private PropertyInfo? _descProductIdProp;
        private PropertyInfo? _descVariantProp;
        private PropertyInfo? _stateAvailableProp;
        private bool _diagResolveAttempted;
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

                Type rwType = rw.GetType();
                FieldInfo? vhField = rwType.GetField(
                    "variantHelper", BindingFlags.NonPublic | BindingFlags.Instance);
                if (vhField == null)
                {
                    LogGiveUp("RemapperWorker.variantHelper field not found");
                    return false;
                }
                object? vh = vhField.GetValue(rw);
                if (vh == null) return false;

                Type vhType = vh.GetType();
                FieldInfo? providersField = vhType.GetField(
                    "VariantProviders", BindingFlags.NonPublic | BindingFlags.Instance);
                if (providersField == null)
                {
                    LogGiveUp("VariantHelper.VariantProviders field not found");
                    return false;
                }
                _updateProvidersMethod = rwType.GetMethod(
                    "UpdateVariantProviders",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                // VariantHelper.VariantProviders is lazy-created inside
                // VariantHelper.Start() — and Start() only runs when the user
                // has Control Mapper's "Recognize Individual Wheels" toggle
                // enabled (RemapperWorker.UpdateVariantProviders gates on
                // ControlMapperPluginSettings.RecognizeIndiviualWheels). If
                // the list is null right now we have to materialize it
                // ourselves so we have somewhere to add our provider. The
                // user's toggle is then respected by the
                // UpdateVariantProviders call at the end of this method: if
                // disabled, Stop() unsubscribes everything but leaves the
                // populated list intact, so when the user flips the toggle
                // on later our provider is already present.
                object? providersRaw = providersField.GetValue(vh);
                if (providersRaw == null)
                {
                    MethodInfo? startMethod = vhType.GetMethod(
                        "Start", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (startMethod == null)
                    {
                        LogGiveUp("VariantHelper.Start method not found (cannot materialize provider list)");
                        return false;
                    }
                    try { startMethod.Invoke(vh, null); }
                    catch (Exception ex)
                    {
                        LogGiveUp(
                            $"VariantHelper.Start threw while materializing list: " +
                            ex.GetBaseException().Message);
                        return false;
                    }
                    providersRaw = providersField.GetValue(vh);
                }

                if (providersRaw is not IList providers)
                {
                    LogGiveUp(
                        $"VariantHelper.VariantProviders is " +
                        (providersRaw == null ? "still null after Start()" : "not an IList") +
                        " (declared type: " + providersField.FieldType.FullName + ")");
                    return false;
                }

                // Idempotency: a prior plugin instance may have left a
                // provider in the list (plugin reload without SimHub
                // restart). Reuse it instead of double-registering.
                foreach (var existing in providers)
                {
                    if (existing is MozaVariantProvider existingProvider)
                    {
                        _provider = existingProvider;
                        _providers = providers;
                        _remapperWorker = rw;
                        _registered = true;
                        MozaLog.Info(
                            "[AZOM] ControlMapper bridge: MozaVariantProvider already present, reusing existing entry");
                        return true;
                    }
                }

                providers.Add(_provider);
                _providers = providers;
                _remapperWorker = rw;

                if (_updateProvidersMethod != null)
                {
                    try { _updateProvidersMethod.Invoke(rw, null); }
                    catch (Exception ex)
                    {
                        MozaLog.Warn(
                            "[AZOM] ControlMapper bridge: UpdateVariantProviders threw — provider registered " +
                            "but VariantHelper may not have subscribed to VariantChanged: " +
                            ex.GetBaseException().Message);
                    }
                }
                else
                {
                    MozaLog.Warn(
                        "[AZOM] ControlMapper bridge: RemapperWorker.UpdateVariantProviders not found — wheel " +
                        "hot-swap won't refresh Control Mapper automatically (user can manually rescan).");
                }

                _registered = true;
                MozaLog.Info(
                    $"[AZOM] ControlMapper bridge: registered MozaVariantProvider " +
                    $"({providers.Count} providers total)");

                // Diagnostic capture — settings reference for periodic mapping dumps.
                try
                {
                    FieldInfo? settingsField = cmType.GetField(
                        "controlMapperPluginSettings",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (settingsField != null)
                        _controlMapperSettings = settingsField.GetValue(cmInstance);

                    // Subscribe to ControllerMappings.CollectionChanged so we
                    // get a dump every time the user clicks Add Source
                    // Controller (or anything else mutates the collection).
                    HookMappingsCollectionChanged();
                }
                catch (Exception ex) { MozaLog.Debug($"[AZOM] CM diag: settings capture: {ex.Message}"); }

                // Force an immediate controller re-enumeration so our provider's
                // variant is picked up on the first pass instead of waiting for
                // the wheel-attach VariantChanged later. ControlMapperPlugin.Init
                // runs UpdateControllerList() once at startup — before our bridge
                // has registered — so any wheel already plugged in at SimHub
                // launch gets enumerated without a variant. Re-running it here
                // re-keys the wheelbase entry with the current MOZA variant.
                _updateControllerListMethod = rwType.GetMethod(
                    "UpdateControllerList",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_updateControllerListMethod != null)
                {
                    try
                    {
                        _updateControllerListMethod.Invoke(rw, null);
                        MozaLog.Debug(
                            "[AZOM] ControlMapper bridge: forced UpdateControllerList " +
                            "to re-key controllers with MOZA variant");
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Debug(
                            "[AZOM] ControlMapper bridge: UpdateControllerList threw — " +
                            ex.GetBaseException().Message);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                LogGiveUp($"unexpected exception: {ex.GetBaseException().Message}");
                return false;
            }
        }

        /// <summary>
        /// Drive the provider's wheel-change detection. Called once per
        /// <c>MozaPlugin.DataUpdate</c> tick. Cheap — the provider compares
        /// a single string against its cache and only fires
        /// <see cref="MozaVariantProvider.VariantChanged"/> on transition.
        ///
        /// We deliberately do NOT clone mappings or override <c>Available</c>
        /// here. The IL of <c>RemapperWorker.UpdateControllerList</c> +
        /// <c>SharpHelper.AquireController</c> shows SimHub already handles
        /// per-variant mappings natively:
        ///   - The match cascade in <c>UpdateControllerList</c> (b__2/b__3/
        ///     b__10) includes Variant in every predicate, so each variant
        ///     gets its own slot in ControllerMappings and a device whose
        ///     current variant has no matching mapping shows up in
        ///     UnmappedControllers (the "Add Source Controller" dropdown).
        ///   - <c>AquireController</c> compares <c>GetCurrentVariant</c>
        ///     against <c>Description.Variant</c> on every tick and calls
        ///     <c>SetAsUnplugged</c> on mismatches — so only the mapping
        ///     whose stored Variant matches the currently-attached wheel
        ///     gets a Joystick acquire and forwards input.
        /// Our role is just to provide the Variant string via the provider;
        /// SimHub does the rest.
        /// </summary>
        public void Poll()
        {
            if (!_registered) return;
            try { _provider.Poll(); }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] ControlMapper bridge poll: {ex.Message}");
            }

            // We deliberately do NOT auto-create a ControllerSourceMapping for a
            // newly-attached wheel. The user adds each MOZA source controller via
            // SimHub's "Add Source Controller" flow; the provider supplies the
            // Variant string and AquireController's per-variant gate dispatches
            // input to the matching mapping. (A previous build synthesized a
            // per-variant mapping here, but it produced an unwanted extra mapping
            // that SimHub never marked Available — see docs/controlmapper.md.)
            string? currentVariant = ComputeCurrentVariant();

            // Diagnostic: when the wheel-side variant changes, dump every
            // MOZA mapping's stored Description.Variant + Available + IsEnabled
            // + ControllerID. This lets us see whether SimHub's UpdateControllerList
            // is mutating saved Variant strings during a wheel swap.
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

        /// <summary>
        /// Walk <c>ControlMapperPluginSettings.ControllerMappings</c> and log
        /// the current state (Variant / ControllerID / Available / IsEnabled)
        /// of every MOZA wheelbase mapping. Logs once per detected variant
        /// transition to keep noise bounded.
        /// </summary>
        private void DumpMappingsState(string currentVariantLabel)
        {
            if (!_diagResolveAttempted)
            {
                _diagResolveAttempted = true;
                ResolveDiagnosticReflection();
            }
            if (_controlMapperSettings == null
                || _settingsControllerMappingsProp == null
                || _csmDescriptionProp == null
                || _csmStateProp == null
                || _descVendorIDProp == null
                || _descVariantProp == null
                || _descControllerIDProp == null
                || _stateAvailableProp == null)
            {
                MozaLog.Debug("[AZOM] CM diag: reflection unavailable, skipping dump");
                return;
            }
            object? mappingsObj;
            try { mappingsObj = _settingsControllerMappingsProp.GetValue(_controlMapperSettings); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] CM diag: get mappings: {ex.Message}"); return; }
            if (mappingsObj is not IList mappings)
            {
                MozaLog.Debug("[AZOM] CM diag: mappings not an IList");
                return;
            }

            int idx = 0;
            int mozaCount = 0;
            MozaLog.Debug($"[AZOM] CM diag (variant=\"{currentVariantLabel}\"): "
                + $"ControllerMappings.Count={mappings.Count}");
            foreach (object? entry in mappings)
            {
                idx++;
                if (entry == null) continue;
                object? desc;
                try { desc = _csmDescriptionProp.GetValue(entry); }
                catch { continue; }
                if (desc == null) continue;
                if (!IsMozaWheelbaseOrHubDesc(desc)) continue;
                mozaCount++;
                string variant = (_descVariantProp.GetValue(desc) as string) ?? "<null>";
                object? cidObj = null;
                try { cidObj = _descControllerIDProp.GetValue(desc); } catch { }
                string cidShort = cidObj?.ToString() ?? "<null>";
                if (cidShort.Length > 8) cidShort = cidShort.Substring(0, 8);
                object? state = null;
                try { state = _csmStateProp.GetValue(entry); } catch { }
                string availStr = "<n/a>";
                if (state != null && _stateAvailableProp != null)
                {
                    try { availStr = (_stateAvailableProp.GetValue(state) is bool b ? b.ToString() : "<?>"); }
                    catch { }
                }
                string enabledStr = "<n/a>";
                if (_csmIsEnabledProp != null)
                {
                    try { enabledStr = (_csmIsEnabledProp.GetValue(entry) is bool eb ? eb.ToString() : "<?>"); }
                    catch { }
                }
                MozaLog.Debug(
                    $"[AZOM] CM diag   #{idx}: Variant=\"{variant}\" CtrlID={cidShort}.. "
                    + $"Available={availStr} IsEnabled={enabledStr} DescObj={GetHash(desc):X}");
            }
            MozaLog.Debug($"[AZOM] CM diag: {mozaCount} MOZA mapping(s) total");
        }

        private void ResolveDiagnosticReflection()
        {
            if (_controlMapperSettings == null) return;
            try
            {
                Type settingsType = _controlMapperSettings.GetType();
                _settingsControllerMappingsProp = settingsType.GetProperty(
                    "ControllerMappings", BindingFlags.Public | BindingFlags.Instance);
                if (_settingsControllerMappingsProp == null) return;

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
        /// bridge dumps the full mapping state immediately whenever the user
        /// adds or removes a source controller. Reflection-based since the
        /// concrete event type (<c>NotifyCollectionChangedEventHandler</c>)
        /// lives in <c>System.Collections.Specialized</c> and is wired via
        /// <c>ObservableCollection&lt;T&gt;</c>.
        /// </summary>
        private void HookMappingsCollectionChanged()
        {
            if (!_diagResolveAttempted)
            {
                _diagResolveAttempted = true;
                ResolveDiagnosticReflection();
            }
            if (_controlMapperSettings == null || _settingsControllerMappingsProp == null) return;
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
                // object, and stamp its Variant with the currently-attached
                // wheel — that's what the user meant to add. AquireController's
                // variant gating then works correctly per-mapping.
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
        /// anything else, replace its Description with an independent clone
        /// whose <c>Variant</c> is set to the currently-attached wheel.
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

            // Stamp the clone's Variant with the current detected wheel
            // (what the user actually wanted to add). If no wheel is detected
            // right now (currentVariant == null), preserve whatever Variant
            // the original description had so we don't blank it.
            int oldHash = GetHash(desc);
            string? originalVariant;
            try { originalVariant = _descVariantProp.GetValue(desc) as string; }
            catch { originalVariant = null; }
            string targetVariant = currentVariant ?? originalVariant ?? string.Empty;
            try { _descVariantProp.SetValue(clone, targetVariant); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] CM diag: clone set Variant: {ex.Message}"); }

            // Replace the CSM's Description with the independent clone.
            try { _csmDescriptionProp.SetValue(csm, clone); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] CM diag: set CSM Description: {ex.Message}"); return; }

            MozaLog.Debug(
                $"[AZOM] CM diag: detached shared Description on new MOZA mapping "
                + $"(oldDescObj={oldHash:X}, newDescObj={GetHash(clone):X}, "
                + $"originalVariant=\"{originalVariant ?? "<null>"}\", "
                + $"setVariant=\"{targetVariant}\")");
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
            // Detach the CollectionChanged handler first — it can be subscribed
            // even when provider registration never completed (_registered == false).
            UnhookMappingsCollectionChanged();
            if (!_registered) return;
            try
            {
                if (_providers != null)
                {
                    for (int i = _providers.Count - 1; i >= 0; i--)
                    {
                        if (_providers[i] is MozaVariantProvider)
                            _providers.RemoveAt(i);
                    }
                    if (_updateProvidersMethod != null && _remapperWorker != null)
                    {
                        try { _updateProvidersMethod.Invoke(_remapperWorker, null); }
                        catch (Exception ex)
                        {
                            MozaLog.Debug(
                                $"[AZOM] ControlMapper bridge unregister UpdateVariantProviders: {ex.Message}");
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
                _remapperWorker = null;
                _updateProvidersMethod = null;
                _updateControllerListMethod = null;
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
