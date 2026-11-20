using System;
using System.Collections.Generic;

namespace MozaPlugin.Hardware
{
    /// <summary>
    /// Write-on-change against a device's own readback, for devices whose setting
    /// writes are EEPROM commits (standalone shifters, FCS). Detect ARMS a reconcile
    /// for a device's profile-backed commands; each readback adopts the device value
    /// and reports whether the caller should push the profile's value for that one
    /// setting. Profile applies skip a setting while its readback is still due, and
    /// never write a device that has answered nothing (a device whose stored values
    /// can't be read is not written blind — bundle W3H1TH2S). User writes always go
    /// out and retire the pending reconcile.
    ///
    /// Static per process: a plugin reload reconstructs the applier and re-applies
    /// the profile, and must not re-write what the device already holds.
    /// Leaf lock: readbacks land on serial threads, applies on the UI/profile thread.
    /// </summary>
    internal sealed class ReadbackReconcileCache
    {
        private readonly Dictionary<string, long> _cache = new Dictionary<string, long>(StringComparer.Ordinal);
        // Settings still awaiting their connect-time readback.
        private readonly HashSet<string> _pending = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _armedTicks = new Dictionary<string, long>(StringComparer.Ordinal);
        // Devices that delivered at least one readback since they were armed.
        private readonly HashSet<string> _answeredAny = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _silentLogged = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _lock = new object();
        private readonly double _graceMs;

        public ReadbackReconcileCache(double graceMs) { _graceMs = graceMs; }

        private static string Key(string device, string command) => device + ":" + command;

        /// <summary>True (and records the value) iff it differs from the device's last
        /// known value for this setting, or nothing is known yet.</summary>
        public bool Changed(string device, string command, long value)
        {
            string key = Key(device, command);
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var prev) && prev == value) return false;
                _cache[key] = value;
                return true;
            }
        }

        /// <summary>User-initiated write: record it and retire any pending reconcile.</summary>
        public void NoteUserWrite(string device, string command, long value)
        {
            string key = Key(device, command);
            lock (_lock)
            {
                _cache[key] = value;
                _pending.Remove(key);
            }
        }

        /// <summary>Arm the connect-time reconcile for <paramref name="commands"/>.</summary>
        public void Arm(string device, IEnumerable<string> commands)
        {
            lock (_lock)
            {
                _armedTicks[device] = DateTime.UtcNow.Ticks;
                // Fresh connect, fresh evidence.
                _answeredAny.Remove(device);
                _silentLogged.Remove(device);
                foreach (var cmd in commands)
                    _pending.Add(Key(device, cmd));
            }
        }

        /// <summary>True while this setting's connect-time readback is still due. After
        /// the grace it is released, but only for a device that has answered at least
        /// one read; a silent device stays pending (logged once via
        /// <paramref name="onSilent"/>).</summary>
        public bool Pending(string device, string command, Action? onSilent = null)
        {
            string key = Key(device, command);
            bool logSilent = false, pending;
            lock (_lock)
            {
                if (!_pending.Contains(key)) return false;
                bool withinGrace = _armedTicks.TryGetValue(device, out var armed)
                    && (DateTime.UtcNow.Ticks - armed) < (long)(_graceMs * TimeSpan.TicksPerMillisecond);
                if (withinGrace) return true;
                if (_answeredAny.Contains(device))
                {
                    _pending.Remove(key);
                    pending = false;
                }
                else
                {
                    logSilent = _silentLogged.Add(device);
                    pending = true;
                }
            }
            if (logSilent) onSilent?.Invoke();
            return pending;
        }

        /// <summary>Adopt a readback as ground truth. Returns true when this setting's
        /// connect-time reconcile was outstanding (caller pushes the profile value,
        /// which <see cref="Changed"/> then writes iff it differs).</summary>
        public bool Prime(string device, string command, long deviceValue)
        {
            string key = Key(device, command);
            lock (_lock)
            {
                _cache[key] = deviceValue;
                _answeredAny.Add(device);
                return _pending.Remove(key);
            }
        }

        /// <summary>Forget everything known about <paramref name="device"/>.</summary>
        public void Forget(string device)
        {
            string prefix = device + ":";
            lock (_lock)
            {
                List<string>? keys = null;
                foreach (var k in _cache.Keys)
                    if (k.StartsWith(prefix, StringComparison.Ordinal)) (keys ??= new List<string>()).Add(k);
                if (keys != null) foreach (var k in keys) _cache.Remove(k);
                _pending.RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal));
            }
        }
    }
}
