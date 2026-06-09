using System;
using System.Collections;
using System.Reflection;
using SimHub.Plugins;

namespace MozaPlugin.ControlMapper
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

        private readonly MozaVariantProvider _provider = new MozaVariantProvider();

        private object? _remapperWorker;
        private IList? _providers;
        private MethodInfo? _updateProvidersMethod;
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
        // Throttle the reflection-heavy AutoCreate scan (GetValue(ControllerMappings)
        // + per-entry foreach). It only needs to react to a wheel/variant change,
        // so cap it to once per AutoCreateScanIntervalMs plus immediately on a
        // variant change — was running on every DataUpdate tick.
        private string? _lastAutoCreateVariant;
        private int _lastAutoCreateScanMs;
        private const int AutoCreateScanIntervalMs = 1000;
        // Cached so Unregister can detach the CollectionChanged handler: the
        // publisher (SimHub's ControllerMappings) outlives a plugin reload, and
        // RemoveEventHandler needs the exact Delegate instance AddEventHandler used.
        private EventInfo? _mappingsCollChangedEvent;
        private Delegate? _mappingsCollChangedHandler;
        private object? _mappingsCollChangedTarget;

        // Set of variants we've auto-created mappings for this session.
        // Prevents re-creating after the user explicitly deletes one.
        // Case-insensitive because Variant comparisons everywhere are ToLower'd.
        private readonly System.Collections.Generic.HashSet<string> _autoCreatedVariants =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                    if (existing is MozaVariantProvider)
                    {
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
                MethodInfo? updateControllerListMethod = rwType.GetMethod(
                    "UpdateControllerList",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (updateControllerListMethod != null)
                {
                    try
                    {
                        updateControllerListMethod.Invoke(rw, null);
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

            // Auto-create a new ControllerSourceMapping for the currently-
            // attached wheel when:
            //   (a) at least one MOZA wheelbase mapping for a CURRENTLY-CONNECTED
            //       device already exists (user has engaged with the feature by
            //       adding the wheelbase once, and that base is plugged in now),
            //       AND
            //   (b) no existing MOZA mapping has Variant == currentVariant
            //       (the new wheel doesn't have its own slot yet), AND
            //   (c) we haven't already auto-created for this variant this
            //       session (so the user can delete an unwanted auto-add
            //       without it re-appearing).
            // SimHub's "Add Source Controller" UI dedupes the wheelbase by
            // ControllerID, so once one MOZA mapping exists the UI hides the
            // wheelbase entirely — even when the current variant has no
            // mapping. This bypasses that UI bottleneck. The connected-device
            // requirement in (a) keeps us from cloning an unrelated base's
            // ControllerID for a wheel that is actually a distinct DirectInput
            // device (e.g. an ES wheel on a separate base) — SimHub surfaces
            // those in the dropdown natively, so a synthesized clone would only
            // leave a phantom disconnected mapping.
            // Wheel variant resolved once per tick; shared by auto-create and
            // the diagnostic dump below (was computed independently in both).
            string? currentVariant = ComputeCurrentVariant();
            // Run the reflection-heavy scan only when the variant changed or the
            // throttle window elapsed — not every DataUpdate tick. The 1 s ceiling
            // keeps auto-create responsive (a freshly-added wheelbase mapping is
            // picked up within a second) without per-frame reflection.
            int nowMs = Environment.TickCount;
            bool variantChanged = !string.Equals(currentVariant, _lastAutoCreateVariant, StringComparison.Ordinal);
            if (variantChanged || (uint)(nowMs - _lastAutoCreateScanMs) >= AutoCreateScanIntervalMs)
            {
                _lastAutoCreateVariant = currentVariant;
                _lastAutoCreateScanMs = nowMs;
                try { AutoCreateVariantMappingIfNeeded(currentVariant); }
                catch (Exception ex)
                {
                    MozaLog.Debug($"[AZOM] CM auto-create: {ex.Message}");
                }
            }

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
        /// True when the mapping's DirectInput device is currently connected
        /// (<c>ControllerState.Available</c>) — i.e. plugged in, irrespective of
        /// whether its stored Variant matches the live wheel. Returns false if
        /// the connection state can't be read so the caller can decide how to
        /// fall back.
        /// </summary>
        private bool IsMappingConnected(object entry)
        {
            if (_csmStateProp == null || _stateAvailableProp == null) return false;
            try
            {
                object? state = _csmStateProp.GetValue(entry);
                if (state == null) return false;
                return _stateAvailableProp.GetValue(state) is bool b && b;
            }
            catch { return false; }
        }

        /// <summary>
        /// Returns true iff the given <c>ControllerDescription</c> represents a
        /// MOZA wheelbase or hub — i.e. a USB endpoint that can carry a
        /// swappable wheel. Keeps the bridge's per-mapping bookkeeping (clone-
        /// description on Add, auto-create per variant, diag dump) narrowed to
        /// devices that actually have a wheel variant, so MOZA pedals /
        /// shifters / handbrakes / mBoosters / dashboards under VID 0x346E are
        /// left alone. Matches <see cref="MozaVariantProvider.GetVariant"/>'s
        /// own filter so the variant pipeline and the bridge agree on scope.
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

        /// <summary>
        /// If the currently-attached MOZA wheel has no matching
        /// ControllerSourceMapping (and at least one MOZA mapping exists),
        /// programmatically build a new mapping by cloning an existing MOZA
        /// CSM's Description and setting Variant to the current wheel. This
        /// bypasses SimHub's Add Source Controller UI, which hides the
        /// wheelbase from its dropdown once any mapping references the same
        /// ControllerID — even when the user is on a different variant that
        /// doesn't have a slot yet.
        /// </summary>
        // Resolve the current wheel variant friendly-name from live plugin state.
        // Delegates to the canonical resolver so the old-protocol ("ES") rule
        // and any future variant logic stay in one place.
        private static string? ComputeCurrentVariant() => MozaVariantProvider.ComputeCurrentVariant();

        private void AutoCreateVariantMappingIfNeeded(string? currentVariant)
        {
            if (!_diagResolveAttempted)
            {
                _diagResolveAttempted = true;
                ResolveDiagnosticReflection();
            }
            if (_controlMapperSettings == null
                || _settingsControllerMappingsProp == null
                || _csmDescriptionProp == null
                || _descVendorIDProp == null
                || _descProductIdProp == null
                || _descVariantProp == null) return;

            if (string.IsNullOrEmpty(currentVariant)) return;

            // Never auto-create for old-protocol (ES) wheels. The user attaches a
            // single old wheel and adds it once via SimHub's normal "Add Source
            // Controller" flow, which works. Synthesizing a clone here instead
            // produces a mapping SimHub never marks Available — it stays
            // "disconnected" — because the cloned Description churns through
            // SimHub's shared-reference CopyFrom path (confirmed in live logs:
            // three same-ControllerID mappings, the auto-added "ES" one alone
            // stuck Available=False after UpdateControllerList). There is also no
            // multi-old-wheel swap scenario to justify the synthesis.
            if (MozaPlugin.Instance?.IsOldWheelDetected == true) return;

            if (_autoCreatedVariants.Contains(currentVariant!)) return;

            object? mappingsObj;
            try { mappingsObj = _settingsControllerMappingsProp.GetValue(_controlMapperSettings); }
            catch { return; }
            if (mappingsObj is not IList mappings) return;

            // Pick the clone source. Auto-create only makes sense for the
            // same-device hot-swap case: SimHub hides an already-mapped, still-
            // connected wheelbase from the "Add Source Controller" dropdown, so a
            // freshly-attached wheel on that SAME base has no way in and we
            // synthesize its mapping. When the attached wheel is instead a
            // DISTINCT DirectInput device (e.g. an old-protocol ES wheel on a
            // separate base, which enumerates under its own ControllerID), SimHub
            // already offers it in the dropdown — cloning a different base's
            // mapping here would copy the wrong ControllerID and leave a phantom
            // "disconnected" mapping behind. So prefer a clone source whose device
            // is CURRENTLY connected (ControllerState.Available), and when we can
            // read connection state, bail entirely if none is connected.
            object? primaryMoza = null;     // first MOZA mapping (fallback if state unreadable)
            object? connectedMoza = null;   // first CONNECTED MOZA mapping (preferred)
            foreach (object? entry in mappings)
            {
                if (entry == null) continue;
                object? desc;
                try { desc = _csmDescriptionProp.GetValue(entry); } catch { continue; }
                if (desc == null) continue;
                if (!IsMozaWheelbaseOrHubDesc(desc)) continue;
                primaryMoza ??= entry;
                if (connectedMoza == null && IsMappingConnected(entry))
                    connectedMoza = entry;
                string variant = (_descVariantProp.GetValue(desc) as string) ?? string.Empty;
                if (string.Equals(variant, currentVariant, StringComparison.OrdinalIgnoreCase))
                    return; // mapping already exists for current variant — nothing to do
            }
            if (primaryMoza == null) return; // user hasn't added the wheelbase yet

            // When connection state is readable, only clone a connected base. If
            // none is connected, the attached wheel is a separate device SimHub
            // will surface in the Add dropdown — leave it to the native flow
            // rather than minting a phantom disconnected mapping.
            bool canReadAvailability = _csmStateProp != null && _stateAvailableProp != null;
            object? cloneSource = canReadAvailability ? connectedMoza : primaryMoza;
            if (cloneSource == null) return;

            // Build a fresh CSM + cloned Description with current Variant.
            object? primaryDesc;
            try { primaryDesc = _csmDescriptionProp.GetValue(cloneSource); } catch { return; }
            if (primaryDesc == null) return;
            Type csmType = cloneSource.GetType();
            Type descType = primaryDesc.GetType();
            MethodInfo? copyFrom = descType.GetMethod("CopyFrom");
            if (copyFrom == null) return;

            object? newCsm;
            try { newCsm = Activator.CreateInstance(csmType); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] CM auto-create: csm ctor: {ex.Message}"); return; }
            if (newCsm == null) return;

            object? newDesc;
            try { newDesc = Activator.CreateInstance(descType); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] CM auto-create: desc ctor: {ex.Message}"); return; }
            if (newDesc == null) return;

            try { copyFrom.Invoke(newDesc, new[] { primaryDesc }); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] CM auto-create: desc CopyFrom: {ex.Message}"); return; }
            try { _descVariantProp.SetValue(newDesc, currentVariant); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] CM auto-create: set Variant: {ex.Message}"); return; }
            try { _csmDescriptionProp.SetValue(newCsm, newDesc); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] CM auto-create: set csm Description: {ex.Message}"); return; }

            // Mark before Add so we don't re-attempt on the next tick if the
            // async dispatch is still pending.
            _autoCreatedVariants.Add(currentVariant!);

            // ControllerMappings is bound to a WPF CollectionView, which only
            // accepts modifications from the UI dispatcher thread. We're
            // running from MozaPlugin.DataUpdate (SimHub's data thread), so
            // marshal the Add through Application.Current.Dispatcher. Without
            // this, the backing list updates but WPF throws on the change
            // notification and the Control Mapper UI shows stale state.
            System.Windows.Threading.Dispatcher? dispatcher = null;
            try { dispatcher = System.Windows.Application.Current?.Dispatcher; }
            catch { }

            var capturedMappings = mappings;
            var capturedCsm = newCsm;
            var capturedVariant = currentVariant!;

            void DoAdd()
            {
                try
                {
                    capturedMappings.Add(capturedCsm);
                    MozaLog.Info(
                        $"[AZOM] CM auto-create: added new mapping for variant \"{capturedVariant}\" "
                        + $"(now {capturedMappings.Count} total mappings)");
                }
                catch (Exception ex)
                {
                    MozaLog.Warn(
                        $"[AZOM] CM auto-create: Add threw on UI thread: {ex.GetBaseException().Message}");
                }
            }

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                DoAdd();
            }
            else
            {
                try { dispatcher.BeginInvoke(new Action(DoAdd)); }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] CM auto-create: dispatcher BeginInvoke threw: {ex.GetBaseException().Message}");
                }
            }
        }

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
                        if (added != null)
                            DetachMozaDescription(added, currentVariant);
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
