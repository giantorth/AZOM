using System;
using MozaPlugin.Devices;
using MozaPlugin.Settings;
using MozaPlugin.Devices.Led;
using MozaPlugin.Devices.Extensions;

namespace MozaPlugin.Hardware
{
    /// <summary>
    /// All hardware-side writes: Apply*ToHardware + the detection-gated WriteIf*
    /// family. Profile + per-page overlay are the source of truth; writes are
    /// detection-gated and sentinel-guarded.
    /// </summary>
    internal sealed partial class HardwareApplier
    {
        private readonly MozaPlugin _plugin;
        private readonly MozaData _data;
        private readonly MozaDeviceManager _deviceManager;
        private readonly MozaAb9DeviceManager _ab9Manager;
        private readonly DeviceDetectionState _detectionState;
        // Dedicated pipe for a standalone-USB CM2 dashboard — the CM2 write
        // routing in HardwareApplier.Cm2.cs picks between this and _deviceManager.
        private readonly MozaDashboardDeviceManager _dashboardManager;

        public HardwareApplier(
            MozaPlugin plugin,
            MozaData data,
            MozaDeviceManager deviceManager,
            MozaAb9DeviceManager ab9Manager,
            DeviceDetectionState detectionState,
            MozaDashboardDeviceManager dashboardManager)
        {
            _plugin = plugin;
            _data = data;
            _deviceManager = deviceManager;
            _ab9Manager = ab9Manager;
            _detectionState = detectionState;
            _dashboardManager = dashboardManager;
        }

        // ── Write-on-change cache for persistent (flash-backed) wheel settings ──
        // The wheel persists LED / idle / brightness / colour settings to its
        // parameter flash. ApplyWheelToHardware runs on every (re)detection, so
        // re-writing unchanged settings each time needlessly wears that flash —
        // a re-detect loop has been observed to wear it until reads AND writes
        // start failing (firmware "Table 8: Failed to Read/Write Parameter"),
        // which kills the wheel's identity readback and bricks it. PitHouse
        // writes these once. So: only write a persistent setting when its value
        // actually changed since the last write to THIS wheel. Keyed by MCU UID
        // so a genuinely different wheel (hot-swap) re-writes its config once.
        private readonly System.Collections.Generic.Dictionary<string, long> _wheelCfgCache
            = new System.Collections.Generic.Dictionary<string, long>();
        private byte[] _wheelCfgCacheUid = System.Array.Empty<byte>();
        // Leaf lock guarding _wheelCfgCache/_wheelCfgCacheUid only. Three threads reach
        // them — the detection/UI thread via ApplyWheelToHardware, the UI thread via the
        // WriteIf* handlers, and the coalescing flush timer's ThreadPool callback. Every
        // critical section is a dictionary get/set with no I/O and no nesting; device
        // writes always happen after the lock is released.
        private readonly object _wheelCfgCacheLock = new object();
        // Guarded by _wheelCfgCacheLock, all keyed like _wheelCfgCache:
        //  · LastWriteTicks — when we last actually issued a write, so a readback that
        //    contradicts the cache can tell "the wheel really diverged" from "our write
        //    is still in flight".
        //  · Desired — the value we last INTENDED, recorded even when the change gate
        //    suppressed the write, so a divergence can be re-asserted.
        //  · ReassertCount — bounded so a register the wheel refuses to accept can't turn
        //    the ~80 s parity-poll readback into an endless flash-write loop.
        private readonly System.Collections.Generic.Dictionary<string, long> _wheelCfgLastWriteTicks
            = new System.Collections.Generic.Dictionary<string, long>(System.StringComparer.Ordinal);
        private readonly System.Collections.Generic.Dictionary<string, long> _wheelCfgDesired
            = new System.Collections.Generic.Dictionary<string, long>(System.StringComparer.Ordinal);
        private readonly System.Collections.Generic.Dictionary<string, int> _wheelCfgReassertCount
            = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal);
        private const double WheelCfgAdoptQuietMs = 1500.0;
        private const int WheelCfgMaxReasserts = 3;

        private void SyncWheelCfgCache()
        {
            lock (_wheelCfgCacheLock) SyncWheelCfgCacheLocked();
        }

        private void SyncWheelCfgCacheLocked()
        {
            var uid = _data.WheelMcuUid ?? System.Array.Empty<byte>();

            // An UNKNOWN uid is not evidence of a different wheel. The uid arrives
            // asynchronously (and on some rims — the FSR1 among them — never does), so
            // treating empty→known or known→empty as a hot-swap would clear the cache
            // mid-session and re-write every flash-backed setting the connect-time
            // apply had already written. Only a change between two KNOWN uids is a
            // genuine hot-swap. Adopt the uid the first time we learn it.
            if (uid.Length == 0) return;
            if (_wheelCfgCacheUid.Length == 0) { _wheelCfgCacheUid = (byte[])uid.Clone(); return; }

            bool same = uid.Length == _wheelCfgCacheUid.Length;
            for (int i = 0; same && i < uid.Length; i++)
                if (uid[i] != _wheelCfgCacheUid[i]) same = false;
            if (!same)
            {
                _wheelCfgCache.Clear();
                _wheelCfgLastWriteTicks.Clear();
                _wheelCfgDesired.Clear();
                _wheelCfgReassertCount.Clear();
                _wheelCfgCacheUid = (byte[])uid.Clone();
            }
        }

        /// <summary>True (and records the new value) iff this flash-backed setting
        /// differs from the last value written to the current wheel — i.e. an actual
        /// change worth a flash write. Returns false to skip a redundant re-write.</summary>
        private bool WheelCfgChanged(string key, long value)
        {
            lock (_wheelCfgCacheLock)
            {
                // Record the intent even when the write is suppressed — that's what
                // PrimeWheelCfgFromDevice re-asserts after adopting a contradicting readback.
                _wheelCfgDesired[key] = value;
                if (_wheelCfgCache.TryGetValue(key, out var prev) && prev == value) return false;
                _wheelCfgCache[key] = value;
                _wheelCfgLastWriteTicks[key] = System.DateTime.UtcNow.Ticks;
                return true;
            }
        }

        /// <summary>Diagnostics read-out: what the change gate believes is in the wheel's
        /// register for this key, and what we last intended to put there. Either is null
        /// when the key has never been seen.</summary>
        internal (long? Cached, long? Desired) WheelCfgDiag(string key)
        {
            lock (_wheelCfgCacheLock)
            {
                long? cached = _wheelCfgCache.TryGetValue(key, out var c) ? c : (long?)null;
                long? desired = _wheelCfgDesired.TryGetValue(key, out var d) ? d : (long?)null;
                return (cached, desired);
            }
        }

        /// <summary>Peek the change gate WITHOUT recording — for "should I call the writer"
        /// decisions where the writer itself owns the gate (see
        /// <see cref="WriteKnobRingColors"/>). Recording here would consume the change and
        /// make the writer's own gate return false.</summary>
        private bool WheelCfgDiffers(string key, long value)
        {
            lock (_wheelCfgCacheLock)
                return !(_wheelCfgCache.TryGetValue(key, out var prev) && prev == value);
        }

        /// <summary>
        /// Prime the wheel write cache with a value the WHEEL ITSELF reported, so a
        /// profile that already matches the device writes nothing.
        ///
        /// The wheel's readback is GROUND TRUTH: when it contradicts what we believe we
        /// wrote, the cache adopts the device value and the intended value is re-asserted
        /// once. This used to be add-only, which let a single divergence become permanent —
        /// the register drifted (power cycle, wheel-side menu, another host), the plugin
        /// read the new value, the gate still said "already written" and suppressed the
        /// corrective write forever. Bundle GY9RWKMR is exactly that: buttons/knob
        /// brightness sat at 5/10 while the cache said 100, and no brightness write went
        /// out again for the rest of the session. Adoption is skipped while our own write
        /// is still in flight (<see cref="WheelCfgAdoptQuietMs"/>) and the re-assert is
        /// capped (<see cref="WheelCfgMaxReasserts"/>) so a register the firmware refuses
        /// can't turn the periodic readback into a flash-write loop.
        ///
        /// Why: these settings are flash-backed and the wheel persists them across
        /// power cycles, so re-asserting them at every connect is pure parameter-store
        /// wear. PitHouse never does it — across four FSR1 captures it issues ZERO
        /// writes to the idle/sleep-light family (0x3F cmds 1c/1d/1e/20/21/22/24) and
        /// only ever READS them, writing solely when the user changes a setting in its
        /// UI. The plugin wrote 8-12 of them per connect. On the FSR1 — whose param
        /// store wedges into an unrecoverable read-failure storm (wheel-0x17.md
        /// § Param-store wedge) — that difference is the whole ballgame.
        /// </summary>
        internal void PrimeWheelCfgFromDevice(string key, long deviceValue)
        {
            long desired = 0;
            bool reassert = false;
            lock (_wheelCfgCacheLock)
            {
                SyncWheelCfgCacheLocked();
                if (!_wheelCfgCache.TryGetValue(key, out var cached))
                {
                    _wheelCfgCache[key] = deviceValue;
                    return;
                }
                if (cached == deviceValue)
                {
                    // Converged — clear the retry budget so a genuinely new divergence
                    // later gets its full allowance.
                    _wheelCfgReassertCount.Remove(key);
                    return;
                }

                // Don't fight a write that hasn't had time to land.
                if (_wheelCfgLastWriteTicks.TryGetValue(key, out var lastWrite)
                    && (System.DateTime.UtcNow.Ticks - lastWrite)
                       < (long)(WheelCfgAdoptQuietMs * System.TimeSpan.TicksPerMillisecond))
                    return;

                _wheelCfgCache[key] = deviceValue;
                _wheelCfgReassertCount.TryGetValue(key, out int tries);
                if (tries < WheelCfgMaxReasserts
                    && _wheelCfgDesired.TryGetValue(key, out desired)
                    && desired != deviceValue)
                {
                    _wheelCfgReassertCount[key] = tries + 1;
                    reassert = true;
                }
            }

            if (!reassert)
            {
                MozaLog.Debug($"[AZOM] wheel-cfg '{key}': wheel reports {deviceValue}, cache adopted it");
                return;
            }

            MozaLog.Debug(
                $"[AZOM] wheel-cfg '{key}': wheel reports {deviceValue} but {desired} was intended — re-asserting");
            // This runs on the serial READ thread. Hop to the pool so the read thread's
            // ack path never touches the coalescing lock or the flush timer.
            long value = desired;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { QueueWheelCfgWrite(key, value, () => _deviceManager.WriteSetting(key, (int)value)); }
                catch (System.Exception ex) { MozaLog.Warn($"[AZOM] wheel-cfg re-assert '{key}': {ex.Message}"); }
            });
        }

        /// <summary>
        /// Flash-backed wheel settings — the scalar/array/colour commands the wheel
        /// persists to its parameter store. Exactly the set
        /// <see cref="ApplyWheelToHardware"/> guards with <see cref="WheelCfgChanged"/>,
        /// minus the LED colour arrays (the live telemetry pipeline owns those
        /// registers and repaints them every frame, so caching them would suppress a
        /// repaint the pipeline needs).
        ///
        /// The UI's WriteIf* handlers write these same commands. Until they shared
        /// this cache every dropdown/slider interaction was an unconditional flash
        /// write, and a later ApplyWheelToHardware then wrote the value a SECOND time
        /// because the cache had never seen the UI's write. On the FSR1 — whose param
        /// store wedges permanently into a read-failure storm (wheel-0x17.md
        /// § Param-store wedge) — that is the difference between a working display and
        /// one that needs a power cycle.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> s_flashBackedWheelCfg =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
            {
                "wheel-telemetry-mode", "wheel-buttons-led-mode", "wheel-knob-led-mode",
                "wheel-telemetry-idle-effect", "wheel-buttons-idle-effect", "wheel-knob-idle-effect",
                "wheel-telemetry-idle-interval", "wheel-buttons-idle-interval", "wheel-knob-idle-interval",
                "wheel-idle-mode", "wheel-idle-timeout", "wheel-idle-speed", "wheel-idle-color",
                "wheel-rpm-brightness", "wheel-buttons-brightness", "wheel-knob-brightness",
                "wheel-old-rpm-brightness", "dash-flags-brightness",
                "wheel-rpm-indicator-mode", "wheel-set-rpm-display-mode", "wheel-knob-mode",
            };

        /// <summary>Flash-backed per the set above, plus the per-knob signal modes
        /// (<c>wheel-knob-signal-mode{fwIdx}</c>) whose names carry a firmware index.</summary>
        private static bool IsFlashBackedWheelCfg(string command) =>
            command != null
            && (s_flashBackedWheelCfg.Contains(command)
                || command.StartsWith("wheel-knob-signal-mode", System.StringComparison.Ordinal));

        // ── Coalescing gate for UI-driven flash-backed wheel writes ──
        // A slider raises ValueChanged per tick, so dragging "Sleep speed" or a
        // brightness slider used to emit one flash write per tick — ~50 per drag. The
        // change-cache above can't help: every intermediate value genuinely differs.
        // So UI writes to flash-backed commands are parked in a latest-wins slot per
        // command and flushed once the user stops moving, which turns a whole drag into
        // a single write. Same pending+coalesce+throttle shape Fsr1DisplayDriver already
        // uses for its own EEPROM writes (dashboard select / display brightness).
        //
        // The change-cache check happens at FLUSH time, not queue time, so a drag that
        // ends back where it started writes nothing at all.
        private const double WheelCfgFlushDelayMs = 400.0;
        private readonly System.Collections.Generic.Dictionary<string, (long CacheValue, System.Action Write)> _pendingWheelCfg
            = new System.Collections.Generic.Dictionary<string, (long, System.Action)>(System.StringComparer.Ordinal);
        // Leaf lock: guards only the dictionary above. Never held across a device
        // write — the flush copies out, releases, then writes.
        private readonly object _pendingWheelCfgLock = new object();
        private System.Timers.Timer? _wheelCfgFlushTimer;

        /// <summary>
        /// Park a flash-backed wheel write until the user stops changing it.
        /// Latest value per command wins; the quiet window restarts on every call.
        /// </summary>
        private void QueueWheelCfgWrite(string command, long cacheValue, System.Action write)
        {
            lock (_pendingWheelCfgLock)
            {
                _pendingWheelCfg[command] = (cacheValue, write);
                if (_wheelCfgFlushTimer == null)
                {
                    _wheelCfgFlushTimer = new System.Timers.Timer(WheelCfgFlushDelayMs) { AutoReset = false };
                    _wheelCfgFlushTimer.Elapsed += (_, __) => FlushPendingWheelCfgWrites();
                }
                // Restart the quiet window.
                _wheelCfgFlushTimer.Stop();
                _wheelCfgFlushTimer.Start();
            }
        }

        private void FlushPendingWheelCfgWrites()
        {
            System.Collections.Generic.KeyValuePair<string, (long CacheValue, System.Action Write)>[] due;
            lock (_pendingWheelCfgLock)
            {
                if (_pendingWheelCfg.Count == 0) return;
                due = System.Linq.Enumerable.ToArray(_pendingWheelCfg);
                _pendingWheelCfg.Clear();
            }

            SyncWheelCfgCache();
            foreach (var kv in due)
            {
                try
                {
                    // Re-check against the cache now: the value may have travelled and
                    // come back, or an apply may have written it in the meantime.
                    if (!WheelCfgChanged(kv.Key, kv.Value.CacheValue)) continue;
                    kv.Value.Write();
                }
                catch (System.Exception ex)
                {
                    MozaLog.Warn($"[AZOM] wheel-cfg flush '{kv.Key}': {ex.Message}");
                }
            }
        }

        /// <summary>Stop the coalescing timer and drop anything still parked. Called
        /// from the plugin teardown; pending values are already persisted in the
        /// profile, so the next connect's apply carries them.</summary>
        internal void Shutdown()
        {
            System.Timers.Timer? t;
            lock (_pendingWheelCfgLock)
            {
                _pendingWheelCfg.Clear();
                t = _wheelCfgFlushTimer;
                _wheelCfgFlushTimer = null;
            }
            try { t?.Stop(); t?.Dispose(); } catch { /* teardown is best-effort */ }
        }

        private static long Fnv(long h, long v) { unchecked { return (h ^ v) * 1099511628211L; } }

        /// <summary>
        /// Change-gate keyed on the PAYLOAD BYTES actually going out. Used for the
        /// multi-field array commands so the UI handler and
        /// <see cref="ApplyWheelToHardware"/> agree on the cache value without having
        /// to encode the same (mode, ms) composite identically in two places — what
        /// matters is only whether the bytes on the wire changed.
        /// </summary>
        private bool WheelCfgChangedBytes(string key, byte[]? payload) =>
            WheelCfgChanged(key, HashPayload(payload));

        private static long HashPayload(byte[]? payload)
        {
            long h = unchecked((long)1469598103934665603UL);
            if (payload == null) return Fnv(h, -9);
            h = Fnv(h, payload.Length);
            foreach (var b in payload) h = Fnv(h, b);
            return h;
        }

        /// <summary>
        /// True when a profile apply must NOT touch flash-backed wheel settings at all.
        ///
        /// The FSR1 is the one wheel we deliberately read NOTHING back from — its param
        /// store wedges into a permanent read-failure storm (wheel-0x17.md § Param-store
        /// wedge), so <c>DeviceProber.BuildNewWheelLedReadCommands</c> returns an empty
        /// list for it. That makes <see cref="PrimeWheelCfgFromDevice"/> inert here (it
        /// can only prime keys the wheel reports), so every connect re-asserted the whole
        /// idle/sleep-light family from the profile — 8 commands, 2 of which reached
        /// flash, on a wheel that bricks its display from exactly that wear.
        ///
        /// PitHouse's own behaviour is the model: it never writes this family on connect
        /// and only writes when the user changes a control. The wheel persists the values
        /// across power cycles, so nothing is lost except re-asserting a per-game profile's
        /// wheel LED/idle settings on an FSR1 — that now happens when the user touches the
        /// control, not at every connect.
        /// </summary>
        private bool SuppressApplyFlashCfgWrites => _data.IsFsr1DisplayWheel;

        /// <summary>
        /// Apply-path change gate — <see cref="WheelCfgChanged"/> plus the FSR1
        /// suppression above. Returns false WITHOUT recording the value, so a later user
        /// edit to that same value still reaches the wheel.
        /// </summary>
        private bool WheelCfgChangedForApply(string key, long value) =>
            !(SuppressApplyFlashCfgWrites && IsFlashBackedWheelCfg(key)) && WheelCfgChanged(key, value);

        /// <summary>Apply-path counterpart of <see cref="WheelCfgChangedBytes"/>.</summary>
        private bool WheelCfgChangedBytesForApply(string key, byte[]? payload) =>
            !(SuppressApplyFlashCfgWrites && IsFlashBackedWheelCfg(key)) && WheelCfgChangedBytes(key, payload);

        private bool WheelCfgChangedArr(string key, int[]? arr)
        {
            long h = unchecked((long)1469598103934665603UL);
            if (arr == null) h = Fnv(h, -7);
            else { h = Fnv(h, arr.Length); foreach (var v in arr) h = Fnv(h, v); }
            return WheelCfgChanged(key, h);
        }

        private bool WheelCfgChangedArr(string key, bool[]? arr)
        {
            long h = unchecked((long)1469598103934665603UL);
            if (arr == null) h = Fnv(h, -8);
            else { h = Fnv(h, arr.Length); foreach (var v in arr) h = Fnv(h, v ? 1 : 0); }
            return WheelCfgChanged(key, h);
        }

        // Base FFB/motor settings are flash-backed exactly like the wheel config
        // above. ApplyBaseToHardware fires on every wheel-detect (not just base
        // detect), and it re-wrote the WHOLE base parameter table unconditionally.
        // On an R5 + bare-"CS" rim (bundle 2026-06-22) that re-push, while the rim
        // was mid-attach, bounced the motor (motor_wrapper "MotorMode From 12 to
        // 0") and reset the base — dropping the rim — a self-sustaining
        // detect->reapply->reset->redetect loop. PitHouse never re-writes base FFB
        // params (it reads them; the base holds them in NVM), so it never trips
        // this. Mirror the wheel cache: only write a base setting when its value
        // actually changed since the last write to THIS base.
        //
        // CAUTION — empty UID must NOT invalidate. ResetBaseDetection ->
        // _data.ClearBaseIdentity() blanks _data.BaseMcuUid on connection loss, and
        // ApplyBaseToHardware can run before the re-probe answers, so the UID reads
        // back empty for a window on every reconnect. Treat an empty UID as
        // "unknown, keep the cache" rather than a new base — otherwise each flap
        // would clear the cache and re-push the very storm this guards against. A
        // genuinely different base reports a different non-empty UID and re-writes
        // its config once.
        //
        // A rim detach no longer blanks the base UID (ClearWheelIdentity is
        // rim-scoped), so a rim flap keeps both the identity and the cache.
        //
        // STATIC so the "write base config once per physical base" guarantee spans
        // plugin reloads: HardwareApplier is reconstructed on every game-switch
        // (it is not part of the persistent wire), and a single full re-push WITH
        // the rim attached is exactly what reboots a marginal base. The persistent
        // wire already keeps the wheel from re-negotiating across a reload; this
        // keeps the base from being re-flashed across one. A game switch still
        // applies the new profile's *changed* base values as minimal diffs.
        private static readonly System.Collections.Generic.Dictionary<string, long> s_baseCfgCache
            = new System.Collections.Generic.Dictionary<string, long>();
        private static byte[] s_baseCfgCacheUid = System.Array.Empty<byte>();

        private void SyncBaseCfgCache()
        {
            var uid = _data.BaseMcuUid ?? System.Array.Empty<byte>();
            if (uid.Length == 0) return;   // unknown/blanked identity — keep cache
            // First non-empty identity on a fresh cache: adopt it without clearing
            // so config written while the UID was still being read (cold start)
            // isn't needlessly re-sent. Only a genuinely DIFFERENT base clears.
            if (s_baseCfgCacheUid.Length == 0)
            {
                s_baseCfgCacheUid = (byte[])uid.Clone();
                return;
            }
            bool same = uid.Length == s_baseCfgCacheUid.Length;
            for (int i = 0; same && i < uid.Length; i++)
                if (uid[i] != s_baseCfgCacheUid[i]) same = false;
            if (!same)
            {
                s_baseCfgCache.Clear();
                s_baseCfgCacheUid = (byte[])uid.Clone();
            }
        }

        /// <summary>True (and records the new value) iff this base setting differs
        /// from the last value written to the current base — i.e. an actual change
        /// worth a wire write. Returns false to skip a redundant re-write, which on
        /// some bases bounces the motor mode and resets the base.</summary>
        private bool BaseCfgChanged(string key, long value)
        {
            if (s_baseCfgCache.TryGetValue(key, out var prev) && prev == value) return false;
            s_baseCfgCache[key] = value;
            return true;
        }

        /// <summary>Prime the write cache with the base's READ-BACK value (add-only, never
        /// overwrites a recorded write). With the cache primed, the first apply of a session
        /// skips every param the base already holds — the base persists its settings, so a
        /// fresh SimHub session re-writing an unchanged profile was pure parameter-store
        /// wear (observed as the full Table-5 "Written" burst on every connect).</summary>
        private void BaseCfgPrime(string key, long deviceValue)
        {
            if (!s_baseCfgCache.ContainsKey(key)) s_baseCfgCache[key] = deviceValue;
        }

        // Resolve the pipe that owns pedals / handbrake. Pedals or a handbrake
        // can be attached to the base OR to a dedicated Universal Hub pipe, so
        // settings reads and calibration writes must target whichever connection
        // detected the device (recorded owner-before-flag by DeviceProber). Null
        // owner → no opinion → fall back to the primary manager (today's behavior
        // for base-attached peripherals).
        private MozaDeviceManager PedalsManager => _detectionState.PedalsOwner ?? _deviceManager;
        private MozaDeviceManager HandbrakeManager => _detectionState.HandbrakeOwner ?? _deviceManager;
        // HGP and SGP are independent devices, each routed to the pipe it was detected
        // on (each on its own USB port, or one relayed on the base/hub).
        private MozaDeviceManager HgpManager => _detectionState.HgpOwner ?? _deviceManager;
        private MozaDeviceManager SgpManager => _detectionState.SgpOwner ?? _deviceManager;
        // Base FFB/motor/ambient writes must target whichever pipe detected the
        // base. Normally that's the primary; after a base→hub primary migration
        // (broken base, wheel on hub) the base lives on a dedicated base-aux pipe,
        // so its writes must NOT follow the now-hub-bound primary _deviceManager.
        // Null owner → fall back to the primary (today's behavior). Mirrors the
        // Pedals/Handbrake resolvers above.
        private MozaDeviceManager BaseManager => _detectionState.BaseOwner ?? _deviceManager;

        private static int Eff(int overlayVal, int baselineVal) =>
            overlayVal >= 0 ? overlayVal : baselineVal;

        private static int[]? EffArr(int[]? overlayArr, int[]? baselineArr) =>
            overlayArr ?? baselineArr;

        // ===== Apply*ToHardware =====

        /// <summary>Push wheel-scoped settings to the connected wheel. Mirrors to _data unconditionally; writes detection-gated.</summary>
        public void ApplyWheelToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

            var ov = _plugin.GetCurrentWheelOverlay(profile);

            int telemMode      = Eff(ov?.WheelTelemetryMode ?? -1, profile.WheelTelemetryMode);
            // Idle-effect / idle-speed bundle is per-wheel-page (schema v9); null
            // = leave wheel's value alone. These six fields used to live on the
            // per-game overlay + profile baseline; v9 promoted them to a
            // wheel-level bundle because the idle animation is a property of
            // the wheel, not the game.
            var idleBundle     = _plugin.ActiveWheelIdle;
            int idleEffect     = idleBundle?.TelemetryEffect ?? -1;
            int btnIdleEffect  = idleBundle?.ButtonsEffect ?? -1;
            int knobIdleEffect = idleBundle?.KnobEffect ?? -1;
            int idleSpeed      = idleBundle?.TelemetrySpeedMs ?? -1;
            int btnIdleSpeed   = idleBundle?.ButtonsSpeedMs ?? -1;
            int knobIdleSpeed  = idleBundle?.KnobSpeedMs ?? -1;
            int knobLedMode    = Eff(ov?.WheelKnobLedMode ?? -1, profile.WheelKnobLedMode);
            int btnLedMode     = Eff(ov?.WheelButtonsLedMode ?? -1, profile.WheelButtonsLedMode);
            // Sleep bundle is per-wheel-page (schema v8); null = leave wheel's value alone.
            var sleepBundle    = _plugin.ActiveWheelSleep;
            int sleepMode      = sleepBundle?.Mode ?? -1;
            int sleepTimeout   = sleepBundle?.TimeoutMin ?? -1;
            int sleepSpeed     = sleepBundle?.SpeedMs ?? -1;
            int[]? sleepColor  = sleepBundle?.Color;
            int rpmBri         = Eff(ov?.WheelRpmBrightness ?? -1, profile.WheelRpmBrightness);
            int btnBri         = Eff(ov?.WheelButtonsBrightness ?? -1, profile.WheelButtonsBrightness);
            int flagsBri       = Eff(ov?.WheelFlagsBrightness ?? -1, profile.WheelFlagsBrightness);
            int rpmInd         = Eff(ov?.WheelRpmIndicatorMode ?? -1, profile.WheelRpmIndicatorMode);
            int rpmDisp        = Eff(ov?.WheelRpmDisplayMode ?? -1, profile.WheelRpmDisplayMode);
            int esRpmBri       = Eff(ov?.WheelESRpmBrightness ?? -1, profile.WheelESRpmBrightness);
            // Inputs — overlay-only (no profile baseline).
            int paddles        = ov?.WheelPaddlesMode ?? -1;
            int clutchPoint    = ov?.WheelClutchPoint ?? -1;
            int knobMode       = ov?.WheelKnobMode ?? -1;
            int stickMode      = ov?.WheelStickMode ?? -1;
            int knobRingBri    = Eff(ov?.WheelKnobRingBrightness ?? -1, profile.WheelKnobRingBrightness);

            // Shared/master LED brightness override. Once the user has moved
            // SimHub's master LED-brightness slider (WheelLedMasterBrightness >= 0),
            // it is the authority for the wheel's firmware group brightness — the
            // rpm/buttons/knob-ring values follow it equally, so a connect/profile
            // re-apply asserts the master rather than reverting to the device-read
            // profile value. -1 = user has not engaged the master; keep the
            // per-group profile brightness untouched (unchanged legacy behaviour).
            // ES (old-protocol) RPM brightness is a different command/range and is
            // deliberately not overridden here. Live drags apply via the data-thread
            // ApplyMasterWheelLedBrightness path, which shares the same cfg cache.
            int ledMaster = _plugin.WheelLedMasterBrightness;
            if (ledMaster >= 0) { rpmBri = ledMaster; btnBri = ledMaster; knobRingBri = ledMaster; }
            // Per-zone "Brightness limiter and balance" sliders are more specific than the
            // master, so a zone the user has actually moved wins over it (and over the
            // profile value). -1 = zone never moved; keep whatever the two lines above left.
            int zoneRpmBri  = _plugin.WheelLedBrightnessRpm;
            int zoneBtnBri  = _plugin.WheelLedBrightnessButtons;
            int zoneKnobBri = _plugin.WheelLedBrightnessKnob;
            if (zoneRpmBri  >= 0) rpmBri      = zoneRpmBri;
            if (zoneBtnBri  >= 0) btnBri      = zoneBtnBri;
            if (zoneKnobBri >= 0) knobRingBri = zoneKnobBri;

            // _data mirror (UI binding).
            if (telemMode      >= 0) _data.WheelTelemetryMode      = telemMode;
            if (idleEffect     >= 0) _data.WheelTelemetryIdleEffect = idleEffect;
            if (btnIdleEffect  >= 0) _data.WheelButtonsIdleEffect  = btnIdleEffect;
            if (knobIdleEffect >= 0) _data.WheelKnobIdleEffect     = knobIdleEffect;
            if (knobLedMode    >= 0) _data.WheelKnobLedMode        = knobLedMode;
            if (btnLedMode     >= 0) _data.WheelButtonsLedMode     = btnLedMode;
            if (idleSpeed      >= 0) _data.WheelTelemetryIdleSpeedMs = idleSpeed;
            if (btnIdleSpeed   >= 0) _data.WheelButtonsIdleSpeedMs = btnIdleSpeed;
            if (knobIdleSpeed  >= 0) _data.WheelKnobIdleSpeedMs    = knobIdleSpeed;
            if (sleepMode      >= 0) _data.WheelIdleMode           = sleepMode;
            if (sleepTimeout   >= 0) _data.WheelIdleTimeout        = sleepTimeout;
            if (sleepSpeed     >= 0) _data.WheelIdleSpeed          = sleepSpeed;
            if (sleepColor != null && sleepColor.Length > 0)
            {
                var rgb = MozaProfile.UnpackColor(sleepColor[0]);
                _data.WheelIdleColor[0] = rgb[0];
                _data.WheelIdleColor[1] = rgb[1];
                _data.WheelIdleColor[2] = rgb[2];
            }
            if (rpmBri    >= 0) _data.WheelRpmBrightness     = rpmBri;
            if (btnBri    >= 0) _data.WheelButtonsBrightness = btnBri;
            if (flagsBri  >= 0) _data.WheelFlagsBrightness   = flagsBri;
            if (esRpmBri  >= 0) _data.WheelESRpmBrightness   = esRpmBri;
            if (rpmInd    >= 0) _data.WheelRpmIndicatorMode  = rpmInd;
            if (rpmDisp   >= 0) _data.WheelRpmDisplayMode    = rpmDisp;
            if (paddles   >= 0) _data.WheelPaddlesMode       = paddles;
            if (clutchPoint >= 0) _data.WheelClutchPoint     = clutchPoint;
            if (knobMode  >= 0) _data.WheelKnobMode          = knobMode;
            if (stickMode >= 0) _data.WheelStickMode         = stickMode;
            // Per-knob signal modes — overlay-only (no profile baseline), mirrored
            // for UI display here; re-pushed to the wheel (change-gated) in the
            // NewWheelDetected block below so a per-game BUTTON/KNOB choice reaches
            // the wheel on profile switch (firmware persists a single value, so a
            // readback alone can't re-assert a different profile's saved mode).
            if (ov?.WheelKnobSignalModes != null)
                for (int i = 0; i < Math.Min(_data.WheelKnobSignalModes.Length, ov.WheelKnobSignalModes.Length); i++)
                    if (ov.WheelKnobSignalModes[i] >= 0) _data.WheelKnobSignalModes[i] = ov.WheelKnobSignalModes[i];

            int[]? rpmColors          = EffArr(ov?.WheelRpmColors, profile.WheelRpmColors);
            int[]? rpmBlinkColors     = EffArr(ov?.WheelRpmBlinkColors, profile.WheelRpmBlinkColors);
            int[]? buttonColors       = EffArr(ov?.WheelButtonColors, profile.WheelButtonColors);
            bool[]? buttonDefaults    = ov?.WheelButtonDefaultDuringTelemetry
                                        ?? profile.WheelButtonDefaultDuringTelemetry;
            int[]? flagColors         = EffArr(ov?.WheelFlagColors, profile.WheelFlagColors);
            int[]? idleColor          = EffArr(ov?.WheelIdleColor, profile.WheelIdleColor);
            int[]? esRpmColors        = EffArr(ov?.WheelESRpmColors, profile.WheelESRpmColors);
            int[]? knobBgColors       = EffArr(ov?.WheelKnobBackgroundColors, profile.WheelKnobBackgroundColors);
            int[]? knobPrimaryColors  = EffArr(ov?.WheelKnobPrimaryColors, profile.WheelKnobPrimaryColors);
            int[]? knobRingColors     = EffArr(ov?.WheelKnobRingColors, profile.WheelKnobRingColors);
            bool knobDefaultTelemetry = ov?.WheelKnobDefaultDuringTelemetry
                                        ?? profile.WheelKnobDefaultDuringTelemetry;
            int knobStaticTimeoutMs   = (ov != null && ov.WheelKnobStaticTimeoutMs >= 0)
                                        ? ov.WheelKnobStaticTimeoutMs
                                        : profile.WheelKnobStaticTimeoutMs;

            // Mirror colors into _data (UI uses _data.* for swatches).
            MozaProfile.UnpackColorsInto(rpmColors, _data.WheelRpmColors);
            MozaProfile.UnpackColorsInto(rpmBlinkColors, _data.WheelRpmBlinkColors);
            MozaProfile.UnpackColorsInto(buttonColors, _data.WheelButtonColors);
            if (buttonDefaults != null)
            {
                int n = Math.Min(buttonDefaults.Length, _data.WheelButtonDefaultDuringTelemetry.Length);
                for (int i = 0; i < n; i++)
                    _data.WheelButtonDefaultDuringTelemetry[i] = buttonDefaults[i];
            }
            MozaProfile.UnpackColorsInto(flagColors, _data.WheelFlagColors);
            if (idleColor != null && idleColor.Length > 0)
            {
                var rgb = MozaProfile.UnpackColor(idleColor[0]);
                _data.WheelIdleColor[0] = rgb[0];
                _data.WheelIdleColor[1] = rgb[1];
                _data.WheelIdleColor[2] = rgb[2];
            }
            MozaProfile.UnpackColorsInto(esRpmColors, _data.WheelESRpmColors);
            MozaProfile.UnpackColorsInto(knobBgColors, _data.WheelKnobBackgroundColors);
            MozaProfile.UnpackColorsInto(knobPrimaryColors, _data.WheelKnobPrimaryColors);
            MozaProfile.UnpackColorsInto(knobRingColors, _data.KnobRingColors);
            if (knobRingBri >= 0) _data.KnobRingBrightness = knobRingBri;
            _data.WheelKnobDefaultDuringTelemetry = knobDefaultTelemetry;
            _data.WheelKnobStaticTimeoutMs = knobStaticTimeoutMs;

            // Hardware writes — gated per-section on the matching detection
            // flag. NOT gated on _data.IsConnected: that's the "any device
            // responded" proxy, which is false on a fresh MozaData (hot-reload
            // case where _data = new MozaData() but the serial wire and the
            // wheel's prior detection state are preserved via
            // s_persistentConnection / s_persistentDetectionState). Gating
            // the whole method on IsConnected meant ApplyProfile fired at
            // Init couldn't write anything until base-mcu-temp / hub-* /
            // dash-* echoed back to flip IsConnected, and DeviceProber's
            // wheel-model-name handler skips its re-apply path when
            // LastKnownWheelModel was preserved — so static-mode colors
            // never reached the wheel on game switch. _deviceManager.WriteX
            // already checks _connection.IsConnected and returns false on
            // a dead wire, so per-section gates plus the connection-level
            // check is enough.
            // Persistent (flash-backed) wheel settings below: only write when the
            // value changed since the last write to THIS wheel, so re-detection /
            // re-apply doesn't re-flash unchanged settings and wear the wheel's
            // parameter store. PitHouse writes each of these exactly once per connect.
            SyncWheelCfgCache();

            // Only push persistent config once the wheel's identity has resolved.
            // Previously this fell back to WheelModelInfo.Default and wrote a generic
            // subset to UNIDENTIFIED wheels — but an older/unknown wheel may not
            // support those params, and writing/reading params a wheel doesn't have
            // can wedge its firmware (observed: "Table 8: Failed to Read/Write
            // Parameter" → dead identity → re-detect loop → bricked). PitHouse
            // identifies first, then writes only that model's params. A genuine wheel
            // resolves identity within ~1-2 s and DeviceProber re-applies then; a wheel
            // we can't identify is left alone rather than poked with guessed config.
            var model = _plugin.WheelModelInfo;   // null until identity resolves
            if (_detectionState.NewWheelDetected && model != null)
            {
                // Capability snapshot for the (now identified) active wheel.
                int rpmCount = model.RpmLedCount;
                int btnCount = model.ButtonLedCount;
                bool hasRpm        = rpmCount > 0;
                bool hasBtn        = btnCount > 0;
                bool hasKnob       = model.KnobCount > 0;
                bool hasSleepLight = model.HasSleepLight;
                // The per-zone idle-EFFECT (cmd 0x1d) and idle-INTERVAL (0x1e)
                // writes are idle/standby LED animations, not the live RPM bar.
                // The legacy bare-"CS" rim has RPM LEDs but does NOT implement
                // these idle params — writing them (gated only on zone presence)
                // is what storms its Table 8 param manager. Gate them on the same
                // "supports idle LED features" capability as the sleep light:
                // every known wheel with idle effects also has HasSleepLight=true;
                // CS and unidentified wheels (HasSleepLight=false) are skipped.
                bool hasIdleLed = hasSleepLight;

                // FSR1: never push the idle / sleep-light family from an APPLY (connect,
                // profile switch, re-detect). This wheel HAS the feature — PitHouse
                // exposes it and reads those params back — but across every FSR1 capture
                // PitHouse issues ZERO writes to the family (0x3F cmds 1d/1e/20/21/22/24)
                // and writes only when the user moves the control in its UI. The wheel
                // persists the values itself, so an apply that re-asserts them is pure
                // parameter-store wear on the one rim whose store wedges irrecoverably
                // (docs wheel-0x17.md § Param-store wedge; 2026-08-13 bundle: 7 writes →
                // 8 Table-2 param writes at connect, storm 3.4 min later).
                //
                // Nothing is lost: the sleep/idle controls write straight through on user
                // edit via WriteIfWheelDetected / WriteArrayIfWheelDetected /
                // WriteColorIfWheelDetected (MozaWheelSettingsControl), which is exactly
                // PitHouse's model. These are also wheel-level prefs (not per-game), so
                // there is no profile-switch behaviour to preserve here.
                if (_plugin.IsFsr1DisplayWheel)
                {
                    hasIdleLed = false;
                    hasSleepLight = false;
                }

                if (telemMode      >= 0            && WheelCfgChangedForApply("wheel-telemetry-mode", telemMode))          _deviceManager.WriteSetting("wheel-telemetry-mode", telemMode);
                if (idleEffect     >= 0 && hasRpm  && hasIdleLed && WheelCfgChangedForApply("wheel-telemetry-idle-effect", idleEffect)) _deviceManager.WriteSetting("wheel-telemetry-idle-effect", idleEffect);
                if (btnIdleEffect  >= 0 && hasBtn  && hasIdleLed && WheelCfgChangedForApply("wheel-buttons-idle-effect", btnIdleEffect))_deviceManager.WriteSetting("wheel-buttons-idle-effect", btnIdleEffect);
                if (knobIdleEffect >= 0 && hasKnob && hasIdleLed && WheelCfgChangedForApply("wheel-knob-idle-effect", knobIdleEffect))  _deviceManager.WriteSetting("wheel-knob-idle-effect", knobIdleEffect);
                if (knobLedMode    >= 0 && hasKnob && WheelCfgChangedForApply("wheel-knob-led-mode", knobLedMode))        _deviceManager.WriteSetting("wheel-knob-led-mode", knobLedMode);
                if (btnLedMode     >= 0 && hasBtn  && WheelCfgChangedForApply("wheel-buttons-led-mode", btnLedMode))      _deviceManager.WriteSetting("wheel-buttons-led-mode", btnLedMode);

                // Knob input signal mode (encoder = BUTTON vs KNOB) — overlay-only,
                // per-(profile x wheel-page). Re-push on connect/profile-switch,
                // change-gated like the LED/brightness settings above: the wheel
                // firmware persists a single value, so a per-game mode only reaches
                // the wheel if we re-assert it here. Legacy single mode on wheels
                // without per-knob support; per-knob wheel-knob-signal-mode{fw}
                // (logical->firmware index remapped) on those that report it. The UI
                // only edits one family per wheel, so the overlay only carries the
                // family this wheel supports — writing whatever is set is safe.
                //
                // No hasKnob gate: that is the knob-LED capability, and most rims
                // have encoders with no knob LEDs. The overlay is its own capability
                // evidence here — it is keyed by the wheel's page GUID and has no
                // profile baseline, so a value >= 0 means the user set it on THIS
                // rim's page, which only happens when the UI offered the control,
                // which only happens when the wheel answered the read. Same reasoning
                // as the paddle/clutch/stick block below. A capability gate would
                // also mis-fire on ordering: DeviceProber issues the signal-mode
                // reads just BEFORE the first-sight ApplyProfile, so the answers land
                // after this runs and the re-assert would be skipped on exactly the
                // wheels that need it.
                if (knobMode >= 0 && WheelCfgChangedForApply("wheel-knob-mode", knobMode))
                    _deviceManager.WriteSetting("wheel-knob-mode", knobMode);
                if (ov?.WheelKnobSignalModes != null)
                {
                    int nSig = ov.WheelKnobSignalModes.Length;
                    for (int i = 0; i < nSig && i < 5; i++)
                    {
                        int sm = ov.WheelKnobSignalModes[i];
                        if (sm < 0) continue;
                        int fwIdx = model.SignalModeFirmwareIndex(i);
                        if (WheelCfgChangedForApply($"wheel-knob-signal-mode{fwIdx}", sm))
                            _deviceManager.WriteSetting($"wheel-knob-signal-mode{fwIdx}", sm);
                    }
                }
                // Paddle input mode + the combined-mode clutch split point —
                // overlay-only, per-(profile x wheel-page). Re-pushed here for the
                // same reason as the knob signal modes above: the wheel firmware
                // persists a single value, and newer firmware silently drops the
                // readback (see WheelOverride's "Inputs" comment), so a per-game —
                // or per-WHEEL — pick only reaches the rim if we re-assert it.
                // Without this a mode that landed on the wrong rim stayed there,
                // since nothing ever rewrote the right one.
                // No capability gate: DeviceProber.NewWheelCoreReadCommands already
                // reads these from every new-protocol wheel model-blind ("paddles/
                // clutch/stick exist on every new-protocol wheel"), and this block
                // is new-protocol-gated. The cfg cache is keyed on the wheel's MCU
                // UID (SyncWheelCfgCache), so each rim re-asserts its own value on
                // attach instead of dedup'ing against the previous rim's write.
                // Wire form is 1/2/3 while the overlay stores the 0/1/2 display
                // form — hence the +1, matching the UI handler.
                if (paddles >= 0 && WheelCfgChangedForApply("wheel-paddles-mode", paddles))
                    _deviceManager.WriteSetting("wheel-paddles-mode", paddles + 1);
                if (clutchPoint >= 0 && WheelCfgChangedForApply("wheel-clutch-point", clutchPoint))
                    _deviceManager.WriteSetting("wheel-clutch-point", clutchPoint);
                if (idleEffect >= 0 && idleSpeed >= 0 && hasRpm && hasIdleLed)
                {
                    var p = BuildIdleIntervalPayload(idleEffect, idleSpeed);
                    if (WheelCfgChangedBytesForApply("wheel-telemetry-idle-interval", p))
                        _deviceManager.WriteArray("wheel-telemetry-idle-interval", p);
                }
                if (btnIdleEffect >= 0 && btnIdleSpeed >= 0 && hasBtn && hasIdleLed)
                {
                    var p = BuildIdleIntervalPayload(btnIdleEffect, btnIdleSpeed);
                    if (WheelCfgChangedBytesForApply("wheel-buttons-idle-interval", p))
                        _deviceManager.WriteArray("wheel-buttons-idle-interval", p);
                }
                if (knobIdleEffect >= 0 && knobIdleSpeed >= 0 && hasKnob && hasIdleLed)
                {
                    var p = BuildIdleIntervalPayload(knobIdleEffect, knobIdleSpeed);
                    if (WheelCfgChangedBytesForApply("wheel-knob-idle-interval", p))
                        _deviceManager.WriteArray("wheel-knob-idle-interval", p);
                }
                if (sleepMode    >= 0 && hasSleepLight && WheelCfgChangedForApply("wheel-idle-mode", sleepMode))       _deviceManager.WriteSetting("wheel-idle-mode", sleepMode);
                if (sleepTimeout >= 0 && hasSleepLight && WheelCfgChangedForApply("wheel-idle-timeout", sleepTimeout)) _deviceManager.WriteSetting("wheel-idle-timeout", sleepTimeout);
                if (sleepMode >= 0 && sleepSpeed >= 0 && hasSleepLight)
                {
                    var p = BuildIdleIntervalPayload(sleepMode, sleepSpeed);
                    if (WheelCfgChangedBytesForApply("wheel-idle-speed", p))
                        _deviceManager.WriteArray("wheel-idle-speed", p);
                }
                if (sleepColor != null && sleepColor.Length > 0 && hasSleepLight)
                {
                    var rgb = MozaProfile.UnpackColor(sleepColor[0]);
                    if (WheelCfgChangedForApply("wheel-idle-color", ((long)rgb[0] << 16) | ((long)rgb[1] << 8) | rgb[2]))
                        _deviceManager.WriteColor("wheel-idle-color", rgb[0], rgb[1], rgb[2]);
                }
                if (rpmBri   >= 0 && hasRpm && WheelCfgChangedForApply("wheel-rpm-brightness", rpmBri))     _deviceManager.WriteSetting("wheel-rpm-brightness", rpmBri);
                if (btnBri   >= 0 && hasBtn && WheelCfgChangedForApply("wheel-buttons-brightness", btnBri)) _deviceManager.WriteSetting("wheel-buttons-brightness", btnBri);
                if (flagsBri >= 0 && _detectionState.DashDetected && WheelCfgChangedForApply("dash-flags-brightness", flagsBri))
                    _deviceManager.WriteSetting("dash-flags-brightness", flagsBri);

                if (WheelCfgChangedArr("wheel-rpm-color", rpmColors))
                    WriteColorArray(rpmColors, "wheel-rpm-color", rpmCount);
                if (hasRpm && WheelCfgChangedArr("wheel-rpm-blink-color", rpmBlinkColors))
                    WriteColorArray(rpmBlinkColors, "wheel-rpm-blink-color", Math.Min(10, rpmCount));
                bool btnColChg = WheelCfgChangedArr("wheel-button-color", buttonColors);
                bool btnDefChg = WheelCfgChangedArr("wheel-button-defaults", buttonDefaults);
                if (btnColChg || btnDefChg) WriteButtonStaticColors(buttonColors, model);
                if (_detectionState.DashDetected && WheelCfgChangedArr("dash-flag-color", flagColors))
                    WriteColorArray(flagColors, "dash-flag-color", 6);
                if (idleColor != null && idleColor.Length > 0 && hasSleepLight)
                {
                    var rgb = MozaProfile.UnpackColor(idleColor[0]);
                    if (WheelCfgChangedForApply("wheel-idle-color", ((long)rgb[0] << 16) | ((long)rgb[1] << 8) | rgb[2]))
                        _deviceManager.WriteColor("wheel-idle-color", rgb[0], rgb[1], rgb[2]);
                }
                bool knobBgChg  = WheelCfgChangedArr("wheel-knob-bg-color", knobBgColors);
                bool knobPriChg = WheelCfgChangedArr("wheel-knob-primary-color", knobPrimaryColors);
                // Brightness keys on the REAL command name ("wheel-knob-brightness"), the
                // one the readback and the zone/master paths use — a separate
                // "wheel-knob-ring-brightness" key described the same 1B 03 FF register
                // under a name no command owns, so the two writers could not dedupe
                // against each other and the wheel's readback never primed it.
                bool knobRingChg = WheelCfgChangedArr("wheel-knob-ring-color", knobRingColors)
                                   | (knobRingBri >= 0
                                      && WheelCfgDiffers("wheel-knob-brightness", knobRingBri));
                // Invalidate the live cache after each Apply pass so the next live tick
                // re-sends instead of dedup'ing against a frame whose underlying wheel
                // state we may have just rewritten. Live cache is volatile (no flash
                // cost), so keep this unconditional as before.
                MozaLedDeviceManager.InvalidateLiveCacheAny(
                    LedKind.Rpm | LedKind.Button | LedKind.Flag);
                if (knobBgChg || knobPriChg) WriteKnobColors(knobBgColors, knobPrimaryColors);
                if (knobRingChg) WriteKnobRingColors(knobRingColors, knobRingBri);

                // If we have no saved active-colour overlay (fresh install, or
                // user never touched the centre swatch), read the wheel's own
                // stored values back — they'll land in MozaData via
                // UpdateFromArray for wheel-knob{N}-active-color and surface
                // as the visible defaults instead of black-on-load.
                if (knobPrimaryColors == null && hasKnob)
                {
                    int knobs = model.KnobCount;
                    for (int i = 0; i < knobs && i < 5; i++)
                        _deviceManager.ReadSetting($"wheel-knob{i + 1}-active-color");
                }
            }

            if (_detectionState.OldWheelDetected)
            {
                // Cache the value that goes on the WIRE (+1), not the stored form —
                // the UI handler writes the same +1 raw, so both paths must key the
                // cache identically or each would see the other's write as a change.
                if (rpmInd   >= 0 && WheelCfgChangedForApply("wheel-rpm-indicator-mode", rpmInd + 1)) _deviceManager.WriteSetting("wheel-rpm-indicator-mode", rpmInd + 1);
                if (rpmDisp  >= 0 && WheelCfgChangedForApply("wheel-set-rpm-display-mode", rpmDisp))   _deviceManager.WriteSetting("wheel-set-rpm-display-mode", rpmDisp);
                if (esRpmBri >= 0 && WheelCfgChangedForApply("wheel-old-rpm-brightness", esRpmBri))    _deviceManager.WriteSetting("wheel-old-rpm-brightness", esRpmBri);
                if (WheelCfgChangedArr("wheel-old-rpm-color", esRpmColors))
                    WriteColorArray(esRpmColors, "wheel-old-rpm-color", 10);
            }

            // Display-rotation mode (0=off, 1=smooth, 2=immediate). Session-0x02
            // FF property push (kind=5), so it goes through the wheel's main sender,
            // NOT the group-0x3F device-manager write path. VGS: profile-driven.
            // Every other display wheel: forced off — rotation can get latched on
            // in wheel flash by misrouted V0 value frames (kind=5 collision, seen
            // on W13/FSR V2 under a wrong manual era pick) and these wheels have
            // no UI anywhere to clear it. Fires on every wheel (re)detection and
            // profile switch; TelemetrySender.SendSessionInitHandshake covers the
            // session-init case where this push may predate an Active session.
            if (model?.SupportsDisplayRotation == true)
            {
                if (profile.DashDisplayRotation >= 0)
                    _plugin.TelemetrySender?.SendDashDisplayRotation(profile.DashDisplayRotation);
            }
            else if (model?.HasDisplay == true)
            {
                _plugin.TelemetrySender?.SendDashDisplayRotation(0);
            }
        }

        /// <summary>
        /// Push dashboard-scoped settings (brightness, indicator modes, colors,
        /// display brightness/standby) to the dash. _data mirrored always;
        /// writes gated on detection.
        /// </summary>
        public void ApplyDashToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

            // SimHub auto-creates per-game profiles with all-sentinel dash fields
            // and never seeds their baselines. Without seeding, the >=0 guards
            // below skip every write — _data keeps its sentinel default and the
            // wire push never fires, so the wheel display sits at whatever value
            // happened to be on it. Seed sentinels from the global defaults here;
            // the helper is idempotent (sentinel-only).
            if (_plugin.Settings != null)
                profile.SeedBaselineFromFlatFields(_plugin.Settings);

            if (profile.DashRpmBrightness     >= 0) _data.DashRpmBrightness     = profile.DashRpmBrightness;
            if (profile.DashFlagsBrightness   >= 0) _data.DashFlagsBrightness   = profile.DashFlagsBrightness;
            if (profile.DashDisplayBrightness >= 0) _data.DashDisplayBrightness = profile.DashDisplayBrightness;
            if (profile.DashDisplayStandbyMin >= 0) _data.DashDisplayStandbyMin = profile.DashDisplayStandbyMin;
            if (profile.DashRpmIndicatorMode   >= 0) _data.DashRpmIndicatorMode   = profile.DashRpmIndicatorMode;
            if (profile.DashRpmDisplayMode     >= 0) _data.DashRpmDisplayMode     = profile.DashRpmDisplayMode;
            if (profile.DashFlagsIndicatorMode >= 0) _data.DashFlagsIndicatorMode = profile.DashFlagsIndicatorMode;
            MozaProfile.UnpackColorsInto(profile.DashRpmColors, _data.DashRpmColors);
            MozaProfile.UnpackColorsInto(profile.DashRpmBlinkColors, _data.DashRpmBlinkColors);
            MozaProfile.UnpackColorsInto(profile.DashFlagColors, _data.DashFlagColors);

            // Per-section gate only — _data.IsConnected check dropped for
            // the hot-reload-with-persistent-wire case (see ApplyWheelToHardware
            // comment). _deviceManager.WriteX bails on a dead wire.
            if (!_detectionState.DashDetected) return;

            // CM2 standalone path: route via the verified group-0x32 / dev=0x12
            // surface (`cm2-*` commands). The legacy dash-* writes at dev=0x14
            // had no visible effect on CM2 in usb-capture/CM2.md lab tests, so
            // skip the per-LED color and indicator-mode writes for CM2.
            // Brightness goes through both paths as belt-and-suspenders: the
            // cm2-indicator-brightness write is the authoritative one, but
            // sending the legacy dash-rpm-brightness too costs nothing and
            // might engage on different firmware revisions.
            // A CM2 is present (bus or USB) — apply its meter LED config. DECOUPLED:
            // keyed on presence, not on the retired "main sender drives the CM2"
            // predicate, so a CM2 alongside a DISPLAY wheel (which the old predicate
            // excluded) now also gets its meter config.
            // Excluded once the discriminator confirms the bridged dash is a CM1: the
            // cm2-* group-0x32 block below (normal/rpm-group mode, thresholds, the 16
            // stored colours) addresses CM2 meter registers a CM1 doesn't implement.
            bool isCm2 = _plugin.IsCm2Present && !_plugin.DashIsCm1;

            if (profile.DashRpmBrightness   >= 0) _deviceManager.WriteSetting("dash-rpm-brightness", profile.DashRpmBrightness);
            if (profile.DashFlagsBrightness >= 0) _deviceManager.WriteSetting("dash-flags-brightness", profile.DashFlagsBrightness);
            // DECOUPLED: dash screen brightness + standby target the CM2's OWN sender
            // (the _cm2Sender drives the CM2 screen now — the main sender is wheel-only,
            // and idle entirely for a screenless/no-wheel rig, so routing these via the
            // main sender silently no-op'd the CM2 screen). ApplyDashToHardware only
            // runs once a dash is detected, so ActiveCm2Sender is the right target;
            // fall back to the main sender only if no dedicated CM2 sender exists.
            var sender = _plugin.ActiveCm2Sender ?? _plugin.TelemetrySender;
            if (profile.DashDisplayBrightness   >= 0) sender?.SendDashDisplayBrightness(profile.DashDisplayBrightness);
            if (profile.DashDisplayStandbyMin >= 0) sender?.SendDashDisplayStandbyMinutes(profile.DashDisplayStandbyMin);

            if (isCm2)
                ApplyCm2DashboardConfig(profile);
        }

        /// <summary>
        /// Write the CM2-specific meter-config + persistent-color stack on
        /// dev=0x12 (CM2 bridge/main). Sub-cmds and behavior verified in
        /// usb-capture/CM2.md (2026-05-21 lab notes). Called from
        /// <see cref="ApplyDashToHardware"/> when the connection target is a
        /// standalone CM2 (PID 0x0025).
        /// </summary>
        private void ApplyCm2DashboardConfig(MozaProfile profile)
        {
            // All writes go through _plugin.WriteCm2Config so they reach the CM2's OWN
            // pipe + device — a standalone-USB CM2 is on the dedicated dashboard
            // connection, not the wheelbase _deviceManager (which would land these on
            // the wheelbase's 0x12 base-main and drop them). Bus CM2 → wheelbase 0x14.

            // Meter mode toggles — required to put CM2 firmware in SimHub
            // telemetry mode so screen widgets + LED ramp follow value frames.
            // TODO(cm2): cm2-normal-mode 1 vs 2 visually similar in CM2.md
            // lab — confirm 1 is the correct SimHub-mode value via capture.
            WriteCm2Config("cm2-normal-mode", 1);
            WriteCm2Config("cm2-rpm-group-mode", 1);
            WriteCm2Config("cm2-flag-group-mode", 1);

            // RPM regulation mode + thresholds. CM2.md notes percent-vs-absolute
            // encoding is not independently verified, so we write BOTH (percent
            // mode + percent thresholds, plus absolute thresholds derived from
            // MaxRpm) and let the firmware honour whichever it actually uses.
            // TODO(cm2): confirm regulation-mode encoding via capture.
            WriteCm2Config("cm2-rpm-regulation-mode", 0);

            // Default percent ramp: 50,55,60,…,95 covering the upper half of
            // the rev range. CM2 has 16 physical LEDs but the firmware accepts
            // a 10-entry percent ramp (one entry per "rung"; the firmware
            // interpolates across physical positions).
            byte[] percentRamp = new byte[] { 50, 55, 60, 65, 70, 75, 80, 85, 90, 95 };
            WriteCm2Config("cm2-rpm-percent-thresholds", percentRamp);

            // Absolute thresholds derived from MaxRpm (fallback 8000 per
            // CM2.md). Each rung gets (rpm * (i+1) / 10) so the 10 thresholds
            // span 10%..100% of the configured max.
            int maxRpm = 8000;
            // TODO(cm2): plumb MaxRpm from active game when SimHub provides it
            // (currently using a sensible static default).
            for (byte i = 0; i < 10; i++)
            {
                int threshold = (int)((long)maxRpm * (i + 1) / 10);
                WriteCm2Config($"cm2-rpm-absolute-threshold{i + 1}", threshold);
            }

            // Indicator brightness — authoritative path for CM2. Reuse the
            // existing DashRpmBrightness slider as the source so the UI knob
            // does not double up; the legacy dash-rpm-brightness write above
            // is kept for compatibility.
            if (profile.DashRpmBrightness >= 0)
                WriteCm2Config("cm2-indicator-brightness", profile.DashRpmBrightness);

            // STANDBY per-LED colors only (idle appearance, shown when no game
            // is running). rs21_parameter.db: SetIndicatorGroupStandbyModeColor
            // (0x1B 00 FF <i>) + RGB. RPM colors 1-10 → cm2-stored-color1..10,
            // flag colors 1-6 → cm2-stored-color11..16.
            //
            // The LIVE/active colors (0x0B) are NOT written here — they are
            // pushed per-frame through the SimHub LED pipeline
            // (MozaDashLedDeviceManager.SendCm2LiveColors), exactly like the
            // wheel's RPM LEDs. Config sets up the device + idle look only.
            if (profile.DashRpmColors != null)
            {
                int rpmCount = System.Math.Min(profile.DashRpmColors.Length, 10);
                for (int i = 0; i < rpmCount; i++)
                {
                    var rgb = MozaProfile.UnpackColor(profile.DashRpmColors[i]);
                    WriteCm2Config($"cm2-stored-color{i + 1}", new byte[] { rgb[0], rgb[1], rgb[2] });
                }
            }
            if (profile.DashFlagColors != null)
            {
                int flagCount = System.Math.Min(profile.DashFlagColors.Length, 6);
                for (int i = 0; i < flagCount; i++)
                {
                    var rgb = MozaProfile.UnpackColor(profile.DashFlagColors[i]);
                    WriteCm2Config($"cm2-stored-color{i + 11}", new byte[] { rgb[0], rgb[1], rgb[2] });
                }
            }
        }

        /// <summary>
        /// Push wheel-base ambient LED settings. No-op unless the runtime probe
        /// confirmed strip support.
        /// </summary>
        public void ApplyBaseAmbientToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

            if (profile.BaseAmbientBrightness     >= 0) _data.BaseAmbientBrightness     = profile.BaseAmbientBrightness;
            if (profile.BaseAmbientStandbyMode    >= 0) _data.BaseAmbientStandbyMode    = profile.BaseAmbientStandbyMode;
            if (profile.BaseAmbientIndicatorState >= 0) _data.BaseAmbientIndicatorState = profile.BaseAmbientIndicatorState;
            if (profile.BaseAmbientSleepMode      >= 0) _data.BaseAmbientSleepMode      = profile.BaseAmbientSleepMode;
            if (profile.BaseAmbientSleepTimeout   >= 0) _data.BaseAmbientSleepTimeout   = profile.BaseAmbientSleepTimeout;
            if (profile.BaseAmbientStartupColor   >= 0) UnpackPackedColor(profile.BaseAmbientStartupColor, _data.BaseAmbientStartupColor);
            if (profile.BaseAmbientShutdownColor  >= 0) UnpackPackedColor(profile.BaseAmbientShutdownColor, _data.BaseAmbientShutdownColor);

            // Per-section gate only — see ApplyWheelToHardware comment for why
            // _data.IsConnected was dropped here.
            if (!_detectionState.BaseAmbientLedSupported) return;
            // Brightness is percent: clamp so a profile written by an older build
            // (whose slider went to 255) cannot push an out-of-range value.
            if (profile.BaseAmbientBrightness     >= 0) BaseManager.WriteSetting("base-ambient-brightness", System.Math.Min(100, profile.BaseAmbientBrightness));
            if (profile.BaseAmbientStandbyMode    >= 0) BaseManager.WriteSetting("base-ambient-standby-mode", profile.BaseAmbientStandbyMode);
            if (profile.BaseAmbientIndicatorState >= 0) BaseManager.WriteSetting("base-ambient-indicator-state", profile.BaseAmbientIndicatorState);
            if (profile.BaseAmbientSleepMode      >= 0) BaseManager.WriteSetting("base-ambient-sleep-mode", profile.BaseAmbientSleepMode);
            if (profile.BaseAmbientSleepTimeout   >= 0) BaseManager.WriteSetting("base-ambient-sleep-timeout", profile.BaseAmbientSleepTimeout);
            if (profile.BaseAmbientStartupColor   >= 0) WritePackedColor("base-ambient-startup-color", profile.BaseAmbientStartupColor);
            if (profile.BaseAmbientShutdownColor  >= 0) WritePackedColor("base-ambient-shutdown-color", profile.BaseAmbientShutdownColor);

            // Per-mode animation intervals (modes 2..5 only — off and constant
            // have no register) and the sleep breathing speed.
            if (profile.BaseAmbientStandbyIntervals != null)
            {
                for (int mode = 2; mode <= 5 && mode < profile.BaseAmbientStandbyIntervals.Length; mode++)
                {
                    int ms = profile.BaseAmbientStandbyIntervals[mode];
                    if (ms >= 0)
                        BaseManager.WriteSetting($"base-ambient-standby-interval-mode{mode}", ms);
                }
            }
            if (profile.BaseAmbientSleepBreathInterval >= 0)
                BaseManager.WriteSetting("base-ambient-sleep-breath-interval", profile.BaseAmbientSleepBreathInterval);

            ApplyBaseAmbientPalettes(profile);
        }

        /// <summary>
        /// Push the per-LED idle (standby modes 1 + 2) and sleep palettes. Only
        /// entries the user has actually set (>= 0) are written, so an untouched
        /// profile leaves the firmware's stored colours alone.
        ///
        /// Each command carries its own mode byte, so all three palettes can be
        /// written regardless of which standby mode is currently active — the
        /// device stores them independently. Only the palette of the *active*
        /// mode is visible, which is a display consequence, not a write gate.
        /// </summary>
        private void ApplyBaseAmbientPalettes(MozaProfile profile)
        {
            int ledsPerStrip = _data.ResolvedAmbientLedsPerStrip;
            int stride = Devices.BaseModelInfo.MaxLedsPerStrip;

            for (int strip = 0; strip < 2; strip++)
            {
                for (int led = 0; led < ledsPerStrip; led++)
                {
                    int i = strip * stride + led;
                    WritePaletteEntry(profile.BaseAmbientIdleColorsConstant, i,
                        $"base-ambient-led-color-strip{strip}-mode1-led{led}");
                    WritePaletteEntry(profile.BaseAmbientIdleColorsBreath, i,
                        $"base-ambient-led-color-strip{strip}-mode2-led{led}");
                    WritePaletteEntry(profile.BaseAmbientSleepColors, i,
                        $"base-ambient-sleep-led-color-strip{strip}-led{led}");
                }
            }
        }

        private void WritePaletteEntry(int[]? palette, int index, string command)
        {
            if (palette == null || index < 0 || index >= palette.Length) return;
            int packed = palette[index];
            if (packed < 0) return;
            WritePackedColor(command, packed);
        }

        /// <summary>Push handbrake settings. No-op unless detected.</summary>
        public void ApplyHandbrakeToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

            if (profile.HandbrakeMode            >= 0) _data.HandbrakeMode            = profile.HandbrakeMode;
            if (profile.HandbrakeButtonThreshold >= 0) _data.HandbrakeButtonThreshold = profile.HandbrakeButtonThreshold;
            if (profile.HandbrakeDirection       >= 0) _data.HandbrakeDirection       = profile.HandbrakeDirection;
            if (profile.HandbrakeMin             >= 0) _data.HandbrakeMin             = profile.HandbrakeMin;
            if (profile.HandbrakeMax             >  0) _data.HandbrakeMax             = profile.HandbrakeMax;
            if (profile.HandbrakeCurve != null)
            {
                for (int i = 0; i < Math.Min(5, profile.HandbrakeCurve.Length); i++)
                    _data.HandbrakeCurve[i] = profile.HandbrakeCurve[i];
            }

            if (!_detectionState.HandbrakeDetected) return;
            var dm = HandbrakeManager;
            if (profile.HandbrakeMode            >= 0) dm.WriteSetting("handbrake-mode", profile.HandbrakeMode);
            if (profile.HandbrakeButtonThreshold >= 0) dm.WriteSetting("handbrake-button-threshold", profile.HandbrakeButtonThreshold);
            if (profile.HandbrakeDirection       >= 0) dm.WriteSetting("handbrake-direction", profile.HandbrakeDirection);
            if (profile.HandbrakeMin             >= 0) dm.WriteSetting("handbrake-min", profile.HandbrakeMin);
            if (profile.HandbrakeMax             >  0) dm.WriteSetting("handbrake-max", profile.HandbrakeMax);
            if (profile.HandbrakeCurve != null)
            {
                for (int i = 0; i < Math.Min(5, profile.HandbrakeCurve.Length); i++)
                    dm.WriteFloat($"handbrake-y{i + 1}", profile.HandbrakeCurve[i]);
            }
        }

        /// <summary>Push pedal settings. No-op unless detected.</summary>
        public void ApplyPedalsToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

            if (profile.PedalsThrottleDir      >= 0) _data.PedalsThrottleDir      = profile.PedalsThrottleDir;
            if (profile.PedalsThrottleMin      >= 0) _data.PedalsThrottleMin      = profile.PedalsThrottleMin;
            if (profile.PedalsThrottleMax      >  0) _data.PedalsThrottleMax      = profile.PedalsThrottleMax;
            if (profile.PedalsBrakeDir         >= 0) _data.PedalsBrakeDir         = profile.PedalsBrakeDir;
            if (profile.PedalsBrakeMin         >= 0) _data.PedalsBrakeMin         = profile.PedalsBrakeMin;
            if (profile.PedalsBrakeMax         >  0) _data.PedalsBrakeMax         = profile.PedalsBrakeMax;
            if (profile.PedalsClutchDir        >= 0) _data.PedalsClutchDir        = profile.PedalsClutchDir;
            if (profile.PedalsClutchMin        >= 0) _data.PedalsClutchMin        = profile.PedalsClutchMin;
            if (profile.PedalsClutchMax        >  0) _data.PedalsClutchMax        = profile.PedalsClutchMax;
            if (profile.PedalsBrakeAngleRatio  >= 0) _data.PedalsBrakeAngleRatio  = profile.PedalsBrakeAngleRatio;
            if (profile.PedalsThrottleCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsThrottleCurve.Length); i++)
                    _data.PedalsThrottleCurve[i] = profile.PedalsThrottleCurve[i];
            if (profile.PedalsBrakeCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsBrakeCurve.Length); i++)
                    _data.PedalsBrakeCurve[i] = profile.PedalsBrakeCurve[i];
            if (profile.PedalsClutchCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsClutchCurve.Length); i++)
                    _data.PedalsClutchCurve[i] = profile.PedalsClutchCurve[i];

            if (!_detectionState.PedalsDetected) return;
            var dm = PedalsManager;
            if (profile.PedalsThrottleDir      >= 0) dm.WriteSetting("pedals-throttle-dir", profile.PedalsThrottleDir);
            if (profile.PedalsThrottleMin      >= 0) dm.WriteSetting("pedals-throttle-min", profile.PedalsThrottleMin);
            if (profile.PedalsThrottleMax      >  0) dm.WriteSetting("pedals-throttle-max", profile.PedalsThrottleMax);
            if (profile.PedalsBrakeDir         >= 0) dm.WriteSetting("pedals-brake-dir", profile.PedalsBrakeDir);
            if (profile.PedalsBrakeMin         >= 0) dm.WriteSetting("pedals-brake-min", profile.PedalsBrakeMin);
            if (profile.PedalsBrakeMax         >  0) dm.WriteSetting("pedals-brake-max", profile.PedalsBrakeMax);
            if (profile.PedalsClutchDir        >= 0) dm.WriteSetting("pedals-clutch-dir", profile.PedalsClutchDir);
            if (profile.PedalsClutchMin        >= 0) dm.WriteSetting("pedals-clutch-min", profile.PedalsClutchMin);
            if (profile.PedalsClutchMax        >  0) dm.WriteSetting("pedals-clutch-max", profile.PedalsClutchMax);
            if (profile.PedalsBrakeAngleRatio  >= 0) dm.WriteFloat("pedals-brake-angle-ratio", profile.PedalsBrakeAngleRatio);
            if (profile.PedalsThrottleCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsThrottleCurve.Length); i++)
                    dm.WriteFloat($"pedals-throttle-y{i + 1}", profile.PedalsThrottleCurve[i]);
            if (profile.PedalsBrakeCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsBrakeCurve.Length); i++)
                    dm.WriteFloat($"pedals-brake-y{i + 1}", profile.PedalsBrakeCurve[i]);
            if (profile.PedalsClutchCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsClutchCurve.Length); i++)
                    dm.WriteFloat($"pedals-clutch-y{i + 1}", profile.PedalsClutchCurve[i]);
        }

        // HGP and SGP are independent devices — each applies from its own profile
        // fields, mirrors into its own _data slot, and writes to its own pipe. _data is
        // mirrored regardless of detection; writes gate on that model being present.
        // shifter-type (apply-mode) is device identity, never profile-applied — the
        // v1.5.1 shared-profile apply of it is what flipped HGPs into sequential mode.
        public void ApplyHgpToHardware(MozaProfile? profile)
        {
            if (profile == null) return;
            var d = _data.ShifterHgp;
            if (profile.HgpDirection  >= 0) d.Direction  = profile.HgpDirection;
            if (profile.HgpPaddleSync >= 0) d.PaddleSync = profile.HgpPaddleSync;
            if (profile.HgpHidMode    >= 0) d.HidMode    = profile.HgpHidMode;

            if (!_detectionState.HgpDetected) return;
            var dm = HgpManager;
            if (profile.HgpDirection  >= 0) dm.WriteSetting("shifter-direction", profile.HgpDirection);
            if (profile.HgpPaddleSync >= 0) dm.WriteSetting("shifter-paddle-sync", profile.HgpPaddleSync);
            if (profile.HgpHidMode    >= 0) dm.WriteSetting("shifter-hid-mode", profile.HgpHidMode);
        }

        public void ApplySgpToHardware(MozaProfile? profile)
        {
            if (profile == null) return;
            var d = _data.ShifterSgp;
            if (profile.SgpDirection  >= 0) d.Direction  = profile.SgpDirection;
            if (profile.SgpPaddleSync >= 0) d.PaddleSync = profile.SgpPaddleSync;
            if (profile.SgpHidMode    >= 0) d.HidMode    = profile.SgpHidMode;
            if (profile.SgpBrightness >= 0) d.Brightness = profile.SgpBrightness;
            if (profile.SgpLed1Index  >= 0) d.Led1Index  = profile.SgpLed1Index;
            if (profile.SgpLed2Index  >= 0) d.Led2Index  = profile.SgpLed2Index;

            if (!_detectionState.SgpDetected) return;
            var dm = SgpManager;
            if (profile.SgpDirection  >= 0) dm.WriteSetting("shifter-direction", profile.SgpDirection);
            if (profile.SgpPaddleSync >= 0) dm.WriteSetting("shifter-paddle-sync", profile.SgpPaddleSync);
            if (profile.SgpHidMode    >= 0) dm.WriteSetting("shifter-hid-mode", profile.SgpHidMode);
            if (profile.SgpBrightness >= 0) dm.WriteSetting("shifter-brightness", profile.SgpBrightness);
            // Both LEDs ride one 2-byte command, so only push when BOTH indices are
            // known — otherwise we'd coerce the unknown LED to index 0 (red) and clobber
            // it. In the normal flow the pair always travels together.
            int s1 = d.Led1Index, s2 = d.Led2Index;
            if (s1 >= 0 && s2 >= 0)
                dm.WriteArray("shifter-colors",
                    new byte[] { (byte)Math.Min(7, s1), (byte)Math.Min(7, s2) });
        }

        /// <summary>
        /// Push base/FFB settings (motor limits, FFB curve breakpoints) to the
        /// wheelbase. _data is mirrored always; writes gated on base-connected.
        /// </summary>
        public void ApplyBaseToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

            // Drop the per-base write cache if a different base is now attached
            // (keeps base config from being re-pushed on every wheel hot-attach —
            // see s_baseCfgCache notes). Empty UID is treated as "same base".
            SyncBaseCfgCache();

            // Debug-level — fires on every game switch / wheel-detect /
            // dashboard re-apply, which is too noisy for SimHub.txt. The
            // in-process MozaLog ring buffer still records it so future bug
            // reports can pull it from the Diagnostics tab export.
            // Show both the persisted BaseDetected (the actual gate) and the
            // volatile IsBaseConnected (which is false on the hot-reload that
            // ate the writes before the 2026-05-27 gate fix).
            MozaLog.Debug(
                $"[AZOM] ApplyBaseToHardware '{profile.Name}': " +
                $"Limit={profile.Limit} ({(profile.Limit >= 0 ? (profile.Limit * 2) + "°" : "skip")}), " +
                $"MaxAngle={profile.MaxAngle} ({(profile.MaxAngle >= 0 ? (profile.MaxAngle * 2) + "°" : "skip")}), " +
                $"FfbStrength={profile.FfbStrength}, Torque={profile.Torque}, Speed={profile.Speed}, " +
                $"BaseDetected={_detectionState.BaseDetected}, " +
                $"_data.IsBaseConnected={_data.IsBaseConnected}, baseSettingsRead={_data.BaseSettingsRead}");

            // Each call below is the SOLE site that names a base/motor field
            // in this method. The helper handles the full lifecycle in one
            // pass: sentinel→seed-from-_data (so SimHub-auto-created profiles
            // inherit current device state instead of silently skipping the
            // write — the 2026-05-27 "rotation angle carries over to new
            // profile" pattern) → mirror to _data → write to the wire when
            // BaseDetected (persisted across plugin reloads). Adding a new
            // base/motor setting requires one line here, one line each in
            // MozaProfile.CopyProfilePropertiesFrom and CaptureFromCurrent,
            // and the field declaration itself — no parallel seed list to
            // drift out of sync.
            // Two independent registers: base-limit is the mechanical stop,
            // base-max-angle the in-game full lock (gameMax <= limit).
            // ORDER IS LOAD-BEARING: base-limit first — the base rejects a
            // max-angle write made while a higher old limit still stands.
            Apply(() => profile.Limit,              v => profile.Limit              = v,
                  () => _data.Limit,                v => _data.Limit                = v,
                  "base-limit");
            Apply(() => profile.MaxAngle,           v => profile.MaxAngle           = v,
                  () => _data.MaxAngle,             v => _data.MaxAngle             = v,
                  "base-max-angle");
            Apply(() => profile.FfbStrength,        v => profile.FfbStrength        = v,
                  () => _data.FfbStrength,          v => _data.FfbStrength          = v,
                  "base-ffb-strength");
            Apply(() => profile.Interpolation,      v => profile.Interpolation      = v,
                  () => _data.Interpolation,        v => _data.Interpolation        = v,
                  "main-set-interpolation");
            Apply(() => profile.Torque,             v => profile.Torque             = v,
                  () => _data.Torque,               v => _data.Torque               = v,
                  "base-torque");
            Apply(() => profile.Speed,              v => profile.Speed              = v,
                  () => _data.Speed,                v => _data.Speed                = v,
                  "base-speed");
            Apply(() => profile.Damper,             v => profile.Damper             = v,
                  () => _data.Damper,               v => _data.Damper               = v,
                  "base-damper");
            Apply(() => profile.Friction,           v => profile.Friction           = v,
                  () => _data.Friction,             v => _data.Friction             = v,
                  "base-friction");
            Apply(() => profile.Inertia,            v => profile.Inertia            = v,
                  () => _data.Inertia,              v => _data.Inertia              = v,
                  "base-inertia");
            Apply(() => profile.Spring,             v => profile.Spring             = v,
                  () => _data.Spring,               v => _data.Spring               = v,
                  "base-spring");
            Apply(() => profile.SpeedDamping,       v => profile.SpeedDamping       = v,
                  () => _data.SpeedDamping,         v => _data.SpeedDamping         = v,
                  "base-speed-damping");
            Apply(() => profile.SpeedDampingPoint,  v => profile.SpeedDampingPoint  = v,
                  () => _data.SpeedDampingPoint,    v => _data.SpeedDampingPoint    = v,
                  "base-speed-damping-point");
            Apply(() => profile.NaturalInertia,     v => profile.NaturalInertia     = v,
                  () => _data.NaturalInertia,       v => _data.NaturalInertia       = v,
                  "base-natural-inertia");
            Apply(() => profile.SoftLimitStiffness, v => profile.SoftLimitStiffness = v,
                  () => _data.SoftLimitStiffness,   v => _data.SoftLimitStiffness   = v,
                  "base-soft-limit-stiffness");
            Apply(() => profile.SoftLimitRetain,    v => profile.SoftLimitRetain    = v,
                  () => _data.SoftLimitRetain,      v => _data.SoftLimitRetain      = v,
                  "base-soft-limit-retain");
            Apply(() => profile.FfbReverse,         v => profile.FfbReverse         = v,
                  () => _data.FfbReverse,           v => _data.FfbReverse           = v,
                  "base-ffb-reverse");
            Apply(() => profile.Protection,         v => profile.Protection         = v,
                  () => _data.Protection,           v => _data.Protection           = v,
                  "base-protection");
            Apply(() => profile.GameDamper,         v => profile.GameDamper         = v,
                  () => _data.GameDamper,           v => _data.GameDamper           = v,
                  "main-set-damper-gain");
            Apply(() => profile.GameFriction,       v => profile.GameFriction       = v,
                  () => _data.GameFriction,         v => _data.GameFriction         = v,
                  "main-set-friction-gain");
            Apply(() => profile.GameInertia,        v => profile.GameInertia        = v,
                  () => _data.GameInertia,          v => _data.GameInertia          = v,
                  "main-set-inertia-gain");
            Apply(() => profile.GameSpring,         v => profile.GameSpring         = v,
                  () => _data.GameSpring,           v => _data.GameSpring           = v,
                  "main-set-spring-gain");
            Apply(() => profile.WorkMode,           v => profile.WorkMode           = v,
                  () => _data.WorkMode,             v => _data.WorkMode             = v,
                  "main-set-work-mode");
            Apply(() => profile.GearshiftVibration, v => profile.GearshiftVibration = v,
                  () => _data.GearshiftVibration,   v => _data.GearshiftVibration   = v,
                  "base-gearshift-vibration");
            Apply(() => profile.TempStrategy,       v => profile.TempStrategy       = v,
                  () => _data.TempStrategy,         v => _data.TempStrategy         = v,
                  "base-temp-strategy");
            Apply(() => profile.RoadSensitivity,    v => profile.RoadSensitivity    = v,
                  () => _data.RoadSensitivity,      v => _data.RoadSensitivity      = v,
                  "base-road-sensitivity");

            // Local helper — does seed + mirror + write in one pass. Closes
            // over `profile` and `_data` via the enclosing scope so callers
            // only need to provide the field accessors and command names.
            void Apply(
                Func<int> profileGet, Action<int> profileSet,
                Func<int> dataGet,    Action<int> dataSet,
                params string[] commands)
            {
                // Device-read value (valid only once the settings read sweep populated
                // _data) — captured BEFORE the mirror below overwrites it. Primes the
                // write cache so an unchanged profile writes nothing (write-on-diff).
                int deviceVal = _data.BaseSettingsRead ? dataGet() : -1;
                int val = profileGet();
                if (val < 0)
                {
                    // Profile field at sentinel — seed from current device state.
                    // Requires BaseSettingsRead so we don't propagate uninitialized
                    // zeros from a fresh MozaData (hot-reload before first echo).
                    if (!_data.BaseSettingsRead) return;
                    int seed = dataGet();
                    if (seed < 0) return;  // _data sentinel too — nothing to apply
                    val = seed;
                    profileSet(val);
                }
                dataSet(val);
                if (_detectionState.BaseDetected)
                    foreach (var cmd in commands)
                    {
                        if (deviceVal >= 0) BaseCfgPrime(cmd, deviceVal);
                        if (BaseCfgChanged(cmd, val))
                            BaseManager.WriteSetting(cmd, val);
                    }
            }

            // FFB Equalizer (sentinel = -1000): mirror always, write when live.
            // Gate on the persisted BaseDetected (not volatile _data.IsBaseConnected)
            // for the same reason as ApplyBaseSettingIfSet — see the comment there.
            void ApplyEq(int val, Action<int> setData, string cmd)
            {
                if (val <= -1000) return;
                setData(val);
                if (_detectionState.BaseDetected && BaseCfgChanged(cmd, val))
                    BaseManager.WriteSetting(cmd, val);
            }
            ApplyEq(profile.Equalizer1, v => _data.Equalizer1 = v, "base-equalizer1");
            ApplyEq(profile.Equalizer2, v => _data.Equalizer2 = v, "base-equalizer2");
            ApplyEq(profile.Equalizer3, v => _data.Equalizer3 = v, "base-equalizer3");
            ApplyEq(profile.Equalizer4, v => _data.Equalizer4 = v, "base-equalizer4");
            ApplyEq(profile.Equalizer5, v => _data.Equalizer5 = v, "base-equalizer5");
            ApplyEq(profile.Equalizer6, v => _data.Equalizer6 = v, "base-equalizer6");
            // Bands 7-10 exist only on 10-band firmware — old bases must never
            // see cmds 0x32..0x35.
            if (_data.BaseSupportsEq10)
            {
                ApplyEq(profile.Equalizer7,  v => _data.Equalizer7  = v, "base-equalizer7");
                ApplyEq(profile.Equalizer8,  v => _data.Equalizer8  = v, "base-equalizer8");
                ApplyEq(profile.Equalizer9,  v => _data.Equalizer9  = v, "base-equalizer9");
                ApplyEq(profile.Equalizer10, v => _data.Equalizer10 = v, "base-equalizer10");
            }

            // FFB Curve X/Y values: mirror always; write when live.
            if (profile.FfbCurveX1 >= 0) _data.FfbCurveX1 = profile.FfbCurveX1;
            if (profile.FfbCurveX2 >= 0) _data.FfbCurveX2 = profile.FfbCurveX2;
            if (profile.FfbCurveX3 >= 0) _data.FfbCurveX3 = profile.FfbCurveX3;
            if (profile.FfbCurveX4 >= 0) _data.FfbCurveX4 = profile.FfbCurveX4;
            if (profile.FfbCurveY1 >= 0) _data.FfbCurveY1 = profile.FfbCurveY1;
            if (profile.FfbCurveY2 >= 0) _data.FfbCurveY2 = profile.FfbCurveY2;
            if (profile.FfbCurveY3 >= 0) _data.FfbCurveY3 = profile.FfbCurveY3;
            if (profile.FfbCurveY4 >= 0) _data.FfbCurveY4 = profile.FfbCurveY4;
            if (profile.FfbCurveY5 >= 0) _data.FfbCurveY5 = profile.FfbCurveY5;
            // Persisted BaseDetected gate (see ApplyBaseSettingIfSet comment).
            if (!_detectionState.BaseDetected) return;
            // The device doesn't persist the X breakpoints, so they have to ride
            // every curve write — but the curve only needs re-sending when a
            // point actually changed. Gate the whole curve as a unit (X + Y) on
            // the X+Y hash so an unchanged re-apply (e.g. a wheel hot-attach)
            // sends nothing — re-pushing it bounces the motor mode on some bases.
            long curveHash = unchecked((long)1469598103934665603UL);
            curveHash = Fnv(curveHash, _data.FfbCurveX1);
            curveHash = Fnv(curveHash, _data.FfbCurveX2);
            curveHash = Fnv(curveHash, _data.FfbCurveX3);
            curveHash = Fnv(curveHash, _data.FfbCurveX4);
            curveHash = Fnv(curveHash, _data.FfbCurveY1);
            curveHash = Fnv(curveHash, _data.FfbCurveY2);
            curveHash = Fnv(curveHash, _data.FfbCurveY3);
            curveHash = Fnv(curveHash, _data.FfbCurveY4);
            curveHash = Fnv(curveHash, _data.FfbCurveY5);
            if (BaseCfgChanged("base-ffb-curve", curveHash))
            {
                BaseManager.WriteSetting("base-ffb-curve-x1", _data.FfbCurveX1);
                BaseManager.WriteSetting("base-ffb-curve-x2", _data.FfbCurveX2);
                BaseManager.WriteSetting("base-ffb-curve-x3", _data.FfbCurveX3);
                BaseManager.WriteSetting("base-ffb-curve-x4", _data.FfbCurveX4);
                BaseManager.WriteSetting("base-ffb-curve-y1", _data.FfbCurveY1);
                BaseManager.WriteSetting("base-ffb-curve-y2", _data.FfbCurveY2);
                BaseManager.WriteSetting("base-ffb-curve-y3", _data.FfbCurveY3);
                BaseManager.WriteSetting("base-ffb-curve-y4", _data.FfbCurveY4);
                BaseManager.WriteSetting("base-ffb-curve-y5", _data.FfbCurveY5);
            }
        }

        /// <summary>
        /// Push AB9 active-shifter settings to the AB9 manager. No-op unless AB9
        /// is detected/connected. A profile with no Ab9 block applies factory
        /// defaults so the device follows the active per-game profile (reset
        /// semantics) instead of retaining the previously-applied profile's
        /// settings.
        /// </summary>
        public void ApplyAb9ToHardware(MozaProfile? profile)
        {
            if (!_detectionState.Ab9Detected || _ab9Manager == null || !_ab9Manager.IsConnected) return;

            var ab9 = profile?.Ab9 ?? new Ab9Settings();
            _ab9Manager.SendInputMode(ab9.InputMode);
            _ab9Manager.SendMode(ab9.Mode);
            _ab9Manager.SendSlider(Ab9Slider.MechanicalResistance, ab9.MechanicalResistance);
            _ab9Manager.SendSlider(Ab9Slider.Spring,               ab9.Spring);
            _ab9Manager.SendSlider(Ab9Slider.NaturalDamping,       ab9.NaturalDamping);
            _ab9Manager.SendSlider(Ab9Slider.NaturalFriction,      ab9.NaturalFriction);
            _ab9Manager.SendSlider(Ab9Slider.MaxTorqueLimit,       ab9.MaxTorqueLimit);
            _ab9Manager.SendGearShiftVibrationIntensity(ab9.GearShiftVibrationIntensity);
        }

        /// <summary>
        /// Run all Apply*ToHardware methods for a profile. The orchestrator
        /// (persist + dashboard-pending + telemetry sync) stays with
        /// <see cref="MozaPlugin.ApplyProfile"/>; this is the hardware-write half.
        /// </summary>
        public void ApplyProfileHardware(MozaProfile profile)
        {
            // Guard: a profile with all core base settings at zero was captured
            // from uninitialized device data (first-launch race). Reset to
            // sentinels so they're skipped — device keeps its own values.
            if (profile.Limit == 0 && profile.FfbStrength == 0 && profile.Torque == 0 && profile.Speed == 0)
            {
                MozaLog.Warn("[AZOM] Profile has zeroed base settings — resetting to sentinels");
                profile.Limit = -1; profile.MaxAngle = -1;
                profile.FfbStrength = -1; profile.Torque = -1; profile.Speed = -1;
                profile.Damper = -1; profile.Friction = -1; profile.Inertia = -1; profile.Spring = -1;
                profile.SpeedDamping = -1; profile.SpeedDampingPoint = -1;
                profile.NaturalInertia = -1; profile.SoftLimitStiffness = -1;
                profile.SoftLimitRetain = -1; profile.FfbReverse = -1; profile.Protection = -1;
                profile.GameDamper = -1; profile.GameFriction = -1;
                profile.GameInertia = -1; profile.GameSpring = -1;
                profile.WorkMode = -1;
            }

            ApplyBaseToHardware(profile);
            ApplyWheelToHardware(profile);
            ApplyDashToHardware(profile);
            ApplyBaseAmbientToHardware(profile);
            ApplyHandbrakeToHardware(profile);
            ApplyPedalsToHardware(profile);
            ApplyHgpToHardware(profile);
            ApplySgpToHardware(profile);
            ApplyAb9ToHardware(profile);
            ApplyMBoosterToHardware(profile);
        }

        /// <summary>
        /// Re-push mBooster hardware calibration (Direction/Min/Max/output-
        /// curve/Travel Start-End/Endstop stiffness/Sensor Ratio/Max
        /// Threshold) for every currently-connected mBooster. Before this,
        /// <see cref="MozaPlugin.ApplyMBoosterToHardware"/> only ever ran on
        /// (re)connect (<see cref="MozaPlugin.OnMBoosterDeviceDetected"/>) or
        /// the manual "Apply Cal" button — a profile switch with the device
        /// already connected silently left the PREVIOUS profile's calibration
        /// on the hardware, since nothing re-applied the new one. Doesn't
        /// read <paramref name="profile"/> directly (unlike the other
        /// Apply*ToHardware methods) — MozaPlugin.GetOrCreateMBoosterSettings
        /// resolves per-device settings off <c>ProfileStore.CurrentProfile</c>,
        /// which is already the new profile by the time
        /// ProfileCoordinator.OnProfileChanged calls this (it reads
        /// CurrentProfile fresh before invoking ApplyProfile). Host-rendered
        /// effect settings (Abs/Lockup/Engine/etc., Pedal Feel, custom
        /// effects) don't need this — MBoosterEffectWorker re-reads settings
        /// fresh every ~20ms tick regardless of profile switches; only the
        /// one-shot hardware calibration writes needed this explicit re-push.
        /// </summary>
        private void ApplyMBoosterToHardware(MozaProfile profile)
        {
            var registry = _plugin.MBoosterRegistry;
            if (registry == null) return;
            foreach (var controller in registry.Devices)
            {
                var s = _plugin.GetOrCreateMBoosterSettings(controller.Identity);
                ApplyMBoosterToHardware(controller, s);
            }
        }

        // ===== Device-extension entry points =====

        /// <summary>
        /// Apply wheel settings from the SimHub device extension profile system.
        /// Updates settings, _data, overlay, then writes through ApplyWheelToHardware.
        /// </summary>
        public void ApplyWheelExtensionSettings(MozaWheelExtensionSettings extSettings, string? pageModelPrefix = null)
        {
            MozaLog.Debug($"[AZOM] Applying wheel device extension settings (prefix={pageModelPrefix ?? "(null)"})");

            var settings = _plugin.Settings;
            var profile = settings?.ProfileStore?.CurrentProfile;
            extSettings.ApplyTo(settings!, _data, profile, pageModelPrefix);

            // Hardware writes gated on model match — other-model extensions
            // must not poke the active wheel's hardware.
            string extModel = extSettings.WheelModelName ?? "";
            string activeModel = _data.WheelModelName ?? "";
            bool hasExtModel = !string.IsNullOrEmpty(extModel);
            bool modelMatches = hasExtModel &&
                string.Equals(extModel, activeModel, StringComparison.OrdinalIgnoreCase);
            bool writeHardware = !hasExtModel || modelMatches;

            if (writeHardware)
                ApplyWheelToHardware(profile);

            _plugin.PersistSettings();

            // SimHub invokes SetSettings on every registered extension at
            // startup; gate live telemetry pushes on modelMatches so a
            // non-matching extension can't bleed its TelemetryProfileName
            // into the active sender.
            if (extSettings.TelemetrySettingsPresent && modelMatches)
            {
                if (settings!.TelemetryEnabled)
                {
                    _plugin.ApplyTelemetrySettings();
                    _plugin.StartTelemetryIfReady();
                }
                else
                {
                    _plugin.TelemetrySender?.Stop();
                }
            }
        }

        public void ApplyDashExtensionSettings(MozaDashExtensionSettings extSettings)
        {
            MozaLog.Debug("[AZOM] Applying dash device extension settings");

            var settings = _plugin.Settings;
            extSettings.ApplyTo(settings!, _data, settings?.ProfileStore?.CurrentProfile);
            ApplyDashToHardware(settings?.ProfileStore?.CurrentProfile);
            _plugin.PersistSettings();
        }

        public void ApplyBaseExtensionSettings(MozaBaseExtensionSettings extSettings)
        {
            MozaLog.Debug("[AZOM] Applying base ambient device extension settings");

            var settings = _plugin.Settings;
            extSettings.ApplyTo(settings!, _data, settings?.ProfileStore?.CurrentProfile);
            ApplyBaseAmbientToHardware(settings?.ProfileStore?.CurrentProfile);
            _plugin.PersistSettings();
        }

        // ===== WriteIf* (UI handler hardening) =====
        // Skip the wire when the matching device isn't detected. Sentinel-guard
        // numeric values to drop "no opinion" writes.

        public void WriteIfWheelDetected(string command, int value)
        {
            if (value < 0) return;
            if (!_detectionState.NewWheelDetected && !_detectionState.OldWheelDetected) return;
            if (IsFlashBackedWheelCfg(command))
            {
                QueueWheelCfgWrite(command, value, () => _deviceManager.WriteSetting(command, value));
                return;
            }
            _deviceManager.WriteSetting(command, value);
        }
        public void WriteIfDashDetected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.DashDetected) _deviceManager.WriteSetting(command, value);
        }
        // The "base connected" gate uses the persisted DetectionState flag
        // (set on first base-mcu-temp echo and preserved across SimHub plugin
        // reloads via s_persistentDetectionState), not the volatile
        // _data.IsBaseConnected — see the ApplyBaseSettingIfSet comment for
        // the hot-reload rationale. _deviceManager.WriteSetting still bails
        // on a dead wire, so this is correct even mid-reconnect.
        public void WriteIfBaseConnected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.BaseDetected) BaseManager.WriteSetting(command, value);
        }
        public void WriteFloatIfBaseConnected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.BaseDetected) BaseManager.WriteFloat(command, value);
        }
        // Readback path for base settings the firmware may clamp (e.g. the
        // rotation-limit floor probe): the reply lands in _data, so the UI
        // shows what the base actually stored rather than what was written.
        public void ReadIfBaseConnected(string command)
        {
            if (_detectionState.BaseDetected) BaseManager.ReadSetting(command);
        }
        public void WriteIfHandbrakeDetected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.HandbrakeDetected) HandbrakeManager.WriteSetting(command, value);
        }
        public void WriteFloatIfHandbrakeDetected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.HandbrakeDetected) HandbrakeManager.WriteFloat(command, value);
        }
        public void WriteIfPedalsDetected(string command, int value)
        {
            if (value < 0) return;
            if (SuppressPedalsWrite(command)) return;
            if (_detectionState.PedalsDetected) PedalsManager.WriteSetting(command, value);
        }
        public void WriteFloatIfPedalsDetected(string command, int value)
        {
            if (value < 0) return;
            if (SuppressPedalsWrite(command)) return;
            if (_detectionState.PedalsDetected) PedalsManager.WriteFloat(command, value);
        }

        // An mBooster on a base/hub pedal port answers as device 0x19 and so
        // latches PedalsDetected, but the pedals-* command set writes the SAME
        // group/cmd bytes as mbooster-* — every write here would land on the
        // mBooster's own registers, and pedals-*-cal-start would run a CRP/SRP
        // calibration sweep against a motorized pedal. Gated at the write path,
        // not just in the UI, because the SDK/CoAP pedal resources reach these
        // same two methods. The mBooster card owns that hardware.
        private readonly System.Collections.Generic.HashSet<string> _pedalsWriteSuppressedLogged =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        private bool SuppressPedalsWrite(string command)
        {
            if (!(_plugin?.MBoosterRegistry?.AnyRoutedPedalLane ?? false)) return false;
            bool isNew;
            lock (_pedalsWriteSuppressedLogged) isNew = _pedalsWriteSuppressedLogged.Add(command ?? "");
            if (isNew)
                MozaLog.Info(
                    $"[AZOM] '{command}' suppressed — the pedal slot (0x19) holds an mBooster, " +
                    "not CRP/SRP pedals; use the mBooster card");
            return true;
        }
        public void WriteIfHgpDetected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.HgpDetected) HgpManager.WriteSetting(command, value);
        }
        public void WriteIfSgpDetected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.SgpDetected) SgpManager.WriteSetting(command, value);
        }
        // Readback path for the HGP shifter-type repair control: the reply lands in
        // the per-model mirror, so the tab shows what the device actually stored.
        public void ReadIfHgpDetected(string command)
        {
            if (_detectionState.HgpDetected) HgpManager.ReadSetting(command);
        }
        // The 2 SGP LEDs ride one 2-byte command [S1,S2] (palette indices 0-7); the
        // UI re-sends both whenever either changes. SGP-only (the HGP has no LEDs).
        public void WriteArrayIfSgpDetected(string command, byte[] payload)
        {
            if (_detectionState.SgpDetected) SgpManager.WriteArray(command, payload);
        }
        public void WriteIfBaseAmbientSupported(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.BaseAmbientLedSupported) BaseManager.WriteSetting(command, value);
        }
        public void WriteColorIfWheelDetected(string command, byte r, byte g, byte b)
        {
            if (!_detectionState.NewWheelDetected && !_detectionState.OldWheelDetected) return;
            // Only the flash-backed colours (idle/sleep light) are coalesced; LED colour
            // registers are owned by the live pipeline and must stay immediate+uncached.
            if (IsFlashBackedWheelCfg(command))
            {
                QueueWheelCfgWrite(command, ((long)r << 16) | ((long)g << 8) | b,
                    () => _deviceManager.WriteColor(command, r, g, b));
                return;
            }
            _deviceManager.WriteColor(command, r, g, b);
        }

        /// <summary>
        /// Wheel LED colour write from UI handlers that also invalidates the live
        /// cache for <paramref name="kind"/>. Writing during live telemetry is safe
        /// per user confirmation (the next live frame overpaints the static value);
        /// the cache invalidation just ensures the live pipeline can't dedup its
        /// next frame against a stale cache after our write changed the wheel's
        /// frame buffer. Use this from UI handlers for every wheel-LED colour
        /// command (knob active, knob bg, RPM, button, flag); use the plain
        /// <see cref="WriteColorIfWheelDetected"/> for non-LED commands (idle/sleep
        /// colour, ambient colour, etc.).
        /// </summary>
        public void WriteLedColorIfWheelDetected(string command, byte r, byte g, byte b, LedKind kind)
        {
            if (!_detectionState.NewWheelDetected && !_detectionState.OldWheelDetected) return;

            // Per-group SimHub-mode gate, symmetric with the read-side gate
            // in MozaWheelSettingsControl.WheelTabs_SelectionChanged. Mode
            // values per group (MozaData): 0=Off, 1=SimHub/telemetry-driven,
            // 2=Static. Suppress UI-initiated static-color writes when the
            // group is in SimHub mode — the live pipeline owns the wheel's
            // color registers for that group, and a static write briefly
            // clobbers the live frame buffer until the next live frame
            // overpaints (visible 1-keepalive flicker). Caller writes to
            // _data + persisted overlay BEFORE invoking us, so the user's
            // intent survives — it just doesn't reach the wheel while live
            // is rendering this group. When the user switches the group
            // back to Static, the mode-change handler in
            // MozaWheelSettingsControl re-pushes the stored palette so the
            // EEPROM catches up with anything edited during SimHub mode.
            int? groupMode = kind switch
            {
                LedKind.Rpm    => _data.WheelTelemetryMode,
                LedKind.Button => _data.WheelButtonsLedMode,
                LedKind.Knob   => _data.WheelKnobLedMode,
                _ => (int?)null,  // Flag / None / combined: no mode tracking, write through
            };
            if (groupMode == 1)
            {
                MozaLog.Debug(
                    $"[AZOM] LED write '{command}' suppressed: group {kind} in SimHub mode " +
                    "(live pipeline owns the frame buffer; _data and overlay updated regardless)");
                return;
            }

            _deviceManager.WriteColor(command, r, g, b);
            MozaLedDeviceManager.InvalidateLiveCacheAny(kind);
        }

        /// <summary>
        /// Re-push the stored static palette for a group from <c>_data</c> to the
        /// wheel's EEPROM. Called from the per-group mode combo handlers when the
        /// user transitions a group to Static mode (val=2). Required because
        /// <see cref="WriteLedColorIfWheelDetected"/> suppresses static-color
        /// writes while the group is in SimHub mode; the suppressed writes still
        /// land in <c>_data</c> and the persisted overlay, but the wheel EEPROM
        /// falls out of sync. Re-pushing on transition-to-Static brings EEPROM
        /// back to match <c>_data</c> so the static colors the user picked while
        /// in SimHub mode actually appear when they switch back.
        ///
        /// Bypasses the SimHub-mode gate intentionally: this is invoked AFTER
        /// the mode flip, when the group is already in Static, so the gate
        /// would allow writes anyway — going direct keeps the call independent
        /// of any future gate changes.
        /// </summary>
        public void RepushStaticPalette(LedKind kind)
        {
            if (!_detectionState.NewWheelDetected && !_detectionState.OldWheelDetected) return;
            var model = _plugin.WheelModelInfo;
            if (model == null) return;

            switch (kind)
            {
                case LedKind.Rpm:
                {
                    // No IsWheelLedGroupPresent check: that mask only tracks the EXTENDED
                    // groups (2 Single / 3 Rotary / 4 Ambient) and returns false for 0/1 by
                    // construction, which made this arm dead code. Groups 0/1 are proven
                    // present by the model's own LED counts.
                    int count = model.RpmLedCount;
                    if (count <= 0) return;
                    var src = _data.WheelRpmColors;
                    int len = Math.Min(src.Length, count);
                    for (int i = 0; i < len; i++)
                    {
                        var rgb = src[i];
                        _deviceManager.WriteColor($"wheel-rpm-color{i + 1}", rgb[0], rgb[1], rgb[2]);
                    }
                    MozaLedDeviceManager.InvalidateLiveCacheAny(LedKind.Rpm);
                    break;
                }
                case LedKind.Button:
                {
                    int count = model.ButtonLedCount;
                    if (count <= 0) return;   // see the group-0 note above
                    var src = _data.WheelButtonColors;
                    int len = Math.Min(src.Length, count);
                    for (int i = 0; i < len; i++)
                    {
                        var rgb = src[i];
                        _deviceManager.WriteColor($"wheel-button-color{i + 1}", rgb[0], rgb[1], rgb[2]);
                    }
                    MozaLedDeviceManager.InvalidateLiveCacheAny(LedKind.Button);
                    break;
                }
                case LedKind.Knob:
                {
                    int knobs = model.KnobCount;
                    if (knobs <= 0) return;

                    // Unlike the RPM/button palettes — which DeviceProber reads back at
                    // every detection, so _data always holds the wheel's own values —
                    // the knob palettes are only read on Knobs-tab activation, and that
                    // read is skipped while the group is in SimHub mode. So on the
                    // transition INTO Static, _data can still be the all-black
                    // InitColorArray default, and pushing it would erase the wheel's
                    // stored palette instead of restoring the user's (bundle 0TWEX2AK:
                    // "Static mode does not seem to work, all knobs go black").
                    // Push only what the user actually has saved — the same overlay/
                    // profile arrays ApplyWheelToHardware writes from — and when there
                    // is nothing saved, read the wheel's values back to seed the UI and
                    // write nothing.
                    var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
                    var ov = _plugin.GetCurrentWheelOverlay(profile);
                    int[]? savedPrimary = EffArr(ov?.WheelKnobPrimaryColors, profile?.WheelKnobPrimaryColors);
                    int[]? savedRing    = EffArr(ov?.WheelKnobRingColors,    profile?.WheelKnobRingColors);

                    // Per-knob "Active" LED color (cmd 0x27 ROLE=0).
                    if (savedPrimary != null)
                    {
                        int primLen = Math.Min(savedPrimary.Length, knobs);
                        for (int i = 0; i < primLen; i++)
                        {
                            var rgb = MozaProfile.UnpackColor(savedPrimary[i]);
                            _deviceManager.WriteColor($"wheel-knob{i + 1}-active-color", rgb[0], rgb[1], rgb[2]);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < knobs && i < 5; i++)
                            _deviceManager.ReadSetting($"wheel-knob{i + 1}-active-color");
                    }

                    // Per-ring-LED "background" color (cmd 0x1F 0x03 0x01), indexed by
                    // RING LED 0..KnobRingLedTotal-1 — NOT by knob. The old source,
                    // _data.WheelKnobBackgroundColors, is a 5-entry per-KNOB scratch for
                    // "fill ring with selected", so it wrote knob-indexed colours into
                    // ring-LED slots and Min(5, KnobRingLedTotal) capped the sweep at the
                    // first 5 LEDs — on a KS Pro (rings 12/12/8/12/12) that repainted
                    // part of knob 1 and left the other 51 untouched.
                    if (model.KnobRingLeds != null && _detectionState.IsWheelLedGroupPresent(3))
                    {
                        if (savedRing != null)
                        {
                            int ringLen = Math.Min(savedRing.Length, model.KnobRingLedTotal);
                            for (int i = 0; i < ringLen; i++)
                            {
                                var rgb = MozaProfile.UnpackColor(savedRing[i]);
                                _deviceManager.WriteColor($"wheel-knob-bg-color{i + 1}", rgb[0], rgb[1], rgb[2]);
                            }
                        }
                        else
                        {
                            var reads = new string[model.KnobRingLedTotal];
                            for (int i = 0; i < reads.Length; i++)
                                reads[i] = $"wheel-knob-bg-color{i + 1}";
                            _deviceManager.ReadSettingsPaced(reads);
                        }
                    }
                    MozaLedDeviceManager.InvalidateLiveCacheAny(LedKind.Knob);
                    break;
                }
            }
        }

        public void WriteColorIfDashDetected(string command, byte r, byte g, byte b)
        {
            if (_detectionState.DashDetected) _deviceManager.WriteColor(command, r, g, b);
        }
        public void WriteColorIfBaseAmbientSupported(string command, byte r, byte g, byte b)
        {
            if (_detectionState.BaseAmbientLedSupported) BaseManager.WriteColor(command, r, g, b);
        }
        public void WriteArrayIfWheelDetected(string command, byte[] payload)
        {
            if (!_detectionState.NewWheelDetected && !_detectionState.OldWheelDetected) return;
            if (IsFlashBackedWheelCfg(command))
            {
                QueueWheelCfgWrite(command, HashPayload(payload), () => _deviceManager.WriteArray(command, payload));
                return;
            }
            _deviceManager.WriteArray(command, payload);
        }

        // ===== Per-cluster sentinel-guarded helpers =====
        //
        // Base/motor settings used to live in a public ApplyBaseSettingIfSet
        // here, called once per field from ApplyBaseToHardware alongside a
        // parallel seed-from-_data block. Those merged into the local `Apply`
        // helper inside ApplyBaseToHardware (one call site per field, seed +
        // mirror + write in one pass) — single source of truth, no parallel
        // list to drift out of sync. See the comment block above the helper.

        public void ApplyHandbrakeSettingIfSet(int value, Action<int> setData, string command)
        {
            if (value < 0) return;
            setData(value);
            if (_detectionState.HandbrakeDetected)
                _deviceManager.WriteSetting(command, value);
        }

        public void ApplyPedalSettingIfSet(int value, Action<int> setData, string command)
        {
            if (value < 0) return;
            setData(value);
            if (_detectionState.PedalsDetected)
                _deviceManager.WriteSetting(command, value);
        }

        public void ApplyCurveIfSet(int[]? curve, int[] dataArray, string commandPrefix, bool deviceConnected)
        {
            if (curve == null) return;
            for (int i = 0; i < Math.Min(5, curve.Length); i++)
            {
                dataArray[i] = curve[i];
                if (deviceConnected)
                    _deviceManager.WriteFloat($"{commandPrefix}{i + 1}", curve[i]);
            }
        }

        /// <summary>
        /// Build the 3-byte payload shared by per-effect speed commands:
        /// <c>wheel-{telemetry,buttons,knob}-idle-interval</c> = <c>[effect_id, ms_msb, ms_lsb]</c>;
        /// <c>wheel-idle-speed</c> = <c>[mode, ms_msb, ms_lsb]</c>.
        /// </summary>
        public static byte[] BuildIdleIntervalPayload(int selector, int ms)
        {
            ms = Math.Max(0, Math.Min(0xFFFF, ms));
            return new byte[] {
                (byte)(selector & 0xFF),
                (byte)((ms >> 8) & 0xFF),
                (byte)(ms & 0xFF),
            };
        }

        // ===== LED color helpers =====

        public void WriteColorArray(int[]? packedColors, string commandPrefix, int count)
        {
            if (packedColors == null) return;
            int len = Math.Min(packedColors.Length, count);
            for (int i = 0; i < len; i++)
            {
                var rgb = MozaProfile.UnpackColor(packedColors[i]);
                _deviceManager.WriteColor($"{commandPrefix}{i + 1}", rgb[0], rgb[1], rgb[2]);
            }
        }

        /// <summary>
        /// Write the persisted static button colours. <c>WheelButtonColors</c> is
        /// protocol-indexed (14 slots); on a non-contiguous wheel (e.g. CS V2.1 →
        /// protocol indices 0,1,3,6,8,9) a flat 0..N-1 loop writes phantom slots and
        /// skips the high physical buttons. Drive each mapped protocol index directly
        /// (`wheel-button-color{p+1}` addresses protocol index p) so 6/8/9 aren't lost.
        /// Contiguous-button wheels (ButtonLedMap == null) keep the flat write.
        /// </summary>
        private void WriteButtonStaticColors(int[]? packedColors, WheelModelInfo model)
        {
            if (packedColors == null) return;
            int[]? map = model.ButtonLedMap;
            if (map == null)
            {
                WriteColorArray(packedColors, "wheel-button-color", model.ButtonLedCount);
                return;
            }
            foreach (int p in map)
            {
                if (p < 0 || p >= packedColors.Length) continue;
                var rgb = MozaProfile.UnpackColor(packedColors[p]);
                _deviceManager.WriteColor($"wheel-button-color{p + 1}", rgb[0], rgb[1], rgb[2]);
            }
        }

        /// <summary>
        /// Push per-knob "bulk Inactive default" + per-knob "Active" colors. No-op
        /// unless the active wheel exposes knob LED rings (W17 CS Pro / W18 KS Pro).
        /// Bulk Inactive fans out to all ring LEDs via per-LED writes (cmd 0x1F 0x03 0x01);
        /// Active drives cmd 0x27 ROLE=0.
        ///
        /// Writes are unconditional with respect to live telemetry — per user
        /// confirmation, cmd 0x27 / cmd 0x1F 0x03 0x01 during live frames does not
        /// visibly flicker the live overlay (the next live frame overpaints). After
        /// every successful write batch we invalidate the live cache so the next live
        /// tick re-sends instead of dedup'ing against a frame whose underlying wheel
        /// state we just rewrote.
        /// </summary>
        public void WriteKnobColors(int[]? packedBulkInactive, int[]? packedActive)
        {
            var model = _plugin.WheelModelInfo;
            int knobs = model?.KnobCount ?? 0;
            if (knobs <= 0) return;

            // Per-knob Active LED color (cmd 0x27 ROLE=0).
            if (packedActive != null)
            {
                int len = Math.Min(packedActive.Length, knobs);
                for (int i = 0; i < len; i++)
                {
                    var rgb = MozaProfile.UnpackColor(packedActive[i]);
                    _deviceManager.WriteColor($"wheel-knob{i + 1}-active-color", rgb[0], rgb[1], rgb[2]);
                }
            }

            // Per-knob "bulk Inactive default" fanned over ring LEDs.
            if (packedBulkInactive != null
                && model?.KnobRingLeds != null
                && _detectionState.IsWheelLedGroupPresent(3))
            {
                int kLen = Math.Min(packedBulkInactive.Length, knobs);
                for (int k = 0; k < kLen; k++)
                {
                    var rgb = MozaProfile.UnpackColor(packedBulkInactive[k]);
                    int startIdx = model.KnobRingStartIndex(k);
                    int count = model.KnobRingLeds[k];
                    for (int i = 0; i < count; i++)
                    {
                        int ledIdx = startIdx + i;
                        _deviceManager.WriteColor($"wheel-knob-bg-color{ledIdx + 1}", rgb[0], rgb[1], rgb[2]);
                    }
                }
            }
            MozaLedDeviceManager.InvalidateLiveCacheAny(LedKind.Knob);
        }

        /// <summary>
        /// Push per-LED ring colors (cmd 0x1F 0x03 0x01). No-op unless the active
        /// wheel has KnobRingLeds and Group 3 is present. Brightness &lt; 0 skips
        /// the brightness write.
        /// </summary>
        public void WriteKnobRingColors(int[]? packedColors, int brightness)
        {
            var model = _plugin.WheelModelInfo;
            if (model?.KnobRingLeds == null || !_detectionState.IsWheelLedGroupPresent(3)) return;
            // Change-gated like every other flash-backed write — this register is EEPROM
            // and the colour re-push above can fire for a colour-only change.
            if (brightness >= 0 && WheelCfgChangedForApply("wheel-knob-brightness", brightness))
                _deviceManager.WriteSetting("wheel-knob-brightness", brightness);
            if (packedColors == null) return;
            int total = Math.Min(packedColors.Length, model.KnobRingLedTotal);
            for (int i = 0; i < total; i++)
            {
                var rgb = MozaProfile.UnpackColor(packedColors[i]);
                _deviceManager.WriteColor($"wheel-knob-bg-color{i + 1}", rgb[0], rgb[1], rgb[2]);
            }
            MozaLedDeviceManager.InvalidateLiveCacheAny(LedKind.Knob);
        }

        /// <summary>
        /// Push SimHub's shared/master LED brightness (0..100) to the wheel's
        /// firmware group brightness — the rpm (group 0), buttons (group 1) and
        /// knob-ring (group 3) groups all receive the same value (cmd <c>1B [G] FF</c>).
        /// Called from the data thread when the user moves the master slider (the
        /// wheel LED driver publishes the settled value into
        /// <see cref="MozaPlugin.WheelLedMasterBrightness"/>). Flag brightness lives
        /// on the Meter sub-device and is out of the wheel LED-group scope. ES/ESX
        /// (old-protocol) wheels are handled separately on the steady poll timer
        /// (<see cref="MozaPlugin"/>) — their only dimmer is the legacy brightness
        /// register and neither Display() nor DataUpdate ticks at idle, so they can't
        /// ride this data-thread path (issue #113). Change-gated through the same
        /// per-wheel cfg cache as <c>ApplyWheelToHardware</c>, so a value already on the
        /// wheel is not re-flashed and this never fights the connect/profile write.
        /// </summary>
        public void ApplyMasterWheelLedBrightness(int value)
        {
            if (value < 0) return;
            if (!_detectionState.NewWheelDetected) return;
            var model = _plugin.WheelModelInfo;   // null until identity resolves
            if (model == null) return;

            SyncWheelCfgCache();

            if (model.RpmLedCount > 0 && WheelCfgChanged("wheel-rpm-brightness", value))
            {
                _data.WheelRpmBrightness = value;
                _deviceManager.WriteSetting("wheel-rpm-brightness", value);
            }
            if (model.ButtonLedCount > 0 && WheelCfgChanged("wheel-buttons-brightness", value))
            {
                _data.WheelButtonsBrightness = value;
                _deviceManager.WriteSetting("wheel-buttons-brightness", value);
            }
            if (model.KnobRingLeds != null && _detectionState.IsWheelLedGroupPresent(3)
                    && WheelCfgChanged("wheel-knob-brightness", value))
            {
                _data.KnobRingBrightness = value;
                _deviceManager.WriteSetting("wheel-knob-brightness", value);
            }
        }

        /// <summary>
        /// Push SimHub's PER-ZONE LED brightness ("Brightness limiter and balance": Telemetry
        /// Leds / Buttons / Encoders) to each zone's own firmware register — rpm = group 0,
        /// buttons = group 1, knob rings = group 3, cmd <c>1B [G] FF</c>. Each value already
        /// carries the global master term (SimHub hands the driver
        /// <c>global/100 × zone/100</c>), so the master slider still moves all three.
        /// <c>-1</c> = the user has not moved that zone's slider; leave its register alone.
        ///
        /// This is what makes the per-zone sliders work for a zone the firmware renders from
        /// its static palette (Button / Knob LED mode = Static). The live-frame RGB scaling
        /// in <see cref="MozaLedDeviceManager"/> only reaches zones in SimHub mode, so
        /// before this a Static zone had no reachable dimmer and its slider looked dead.
        ///
        /// Called from the data thread when the driver publishes a settled value, and
        /// change-gated through the same per-wheel cfg cache as
        /// <see cref="ApplyWheelToHardware"/> so a value already on the wheel is not
        /// re-flashed. <see cref="MozaPlugin.WheelLedAppliedBrightnessRpm"/> and siblings
        /// mirror what is now in each register so the driver can divide it back out of its
        /// per-frame factor instead of dimming twice; they stay -1 for a zone this wheel has
        /// no writable register for.
        /// </summary>
        public void ApplyWheelLedZoneBrightness(int rpmValue, int buttonsValue, int knobValue)
        {
            if (!_detectionState.NewWheelDetected) return;
            var model = _plugin.WheelModelInfo;   // null until identity resolves
            if (model == null) return;

            SyncWheelCfgCache();

            if (rpmValue >= 0 && model.RpmLedCount > 0)
            {
                _data.WheelRpmBrightness = rpmValue;
                _plugin.WheelLedAppliedBrightnessRpm = rpmValue;
                if (WheelCfgChanged("wheel-rpm-brightness", rpmValue))
                    _deviceManager.WriteSetting("wheel-rpm-brightness", rpmValue);
            }
            if (buttonsValue >= 0 && model.ButtonLedCount > 0)
            {
                _data.WheelButtonsBrightness = buttonsValue;
                _plugin.WheelLedAppliedBrightnessButtons = buttonsValue;
                if (WheelCfgChanged("wheel-buttons-brightness", buttonsValue))
                    _deviceManager.WriteSetting("wheel-buttons-brightness", buttonsValue);
            }
            if (knobValue >= 0 && model.KnobRingLeds != null
                    && _detectionState.IsWheelLedGroupPresent(3))
            {
                _data.KnobRingBrightness = knobValue;
                _plugin.WheelLedAppliedBrightnessKnob = knobValue;
                if (WheelCfgChanged("wheel-knob-brightness", knobValue))
                    _deviceManager.WriteSetting("wheel-knob-brightness", knobValue);
            }
        }

        public static void UnpackPackedColor(int packed, byte[] dst)
        {
            dst[0] = (byte)((packed >> 16) & 0xFF);
            dst[1] = (byte)((packed >> 8) & 0xFF);
            dst[2] = (byte)(packed & 0xFF);
        }

        public void WritePackedColor(string command, int packed)
        {
            byte r = (byte)((packed >> 16) & 0xFF);
            byte g = (byte)((packed >> 8) & 0xFF);
            byte b = (byte)(packed & 0xFF);
            // Only base-ambient colours route here (startup/shutdown plus the
            // per-LED idle/sleep palettes) — target the base-owning pipe (see
            // BaseManager).
            BaseManager.WriteColor(command, r, g, b);
        }

        /// <summary>Send all-off to wheel and dash LEDs via the device manager.</summary>
        public void ClearLedsOnHardware()
        {
            if (_plugin.Connection == null || !_plugin.Connection.IsConnected) return;
            var modelInfo = _plugin.WheelModelInfo;
            int rpmCount = modelInfo?.RpmLedCount ?? 0;
            int rpmWindow = rpmCount > 0 ? (1 << rpmCount) - 1 : 0;
            // 8-byte active+window form (active=0 = all off), matching the live path.
            _deviceManager.WriteArray("wheel-send-rpm-telemetry",
                MozaLedDeviceManager.BuildWindowedBitmaskBytes(0, rpmWindow));
            _deviceManager.WriteArray("wheel-send-buttons-telemetry",
                MozaLedDeviceManager.BuildWindowedBitmaskBytes(0, modelInfo?.ButtonWindowMask ?? 0));
            _deviceManager.WriteSetting("wheel-old-send-telemetry", 0);
            _deviceManager.WriteSetting("dash-send-telemetry", 0);
        }
    }
}
