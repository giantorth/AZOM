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

        // Track-map (patch/Location*) per-car ground-plane positions. Each
        // entry feeds one 64-bit location_t slot, packed by
        // TelemetryFrameBuilder as two little-endian float32 (X, Z) — the
        // wire format reverse-engineered from the PitHouse FSR2 capture.
        // CarLocations is indexed by SimHub Opponents[] order; PlayerLocation
        // is the local car. Null/default when there's no game or no opponents.
        // Source: Opponent.Coordinates / StatusDataBase.CarCoordinates, which
        // are double[X,Y,Z] — the ground plane is indices 0 (X) and 2 (Z);
        // index 1 (Y) is elevation and unused by the 2-D map.
        public (float X, float Y)[]? CarLocations;
        public (float X, float Y) PlayerLocation;
        // Index of the local car within CarLocations (= the IsPlayer opponent),
        // so the wheel can highlight "you" on the track map. 0 when unknown.
        public int PlayerIndex;

        // Per-car position RELATIVE to the player (already rotated into the
        // player's frame by SimHub), for the radar (patch/ri*) channels. Each
        // entry feeds one uint32 ri slot, packed by TelemetryFrameBuilder as
        // two int16 (X, Y) — the wire format reverse-engineered from the
        // PitHouse FSR2 radar capture. Indexed by Opponents[] order; (0,0)
        // when SimHub has no relative coordinate for that car (out of radar
        // range), which matches PitHouse's empty-slot behaviour. Source:
        // Opponent.RelativeCoordinatesToPlayer (PointF, metres).
        public (float X, float Y)[]? CarRelative;

        /// <summary>Populate from a live StatusDataBase instance.</summary>
        public static GameDataSnapshot FromStatusData(StatusDataBase? data)
        {
            if (data == null) return default;
            var snap = new GameDataSnapshot
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
            PopulateCarLocations(data, ref snap);
            return snap;
        }

        // Ground-plane (X, Z) for the local car and every opponent, for the
        // track-map location_t channels. Defensive against null/short arrays
        // (games that don't expose coordinates leave them null).
        private static void PopulateCarLocations(StatusDataBase data, ref GameDataSnapshot snap)
        {
            var pc = data.CarCoordinates;
            if (pc != null && pc.Length >= 3)
                snap.PlayerLocation = ((float)pc[0], (float)pc[2]);

            var opps = data.Opponents;
            if (opps != null && opps.Count > 0)
            {
                var locs = new (float X, float Y)[opps.Count];
                var rels = new (float X, float Y)[opps.Count];
                for (int i = 0; i < opps.Count; i++)
                {
                    var opp = opps[i];
                    var c = opp?.Coordinates;
                    locs[i] = (c != null && c.Length >= 3)
                        ? ((float)c[0], (float)c[2])
                        : (0f, 0f);
                    var rc = opp?.RelativeCoordinatesToPlayer;
                    rels[i] = rc.HasValue ? (rc.Value.X, rc.Value.Y) : (0f, 0f);
                    if (opp != null && opp.IsPlayer)
                        snap.PlayerIndex = i;
                }
                snap.CarLocations = locs;
                snap.CarRelative = rels;
            }
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
                case SimHubField.PlayerIndex:           return PlayerIndex;
                default:                                return 0.0;
            }
        }
    }
}
