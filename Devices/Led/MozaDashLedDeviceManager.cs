using System;
using System.Drawing;
using System.Threading;
using BA63Driver.Interfaces;
using BA63Driver.Mapper;
using MozaPlugin.Protocol;
using SerialDash;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;
using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;

namespace MozaPlugin.Devices.Led
{
    /// <summary>
    /// A virtual ILedDeviceManager for the MOZA CM2/CM1 dashboard.
    /// Always reports as connected to enable SimHub's LED effects UI.
    ///
    /// The rim is a wheel-style 16-LED strip in physical order
    /// [flag 1-3][RPM 1-10][flag 4-6]. SimHub feeds a single telemetry array in
    /// that order; the wire path depends on the meter firmware era
    /// (MozaPlugin.Cm2HasNewLedFirmware, auto-detected from the 0x0E heartbeat):
    ///
    /// Legacy RPM-ramp firmware (meter_diag.c:89):
    ///   RPM  → 10-bit on/off bitmask (dash-send-telemetry, 41 FD DE) + live
    ///          per-LED indicator colours (cm2-indicator-color / dash-rpm-color,
    ///          0B 00). PitHouse drives the bitmask per frame (cm2.pcapng).
    ///   Flag → live 6×RGB colour array (dash-flag-colors, 32 08 00), black = off
    ///          (cm2t.pcapng: flags lit while the bitmask stayed 0).
    ///
    /// 2026-06 indicator firmware (meter_diag.c:88): the ramp registers are gone;
    /// the whole strip is driven live on group 0x32 — colour chunks (13 00) +
    /// windowed active bitmask (14 00, window = full 16-LED strip 0xFFFF so the
    /// flag LEDs, in series with the RPM band, are addressable). Decoded from
    /// cm2(1).pcapng (PitHouse on an updated CM2). See DisplayNewEra.
    ///
    /// Both SimHub LED modes are supported like the wheel manager: combined
    /// (telemetry effects on the `leds` channel) and individual-exclusive (effects
    /// on `rawState`, merged over the array via ApplyOverrides).
    /// </summary>
    internal class MozaDashLedDeviceManager : ILedDeviceManager
    {
        private LedDeviceState _lastState = SimHubLedCompat.CreateState(
            Array.Empty<Color>(), Array.Empty<Color>(), Array.Empty<Color>(),
            Array.Empty<Color>(), Array.Empty<Color>(), Array.Empty<Color>(),
            1.0, 1.0, 1.0, 1.0);

        private int _lastBitmask = -1;

        // Host-drive latch: until SimHub's effect feed produces a non-black LED,
        // send NOTHING on the bitmask/flag lanes — a zero stream overrides the CM2
        // firmware's own autonomous LED ramp (ApplyCm2DashboardConfig) and holds
        // the dash dark. Re-earned per game-active phase and per connection.
        private bool _hostDriveEngaged;
        private bool _prevGameActive;

        // Latched while this pipeline is standing down for a dashboard upload,
        // so the resume edge can drop the change-detection caches.
        private bool _uploadPaused;

        // LED-bitmask keepalive: the dash firmware blanks its LEDs if it doesn't
        // get a fresh dash-send-telemetry frame within a few seconds, even when the
        // value is unchanged. PitHouse re-sends the bitmask every telemetry frame
        // (cm2.pcapng: ~21/s, including FD DE 00000000 when static); we re-send the
        // last bitmask at 1 Hz when nothing changes — same pattern the wheel uses
        // (MozaLedDeviceManager.KeepaliveIntervalSeconds).
        private DateTime _lastSendTime = DateTime.MinValue;
        private const double KeepaliveIntervalSeconds = 1.0;
        // Keep the bitmask keepalive running for this long after the bar last had a
        // lit bit, then pause so the dash can idle/sleep. A brief all-off lull does
        // not drop engagement; only sustained idle lets the stream go quiet.
        private const double KeepaliveHoldSeconds = 45.0;
        private DateTime _lastLitUtc = DateTime.MinValue;

        // Flag-LED keepalive: the 6 CM2 flag LEDs are driven by the live
        // dash-flag-colors array (group 0x32 cmd 08 00, 6×RGB, black = off), NOT
        // the RPM bitmask — verified cm2t.pcapng, where the flags lit green while
        // the 41 14 FD DE bitmask stayed 0. Unlike the RPM bitmask (a latched
        // on/off state), a flag colour is a momentary push that the firmware blanks
        // sub-second if not refreshed — so a 1 Hz keepalive made solid flags blink.
        // PitHouse streams the array at ~12.5 Hz (cm2t.pcapng); we match that while
        // any flag is lit.
        // CM2 rim, wheel-style: 16 LEDs in physical order [flag 1-3][RPM 1-10][flag 4-6].
        private const int RpmLedCount = 10;
        private const int FlagLedCount = 6;
        private const int FlagLeftCount = 3;
        private const int TotalLedCount = RpmLedCount + FlagLedCount; // 16
        private const double FlagKeepaliveIntervalSeconds = 0.08; // ~12.5 Hz, matches PitHouse
        private readonly byte[] _lastFlagRgb = new byte[FlagLedCount * 3];
        private bool _lastFlagPrimed;
        private DateTime _lastFlagSendTime = DateTime.MinValue;

        // RPM LED colour sync: the bitmask only toggles each RPM LED on/off; the
        // colour comes from a device register. We push SimHub's computed gradient
        // to the live indicator register (cm2-indicator-color / dash-rpm-color,
        // 0B 00) so the bar matches SimHub. This is the LIVE register — no throttle,
        // change-gated only. All 10 indices (incl. redline) come from the pipeline.
        // Last-written colour per index, change detection.
        private readonly Color[] _lastRpmColors = new Color[RpmLedCount];
        private bool _rpmColorsPrimed;

        // New-firmware (2026-06 indicator stack) live path: last full-strip colour
        // frame sent as wheel-style group-0 chunks, change detection.
        private readonly Color[] _lastNewColors = new Color[TotalLedCount];
        private bool _newColorsPrimed;

        // Diagnostics (surfaced in the Diagnostics tab): separates "SimHub feeds
        // black" (everLit=no, sends=0) from "colors produced but writes dropped"
        // (everLit=yes, sends>0). Counters are cumulative per driver instance;
        // the host-drive latch resets separately.
        internal static MozaDashLedDeviceManager? Latest { get; private set; }
        private bool _everLit;
        private long _lastNonBlackUtcTicks;
        private long _lastBitmaskSendUtcTicks;
        private int _bitmaskSends, _rpmColorSends, _flagSends;

        public MozaDashLedDeviceManager() { Latest = this; }

        internal (bool Engaged, bool EverLit, long LastNonBlackTicks, int LastBitmask,
                  long LastBitmaskSendTicks, int BitmaskSends, int RpmColorSends, int FlagSends) DiagSnapshot =>
            (_hostDriveEngaged, _everLit, Interlocked.Read(ref _lastNonBlackUtcTicks), _lastBitmask,
             Interlocked.Read(ref _lastBitmaskSendUtcTicks), _bitmaskSends, _rpmColorSends, _flagSends);

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
                _lastBitmask = -1;
                _lastSendTime = DateTime.MinValue;
                _lastLitUtc = DateTime.MinValue;
                _lastFlagPrimed = false;
                _lastFlagSendTime = DateTime.MinValue;
                _rpmColorsPrimed = false;
                _newColorsPrimed = false;
                _hostDriveEngaged = false;
                _prevGameActive = false;
                _uploadPaused = false;
                OnDisconnect?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool IsConnected() => MozaPlugin.Instance?.IsDashDetected ?? false;

        public string GetSerialNumber() => "MOZA-DASH-VIRTUAL";

        public string GetFirmwareVersion() => "1.0";

        public object GetDriverInstance() => this;

        // Clear the diagnostics static so a closed driver (and the SimHub
        // LedModuleSettings it references) isn't pinned for the process
        // lifetime after the dash extension ends.
        public void Close()
        {
            if (ReferenceEquals(Latest, this)) Latest = null;
        }

        public void ResetDetection() { }

        public void SerialPortCanBeScanned(object sender, SerialDashController.ScanArgs e) { }

        public IPhysicalMapper GetPhysicalMapper() => new NeutralLedsMapper();

        public ILedDriverBase? GetLedDriver() => null;

        /// <summary>
        /// SimHub &lt;= 9.11.x <c>ILedDeviceManager.Display</c>, which has no
        /// <c>overrideState</c> channel. Declared alongside the current overload so one
        /// DLL serves both host generations — the CLR binds an implicitly-implemented
        /// interface method by name and signature at type-load time, so each SimHub
        /// build picks the overload its own interface declares. Dead code on 9.12+.
        /// Drop this once 9.11.x is no longer supported (see SimHubLedCompat).
        ///
        /// <c>virtual</c> is load-bearing, not style: the CLR fills an interface slot only
        /// from a public *virtual* method (ECMA-335 II.12.2). Roslyn marks the overload
        /// matching the compile-time interface virtual automatically, but this one matches
        /// no interface we compile against, so without the keyword it stays non-virtual and
        /// 9.11.x still fails type load with the error this whole shim exists to avoid.
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

                // CM2 rim is a wheel-style 16-LED strip in physical order
                // [flag 1-3][RPM 1-10][flag 4-6]. SimHub feeds one telemetry array
                // in that order, driven by two firmware paths:
                //   RPM  → 10-bit on/off bitmask (41 FD DE) + live indicator colours
                //          (0B 00); the bitmask lights each set bit in its colour.
                //   Flag → live 6×RGB colour array (32 08 00), black = off; flags do
                //          NOT use the bitmask (cm2t.pcapng: flags lit, bitmask 0).
                //
                // Combined AND individual modes are handled like the wheel LED
                // manager: in SimHub's "Individual LEDs (exclusive)" mode the `leds`
                // channel is empty and only `rawState` carries colour, so we merge
                // rawState over the telemetry array first (ApplyOverrides — a no-op
                // in combined mode), then overrideState (dashboard "Device LEDs
                // override" components) on top, and then process the single merged
                // array by position.

                var plugin = MozaPlugin.Instance;
                if (plugin == null || !plugin.Data.IsConnected || !plugin.IsDashDetected)
                    return;

                // Dashboard upload standing the pipeline down (see the same
                // guard, and why it is not the raw in-flight flag, in
                // MozaLedDeviceManager). On resume, drop the change-detection
                // caches: the dash firmware blanked its strip once the keepalive
                // stopped, so an unchanged frame must still be re-sent.
                if (UploadProgressLedBar.IsStandDownActive)
                {
                    _uploadPaused = true;
                    return;
                }
                if (_uploadPaused)
                {
                    _uploadPaused = false;
                    _lastBitmask = -1;
                    _lastFlagPrimed = false;
                    _rpmColorsPrimed = false;
                    _newColorsPrimed = false;
                    _lastSendTime = DateTime.MinValue;
                    _lastFlagSendTime = DateTime.MinValue;
                }

                if (rawColors.Length > 0)
                    ledColors = MozaLedDeviceManager.ApplyOverrides(ledColors, rawColors, 0, TotalLedCount);
                if (overrideColors.Length > 0)
                    ledColors = MozaLedDeviceManager.ApplyOverrides(ledColors, overrideColors, 0, TotalLedCount);

                if (ledColors.Length == 0)
                    return;

                var now = DateTime.UtcNow;
                bool gameActive = plugin.IsGameActive;

                // Host-drive latch maintenance: re-earned per game-active phase,
                // level-triggered on any non-black LED so feed stalls self-heal.
                if (gameActive != _prevGameActive)
                {
                    _prevGameActive = gameActive;
                    _hostDriveEngaged = false;
                }
                int scanLen = Math.Min(ledColors.Length, TotalLedCount);
                for (int i = 0; i < scanLen; i++)
                {
                    var c = ledColors[i];
                    if (c.R > 0 || c.G > 0 || c.B > 0)
                    {
                        _hostDriveEngaged = true;
                        _everLit = true;
                        Interlocked.Exchange(ref _lastNonBlackUtcTicks, now.Ticks);
                        break;
                    }
                }

                // 2026-06 "indicator" meter firmware: the autonomous RPM ramp and
                // the legacy 41 FD DE / 32 0B / 32 08 live registers are gone —
                // drive the 16-LED strip like a new-protocol wheel rim instead:
                // group-0 live colour chunks (3F 19 00) + windowed active bitmask
                // (3F 1A 00) addressed to the CM2. Best-effort mirror of the
                // wheel wire pending a PitHouse capture of this firmware.
                if (plugin.Cm2HasNewLedFirmware)
                {
                    DisplayNewEra(plugin, ledColors, gameActive, now);
                    return;
                }

                // Split flag/RPM/flag by position. Full 16-LED array → flags at
                // [0,1,2] + [13,14,15], RPM at [3..12]. A short array (pre-detection
                // / transition) → no flags, RPM from index 0.
                bool hasFlags = ledColors.Length >= TotalLedCount;
                int flagLeft = hasFlags ? FlagLeftCount : 0;

                var rpmColors = new Color[RpmLedCount];
                int rpmAvail = Math.Max(0, Math.Min(RpmLedCount, ledColors.Length - flagLeft));
                for (int i = 0; i < rpmAvail; i++)
                    rpmColors[i] = ledColors[flagLeft + i];

                // ── RPM colours → live indicator register (0B 00), before the bitmask
                // so an LED is never lit a frame before its colour lands. Change-gated;
                // live register, no throttle. ──
                SyncRpmColors(plugin, rpmColors);

                // ── RPM bar: 10-bit on/off bitmask. PitHouse re-sends per frame
                // (cm2.pcapng); we send on change + 1 Hz keepalive so a held value
                // doesn't blank. ──
                int bitmask = 0;
                for (int i = 0; i < RpmLedCount; i++)
                {
                    if (rpmColors[i].R > 0 || rpmColors[i].G > 0 || rpmColors[i].B > 0)
                        bitmask |= (1 << i);
                }
                bool bitmaskChanged = bitmask != _lastBitmask;
                if (bitmask != 0) _lastLitUtc = now;
                int holdSec = plugin.Settings?.WheelKeepaliveTimeoutSec ?? (int)KeepaliveHoldSeconds;
                bool withinHold = (now - _lastLitUtc).TotalSeconds < holdSec;
                bool keepaliveDue = (now - _lastSendTime).TotalSeconds >= KeepaliveIntervalSeconds;
                // Until the host-drive latch engages, send nothing — the firmware's
                // autonomous ramp owns the LEDs and a zero mask would blank it.
                // Once engaged: while a game is actively feeding telemetry, NEVER
                // pause the keepalive — the dash must stay live for the whole
                // session and only idle once the game is closed. Otherwise resend
                // on change always, and hold the keepalive / always-resend for
                // holdSec after the bar last had a lit bit, then pause. A 1 Hz
                // all-off resend pins the dash in live-render mode and blocks its
                // idle/sleep — the same fix applied to the wheel keepalive.
                if (_hostDriveEngaged
                    && (bitmaskChanged || ((gameActive || withinHold) && keepaliveDue)))
                {
                    _lastBitmask = bitmask;
                    _lastSendTime = now;
                    plugin.HardwareApplier.WriteDashLedBitmask(bitmask);
                    _bitmaskSends++;
                    Interlocked.Exchange(ref _lastBitmaskSendUtcTicks, now.Ticks);
                }

                // ── Flag LEDs: live 6×RGB colour array (32 08 00), black = off.
                // Flags 0-2 are the left block (LEDs 1-3), 3-5 the right (LEDs 14-16).
                // Send on change + ~12.5 Hz keepalive while any flag is lit (matches
                // PitHouse; a slower 1 Hz refresh made solid flags blink). ──
                var rgb = new byte[FlagLedCount * 3];
                bool anyFlagOn = false;
                if (hasFlags)
                {
                    for (int i = 0; i < FlagLedCount; i++)
                    {
                        int srcIdx = i < FlagLeftCount ? i : RpmLedCount + i; // 0,1,2, 13,14,15
                        var c = ledColors[srcIdx];
                        rgb[i * 3] = c.R;
                        rgb[i * 3 + 1] = c.G;
                        rgb[i * 3 + 2] = c.B;
                        if (c.R > 0 || c.G > 0 || c.B > 0) anyFlagOn = true;
                    }
                }

                bool flagsChanged = !_lastFlagPrimed;
                if (!flagsChanged)
                {
                    for (int i = 0; i < rgb.Length; i++)
                        if (rgb[i] != _lastFlagRgb[i]) { flagsChanged = true; break; }
                }
                bool flagKeepaliveDue = anyFlagOn
                    && (now - _lastFlagSendTime).TotalSeconds >= FlagKeepaliveIntervalSeconds;
                // anyFlagOn gates the keepalive: a fully-off flag array is sent once
                // via flagsChanged, then left quiet so the dash can idle instead of
                // being held awake by all-black flag refreshes.
                if (_hostDriveEngaged && hasFlags
                    && (flagsChanged || (flagKeepaliveDue && anyFlagOn)))
                {
                    Array.Copy(rgb, _lastFlagRgb, rgb.Length);
                    _lastFlagPrimed = true;
                    _lastFlagSendTime = now;
                    plugin.HardwareApplier.WriteDashFlagColors(rgb);
                    _flagSends++;
                }

                // Dashboard brightness is stored config (set via plugin UI slider →
                // ApplySavedDashSettings on connect). Don't forward SimHub's per-frame
                // rpmBrightness here — SimHub passes 0 during scene transitions / no-game
                // states, which would blank the dashboard. SimHub brightness applies to
                // wheel RPM + button LEDs only.
            }
            finally
            {
                AfterDisplay?.Invoke(this, EventArgs.Empty);
            }
        }

        // New-firmware live window = the full 16-LED strip. The CM2 rim is one
        // series strip [flag 1-3][RPM 1-10][flag 4-6]; on this firmware an LED
        // lights only when its bit is set in the active mask WITHIN the window, so
        // the window must span all 16 for the flag LEDs (0-2, 13-15) to be
        // addressable at all. PitHouse's cm2(1).pcapng fixes the window at the RPM
        // band 0x1FF8, but that capture never drives a flag LED (its colour chunks
        // touch only indices 3-12) — it is no evidence that flags light by colour
        // alone. Confirmed on hardware: colour without an active bit stays dark.
        private const int NewEraStripWindow = (1 << TotalLedCount) - 1; // 0xFFFF, 16 LEDs

        /// <summary>
        /// 2026-06 indicator-firmware live path (decoded from cm2(1).pcapng): the
        /// whole 16-LED strip as group-0x32 live colour chunks (13 00) plus a
        /// windowed active bitmask (14 00, window = full 16-LED strip). Colours
        /// before the mask, send-on-change + keepalive while game-active / within
        /// hold, gated on the host-drive latch like every other path.
        /// </summary>
        private void DisplayNewEra(MozaPlugin plugin, Color[] ledColors, bool gameActive, DateTime now)
        {
            if (!_hostDriveEngaged) return;
            int count = Math.Min(ledColors.Length, TotalLedCount);
            if (count == 0) return;

            int fullMask = 0;
            bool colorsChanged = !_newColorsPrimed;
            for (int i = 0; i < count; i++)
            {
                var c = ledColors[i];
                if (c.R > 0 || c.G > 0 || c.B > 0) fullMask |= (1 << i);
                if (!colorsChanged
                    && (_lastNewColors[i].R != c.R || _lastNewColors[i].G != c.G || _lastNewColors[i].B != c.B))
                    colorsChanged = true;
            }
            int active = fullMask & NewEraStripWindow;

            bool bitmaskChanged = active != _lastBitmask;
            if (active != 0) _lastLitUtc = now;
            int holdSec = plugin.Settings?.WheelKeepaliveTimeoutSec ?? (int)KeepaliveHoldSeconds;
            bool withinHold = (now - _lastLitUtc).TotalSeconds < holdSec;
            bool keepaliveDue = (now - _lastSendTime).TotalSeconds >= KeepaliveIntervalSeconds;
            bool cadence = (gameActive || withinHold) && keepaliveDue;
            if (!colorsChanged && !bitmaskChanged && !cadence) return;

            if (colorsChanged || cadence)
            {
                for (int i = 0; i < count; i++) _lastNewColors[i] = ledColors[i];
                _newColorsPrimed = true;
                SendNewEraColorChunks(plugin, ledColors, count);
            }
            _lastBitmask = active;
            _lastSendTime = now;
            plugin.HardwareApplier.WriteCm2LiveLedBitmask(
                MozaLedDeviceManager.BuildWindowedBitmaskBytes(active, NewEraStripWindow));
            _bitmaskSends++;
            Interlocked.Exchange(ref _lastBitmaskSendUtcTicks, now.Ticks);
        }

        /// <summary>Variable-length 5-LED colour chunks (idx,R,G,B records, last
        /// chunk short — byte-for-byte as PitHouse in cm2(1).pcapng), one coalescing
        /// slot per chunk. All 16 LEDs sent; the bitmask window spans the full strip
        /// so flag colours light alongside their active bits.</summary>
        private void SendNewEraColorChunks(MozaPlugin plugin, Color[] colors, int count)
        {
            const int ledsPerChunk = 5;
            int chunkIdx = 0;
            for (int start = 0; start < count; start += ledsPerChunk)
            {
                int n = Math.Min(ledsPerChunk, count - start);
                var chunk = new byte[n * 4];
                for (int j = 0; j < n; j++)
                {
                    var c = colors[start + j];
                    chunk[j * 4] = (byte)(start + j);
                    chunk[j * 4 + 1] = c.R;
                    chunk[j * 4 + 2] = c.G;
                    chunk[j * 4 + 3] = c.B;
                }
                plugin.HardwareApplier.WriteCm2LiveLedColorChunk(chunk, chunkIdx);
                _rpmColorSends++;
                chunkIdx++;
            }
        }

        /// <summary>
        /// Push the 10 RPM colours (already extracted from the flag/RPM/flag array)
        /// to the dash's live indicator-colour register (cm2-indicator-color /
        /// dash-rpm-color, wire 0B 00), change-gated. Each index takes the latest
        /// non-black colour — an off LED keeps its last colour, since the bitmask
        /// handles on/off. No throttle: this is the live, non-persistent register.
        /// </summary>
        private void SyncRpmColors(MozaPlugin plugin, Color[] rpmColors)
        {
            for (int i = 0; i < RpmLedCount && i < rpmColors.Length; i++)
            {
                var c = rpmColors[i];
                // Off this frame — keep the last known indicator colour.
                if (c.R == 0 && c.G == 0 && c.B == 0) continue;

                if (!_rpmColorsPrimed
                    || c.R != _lastRpmColors[i].R
                    || c.G != _lastRpmColors[i].G
                    || c.B != _lastRpmColors[i].B)
                {
                    _lastRpmColors[i] = c;
                    plugin.HardwareApplier.WriteDashRpmColor(i, c.R, c.G, c.B);
                    _rpmColorSends++;
                }
            }
            _rpmColorsPrimed = true;
        }
    }
}
