using System;
using System.Threading;
using MozaPlugin.Settings;

namespace MozaPlugin.Devices.StalksTruckSim
{
    /// <summary>
    /// Translates MOZA Stalks button presses into keyboard output for ETS2/ATS
    /// ("truck-sim mode"). Momentary buttons tap a key; wiper/light-knob positions
    /// step the game's cycling controls to the mapped stage. All key output goes
    /// through <see cref="KeyboardSender"/> (its own worker thread + foreground gate),
    /// so the HID and SimHub threads never block.
    ///
    /// Wiring: the plugin subscribes <see cref="OnStalkButton"/> to
    /// <c>MozaHidReader.StalksButtonChanged</c>, calls <see cref="ApplySettings"/>
    /// whenever settings change, and <see cref="SetGameContext"/> each DataUpdate.
    /// </summary>
    internal sealed class StalksTruckSimController : IDisposable
    {
        private readonly KeyboardSender _kb = new KeyboardSender();
        private readonly object _lock = new object();

        // Config snapshot (guarded by _lock).
        private StalkMode _mode = StalkMode.ButtonBox;
        private StalkTruckSimSettings _cfg = new StalkTruckSimSettings();

        // Fast gates (read on the HID thread).
        private volatile bool _truckSimEnabled;   // _mode == TruckSim
        private volatile bool _truckGameRunning;  // running && ETS2/ATS

        // Open-loop tracked game stages (guarded by _lock).
        private int _wiperStage;
        private int _lightStage;
        private int _activeIndicator; // 0 none, 1 left, 2 right

        // Deferred indicator cancel (guarded by _lock). The lever springs back the
        // instant it is let go, which is not a request to stop signalling — so a
        // cancel arriving before the minimum blink time is served by the timer instead.
        private long _indicatorOnTicks;
        private int _pendingCancelSide;
        private readonly Timer _cancelTimer;

        public StalksTruckSimController()
        {
            // Only inject keys when a truck game is the foreground window.
            _kb.SetForegroundProcesses("eurotrucks2", "amtrucks");
            _cancelTimer = new Timer(OnCancelTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        private bool Active => _truckSimEnabled && _truckGameRunning;

        /// <summary>Apply the current mode + config (call on load and on every UI edit).</summary>
        public void ApplySettings(StalkMode mode, StalkTruckSimSettings cfg)
        {
            lock (_lock)
            {
                _mode = mode;
                _cfg = cfg ?? new StalkTruckSimSettings();
                _kb.HoldMs = Math.Max(1, _cfg.KeyHoldMs);
                _kb.GapMs = Math.Max(0, _cfg.KeyGapMs);
            }
            _truckSimEnabled = mode == StalkMode.TruckSim;
            if (mode != StalkMode.TruckSim) { ResetIndicator(); _kb.ReleaseAll(); _kb.Flush(); }
        }

        /// <summary>Push the current game context each DataUpdate tick.</summary>
        public void SetGameContext(string gameName, bool gameRunning)
        {
            bool truck = gameRunning && IsTruckSimGame(gameName);
            bool wasTruck = _truckGameRunning;
            _truckGameRunning = truck;
            if (!truck)
            {
                // The blinker tracker models the game's own state, which is gone with
                // the game — unlike the wiper/light stages below, it is reset here.
                // Only on the edge: this runs at DataUpdate rate.
                if (wasTruck) ResetIndicator();
                _kb.ReleaseAll();
                _kb.Flush();
            }
            // Do NOT re-home the tracked stages when the game re-enters focus. Alt-
            // tabbing flips GameRunning while the truck keeps its lights/wipers, so
            // resetting to 0 here desynced them. Tracking persists across focus
            // changes; a fresh spawn starts at 0 (field defaults) and the Re-sync
            // button realigns if anything ever drifts.
        }

        /// <summary>Handle a stalk button edge (from the HID read thread).</summary>
        public void OnStalkButton(int index, bool pressed)
        {
            StalkAction? action = null;
            StalkTruckSimSettings cfg;
            lock (_lock)
            {
                cfg = _cfg;
                if (_mode == StalkMode.TruckSim && cfg.ButtonActions != null &&
                    cfg.ButtonActions.TryGetValue(index, out var a))
                    action = a;
            }

            // Release edge: only held keys act — always send the key-up so a key can
            // never stick down (a mode/game change also calls ReleaseAll()).
            if (!pressed)
            {
                if (action != null && action.Kind == StalkActionKind.HeldKey)
                    _kb.KeyUp(action.Key);
                return;
            }

            // Press edge: gate on the game being active AND the foreground window.
            // While alt-tabbed the key output is dropped, so processing the press
            // would advance the tracked wiper/light stage and desync it — ignore it.
            if (!Active || action == null || !_kb.IsGameForeground()) return;

            switch (action.Kind)
            {
                case StalkActionKind.HeldKey:
                    _kb.KeyDown(action.Key);
                    break;
                case StalkActionKind.LatchKey:
                    // Latch down and keep held; released by a ReleaseHeld button
                    // (e.g. the neutral stalk position), not this button's release.
                    _kb.KeyDown(action.Key);
                    break;
                case StalkActionKind.ReleaseHeld:
                    _kb.ReleaseAll();
                    break;
                case StalkActionKind.Momentary:
                    _kb.Tap(action.Key);
                    break;
                case StalkActionKind.WiperStage:
                    ConvergeWiper(action.Stage, cfg);
                    break;
                case StalkActionKind.LightStage:
                    ConvergeLight(action.Stage, cfg);
                    break;
                case StalkActionKind.IndicatorLeft:
                    Indicate(cfg, side: 1);
                    break;
                case StalkActionKind.IndicatorRight:
                    Indicate(cfg, side: 2);
                    break;
                case StalkActionKind.IndicatorCancel:
                    CancelIndicator(cfg);
                    break;
                case StalkActionKind.WiperSingleSwipe:
                    // One forward step (wipers on). The spring-loaded stalk returning
                    // to the "off" position fires that button's WiperStage 0 action,
                    // which converges back down and sends the back key. Bump the
                    // tracked stage so that return sends exactly one back tap.
                    _kb.Tap(cfg.WiperForwardKey);
                    lock (_lock) _wiperStage = WrapStage(_wiperStage + 1, cfg.WiperStageCount);
                    break;
            }
        }

        // Turn-signal: tap the side's key and remember it so a later neutral press
        // (IndicatorCancel) can re-tap it to toggle the blinker off. The in-game key
        // is a toggle, so a side that is already lit is switched off instead, and
        // swapping sides switches the old one off before the new one on.
        private void Indicate(StalkTruckSimSettings cfg, int side)
        {
            string key = SideKey(cfg, side);
            string? turnOffFirst = null;
            bool turnOn;
            lock (_lock)
            {
                ClearPendingCancel();
                turnOn = _activeIndicator != side;
                if (_activeIndicator != 0 && _activeIndicator != side)
                    turnOffFirst = SideKey(cfg, _activeIndicator);
                _activeIndicator = turnOn ? side : 0;
                if (turnOn) _indicatorOnTicks = DateTime.UtcNow.Ticks;
            }
            if (turnOffFirst != null) _kb.Tap(turnOffFirst);
            _kb.Tap(key);
        }

        // Lever back at neutral. Cancel now only once the blinker has been lit for the
        // configured minimum; otherwise hand the cancel to the timer so a quick flick
        // still signals for a few seconds (matching Pit House).
        private void CancelIndicator(StalkTruckSimSettings cfg)
        {
            string? key = null;
            lock (_lock)
            {
                ClearPendingCancel();
                if (_activeIndicator == 0) return;

                long remainMs = RemainingBlinkMs(cfg);
                if (remainMs > 0)
                {
                    _pendingCancelSide = _activeIndicator;
                    try { _cancelTimer.Change(remainMs, Timeout.Infinite); }
                    catch (ObjectDisposedException) { _pendingCancelSide = 0; }
                    return;
                }

                key = SideKey(cfg, _activeIndicator);
                _activeIndicator = 0;
            }
            _kb.Tap(key);
        }

        // Minimum blink time is up — send the cancel the neutral position deferred.
        private void OnCancelTimer(object? _)
        {
            StalkTruckSimSettings cfg;
            int side;
            lock (_lock)
            {
                side = _pendingCancelSide;
                cfg = _cfg;
                if (side == 0 || _activeIndicator != side) return;

                // A callback armed for an earlier turn-on can win the race against a
                // re-flick that restamped the clock; re-check and re-arm rather than
                // cutting the new blink short.
                long remainMs = RemainingBlinkMs(cfg);
                if (remainMs > 0)
                {
                    try { _cancelTimer.Change(remainMs, Timeout.Infinite); }
                    catch (ObjectDisposedException) { _pendingCancelSide = 0; }
                    return;
                }
            }

            // The foreground query is a process lookup — never held under _lock.
            bool deliver = Active && _kb.IsGameForeground();
            lock (_lock)
            {
                if (_pendingCancelSide != side || _activeIndicator != side) return;
                _pendingCancelSide = 0;
                // Not foreground: the key would be dropped, so leave the blinker
                // tracked as lit — the next lever flick then still resolves to the
                // right tap instead of desyncing.
                if (!deliver) return;
                _activeIndicator = 0;
            }
            _kb.Tap(SideKey(cfg, side));
        }

        /// <summary>Milliseconds left of the minimum blink time, 0 once it is up.
        /// Caller holds <see cref="_lock"/>.</summary>
        private long RemainingBlinkMs(StalkTruckSimSettings cfg)
        {
            long minMs = Math.Max(0, cfg.IndicatorMinBlinkSeconds) * 1000L;
            long litMs = (DateTime.UtcNow.Ticks - _indicatorOnTicks) / TimeSpan.TicksPerMillisecond;
            return Math.Max(0, minMs - litMs);
        }

        private static string SideKey(StalkTruckSimSettings cfg, int side)
            => side == 1 ? cfg.IndicatorLeftKey : cfg.IndicatorRightKey;

        /// <summary>Drop any armed cancel. Caller holds <see cref="_lock"/>.</summary>
        private void ClearPendingCancel()
        {
            _pendingCancelSide = 0;
            try { _cancelTimer.Change(Timeout.Infinite, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>Forget the tracked blinker and any armed cancel.</summary>
        private void ResetIndicator()
        {
            lock (_lock)
            {
                ClearPendingCancel();
                _activeIndicator = 0;
            }
        }

        private void ConvergeWiper(int target, StalkTruckSimSettings cfg)
        {
            int cur;
            lock (_lock) cur = _wiperStage;
            var steps = StageCycle.PlanSteps(cur, target, cfg.WiperStageCount, cfg.WiperForwardWraps, hasBackKey: true);
            foreach (var s in steps)
                _kb.Tap(s > 0 ? cfg.WiperForwardKey : cfg.WiperBackKey);
            lock (_lock) _wiperStage = WrapStage(target, cfg.WiperStageCount);
        }

        private void ConvergeLight(int target, StalkTruckSimSettings cfg)
        {
            int cur;
            lock (_lock) cur = _lightStage;
            // Light knob uses a single forward-only cycle key (wraps).
            var steps = StageCycle.PlanSteps(cur, target, cfg.LightStageCount, wrap: true, hasBackKey: false);
            foreach (var _ in steps)
                _kb.Tap(cfg.LightCycleKey);
            lock (_lock) _lightStage = WrapStage(target, cfg.LightStageCount);
        }

        /// <summary>Force the game wipers to stage 0 and re-sync the tracker
        /// (the UI "Re-sync wipers" button) — drops enough back-taps to reach off
        /// from any stage.</summary>
        public void ResyncWipers()
        {
            StalkTruckSimSettings cfg;
            lock (_lock) cfg = _cfg;
            // Drive the wipers to off (we have a back key); the light knob's cycle
            // key is forward-only so we can't force it — just reset the light tracker
            // and assume the user has set the lights off.
            if (Active && _kb.IsGameForeground())
            {
                int backs = Math.Max(0, cfg.WiperStageCount - 1);
                for (int i = 0; i < backs; i++) _kb.Tap(cfg.WiperBackKey);
            }
            lock (_lock) { _wiperStage = 0; _lightStage = 0; }
        }

        private static int WrapStage(int stage, int count)
        {
            if (count <= 0) return 0;
            int r = stage % count;
            return r < 0 ? r + count : r;
        }

        /// <summary>SimHub game code / name that indicates ETS2 or ATS.</summary>
        public static bool IsTruckSimGame(string gameName)
        {
            if (string.IsNullOrEmpty(gameName)) return false;
            return gameName.Equals("Ets2", StringComparison.OrdinalIgnoreCase)
                || gameName.Equals("Ats", StringComparison.OrdinalIgnoreCase)
                || gameName.IndexOf("truck", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public void Dispose()
        {
            try { _cancelTimer.Dispose(); } catch { }
            try { _kb.Dispose(); } catch { }
        }
    }
}
