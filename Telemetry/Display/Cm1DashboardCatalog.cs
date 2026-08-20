using System.Collections.Generic;

namespace MozaPlugin.Telemetry.Display
{
    /// <summary>
    /// One field in the CM1 base-bridged dash's group-0x35 value stream. The CM1 does
    /// NOT use positional records like the FSR1 — it is a flat keyed stream: each field
    /// is addressed by a 16-bit <see cref="Key"/> and carries a big-endian float32 value.
    /// There is no per-dashboard layer: the same flat set streams regardless of which
    /// built-in dashboard is selected (the switch changes what the dash *displays*).
    /// </summary>
    internal sealed class Cm1FieldDef
    {
        /// <summary>The 16-bit field key in wire order (e.g. {0xF5,0x4D}). High byte is
        /// part of the key, NOT a type tag — every value is a big-endian float32.</summary>
        public byte[] Key = System.Array.Empty<byte>();

        /// <summary>Stable id for settings/UI keying: the key as 4 hex chars ("f54d").</summary>
        public string FieldId = "";

        public string Label = "";

        /// <summary>Default SimHub property (empty = unmapped; user assigns via the
        /// channel mapper). Sourced from MOZA's channel catalog (see class remark).</summary>
        public string DefaultProperty = "";

        /// <summary>Output = resolved SimHub value × Scale + Bias (then float32 BE). Default 1/0.
        /// The °F unit-variant keys use Scale 1.8, Bias 32 (°C→°F).</summary>
        public double Scale = 1.0;
        public double Bias = 0.0;

        /// <summary>When set and the field is unmapped, stream this constant verbatim.
        /// null = stream 0 when unmapped.</summary>
        public double? Constant = null;

        /// <summary>True when the field's channel is known.</summary>
        public bool Decoded = false;

        /// <summary>Slow-lane field: streamed on group 0x36 at ~3 Hz (session/slow-changing
        /// data — lap counters, positions, fuel, track/air temp), matching PitHouse's split.
        /// False = fast lane (group 0x35, full rate).</summary>
        public bool Slow = false;

        public bool IsUserMappable => true;
    }

    /// <summary>
    /// The CM1 dash field catalog — the field universe streamed on group-0x35 (dev 0x14).
    /// KEYS + the big-endian float32 encoding are proven from <c>FSR1_CM1.pcapng</c> via
    /// <c>tools/cm1-0x35-decode</c>. Each 16-bit key corresponds to a MOZA telemetry channel
    /// (a <c>v1/gameData/&lt;path&gt;</c>); the key's low 15 bits are the channel's catalog index
    /// and the top bit is set on the wire. Each channel is mapped to its SimHub property via
    /// <c>Data/Telemetry.json</c> (MOZA's own channel catalog). Values are raw physical quantities
    /// as float32 (no bit-packing / bias artifacts — the FSR1's +300 / ×1000 do not apply here);
    /// the °F unit-variant keys convert °C→°F (Scale 1.8, Bias 32).
    /// See docs/protocol/devices/wheel-0x17.md (CM1 group-0x35).
    /// </summary>
    internal static class Cm1DashboardCatalog
    {
        private static Cm1FieldDef F(byte hi, byte lo, string label, bool decoded = false,
                                     string prop = "", double scale = 1.0, double bias = 0.0,
                                     double? constant = null, bool slow = false) => new()
        {
            Key = new[] { hi, lo },
            FieldId = $"{hi:x2}{lo:x2}",
            Label = label,
            DefaultProperty = prop,
            Scale = scale,
            Bias = bias,
            Constant = constant,
            Decoded = decoded,
            Slow = slow,
        };

        /// <summary>
        /// The flat field set the CM1 subscribes to (the 67 keys observed streaming in
        /// FSR1_CM1.pcapng), each mapped to its MOZA telemetry channel. Wire encoding is
        /// float32 big-endian; meaning mirrors the FSR1 channel set. Two keys have no generic
        /// SimHub channel (a track-name string and a kPa-unit pressure) and ship unmapped for
        /// the user to assign.
        /// </summary>
        public static readonly Cm1FieldDef[] Fields =
        {
            F(0xd9, 0xe2, "DRS", decoded: true, prop: "DataCorePlugin.GameData.DRSEnabled"),
            F(0xd9, 0xe4, "Current Lap Time", decoded: true, prop: "DataCorePlugin.GameData.CurrentLapTime"),
            F(0xda, 0x3d, "Brake Temp FL", decoded: true, prop: "DataCorePlugin.GameData.BrakeTemperatureFrontLeft"),
            F(0xda, 0x3e, "Brake Temp FR", decoded: true, prop: "DataCorePlugin.GameData.BrakeTemperatureFrontRight"),
            F(0xda, 0x3f, "Brake Temp RL", decoded: true, prop: "DataCorePlugin.GameData.BrakeTemperatureRearLeft"),
            F(0xda, 0x40, "Brake Temp RR", decoded: true, prop: "DataCorePlugin.GameData.BrakeTemperatureRearRight"),
            F(0xda, 0x41, "Track Temp", decoded: true, prop: "DataCorePlugin.GameData.TrackTemp", slow: true),
            F(0xda, 0x42, "Air Temp", decoded: true, prop: "DataCorePlugin.GameData.AirTemp", slow: true),
            F(0xda, 0x5d, "ABS Level", decoded: true, prop: "DataCorePlugin.GameData.ABSLevel"),
            F(0xda, 0x5e, "TC Level", decoded: true, prop: "DataCorePlugin.GameData.TCLevel"),
            F(0xda, 0x67, "Engine Enabled", decoded: true, prop: "DataCorePlugin.GameData.EngineEnabled"),
            F(0xda, 0x68, "Left Blinker", decoded: true, prop: "DataCorePlugin.GameData.TurnIndicatorLeft"),
            F(0xda, 0x69, "Right Blinker", decoded: true, prop: "DataCorePlugin.GameData.TurnIndicatorRight"),
            F(0xda, 0x82, "Fuel Range", decoded: true, prop: "DataCorePlugin.GameData.FuelRange", slow: true),
            F(0xda, 0x8f, "Oil Pressure", decoded: true, prop: "DataCorePlugin.GameData.OilPressure", slow: true),
            F(0xda, 0x93, "Fuel Remains", decoded: true, prop: "DataCorePlugin.GameData.Fuel", slow: true),
            F(0xda, 0xa1, "Brake Temp FL (°F)", decoded: true, prop: "DataCorePlugin.GameData.BrakeTemperatureFrontLeft", scale: 1.8, bias: 32.0),
            F(0xda, 0xa2, "Brake Temp FR (°F)", decoded: true, prop: "DataCorePlugin.GameData.BrakeTemperatureFrontRight", scale: 1.8, bias: 32.0),
            F(0xda, 0xa3, "Brake Temp RL (°F)", decoded: true, prop: "DataCorePlugin.GameData.BrakeTemperatureRearLeft", scale: 1.8, bias: 32.0),
            F(0xda, 0xa4, "Brake Temp RR (°F)", decoded: true, prop: "DataCorePlugin.GameData.BrakeTemperatureRearRight", scale: 1.8, bias: 32.0),
            F(0xda, 0xa5, "Track Temp (°F)", decoded: true, prop: "DataCorePlugin.GameData.TrackTemp", scale: 1.8, bias: 32.0, slow: true),
            F(0xda, 0xa6, "Air Temp (°F)", decoded: true, prop: "DataCorePlugin.GameData.AirTemp", scale: 1.8, bias: 32.0, slow: true),
            F(0xf5, 0x30, "Speed (mph)", decoded: true, prop: "DataCorePlugin.GameData.SpeedMph"),
            F(0xf5, 0x31, "Speed (km/h)", decoded: true, prop: "DataCorePlugin.GameData.SpeedKmh"),
            F(0xf5, 0x32, "Speed (m/s)", decoded: true, prop: "DataCorePlugin.GameData.SpeedMs"),
            F(0xf5, 0x33, "RPM", decoded: true, prop: "DataCorePlugin.GameData.Rpms"),
            F(0xf5, 0x34, "Max RPM", decoded: true, prop: "DataCorePlugin.GameData.MaxRpm"),
            F(0xf5, 0x35, "Gear", decoded: true, prop: "DataCorePlugin.GameData.Gear"),
            F(0xf5, 0x36, "Current Lap", decoded: true, prop: "DataCorePlugin.GameData.CurrentLap", slow: true),
            F(0xf5, 0x37, "Current Pos", decoded: true, prop: "DataCorePlugin.GameData.Position", slow: true),
            F(0xf5, 0x38, "Current Car Count", decoded: true, prop: "DataCorePlugin.GameData.OpponentsCount", slow: true),
            F(0xf5, 0x39, "Last Lap Time", decoded: true, prop: "DataCorePlugin.GameData.LastLapTime", slow: true),
            F(0xf5, 0x3a, "Best Lap Time", decoded: true, prop: "DataCorePlugin.GameData.BestLapTime", slow: true),
            F(0xf5, 0x3b, "Fuel Remainder", decoded: true, prop: "DataCorePlugin.GameData.FuelPercent", slow: true),
            F(0xf5, 0x3d, "Tyre Temp FL", decoded: true, prop: "DataCorePlugin.GameData.TyreTemperatureFrontLeft"),
            F(0xf5, 0x3e, "Tyre Temp Front Left Inner", decoded: true, prop: "DataCorePlugin.GameData.TyreTempFrontLeftInner"),
            F(0xf5, 0x40, "Tyre Temp Front Left Outer", decoded: true, prop: "DataCorePlugin.GameData.TyreTempFrontLeftOuter"),
            F(0xf5, 0x41, "Tyre Temp FR", decoded: true, prop: "DataCorePlugin.GameData.TyreTemperatureFrontRight"),
            F(0xf5, 0x42, "Tyre Temp Front Right Inner", decoded: true, prop: "DataCorePlugin.GameData.TyreTempFrontRightInner"),
            F(0xf5, 0x44, "Tyre Temp Front Right Outer", decoded: true, prop: "DataCorePlugin.GameData.TyreTempFrontRightOuter"),
            F(0xf5, 0x45, "Tyre Temp RL", decoded: true, prop: "DataCorePlugin.GameData.TyreTemperatureRearLeft"),
            F(0xf5, 0x46, "Tyre Temp Rear Left Inner", decoded: true, prop: "DataCorePlugin.GameData.TyreTempRearLeftInner"),
            F(0xf5, 0x48, "Tyre Temp Rear Left Outer", decoded: true, prop: "DataCorePlugin.GameData.TyreTempRearLeftOuter"),
            F(0xf5, 0x49, "Tyre Temp RR", decoded: true, prop: "DataCorePlugin.GameData.TyreTemperatureRearRight"),
            F(0xf5, 0x4a, "Tyre Temp Rear Right Inner", decoded: true, prop: "DataCorePlugin.GameData.TyreTempRearRightInner"),
            F(0xf5, 0x4c, "Tyre Temp Rear Right Outer", decoded: true, prop: "DataCorePlugin.GameData.TyreTempRearRightOuter"),
            F(0xf5, 0x4d, "Tyre Pressure FL", decoded: true, prop: "DataCorePlugin.GameData.TyrePressureFrontLeft"),
            F(0xf5, 0x4e, "Tyre Pressure FR", decoded: true, prop: "DataCorePlugin.GameData.TyrePressureFrontRight"),
            F(0xf5, 0x4f, "Tyre Pressure RL", decoded: true, prop: "DataCorePlugin.GameData.TyrePressureRearLeft"),
            F(0xf5, 0x50, "Tyre Pressure RR", decoded: true, prop: "DataCorePlugin.GameData.TyrePressureRearRight"),
            F(0xf5, 0x54, "Estimated Lap Time", decoded: true, prop: "DataCorePlugin.GameData.EstimatedLapTime"),
            F(0xf5, 0x5d, "Throttle", decoded: true, prop: "DataCorePlugin.GameData.Throttle"),
            F(0xf5, 0x5e, "Brake", decoded: true, prop: "DataCorePlugin.GameData.Brake"),
            F(0xf5, 0x5f, "Clutch", decoded: true, prop: "DataCorePlugin.GameData.Clutch"),
            F(0xf5, 0x62, "Current Lap Count", decoded: true, prop: "DataCorePlugin.GameData.TotalLaps", slow: true),
            F(0xf5, 0xa1, "Brakes Temperature Min (°F)", decoded: true, prop: "DataCorePlugin.GameData.BrakesTemperatureMin", scale: 1.8, bias: 32.0),
            F(0xf5, 0xa2, "Session Odo (mi)", decoded: true, prop: "DataCorePlugin.GameData.SessionOdo"),
            F(0xf5, 0xa4, "Stint Odo (mi)", decoded: true, prop: "DataCorePlugin.GameData.StintOdo"),
            F(0xf5, 0xa5, "Tyres Temperature Avg (°F)", decoded: true, prop: "DataCorePlugin.GameData.TyresTemperatureAvg", scale: 1.8, bias: 32.0),
            F(0xf5, 0xa6, "Tyres Temperature Max (°F)", decoded: true, prop: "DataCorePlugin.GameData.TyresTemperatureMax", scale: 1.8, bias: 32.0),
            F(0xf5, 0xa8, "Tyre Temp Front Right Outer (°F)", decoded: true, prop: "DataCorePlugin.GameData.TyreTempFrontRightOuter", scale: 1.8, bias: 32.0),
            F(0xf5, 0xa9, "Track Name With Config"),
            F(0xf5, 0xaa, "Front ARB", decoded: true, prop: "DataCorePlugin.GameData.FrontARB"),
            F(0xf5, 0xac, "Throttle Shaping", decoded: true, prop: "DataCorePlugin.GameData.ThrottleShaping"),
            F(0xf5, 0xad, "Tyre Pressure RR (kPa)"),
            F(0xf5, 0xae, "Tyre Temp Rear Right Inner (°F)", decoded: true, prop: "DataCorePlugin.GameData.TyreTempRearRightInner", scale: 1.8, bias: 32.0),
            F(0xf5, 0xb0, "Tyre Temp Rear Right Outer (°F)", decoded: true, prop: "DataCorePlugin.GameData.TyreTempRearRightOuter", scale: 1.8, bias: 32.0),
        };

        /// <summary>Dashboard pages selectable via the 0x32/0x81 switch command. The
        /// capture exercised indices 1..13 (1-based). Refine the count on hardware.</summary>
        public const int MinDashboardIndex = 1;
        public const int MaxDashboardIndex = 13;

        private static readonly Dictionary<string, Cm1FieldDef> _byId = BuildIndex();

        private static Dictionary<string, Cm1FieldDef> BuildIndex()
        {
            var d = new Dictionary<string, Cm1FieldDef>();
            foreach (var f in Fields) d[f.FieldId] = f;
            return d;
        }

        public static Cm1FieldDef? ByFieldId(string fieldId) =>
            fieldId != null && _byId.TryGetValue(fieldId, out var f) ? f : null;
    }
}
