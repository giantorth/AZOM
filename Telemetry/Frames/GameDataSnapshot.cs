using System;
using GameReaderCommon;
using MozaPlugin.Telemetry.Dashboard;

namespace MozaPlugin.Telemetry.Frames
{
    /// <summary>
    /// A flat snapshot of the game data fields used by the telemetry sender.
    /// Decouples TelemetryFrameBuilder from the SimHub StatusDataBase API,
    /// enabling test patterns and unit testing without live game data.
    /// </summary>
    public struct GameDataSnapshot
    {
        public double SpeedKmh;
        public double Rpms;
        public double Gear;         // 0=R/N, 1=1st, 2=2nd, …
        public double Throttle;     // 0.0–1.0 (converted from SimHub's 0–100)
        public double Brake;        // 0.0–1.0 (converted from SimHub's 0–100)
        public double BestLapTimeSeconds;
        public double CurrentLapTimeSeconds;
        public double LastLapTimeSeconds;
        public double DeltaToSessionBest;
        public double FuelPercent;  // 0–100
        public double DrsEnabled;   // 0 or 1
        public double ErsPercent;   // 0–100
        public double TyreWearFrontLeft;
        public double TyreWearFrontRight;
        public double TyreWearRearLeft;
        public double TyreWearRearRight;
        public double CurrentLap;       // lap counter (1+)

        /// <summary>Populate from a live StatusDataBase instance.</summary>
        public static GameDataSnapshot FromStatusData(StatusDataBase? data)
        {
            if (data == null) return default;
            return new GameDataSnapshot
            {
                SpeedKmh               = data.SpeedKmh,
                Rpms                   = data.Rpms,
                Gear                   = ParseGear(data.Gear),
                Throttle               = data.Throttle / 100.0,  // SimHub 0–100 → float_001 expects 0.0–1.0
                Brake                  = data.Brake    / 100.0,  // SimHub 0–100 → float_001 expects 0.0–1.0
                BestLapTimeSeconds     = data.BestLapTime.TotalSeconds,
                CurrentLapTimeSeconds  = data.CurrentLapTime.TotalSeconds,
                LastLapTimeSeconds     = data.LastLapTime.TotalSeconds,
                DeltaToSessionBest     = data.DeltaToSessionBest ?? 0.0,
                FuelPercent            = data.FuelPercent,
                DrsEnabled             = data.DRSEnabled != 0 ? 1.0 : 0.0,
                ErsPercent             = data.ERSPercent,
                TyreWearFrontLeft      = data.TyreWearFrontLeft,
                TyreWearFrontRight     = data.TyreWearFrontRight,
                TyreWearRearLeft       = data.TyreWearRearLeft,
                TyreWearRearRight      = data.TyreWearRearRight,
                CurrentLap             = data.CurrentLap,
            };
        }

        private static double ParseGear(string? gear)
        {
            if (gear == null || gear.Length == 0) return 0;
            if (gear == "R") return -1.0; // int30: -1 encodes as raw=31 (5-bit two's complement)
            if (gear == "N") return 0;
            if (int.TryParse(gear, out int n)) return n;
            return 0;
        }

        public double GetField(SimHubField field)
        {
            switch (field)
            {
                case SimHubField.SpeedKmh:              return SpeedKmh;
                case SimHubField.Rpms:                  return Rpms;
                case SimHubField.Gear:                  return Gear;
                case SimHubField.Throttle:              return Throttle;
                case SimHubField.Brake:                 return Brake;
                case SimHubField.BestLapTimeSeconds:    return BestLapTimeSeconds;
                case SimHubField.CurrentLapTimeSeconds: return CurrentLapTimeSeconds;
                case SimHubField.LastLapTimeSeconds:    return LastLapTimeSeconds;
                case SimHubField.DeltaToSessionBest:    return DeltaToSessionBest;
                case SimHubField.FuelPercent:           return FuelPercent;
                case SimHubField.DrsEnabled:            return DrsEnabled;
                case SimHubField.ErsPercent:            return ErsPercent;
                case SimHubField.TyreWearFrontLeft:     return TyreWearFrontLeft;
                case SimHubField.TyreWearFrontRight:    return TyreWearFrontRight;
                case SimHubField.TyreWearRearLeft:      return TyreWearRearLeft;
                case SimHubField.TyreWearRearRight:     return TyreWearRearRight;
                case SimHubField.CurrentLap:            return CurrentLap;
                default:                                return 0.0;
            }
        }
    }
}
