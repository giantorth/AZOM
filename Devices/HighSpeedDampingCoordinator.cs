using System;
using System.Threading;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// High Speed Damping: while the in-game speed is at or above the trigger
    /// speed (<see cref="MozaData.SpeedDampingPoint"/>, km/h), adds the damping
    /// level (<see cref="MozaData.SpeedDamping"/>, %) to the wheelbase damper, and
    /// removes it below. Trigger speed 0 disables it.
    ///
    /// <para>base-damper is an EEPROM-backed parameter, so transitions use speed
    /// hysteresis and a minimum interval between writes.</para>
    /// </summary>
    internal sealed class HighSpeedDampingCoordinator
    {
        private const double HysteresisKmh = 5.0;
        private const long MinWriteIntervalMs = 1000;
        private const long FeedStaleMs = 3000;

        private readonly MozaPlugin _plugin;
        private readonly MozaData _data;
        private readonly DeviceDetectionState _detectionState;

        // Leaf lock apart from HardwareApplier's damper-wire lock, always taken first.
        private readonly object _lock = new object();
        private bool _aboveTrigger;
        private int _applied;               // boost last dispatched; -1 = wire state unknown
        private long _lastWriteTicks;
        private long _lastFeedTicks;
        private volatile bool _lastGameRunning;

        internal HighSpeedDampingCoordinator(MozaPlugin plugin, MozaData data, DeviceDetectionState detectionState)
        {
            _plugin = plugin;
            _data = data;
            _detectionState = detectionState;
        }

        /// <summary>DataUpdate tick.</summary>
        internal void Tick(bool gameRunning, double speedKmh)
        {
            if (MozaPlugin.IsShuttingDown) return;
            Interlocked.Exchange(ref _lastFeedTicks, DateTime.UtcNow.Ticks);
            _lastGameRunning = gameRunning;
            lock (_lock)
            {
                UpdateTrigger(gameRunning, speedKmh);
                ReconcileLocked(force: false);
            }
        }

        /// <summary>PollStatus backstop: DataUpdate goes quiet when the game exits,
        /// so a stale feed releases the boost here.</summary>
        internal void Reconcile()
        {
            if (MozaPlugin.IsShuttingDown) return;
            lock (_lock)
            {
                long lastFeed = Interlocked.Read(ref _lastFeedTicks);
                bool fresh = (DateTime.UtcNow.Ticks - lastFeed) <= FeedStaleMs * TimeSpan.TicksPerMillisecond;
                if (!fresh || !_lastGameRunning) _aboveTrigger = false;
                ReconcileLocked(force: false);
            }
        }

        /// <summary>Plugin teardown: put the user's damper back on the wire.</summary>
        internal void Release()
        {
            lock (_lock)
            {
                _aboveTrigger = false;
                if (_applied == 0 && !_data.DamperWireStale) return;
                ReconcileLocked(force: true);
            }
        }

        private void UpdateTrigger(bool gameRunning, double speedKmh)
        {
            int point = _data.SpeedDampingPoint;
            if (!gameRunning || point <= 0 || double.IsNaN(speedKmh)) { _aboveTrigger = false; return; }
            if (_aboveTrigger)
            {
                double releaseBelow = point - Math.Min(HysteresisKmh, point * 0.5);
                if (speedKmh < releaseBelow) _aboveTrigger = false;
            }
            else if (speedKmh >= point)
            {
                _aboveTrigger = true;
            }
        }

        // Caller holds _lock.
        private void ReconcileLocked(bool force)
        {
            var hw = _plugin.HardwareApplier;
            if (hw == null) return;

            if (!_detectionState.BaseDetected)
            {
                // Base gone: a boost it may still hold must be rewritten on return.
                if (_applied != 0) _applied = -1;
                return;
            }

            int level = _data.SpeedDamping;
            int point = _data.SpeedDampingPoint;
            bool enabled = level > 0 && point > 0;
            int desired = enabled && _aboveTrigger ? level * 10 : 0;

            if (_data.DamperWireStale)
            {
                if (desired == 0) _applied = -1;
                _data.DamperWireStale = false;
            }

            if (desired == _applied)
            {
                // A failed dispatch can leave the overlay set while the wire never changed.
                if (_data.DamperSpeedBoost != desired) hw.SyncDamperSpeedBoost(desired);
                return;
            }
            if (_data.Damper < 0) return; // user value not known yet

            long now = DateTime.UtcNow.Ticks;
            if (!force && _applied >= 0
                && (now - _lastWriteTicks) < MinWriteIntervalMs * TimeSpan.TicksPerMillisecond)
                return;

            if (hw.SetDamperSpeedBoost(desired))
            {
                _applied = desired;
                _lastWriteTicks = now;
                MozaLog.Debug($"[AZOM] High speed damping: {(desired > 0 ? $"+{level}% damper" : "off")}");
            }
        }
    }
}
