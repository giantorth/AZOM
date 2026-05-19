using System;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// User-assigned role for a single mBooster's analog pedal axis. The HID
    /// reader routes the device's position into <c>MozaData.{Throttle,Brake,Clutch}Position</c>
    /// based on this setting. <see cref="Disabled"/> means the device's position
    /// is ignored entirely (motor effects can still fire).
    /// </summary>
    public enum MBoosterRole
    {
        Disabled = 0,
        Throttle = 1,
        Brake    = 2,
        Clutch   = 3,
    }

    /// <summary>
    /// Per-effect knobs the user can tweak. Frequency is computed at runtime
    /// per protocol note § 4 telemetry pseudocode — only enable + intensity
    /// are surfaced in the UI. Each effect's user 0..100 % maps to scale
    /// 0..ScaleMax at apply time (protocol note § 4 suggested defaults:
    /// ABS / Threshold = 0.10, Lockup = 0.15, Engine = 0.10 — engine runs
    /// continuously and would dominate the others without this cap). The
    /// caps live on <c>MBoosterEffectWorker</c>.
    /// </summary>
    public sealed class MBoosterEffectSettings
    {
        public bool Enabled { get; set; } = false;
        public int IntensityPct { get; set; } = 50; // 0..100

        public MBoosterEffectSettings Clone() =>
            new MBoosterEffectSettings { Enabled = Enabled, IntensityPct = IntensityPct };
    }

    /// <summary>
    /// All settings for ONE mBooster device, keyed by stable identity (USB
    /// instance ID) in the profile's per-device dictionary. Each effect has
    /// its own enable + intensity. Calibration values (group 35/36 — marked
    /// "likely but unverified" in the protocol note) are stored separately.
    /// </summary>
    public sealed class MBoosterDeviceSettings
    {
        public MBoosterRole Role { get; set; } = MBoosterRole.Disabled;

        public MBoosterEffectSettings Abs       { get; set; } = new MBoosterEffectSettings();
        public MBoosterEffectSettings Lockup    { get; set; } = new MBoosterEffectSettings();
        public MBoosterEffectSettings Threshold { get; set; } = new MBoosterEffectSettings();
        public MBoosterEffectSettings Engine    { get; set; } = new MBoosterEffectSettings { IntensityPct = 50 };

        // Calibration (experimental per protocol note § 6). -1 = "not yet
        // read / no override"; the worker treats -1 as "do not write".
        // The user has been warned in-UI that these commands may not be
        // acknowledged by mBooster firmware.
        public int Direction { get; set; } = -1;
        public int Min { get; set; } = -1;
        public int Max { get; set; } = -1;
        public float[]? CurveY { get; set; } = null;   // 5-point output curve

        // Friendly display label the user can edit (defaults to "mBooster"
        // with a serial-tail fallback). Survives reconnects with the dict key.
        public string DisplayName { get; set; } = "";

        public MBoosterDeviceSettings Clone()
        {
            return new MBoosterDeviceSettings
            {
                Role = Role,
                Abs = Abs?.Clone() ?? new MBoosterEffectSettings(),
                Lockup = Lockup?.Clone() ?? new MBoosterEffectSettings(),
                Threshold = Threshold?.Clone() ?? new MBoosterEffectSettings(),
                Engine = Engine?.Clone() ?? new MBoosterEffectSettings(),
                Direction = Direction,
                Min = Min,
                Max = Max,
                CurveY = CurveY == null ? null : (float[])CurveY.Clone(),
                DisplayName = DisplayName,
            };
        }
    }

    /// <summary>
    /// Subset of game telemetry the mBooster effect worker reads each tick.
    /// Populated from SimHub's data in <c>MozaPlugin.DataUpdate</c> and
    /// published via <c>MozaMBoosterRegistry.OnDataUpdate</c>.
    /// All speed fields are in m/s for consistency (the protocol note's
    /// "unit gotcha" warns about mixing m/s and km/h — we normalise here).
    /// </summary>
    public readonly struct MBoosterTelemetrySnapshot
    {
        public readonly bool   GameRunning;
        public readonly double Rpm;
        public readonly double IdleRpm;
        public readonly double Brake;        // 0..1
        public readonly bool   AbsActive;
        public readonly double VehicleSpeedMs;
        public readonly double AvgWheelSpeedMs;

        public MBoosterTelemetrySnapshot(
            bool gameRunning, double rpm, double idleRpm, double brake, bool absActive,
            double vehicleSpeedMs, double avgWheelSpeedMs)
        {
            GameRunning = gameRunning;
            Rpm = rpm;
            IdleRpm = idleRpm;
            Brake = brake;
            AbsActive = absActive;
            VehicleSpeedMs = vehicleSpeedMs;
            AvgWheelSpeedMs = avgWheelSpeedMs;
        }

        public static readonly MBoosterTelemetrySnapshot Empty =
            new MBoosterTelemetrySnapshot(false, 0, 800, 0, false, 0, 0);
    }
}
