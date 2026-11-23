using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using BA63Driver.Interfaces;
using BA63Driver.Mapper;
using SerialDash;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;
using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices.Led
{
    /// <summary>
    /// Virtual ILedDeviceManager for devices whose live LEDs are one or more strips of
    /// per-LED <c>[idx, R, G, B]</c> colour chunks plus a lit bitmask, written through the
    /// pipe's <see cref="LedFrameScheduler"/>: the wheelbase ambient strips and the FCS
    /// button LEDs. Subclasses supply the source channel, strip geometry, wire layout
    /// and gates.
    ///
    /// Bitmask keepalive at 1 Hz from the plugin's LED timer (survives SimHub's LED
    /// pipeline going quiet). Idle handoff: when SimHub stops feeding colours (game
    /// exit delivers an empty array) one release mask goes out, then the driver stays
    /// quiet so the firmware's standby animation resumes.
    /// </summary>
    internal abstract class MozaMaskLedDeviceManager : ILedDeviceManager
    {
        private LedDeviceState _lastState = SimHubLedCompat.CreateState(
            Array.Empty<Color>(), Array.Empty<Color>(), Array.Empty<Color>(),
            Array.Empty<Color>(), Array.Empty<Color>(), Array.Empty<Color>(),
            1.0, 1.0, 1.0, 1.0);

        // Per-strip last published bitmask; -1 = nothing published yet.
        private readonly int[] _lastBitmask;

        // Whether we last sent live colours. Read by the keepalive timer, written on
        // SimHub's LED thread.
        private volatile bool _wasActive;

        // Latched while standing down for a dashboard upload.
        private volatile bool _uploadPaused;

        // UTC ticks (0 = never) behind Interlocked.
        private long _lastSendUtcTicks;
        private long _lastLitUtcTicks;
        private const double KeepaliveIntervalSeconds = 1.0;
        // Default hold past the last lit bit when the page has no explicit
        // WheelKeepaliveTimeoutSec, matching the wheel and dash drivers.
        private const int KeepaliveHoldSeconds = 45;

        // Serialises Display() and TickKeepalive() so a replayed bitmask can't land
        // between a strip's colour chunks and the new bitmask that lights them.
        private readonly object _emitLock = new object();

        // Every live driver, so the keepalive timer can reach them.
        private static readonly List<MozaMaskLedDeviceManager> s_instances = new List<MozaMaskLedDeviceManager>();
        private static readonly object s_instancesLock = new object();

        protected MozaMaskLedDeviceManager(int maxStrips)
        {
            _lastBitmask = new int[maxStrips];
            for (int i = 0; i < maxStrips; i++) _lastBitmask[i] = -1;
            lock (s_instancesLock) s_instances.Add(this);
        }

        // ── Subclass surface ──

        /// <summary>Number of strips and LEDs per strip right now.</summary>
        protected abstract int StripCount { get; }
        protected abstract int LedsPerStrip { get; }
        protected abstract LedZone StripZone(int strip);
        /// <summary>Wire layout of a strip on <paramref name="dm"/>, or null if unknown.</summary>
        protected abstract LedZoneLayout? StripLayout(MozaDeviceManager dm, int strip, int ledsPerStrip);
        /// <summary>Pipe the strips are written to (null = not available).</summary>
        protected abstract MozaDeviceManager? Pipe(MozaPlugin plugin);
        /// <summary>Whether live frames may go out now (link up, device in live mode).</summary>
        protected abstract bool LaneReady(MozaPlugin plugin);
        public abstract bool IsConnected();
        public abstract string GetSerialNumber();
        public abstract string GetFirmwareVersion();

        /// <summary>SimHub channel that carries this device's colours.</summary>
        protected virtual Color[] SourceColors(Color[] leds, Color[] buttons) => leds;
        protected virtual double SourceBrightness(double rpmBrightness, double buttonsBrightness) => rpmBrightness;
        /// <summary>Window (ownership) mask published with the lit mask while driving.</summary>
        protected virtual int DriveWindow(int strip) => 0;
        /// <summary>Whether a dashboard upload on the wheelbase link stands this driver down.</summary>
        protected virtual bool HonorsUploadStandDown => true;

        /// <summary>Drive every registered driver's keepalive. Called from the plugin's
        /// LED keepalive timer.</summary>
        internal static void TickKeepaliveAll()
        {
            MozaMaskLedDeviceManager[] snapshot;
            lock (s_instancesLock)
            {
                if (s_instances.Count == 0) return;
                snapshot = s_instances.ToArray();
            }
            foreach (var inst in snapshot)
                inst.TickKeepalive();
        }

        public LedModuleSettings LedModuleSettings { get; set; } = null!;

        public LedDeviceState LastState => _lastState;

        private bool _wasConnected;

        public event EventHandler? BeforeDisplay;
        public event EventHandler? AfterDisplay;
        public event EventHandler? OnConnect;
#pragma warning disable CS0067 // Required by ILedDeviceManager interface
        public event EventHandler? OnError;
#pragma warning restore CS0067
        public event EventHandler? OnDisconnect;

        /// <summary>
        /// Check current detection state and fire OnConnect/OnDisconnect if it changed.
        /// Called from device extension's DataUpdate() every frame.
        /// </summary>
        internal void UpdateConnectionState()
        {
            bool connected = IsConnected();
            if (connected == _wasConnected) return;
            _wasConnected = connected;

            if (connected)
            {
                OnConnect?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                var plugin = MozaPlugin.Instance;
                var leds = plugin != null ? Pipe(plugin)?.Leds : null;
                for (int s = 0; s < _lastBitmask.Length; s++)
                {
                    _lastBitmask[s] = -1;
                    leds?.Drop(StripZone(s));
                }
                _wasActive = false;
                Interlocked.Exchange(ref _lastSendUtcTicks, 0L);
                Interlocked.Exchange(ref _lastLitUtcTicks, 0L);
                OnDisconnect?.Invoke(this, EventArgs.Empty);
            }
        }

        public object GetDriverInstance() => this;

        // Set by SimHub's Close(), cleared by the next Display(). SimHub calls Close()
        // every tick its LED module isn't running and later resumes Display() on this
        // same instance, so Close() pauses the keepalive rather than unregistering.
        private volatile bool _hostClosed;

        public void Close() { _hostClosed = true; }

        /// <summary>Drop this driver from the keepalive registry. Called by the owning
        /// extension's End(), never by SimHub.</summary>
        internal void Release()
        {
            lock (s_instancesLock) s_instances.Remove(this);
        }

        public void ResetDetection() { }

        public void SerialPortCanBeScanned(object sender, SerialDashController.ScanArgs e) { }

        public IPhysicalMapper GetPhysicalMapper() => new NeutralLedsMapper();

        public ILedDriverBase? GetLedDriver() => null;

        /// <summary>
        /// SimHub &lt;= 9.11.x <c>ILedDeviceManager.Display</c>, which has no
        /// <c>overrideState</c> channel. Declared alongside the current overload so one
        /// DLL serves both host generations — the CLR binds an implicitly-implemented
        /// interface method by name and signature at type-load time.
        ///
        /// <c>virtual</c> is load-bearing: the CLR fills an interface slot only from a
        /// public *virtual* method (ECMA-335 II.12.2), and this overload matches no
        /// interface we compile against. Drop it once 9.11.x is unsupported.
        /// </summary>
        public virtual void Display(
            Func<Color[]> leds,
            Func<Color[]> buttons,
            Func<Color[]> encoders,
            Func<Color[]> matrix,
            Func<Color[]> rawState,
            bool forceRefresh,
            Func<object>? extraData = null,
            double rpmBrightness = 1.0,
            double buttonsBrightness = 1.0,
            double encodersBrightness = 1.0,
            double matrixBrightness = 1.0)
            => Display(leds, buttons, encoders, matrix, rawState, SimHubLedCompat.NoOverrides,
                forceRefresh, extraData,
                rpmBrightness, buttonsBrightness, encodersBrightness, matrixBrightness);

        public void Display(
            Func<Color[]> leds,
            Func<Color[]> buttons,
            Func<Color[]> encoders,
            Func<Color[]> matrix,
            Func<Color[]> rawState,
            Func<Color[]> overrideState,
            bool forceRefresh,
            Func<object>? extraData = null,
            double rpmBrightness = 1.0,
            double buttonsBrightness = 1.0,
            double encodersBrightness = 1.0,
            double matrixBrightness = 1.0)
        {
            BeforeDisplay?.Invoke(this, EventArgs.Empty);
            _hostClosed = false;

            bool emitLockHeld = false;

            try
            {
                var ledColors = leds?.Invoke() ?? Array.Empty<Color>();
                var buttonColors = buttons?.Invoke() ?? Array.Empty<Color>();
                var encoderColors = encoders?.Invoke() ?? Array.Empty<Color>();
                var matrixColors = matrix?.Invoke() ?? Array.Empty<Color>();
                var rawColors = rawState?.Invoke() ?? Array.Empty<Color>();
                var overrideColors = overrideState?.Invoke() ?? Array.Empty<Color>();

                _lastState = SimHubLedCompat.CreateState(
                    ledColors, buttonColors, encoderColors, matrixColors, rawColors, overrideColors,
                    rpmBrightness, buttonsBrightness, encodersBrightness, matrixBrightness);

                int strips = StripCount;
                int ledsPerStrip = LedsPerStrip;
                int totalLeds = ledsPerStrip * strips;

                // Merge SimHub's physical-index colour layers (Individual LEDs on
                // rawState, dashboard "Device LEDs override" on overrideState) over
                // the source channel — raw first, then override on top.
                var colors = SourceColors(ledColors, buttonColors);
                if (rawColors.Length > 0)
                    colors = MozaLedDeviceManager.ApplyOverrides(colors, rawColors, 0, totalLeds);
                if (overrideColors.Length > 0)
                    colors = MozaLedDeviceManager.ApplyOverrides(colors, overrideColors, 0, totalLeds);

                var plugin = MozaPlugin.Instance;
                if (plugin == null || !LaneReady(plugin)) return;
                var dm = Pipe(plugin);
                if (dm == null) return;

                // Dashboard upload standing the pipeline down. On resume, drop the
                // change detection: the firmware reclaimed the strips once the 1 Hz
                // bitmask stopped, so an unchanged frame must still be re-sent.
                if (HonorsUploadStandDown && UploadProgressLedBar.IsStandDownActive)
                {
                    _uploadPaused = true;
                    return;
                }
                Monitor.Enter(_emitLock, ref emitLockHeld);

                if (_uploadPaused)
                {
                    _uploadPaused = false;
                    for (int s = 0; s < _lastBitmask.Length; s++)
                    {
                        _lastBitmask[s] = -1;
                        dm.Leds.Invalidate(StripZone(s));
                    }
                    Interlocked.Exchange(ref _lastSendUtcTicks, 0L);
                    _wasActive = false;
                }

                // No colours this frame — one release on the active→idle transition,
                // then quiet so the firmware's standby animation resumes.
                if (colors.Length == 0)
                {
                    if (_wasActive)
                    {
                        for (int s = 0; s < strips; s++)
                        {
                            ReleaseStrip(dm, s, ledsPerStrip);
                            _lastBitmask[s] = 0;
                        }
                        _wasActive = false;
                    }
                    return;
                }

                _wasActive = true;

                double brightness = SourceBrightness(rpmBrightness, buttonsBrightness);
                if (brightness < 0) brightness = 0;
                if (brightness > 1) brightness = 1;

                // A bitmask goes out here only on change; the unchanged refresh is the
                // keepalive timer's job.
                bool sent = false, lit = false;
                for (int s = 0; s < strips; s++)
                {
                    sent |= ProcessStrip(dm, colors, brightness, s, s * ledsPerStrip, ledsPerStrip);
                    lit |= _lastBitmask[s] > 0;
                }
                long nowTicks = DateTime.UtcNow.Ticks;
                if (sent)
                    Interlocked.Exchange(ref _lastSendUtcTicks, nowTicks);
                if (lit)
                    Interlocked.Exchange(ref _lastLitUtcTicks, nowTicks);
            }
            finally
            {
                if (emitLockHeld) Monitor.Exit(_emitLock);
                AfterDisplay?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Re-send each strip's last bitmask at 1 Hz from the LED keepalive timer: while
        /// a game is active, while a strip is lit, or within the hold since the last lit
        /// bit; otherwise the firmware takes the strips back. Nothing after the release.
        /// </summary>
        internal void TickKeepalive()
        {
            if (MozaPlugin.IsShuttingDown || _hostClosed) return;

            var plugin = MozaPlugin.Instance;
            if (plugin == null || !LaneReady(plugin)) return;
            if (!IsConnected()) return;
            if ((HonorsUploadStandDown && UploadProgressLedBar.IsStandDownActive) || _uploadPaused) return;
            if (!_wasActive) return;
            var dm = Pipe(plugin);
            if (dm == null) return;

            long nowTicks = DateTime.UtcNow.Ticks;
            if (!DueAfter(nowTicks, Interlocked.Read(ref _lastSendUtcTicks), KeepaliveIntervalSeconds)) return;

            bool any = false, lit = false;
            for (int s = 0; s < _lastBitmask.Length; s++)
            {
                any |= _lastBitmask[s] >= 0;
                lit |= _lastBitmask[s] > 0;
            }
            if (!any) return;
            int holdSec = plugin.Settings?.WheelKeepaliveTimeoutSec ?? KeepaliveHoldSeconds;
            if (!plugin.IsGameActive && !lit
                && !WithinHold(nowTicks, Interlocked.Read(ref _lastLitUtcTicks), holdSec))
                return;

            if (!Monitor.TryEnter(_emitLock)) return;
            try
            {
                if (!_wasActive) return;
                // Bitmask only. A no-op if the lane lost the strip; Display()
                // republishes it on its next frame.
                for (int s = 0; s < _lastBitmask.Length; s++)
                    if (_lastBitmask[s] >= 0) dm.Leds.RequestRefresh(StripZone(s), colors: false);
                Interlocked.Exchange(ref _lastSendUtcTicks, nowTicks);
            }
            finally
            {
                Monitor.Exit(_emitLock);
            }
        }

        private static bool WithinHold(long nowTicks, long stampTicks, int holdSec)
            => holdSec > 0 && stampTicks != 0
               && (nowTicks - stampTicks) < holdSec * TimeSpan.TicksPerSecond;

        private static bool DueAfter(long nowTicks, long lastTicks, double seconds)
            => lastTicks == 0 || (nowTicks - lastTicks) >= (long)(seconds * TimeSpan.TicksPerSecond);

        // Returns true if this strip's bitmask changed, so the caller can advance the
        // shared keepalive clock.
        private bool ProcessStrip(MozaDeviceManager dm, Color[] colors, double brightness,
            int strip, int sourceOffset, int ledsPerStrip)
        {
            // Pad with black so the bitmask + chunk shape is always strip-complete.
            var stripColors = new Color[ledsPerStrip];
            int available = Math.Max(0, Math.Min(ledsPerStrip, colors.Length - sourceOffset));
            for (int i = 0; i < available; i++)
            {
                var c = colors[sourceOffset + i];
                stripColors[i] = Color.FromArgb(
                    (byte)Math.Round(c.R * brightness),
                    (byte)Math.Round(c.G * brightness),
                    (byte)Math.Round(c.B * brightness));
            }

            // Bit N set = LED N is non-black.
            int bitmask = 0;
            for (int i = 0; i < ledsPerStrip; i++)
            {
                var c = stripColors[i];
                if (c.R > 0 || c.G > 0 || c.B > 0)
                    bitmask |= (1 << i);
            }

            bool bitmaskChanged = bitmask != _lastBitmask[strip];
            var layout = StripLayout(dm, strip, ledsPerStrip);
            if (layout != null)
                dm.Leds.Publish(StripZone(strip), layout,
                    MozaLedDeviceManager.ToRgb(stripColors, ledsPerStrip), bitmask, DriveWindow(strip));
            _lastBitmask[strip] = bitmask;
            return bitmaskChanged;
        }

        // Hand a strip back to the firmware: bitmask 0 (and window 0), no colour write.
        private void ReleaseStrip(MozaDeviceManager dm, int strip, int ledsPerStrip)
        {
            var layout = StripLayout(dm, strip, ledsPerStrip);
            if (layout != null)
                dm.Leds.PublishMaskOnly(StripZone(strip), layout, 0, 0);
        }
    }
}
