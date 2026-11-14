using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MozaPlugin.Settings;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Idle dashboard: a per-device dashboard shown while no game is open (optionally
    /// also in game while not driving: paused, menus, replay, spectating, or no
    /// telemetry between stages), stored per
    /// device page (<see cref="MozaPluginSettings.IdleDashboardByPageGuid"/> /
    /// <see cref="MozaPluginSettings.IdleDashboardIndexByPageGuid"/> /
    /// <see cref="MozaPluginSettings.IdleDashboardWhenNotDrivingByPageGuid"/>), never on
    /// a profile.
    ///
    /// <para>One lane per screen: the tier-def wheel, the CM2, the FSR V1 wheel and the
    /// CM1. While idle a lane is "engaged": the current-pick getters
    /// (<c>ActiveTelemetryProfileName</c>, <c>ActiveCm2DashboardName</c>,
    /// <c>GetActiveFsr1Index</c>, <c>GetActiveCm1Index</c>) return the idle pick, so
    /// channel mappings, the dashboard list and the re-asserts follow the screen, while
    /// the profile's own pick stays untouched. Profile applies arriving while engaged are
    /// held (<see cref="HoldWheel"/> / <see cref="HoldCm2"/>). When idle ends (a game
    /// opens, or driving resumes) the lane disengages and the profile's pick is
    /// re-applied.</para>
    ///
    /// <para>An explicit pick while engaged (dashboard list, cycle action, a switch made
    /// on the device) is a normal profile pick: the lane stands down until idle
    /// ends.</para>
    /// </summary>
    internal sealed class IdleDashboardCoordinator
    {
        private readonly MozaPlugin _plugin;
        private readonly ProfileCoordinator _profiles;
        private readonly Fsr1Cm1MappingCoordinator _indexes;

        // Serialises engage/disengage across PollStatus, the game-start nudge, the UI
        // and the profile-apply holds. Reentrant (Hold* runs inside a tick's restore).
        private readonly object _tickLock = new object();
        private readonly object _settingsLock = new object();

        private sealed class NameEngagement
        {
            public readonly Guid Page;
            public readonly string Name;
            public NameEngagement(Guid page, string name) { Page = page; Name = name; }
        }

        private sealed class IndexEngagement
        {
            public readonly Guid Page;
            public readonly int Index;
            public IndexEngagement(Guid page, int index) { Page = page; Index = index; }
        }

        // Read lock-free by the getters on any thread; written under _tickLock (and
        // cleared by the Yield* calls from any thread).
        private volatile NameEngagement? _wheel;
        private volatile string? _cm2;
        private volatile IndexEngagement? _fsr1;
        private volatile int _cm1 = -1;

        private volatile bool _wheelYielded, _cm2Yielded, _fsr1Yielded, _cm1Yielded;
        private volatile bool _wheelRestorePending, _cm2RestorePending;
        // What the screen showed before engaging, for a profile with no saved pick.
        private string? _wheelPreIdleName, _cm2PreIdleName;

        // The FSR1/CM1 report every page write in their Param-6 log, ours included; a
        // report of the idle page we just selected is not a device-side switch.
        private int _fsr1EchoIndex = -1, _cm1EchoIndex = -1;
        private long _fsr1EchoUntilTicks, _cm1EchoUntilTicks;
        private static readonly long EchoWindowTicks = TimeSpan.FromSeconds(15).Ticks;

        private int _tickQueued;

        // Game open = SimHub sees its process or its telemetry (the test SimHub's own
        // "game process detected" dash start mode uses). GameRunning alone is telemetry
        // only: RBR / DiRT drop it in their menus, which are "not driving", not "no game".
        private volatile bool _gameOpen;
        private long _lastFeedTicks;
        private static readonly long FeedStaleTicks = TimeSpan.FromSeconds(3).Ticks;
        private bool _lastGameOpen; // DataUpdate thread only

        // Not-driving clock from DataUpdate (0 = driving or no game). Settled after
        // NotDrivingSettleTicks so a brief menu/pause flag doesn't swap dashboards.
        private long _notDrivingSinceTicks;
        private int _notDrivingTickSent;
        private static readonly long NotDrivingSettleTicks = TimeSpan.FromSeconds(3).Ticks;

        // The game-open flag reads false until the first DataUpdate stamps the feed, so
        // right after Init (a reload mid-game included) "no game" can't be trusted yet.
        private readonly long _createdTicks = DateTime.UtcNow.Ticks;
        private static readonly long GameStateSettleTicks = TimeSpan.FromSeconds(5).Ticks;
        private bool GameStateKnown => DateTime.UtcNow.Ticks - _createdTicks >= GameStateSettleTicks;

        internal IdleDashboardCoordinator(MozaPlugin plugin, ProfileCoordinator profiles, Fsr1Cm1MappingCoordinator indexes)
        {
            _plugin = plugin;
            _profiles = profiles;
            _indexes = indexes;
        }

        internal enum Hold
        {
            /// <summary>No idle dashboard applies: go ahead with the profile pick.</summary>
            Proceed,
            /// <summary>The idle dashboard is showing; the profile pick waits for the game.</summary>
            Held,
            /// <summary>Game state not known yet: retry later.</summary>
            Defer,
        }

        // ===== Settings =====

        internal string? IdleName(Guid page)
        {
            var d = _plugin.Settings?.IdleDashboardByPageGuid;
            return d != null && d.TryGetValue(page, out var v) && !string.IsNullOrEmpty(v) ? v : null;
        }

        internal int IdleIndex(Guid page)
        {
            var d = _plugin.Settings?.IdleDashboardIndexByPageGuid;
            return d != null && d.TryGetValue(page, out var v) ? v : -1;
        }

        /// <summary>Save a tier-def screen's idle dashboard (null = off). Takes effect at
        /// once while idle; otherwise it waits for idle.</summary>
        internal void SetIdleName(Guid page, string? name)
        {
            var s = _plugin.Settings;
            if (s == null) return;
            lock (_settingsLock)
            {
                // COW: the save debounce serializes this dict off-thread.
                var next = s.IdleDashboardByPageGuid == null
                    ? new Dictionary<Guid, string>()
                    : new Dictionary<Guid, string>(s.IdleDashboardByPageGuid);
                if (string.IsNullOrEmpty(name)) next.Remove(page);
                else next[page] = name!;
                s.IdleDashboardByPageGuid = next;
            }
            if (page == MozaPlugin.Cm2PageGuid) _cm2Yielded = false;
            else _wheelYielded = false;
            MozaLog.Info($"[AZOM] Idle dashboard ({Describe(page)}) set to {(string.IsNullOrEmpty(name) ? "off" : "'" + name + "'")}");
            _plugin.PersistSettings();
            RequestTick();
        }

        internal bool WhenNotDriving(Guid page)
        {
            var d = _plugin.Settings?.IdleDashboardWhenNotDrivingByPageGuid;
            return d != null && d.TryGetValue(page, out var v) && v;
        }

        /// <summary>Also show the page's idle dashboard in game while not driving.</summary>
        internal void SetWhenNotDriving(Guid page, bool on)
        {
            var s = _plugin.Settings;
            if (s == null) return;
            lock (_settingsLock)
            {
                var next = s.IdleDashboardWhenNotDrivingByPageGuid == null
                    ? new Dictionary<Guid, bool>()
                    : new Dictionary<Guid, bool>(s.IdleDashboardWhenNotDrivingByPageGuid);
                if (on) next[page] = true;
                else next.Remove(page);
                s.IdleDashboardWhenNotDrivingByPageGuid = next;
            }
            if (page == MozaPlugin.Cm2PageGuid) _cm2Yielded = false;
            else if (page == MozaPlugin.Cm1PageGuid) _cm1Yielded = false;
            else _wheelYielded = _fsr1Yielded = false;
            MozaLog.Info($"[AZOM] Idle dashboard ({Describe(page)}) when not driving: {(on ? "on" : "off")}");
            _plugin.PersistSettings();
            RequestTick();
        }

        /// <summary>Index form of <see cref="SetIdleName"/> for FSR V1 / CM1 (-1 = off).</summary>
        internal void SetIdleIndex(Guid page, int index)
        {
            var s = _plugin.Settings;
            if (s == null) return;
            lock (_settingsLock)
            {
                var next = s.IdleDashboardIndexByPageGuid == null
                    ? new Dictionary<Guid, int>()
                    : new Dictionary<Guid, int>(s.IdleDashboardIndexByPageGuid);
                if (index < 0) next.Remove(page);
                else next[page] = index;
                s.IdleDashboardIndexByPageGuid = next;
            }
            if (page == MozaPlugin.Cm1PageGuid) _cm1Yielded = false;
            else _fsr1Yielded = false;
            MozaLog.Info($"[AZOM] Idle dashboard ({Describe(page)}) set to {(index < 0 ? "off" : "page index " + index)}");
            _plugin.PersistSettings();
            RequestTick();
        }

        // ===== Overrides read by the current-pick getters =====

        /// <summary>The wheel's idle dashboard name while engaged on the attached wheel, else null.</summary>
        internal string? WheelOverrideName
        {
            get
            {
                var e = _wheel;
                if (e == null) return null;
                return _plugin.GetCurrentWheelPageGuid() == e.Page ? e.Name : null;
            }
        }

        internal bool WheelEngaged => WheelOverrideName != null;

        internal string? Cm2OverrideName => _cm2;

        /// <summary>FSR V1 idle page index while engaged on the attached wheel, else -1.</summary>
        internal int Fsr1OverrideIndex
        {
            get
            {
                var e = _fsr1;
                if (e == null) return -1;
                return _plugin.GetCurrentWheelPageGuid() == e.Page ? e.Index : -1;
            }
        }

        internal int Cm1OverrideIndex => _cm1;

        // ===== Explicit picks =====

        /// <summary>A dashboard was picked for the wheel screen (list, action, wheel-side
        /// switch): it wins over the idle dashboard until the next game session ends.</summary>
        internal void YieldWheel()
        {
            _wheelRestorePending = false;
            if (_wheel == null) return;
            _wheel = null;
            _wheelYielded = true;
            MozaLog.Info("[AZOM] Idle dashboard (wheel): dashboard picked — standing down until idle ends");
        }

        internal void YieldCm2()
        {
            _cm2RestorePending = false;
            if (_cm2 == null) return;
            _cm2 = null;
            _cm2Yielded = true;
            MozaLog.Info("[AZOM] Idle dashboard (CM2): dashboard picked — standing down until idle ends");
        }

        /// <summary>Returns true when the idle page was showing, so the caller must send
        /// its select even if it matches the stored page.</summary>
        internal bool YieldFsr1()
        {
            if (_fsr1 == null) return false;
            _fsr1 = null;
            _fsr1Yielded = true;
            MozaLog.Info("[AZOM] Idle dashboard (FSR1): page picked — standing down until idle ends");
            return true;
        }

        internal bool YieldCm1()
        {
            if (_cm1 < 0) return false;
            _cm1 = -1;
            _cm1Yielded = true;
            MozaLog.Info("[AZOM] Idle dashboard (CM1): page picked — standing down until idle ends");
            return true;
        }

        /// <summary>True when a Param-6 page report is our own idle select coming back.</summary>
        internal bool IsFsr1IdleEcho(int index) =>
            index == Fsr1OverrideIndex
            || (index == Volatile.Read(ref _fsr1EchoIndex)
                && DateTime.UtcNow.Ticks < Interlocked.Read(ref _fsr1EchoUntilTicks));

        internal bool IsCm1IdleEcho(int index) =>
            index == _cm1
            || (index == Volatile.Read(ref _cm1EchoIndex)
                && DateTime.UtcNow.Ticks < Interlocked.Read(ref _cm1EchoUntilTicks));

        // ===== Profile-apply holds =====

        /// <summary>
        /// Called by the wheel's profile-dashboard apply once the wheel is ready: shows
        /// the idle dashboard instead when one applies.
        /// </summary>
        internal Hold HoldWheel()
        {
            var page = _plugin.GetCurrentWheelPageGuid();
            if (!page.HasValue || _wheelYielded) return Hold.Proceed;
            string? name = IdleName(page.Value);
            if (name == null) return Hold.Proceed;
            if (!GameStateKnown) return Hold.Defer;
            if (!LaneIdle(page.Value)) return Hold.Proceed;
            lock (_tickLock) return EngageWheel(page.Value, name) ? Hold.Held : Hold.Proceed;
        }

        /// <summary>CM2 counterpart of <see cref="HoldWheel"/>.</summary>
        internal Hold HoldCm2()
        {
            if (_cm2Yielded || !Cm2LaneUp) return Hold.Proceed;
            string? name = IdleName(MozaPlugin.Cm2PageGuid);
            if (name == null) return Hold.Proceed;
            if (!GameStateKnown) return Hold.Defer;
            if (!LaneIdle(MozaPlugin.Cm2PageGuid)) return Hold.Proceed;
            lock (_tickLock) return EngageCm2(name) ? Hold.Held : Hold.Proceed;
        }

        /// <summary>The CM2 pipeline-start re-assert waits while an idle dashboard may
        /// apply but the game state isn't known, so it doesn't bind the profile pick
        /// only to be switched away a tick later.</summary>
        internal bool Cm2ReassertMustWait =>
            !_cm2Yielded && !GameStateKnown && IdleName(MozaPlugin.Cm2PageGuid) != null;

        // ===== Tick =====

        /// <summary>Run a tick off the caller's thread (game start, setting change).</summary>
        internal void RequestTick()
        {
            if (Interlocked.Exchange(ref _tickQueued, 1) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Interlocked.Exchange(ref _tickQueued, 0);
                try { Tick(); }
                catch (Exception ex) { MozaLog.Warn($"[AZOM] Idle dashboard tick failed: {ex.Message}"); }
            });
        }

        /// <summary>
        /// DataUpdate: the game open/close edges and the not-driving clock. Driving needs
        /// live telemetry; an open game without it (menus between stages) is not driving.
        /// A game that opens in its menus counts as not driving at once, so the idle
        /// dashboard doesn't drop and come back.
        /// </summary>
        internal void NoteGameData(bool gameRunning, bool processDetected, bool driving)
        {
            bool open = gameRunning || processDetected;
            Interlocked.Exchange(ref _lastFeedTicks, DateTime.UtcNow.Ticks);
            _gameOpen = open;
            bool started = open && !_lastGameOpen;
            bool tick = open != _lastGameOpen;
            _lastGameOpen = open;
            driving &= gameRunning;
            if (!open || driving)
            {
                Interlocked.Exchange(ref _notDrivingSinceTicks, 0);
                // Back to driving after a settled stop: swap now, not on the next poll.
                if (Interlocked.Exchange(ref _notDrivingTickSent, 0) != 0) tick = true;
            }
            else
            {
                long now = DateTime.UtcNow.Ticks;
                long since = Interlocked.Read(ref _notDrivingSinceTicks);
                if (since == 0)
                {
                    since = started ? now - NotDrivingSettleTicks : now;
                    Interlocked.Exchange(ref _notDrivingSinceTicks, since);
                }
                if (now - since >= NotDrivingSettleTicks && Interlocked.Exchange(ref _notDrivingTickSent, 1) == 0)
                    tick = true;
            }
            if (tick) RequestTick();
        }

        private bool NotDriving
        {
            get
            {
                long since = Interlocked.Read(ref _notDrivingSinceTicks);
                return since != 0 && DateTime.UtcNow.Ticks - since >= NotDrivingSettleTicks;
            }
        }

        private bool GameOpen =>
            _gameOpen && DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastFeedTicks) <= FeedStaleTicks;

        /// <summary>The page's idle dashboard applies now: no game open, or (when enabled)
        /// in game but not driving.</summary>
        private bool LaneIdle(Guid page) =>
            !GameOpen || (NotDriving && WhenNotDriving(page));

        /// <summary>PollStatus: engage while idle, disengage and restore otherwise.</summary>
        internal void Tick()
        {
            if (MozaPlugin.IsShuttingDown || !GameStateKnown) return;
            lock (_tickLock)
            {
                TickWheel();
                TickCm2();
                TickFsr1();
                TickCm1();
            }
        }

        // ----- Wheel (tier-def) -----

        private void TickWheel()
        {
            var page = _plugin.GetCurrentWheelPageGuid();
            var eng = _wheel;
            // Absent wheel: keep the state for its return. A different wheel: drop it.
            if (!page.HasValue) return;
            if (eng != null && eng.Page != page.Value)
            {
                _wheel = eng = null;
                _wheelRestorePending = false;
            }
            bool idle = LaneIdle(page.Value);
            // An explicit pick holds until the lane next leaves idle.
            if (!idle) _wheelYielded = false;
            if (!_plugin.ShouldDriveDashboard() || _plugin.IsFsr1DisplayWheel) return;

            string? want = idle && !_wheelYielded ? IdleName(page.Value) : null;
            if (want != null)
            {
                EngageWheel(page.Value, want);
                return;
            }
            if (eng != null)
            {
                _wheel = null;
                _wheelRestorePending = true;
                MozaLog.Info($"[AZOM] Idle dashboard (wheel): '{eng.Name}' released ({(idle ? "turned off" : "in game")})");
            }
            if (_wheelRestorePending) RestoreWheel();
        }

        private bool EngageWheel(Guid page, string name)
        {
            var sender = _plugin.TelemetrySender;
            var bindings = _plugin.DashboardBindingCoordinator;
            var list = sender?.WheelState?.ConfigJsonList;
            if (sender == null || bindings == null || !sender.IsActive || list == null || list.Count == 0)
                return false;
            int slot = IndexOf(list, name);
            if (slot < 0)
            {
                MozaLog.DebugIfChanged("idle-dash-wheel-missing",
                    $"[AZOM] Idle dashboard (wheel): '{name}' is not on the wheel — not switching");
                return false;
            }
            string slotName = list[slot];
            var prev = _wheel;
            bool same = prev != null && prev.Page == page
                && string.Equals(prev.Name, slotName, StringComparison.OrdinalIgnoreCase);
            // Same rule as the profile apply: the display latches our frames only after
            // a host kind=4 this session.
            bool bound = sender.LastEmittedKind4Slot >= 0 && sender.ShowingSlot == slot;
            // Already engaged: only a fresh session (binding tracking reset on wheel
            // loss) needs the kind=4 again. Device-side switches stand the lane down
            // through WheelInitiatedSwitch; never fight one.
            if (same && sender.LastEmittedKind4Slot >= 0) return true;
            if (!bound && (sender.IsInSilenceCooldown || !sender.Enabled)) return false;

            if (prev == null || prev.Page != page)
            {
                int showing = sender.ShowingSlot;
                _wheelPreIdleName = showing >= 0 && showing < list.Count
                    ? list[showing]
                    : _plugin.DeviceProfiles?.ActiveWheelProfile?.TelemetryProfileName;
            }
            // Before the switch: it stages settings and mappings through the getters.
            _wheel = new NameEngagement(page, slotName);
            _wheelRestorePending = false;
            if (!bound)
            {
                if (!bindings.OnDashboardSwitched((uint)slot, sender))
                {
                    _wheel = prev;
                    bindings.ApplyTelemetrySettings();
                    sender.ReResolveActiveDashboardMappings();
                    return false;
                }
            }
            else
            {
                bindings.ClearPendingDashboardKey();
                bindings.ApplyTelemetrySettings();
                sender.ReResolveActiveDashboardMappings();
            }
            // The profile pick is no longer on screen; let its re-apply switch back.
            bindings.ForgetLastAppliedDashboardKey();
            MozaLog.Info($"[AZOM] Idle dashboard (wheel): showing '{slotName}' (slot {slot}) while idle"
                         + (bound ? " — already on it" : ""));
            _plugin.RaiseDashboardSelectionChangedInternal();
            return true;
        }

        private void RestoreWheel()
        {
            _wheelRestorePending = false;
            var holder = _plugin.DeviceProfiles?.WheelDashboardKeyProfile;
            if (holder != null && !string.IsNullOrEmpty(holder.TelemetryDashboardKey))
            {
                MozaLog.Info($"[AZOM] Idle dashboard (wheel): restoring profile dashboard {holder.TelemetryDashboardKey}");
                _plugin.DashboardBindingCoordinator?.ForgetLastAppliedDashboardKey();
                // Defers through the pending-apply retry when the wheel isn't ready.
                _plugin.RequestSavedDashboardReapply();
                _plugin.RaiseDashboardSelectionChangedInternal();
                return;
            }
            // No saved pick on this profile: back to what the wheel showed before.
            string? name = _wheelPreIdleName;
            if (string.IsNullOrEmpty(name)) return;
            var sender = _plugin.TelemetrySender;
            var bindings = _plugin.DashboardBindingCoordinator;
            var list = sender?.WheelState?.ConfigJsonList;
            if (sender == null || bindings == null || list == null || list.Count == 0
                || !sender.IsActive || sender.IsInSilenceCooldown)
            {
                _wheelRestorePending = true;
                return;
            }
            int slot = IndexOf(list, name!);
            if (slot < 0 || (sender.LastEmittedKind4Slot >= 0 && sender.ShowingSlot == slot)) return;
            MozaLog.Info($"[AZOM] Idle dashboard (wheel): restoring '{list[slot]}' (slot {slot})");
            if (!bindings.OnDashboardSwitched((uint)slot, sender))
                _wheelRestorePending = true;
            _plugin.RaiseDashboardSelectionChangedInternal();
        }

        // ----- CM2 (tier-def) -----

        private bool Cm2LaneUp => _plugin.IsCm2Present && !_plugin.DashIsCm1;

        private void TickCm2()
        {
            bool idle = LaneIdle(MozaPlugin.Cm2PageGuid);
            if (!idle) _cm2Yielded = false;
            // Absent dash: keep the state for its return.
            if (!Cm2LaneUp) return;
            string? want = idle && !_cm2Yielded ? IdleName(MozaPlugin.Cm2PageGuid) : null;
            if (want != null)
            {
                EngageCm2(want);
                return;
            }
            var eng = _cm2;
            if (eng != null)
            {
                _cm2 = null;
                _cm2RestorePending = true;
                MozaLog.Info($"[AZOM] Idle dashboard (CM2): '{eng}' released ({(idle ? "turned off" : "in game")})");
            }
            if (_cm2RestorePending) RestoreCm2();
        }

        private bool EngageCm2(string name)
        {
            var cm2 = _plugin.ActiveCm2Sender;
            var bindings = _plugin.DashboardBindingCoordinator;
            var list = cm2?.WheelState?.ConfigJsonList;
            if (cm2 == null || bindings == null || !cm2.IsActive || list == null || list.Count == 0)
                return false;
            int slot = IndexOf(list, name);
            if (slot < 0)
            {
                MozaLog.DebugIfChanged("idle-dash-cm2-missing",
                    $"[AZOM] Idle dashboard (CM2): '{name}' is not on the CM2 — not switching");
                return false;
            }
            string slotName = list[slot];
            string? prev = _cm2;
            bool same = string.Equals(prev, slotName, StringComparison.OrdinalIgnoreCase);
            // Same rule as ApplyProfileDashboardToCm2.
            bool bound = cm2.LastEmittedKind4Slot >= 0 && cm2.ShowingSlot == slot;
            // Already engaged: pipeline restarts are re-bound by TickCm2DashboardReassert
            // (which reads the idle name through ActiveCm2DashboardName); dash-side
            // switches stand the lane down through ActiveCm2DashboardName's setter.
            if (same && cm2.LastEmittedKind4Slot >= 0) return true;
            if (!bound && (cm2.IsInSilenceCooldown || !cm2.Enabled)) return false;

            if (prev == null)
            {
                int reported = cm2.ShowingSlot;
                _cm2PreIdleName = reported >= 0 && reported < list.Count
                    ? list[reported]
                    : _plugin.SavedCm2DashboardName;
            }
            _cm2 = slotName;
            _cm2RestorePending = false;
            if (!bound)
            {
                if (!_plugin.OnCm2DashboardSwitched((uint)slot))
                {
                    _cm2 = prev;
                    cm2.ReResolveActiveDashboardMappings();
                    return false;
                }
                // This kind=4 binds the CM2 for the current pipeline.
                _plugin.DualDisplay?.MarkCm2DashboardReasserted();
            }
            else
            {
                cm2.ReResolveActiveDashboardMappings(onlyIfChanged: true);
            }
            bindings.ClearPendingCm2DashboardKey();
            MozaLog.Info($"[AZOM] Idle dashboard (CM2): showing '{slotName}' (slot {slot}) while idle"
                         + (bound ? " — already on it" : ""));
            _plugin.RaiseDashboardSelectionChangedInternal();
            return true;
        }

        private void RestoreCm2()
        {
            _cm2RestorePending = false;
            var profile = _plugin.DeviceProfiles?.Cm2DashboardProfile;
            if (profile != null && !string.IsNullOrEmpty(profile.TelemetryDashboardKey))
            {
                MozaLog.Info($"[AZOM] Idle dashboard (CM2): restoring profile dashboard {profile.TelemetryDashboardKey}");
                // Defers through the CM2 pending-apply retry when the lane isn't ready.
                _profiles.ApplyCm2SavedDashboard(profile);
                _plugin.RaiseDashboardSelectionChangedInternal();
                return;
            }
            string? name = _cm2PreIdleName;
            if (string.IsNullOrEmpty(name)) return;
            var cm2 = _plugin.ActiveCm2Sender;
            var list = cm2?.WheelState?.ConfigJsonList;
            if (cm2 == null || list == null || list.Count == 0 || !cm2.IsActive || cm2.IsInSilenceCooldown)
            {
                _cm2RestorePending = true;
                return;
            }
            int slot = IndexOf(list, name!);
            if (slot < 0 || (cm2.LastEmittedKind4Slot >= 0 && cm2.ShowingSlot == slot)) return;
            MozaLog.Info($"[AZOM] Idle dashboard (CM2): restoring '{list[slot]}' (slot {slot})");
            if (!_plugin.OnCm2DashboardSwitched((uint)slot))
                _cm2RestorePending = true;
            _plugin.RaiseDashboardSelectionChangedInternal();
        }

        // ----- FSR V1 (fixed pages) -----

        private void TickFsr1()
        {
            var page = _plugin.GetCurrentWheelPageGuid();
            if (!page.HasValue) return;
            var eng = _fsr1;
            if (eng != null && eng.Page != page.Value) _fsr1 = eng = null;
            bool idle = LaneIdle(page.Value);
            if (!idle) _fsr1Yielded = false;
            if (!_plugin.IsFsr1DisplayWheel) return;

            int want = idle && !_fsr1Yielded ? IdleIndex(page.Value) : -1;
            if (want >= 0)
            {
                if (eng != null && eng.Index == want) return;
                int shown = eng?.Index ?? _indexes.StoredFsr1Index();
                _fsr1 = new IndexEngagement(page.Value, want);
                if (shown != want)
                {
                    Interlocked.Exchange(ref _fsr1EchoUntilTicks, DateTime.UtcNow.Ticks + EchoWindowTicks);
                    Volatile.Write(ref _fsr1EchoIndex, want);
                    _indexes.QueueFsr1Select(want);
                }
                MozaLog.Info($"[AZOM] Idle dashboard (FSR1): showing page index {want} while idle");
                _plugin.RaiseFsr1ActiveIndexChanged();
                return;
            }
            if (eng == null) return;
            _fsr1 = null;
            int stored = _indexes.StoredFsr1Index();
            if (stored != eng.Index) _indexes.QueueFsr1Select(stored);
            MozaLog.Info($"[AZOM] Idle dashboard (FSR1): page index {eng.Index} released, back to {stored} ({(idle ? "turned off" : "in game")})");
            _plugin.RaiseFsr1ActiveIndexChanged();
        }

        // ----- CM1 (fixed pages) -----

        private void TickCm1()
        {
            bool idle = LaneIdle(MozaPlugin.Cm1PageGuid);
            if (!idle) _cm1Yielded = false;
            // Absent dash: keep the state for its return.
            if (!_plugin.DashIsCm1) return;
            int want = idle && !_cm1Yielded ? IdleIndex(MozaPlugin.Cm1PageGuid) : -1;
            int eng = _cm1;
            if (want >= 0)
            {
                if (eng == want) return;
                int shown = eng >= 0 ? eng : _indexes.StoredCm1Index();
                _cm1 = want;
                if (shown != want)
                {
                    Interlocked.Exchange(ref _cm1EchoUntilTicks, DateTime.UtcNow.Ticks + EchoWindowTicks);
                    Volatile.Write(ref _cm1EchoIndex, want);
                    _indexes.QueueCm1Select(want);
                }
                MozaLog.Info($"[AZOM] Idle dashboard (CM1): showing page {want} while idle");
                _plugin.RaiseCm1ActiveIndexChanged();
                return;
            }
            if (eng < 0) return;
            _cm1 = -1;
            int stored = _indexes.StoredCm1Index();
            if (stored != eng) _indexes.QueueCm1Select(stored);
            MozaLog.Info($"[AZOM] Idle dashboard (CM1): page {eng} released, back to {stored} ({(idle ? "turned off" : "in game")})");
            _plugin.RaiseCm1ActiveIndexChanged();
        }

        // ===== Helpers =====

        private static int IndexOf(IReadOnlyList<string> list, string name)
        {
            for (int i = 0; i < list.Count; i++)
                if (!string.IsNullOrEmpty(list[i]) && string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        private static string Describe(Guid page) =>
            page == MozaPlugin.Cm2PageGuid ? "CM2"
            : page == MozaPlugin.Cm1PageGuid ? "CM1"
            : "wheel " + page.ToString("N").Substring(0, 8);

        private string NotDrivingTag(Guid page) => WhenNotDriving(page) ? "(+not driving)" : "";

        /// <summary>One diagnostics line: configured picks and what is engaged, or null
        /// when nothing is configured or engaged.</summary>
        internal string? DescribeState()
        {
            var parts = new List<string>();
            var names = _plugin.Settings?.IdleDashboardByPageGuid;
            if (names != null)
                foreach (var kv in names) parts.Add($"{Describe(kv.Key)}='{kv.Value}'{NotDrivingTag(kv.Key)}");
            var idx = _plugin.Settings?.IdleDashboardIndexByPageGuid;
            if (idx != null)
                foreach (var kv in idx) parts.Add($"{Describe(kv.Key)}=#{kv.Value}{NotDrivingTag(kv.Key)}");
            var w = _wheel;
            var f = _fsr1;
            string engaged = string.Join(" ", new[]
            {
                w != null ? $"wheel='{w.Name}'" : null,
                _cm2 != null ? $"cm2='{_cm2}'" : null,
                f != null ? $"fsr1=#{f.Index}" : null,
                _cm1 >= 0 ? $"cm1=#{_cm1}" : null,
            }.Where(x => x != null));
            string yielded = string.Join(",", new[]
            {
                _wheelYielded ? "wheel" : null, _cm2Yielded ? "cm2" : null,
                _fsr1Yielded ? "fsr1" : null, _cm1Yielded ? "cm1" : null,
            }.Where(x => x != null));
            if (parts.Count == 0 && engaged.Length == 0) return null;
            return $"{(parts.Count == 0 ? "(none set)" : string.Join(" ", parts))} | engaged: {(engaged.Length == 0 ? "none" : engaged)}"
                   + (yielded.Length > 0 ? $" | yielded: {yielded}" : "")
                   + $" | game={(GameOpen ? (NotDriving ? "not driving" : "driving") : "no")}";
        }
    }
}
