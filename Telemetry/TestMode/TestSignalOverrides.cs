using System;
using System.Collections.Generic;

namespace MozaPlugin.Telemetry.TestMode
{
    /// <summary>
    /// Channels whose test-mode behaviour cannot be expressed as "sweep through
    /// the parsed JSON range". Keyed by <see cref="Dashboard.ChannelDefinition.Name"/>
    /// (which is normally the Telemetry.json <c>name</c> field; for mzdash channels
    /// the JSON doesn't know about, it falls back to the URL suffix — both spellings
    /// are added here when they differ in practice).
    ///
    /// Bounds intentionally chosen for visual plausibility on the wheel display,
    /// not encoder-wire-safety (the compression encoders clamp on their own).
    /// </summary>
    public static class TestSignalOverrides
    {
        private static readonly Dictionary<string, TestSignal> Map =
            new Dictionary<string, TestSignal>(StringComparer.OrdinalIgnoreCase);

        static TestSignalOverrides()
        {
            // --- Engine / drivetrain ---
            Add("Rpm",            TestSignal.Sweep(0, 8000, periodMs: 5000));
            Add("MaxRpm",         TestSignal.Constant_(8000));
            Add("Gear",           TestSignal.Step(-1, 6, stepMs: 1000, isInt: true));
            Add("Throttle",       TestSignal.Sweep(0, 1, periodMs: 4000));
            Add("Brake",          TestSignal.Sweep(0, 1, periodMs: 4000, phaseOffsetMs: 2000));
            Add("Clutch",         TestSignal.Sweep(0, 1, periodMs: 6000));
            Add("Boost",          TestSignal.Sweep(0, 1, periodMs: 5000));
            Add("WheelSpin",      TestSignal.Sweep(0, 1, periodMs: 3000));
            Add("EngineBrake",    TestSignal.Step(0, 5, stepMs: 2000));
            Add("Handbrake",      TestSignal.Sweep(0, 1, periodMs: 7000));
            Add("SteeringWheelAngle", TestSignal.Sweep(-450, 450, periodMs: 4000));
            Add("CurrentTorque",  TestSignal.Sweep(0, 500, periodMs: 5000));
            Add("EngineTorque",   TestSignal.Sweep(0, 500, periodMs: 5000));
            Add("MaxEngineTorque", TestSignal.Constant_(550));
            Add("TurboPercent",   TestSignal.Sweep(0, 100, periodMs: 5000));
            Add("MaxTurbo",       TestSignal.Constant_(2.5));

            // --- Speed ---
            Add("SpeedKmh",       TestSignal.Sweep(0, 300, periodMs: 6000));
            Add("SpeedMph",       TestSignal.Sweep(0, 186, periodMs: 6000));
            Add("SpeedMs",        TestSignal.Sweep(0, 83,  periodMs: 6000));
            Add("MaxSpeedKmh",    TestSignal.Constant_(320));
            Add("MaxSpeedMph",    TestSignal.Constant_(199));

            // --- Lap / timing ---
            // Lap counters start at 1 on every Test Start and clamp at the
            // final lap (20) so the dashboard reads as a finished race
            // rather than looping back to lap 1.
            Add("Lap",             TestSignal.Increment(1, 20, stepMs: 30000, wrap: false));
            Add("CurrentLap",      TestSignal.Increment(1, 20, stepMs: 30000, wrap: false));
            Add("CompletedLaps",   TestSignal.Increment(1, 20, stepMs: 30000, wrap: false));
            Add("LapCount",        TestSignal.Constant_(20));
            Add("CurrentLapCount", TestSignal.Constant_(20));
            Add("Pos",            TestSignal.Step(1, 20, stepMs: 8000));
            Add("CarCount",       TestSignal.Constant_(20));
            Add("OpponentCount",  TestSignal.Constant_(19));
            Add("PlayerIndex",    TestSignal.Constant_(7));
            Add("Gap",            TestSignal.Sweep(-30, 30, periodMs: 8000));
            Add("GAP",            TestSignal.Sweep(-30, 30, periodMs: 8000));
            // Every clock-like channel counts up monotonically from
            // 00:00:00 for the duration of the test session.
            Add("CurrentLapTime",       TestSignal.Elapsed());
            Add("BestLapTime",          TestSignal.Elapsed());
            Add("LastLapTime",          TestSignal.Elapsed());
            Add("EstimatedLapTime",     TestSignal.Elapsed());
            Add("AllTimeBest",          TestSignal.Elapsed());
            Add("Sector1BestTime",      TestSignal.Elapsed());
            Add("Sector2BestTime",      TestSignal.Elapsed());
            Add("Sector3BestTime",      TestSignal.Elapsed());
            Add("Sector1LastLapTime",   TestSignal.Elapsed());
            Add("Sector2LastLapTime",   TestSignal.Elapsed());
            Add("Sector3LastLapTime",   TestSignal.Elapsed());
            Add("SectorlTime",          TestSignal.Elapsed()); // sic — Telemetry.json typo
            Add("Sector2Time",          TestSignal.Elapsed());
            Add("LastSectorTime",       TestSignal.Elapsed());
            Add("LastSectorTimeAnyLap", TestSignal.Elapsed());
            Add("SessionTimeLeft",      TestSignal.Elapsed());
            Add("LastPitStopDuration",  TestSignal.Elapsed());
            Add("IsInPitSince",         TestSignal.Elapsed());
            Add("TimeAbsolute",         TestSignal.Elapsed());
            Add("TimeOfDay",            TestSignal.Elapsed());

            Add("SectorIndex",          TestSignal.Step(0, 2, stepMs: 10000));
            Add("SectorsCount",         TestSignal.Constant_(3));
            Add("TrackPositionPercent", TestSignal.Sweep(0, 1, periodMs: 10000));
            Add("TrackPositionMeters",  TestSignal.Sweep(0, 5000, periodMs: 10000));
            Add("TrackLength",          TestSignal.Constant_(5000));
            // Gap-to-others are not clocks; sweep within plausible bounds.
            Add("TimeGapCarAhead",      TestSignal.Sweep(0, 5,  periodMs: 8000));
            Add("TimeGapCarBehind",     TestSignal.Sweep(0, 5,  periodMs: 8000, phaseOffsetMs: 4000));
            Add("TimeGapPlaceAhead",    TestSignal.Sweep(0, 30, periodMs: 8000));
            Add("TimeGapPlaceBehind",   TestSignal.Sweep(0, 30, periodMs: 8000, phaseOffsetMs: 4000));
            Add("PlayerClassOpponentsCount", TestSignal.Constant_(8));

            // --- Fuel / energy ---
            Add("Fuel",           TestSignal.Constant_(50));
            Add("FuelRemain",     TestSignal.Constant_(40));
            Add("FuelRemains",    TestSignal.Constant_(40));
            Add("FuelCapacity",   TestSignal.Constant_(60));
            Add("FuelRemainLaps", TestSignal.Constant_(8));
            Add("FuelSurplusLaps", TestSignal.Constant_(2));
            Add("FuelUsed",       TestSignal.Sweep(0, 30, periodMs: 30000));
            Add("FuelAverageConsumptionLap", TestSignal.Constant_(2.5));
            Add("FuelClass",      TestSignal.Step(0, 3, stepMs: 5000));
            Add("FuelRange",      TestSignal.Constant_(450));
            Add("FuelTemp",       TestSignal.Constant_(40));
            Add("InstantConsumption_L100KM", TestSignal.Sweep(5, 35, periodMs: 4000));
            Add("InstantConsumption_MPG_UK", TestSignal.Sweep(8, 56, periodMs: 4000));
            Add("InstantConsumption_MPG_US", TestSignal.Sweep(7, 47, periodMs: 4000));
            Add("EnergyRemain",   TestSignal.Sweep(0, 100, periodMs: 12000));
            Add("EnergyDeployed", TestSignal.Sweep(0, 100, periodMs: 12000, phaseOffsetMs: 4000));
            Add("EnergyHarvested", TestSignal.Sweep(0, 100, periodMs: 12000, phaseOffsetMs: 8000));
            Add("Ers",            TestSignal.Sweep(0, 100, periodMs: 5000));
            Add("ERSPercent",     TestSignal.Sweep(0, 100, periodMs: 5000));
            Add("ERSStored",      TestSignal.Sweep(0, 100, periodMs: 5000));
            Add("ERSMax",         TestSignal.Constant_(100));
            Add("VirtualEnergy",  TestSignal.Sweep(0, 100, periodMs: 12000));
            Add("BatteryPercent", TestSignal.Sweep(20, 100, periodMs: 20000));
            Add("SoC",            TestSignal.Sweep(20, 100, periodMs: 20000));
            Add("Regen",          TestSignal.Sweep(0, 100, periodMs: 5000));

            // --- Aids — booleans (Toggle, staggered by per-channel hash phase) ---
            string[] boolChannels =
            {
                "ABS", "ABSActive", "TC", "TCActive", "TC_B", "TCCut",
                "Drs", "DRSAllowed", "DRSAvailable",
                "Pitlane", "PitLimiter", "IsInPit",
                "EngineIgnition", "EngineStarted", "EngineEnabled",
                "ReverseLight", "LapInvalidated", "Ontrack", "ReplayMode",
                "Spectating", "Wipers", "RainLight",
                "HighBeamLight", "LowBeamLight",
                "LeftBlinker", "RightBlinker", "HazardWarning",
                "ParkingLight", "ParkingBrake", "CruiseControl",
                "BeaconLight", "FrontAuxLight", "RoofAuxLight",
                "AirPressureWarning", "WaterTemperatureWarning",
                "BatteryVoltageWarning", "AirPressureEmergency",
                "YellowFlag", "BlueFlag", "GreenFlag", "WhiteFlag",
                "RedFlag", "Flag_Black", "Flag_Checkered", "Flag_Orange",
                "DRSActive", "MapAllowed", "PushToPassActive", "AttackMode",
                "IsFixedSetup", "IsSessionRestart", "EVMode",
                "BrakeLockupFrontLeft", "BrakeLockupFrontRight",
                "BrakeLockupRearLeft", "BrakeLockupRearRight",
            };
            foreach (var name in boolChannels)
                Add(name, TestSignal.Toggle(stepMs: 4000, phaseOffsetMs: StableHash(name) % 4000));

            // --- Aids — levels (small-int steps) ---
            Add("ABSLevel",       TestSignal.Step(0, 11, stepMs: 2000));
            Add("ABSMaxLevel",    TestSignal.Constant_(11));
            Add("TCLevel",        TestSignal.Step(0, 11, stepMs: 2000, isInt: true));
            Add("TCMaxLevel",     TestSignal.Constant_(11));
            Add("TCCutMax",       TestSignal.Constant_(11));
            Add("TCSlipMax",      TestSignal.Constant_(11));
            Add("ECUMap",         TestSignal.Step(0, 11, stepMs: 2000));
            Add("EngineMapMax",   TestSignal.Constant_(11));
            Add("BrakeBias",      TestSignal.Sweep(45, 65, periodMs: 8000));
            // Drs / DrsState are wire-encoded as 1-bit bool in Telemetry.json;
            // Step over a wider range would just be clamped to 0/1. The bool
            // loop above already toggles them at 4s.
            Add("TyreType",       TestSignal.Step(0, 4, stepMs: 4000));
            Add("WiperClass",     TestSignal.Step(0, 3, stepMs: 4000));
            Add("RetarderLevel",  TestSignal.Step(0, 4, stepMs: 4000));
            Add("FrontARB",       TestSignal.Step(1, 11, stepMs: 3000));
            Add("FrontARBMax",    TestSignal.Constant_(11));
            Add("RearARB",        TestSignal.Step(1, 11, stepMs: 3000));
            Add("RearARBMax",     TestSignal.Constant_(11));
            Add("Migration",      TestSignal.Step(0, 11, stepMs: 3000));
            Add("MigrationMax",   TestSignal.Constant_(11));
            Add("LiftAndCoastProgress", TestSignal.Sweep(0, 100, periodMs: 5000));
            Add("ThrottleShaping", TestSignal.Step(0, 5, stepMs: 4000));

            // --- Tyres — pressure / temp / wear / extras ---
            string[] tyrePosShort = { "FL", "FR", "RL", "RR" };
            string[] tyrePosLong  = { "FrontLeft", "FrontRight", "RearLeft", "RearRight" };
            foreach (var pos in tyrePosShort)
            {
                Add("TyrePressure" + pos,            TestSignal.Sweep(15, 40, periodMs: 8000));
                Add("TyrePressure" + pos + "&B",     TestSignal.Sweep(1.03, 2.76, periodMs: 8000));
                Add("TyrePressure" + pos + "&kpa",   TestSignal.Sweep(103, 276, periodMs: 8000));
                Add("TyreTemp" + pos,                TestSignal.Sweep(20, 90, periodMs: 8000));
                Add("TyreTemp" + pos + "&F",         TestSignal.Sweep(68, 194, periodMs: 8000));
                foreach (var seg in new[] { "I", "M", "O" })
                {
                    Add("TyreTemp" + pos + seg,        TestSignal.Sweep(20, 90, periodMs: 8000));
                    Add("TyreTemp" + pos + seg + "&F", TestSignal.Sweep(68, 194, periodMs: 8000));
                }
                Add("TyreWear" + pos,                TestSignal.Sweep(0, 100, periodMs: 60000));
                Add("BrakeTemp" + pos,               TestSignal.Sweep(100, 600, periodMs: 8000));
                Add("BrakeTemp" + pos + "&F",        TestSignal.Sweep(212, 1112, periodMs: 8000));
                Add("TyreOptimalTemp" + pos,         TestSignal.Constant_(85));
                Add("TyreCompoundIndex" + pos,       TestSignal.Constant_(1));
                Add("TyreCompoundType" + pos,        TestSignal.Constant_(1));
                Add("TyreDirt" + pos,                TestSignal.Sweep(0, 100, periodMs: 30000));
            }
            // mzdash uses long-form names; mirror the short-form rules so
            // dashboards that bypass the URL→name resolution still match.
            for (int i = 0; i < tyrePosShort.Length; i++)
            {
                string shortPos = tyrePosShort[i];
                string longPos  = tyrePosLong[i];
                Add("TyrePressure" + longPos, TestSignal.Sweep(15, 40, periodMs: 8000));
                Add("TyreTemp" + longPos,     TestSignal.Sweep(20, 90, periodMs: 8000));
                Add("TyreWear" + longPos,     TestSignal.Sweep(0, 100, periodMs: 60000));
                Add("BrakeTemp" + longPos,    TestSignal.Sweep(100, 600, periodMs: 8000));
            }
            // "Max" channels are stable thresholds / peak observed values
            // so they read as fixed reference labels on the dashboard
            // rather than wandering numbers. Avg sweeps, Min sweeps lower.
            Add("TyresTemperatureAvg",  TestSignal.Sweep(20, 90, periodMs: 8000));
            Add("TyresTemperatureMax",  TestSignal.Constant_(90));
            Add("TyresTemperatureMin",  TestSignal.Sweep(20, 60, periodMs: 8000, phaseOffsetMs: 3000));
            Add("TyresTemperatureAvg&F", TestSignal.Sweep(68, 194, periodMs: 8000));
            Add("TyresTemperatureMax&F", TestSignal.Constant_(194));
            Add("TyresTemperatureMin&F", TestSignal.Sweep(68, 140, periodMs: 8000, phaseOffsetMs: 3000));
            Add("TyresWearAvg",         TestSignal.Sweep(0, 100, periodMs: 60000));
            Add("TyresWearMax",         TestSignal.Constant_(75));
            Add("TyresWearMin",         TestSignal.Sweep(0, 50, periodMs: 60000, phaseOffsetMs: 10000));
            Add("TyresDirtyLevelAvg",   TestSignal.Sweep(0, 100, periodMs: 30000));
            Add("TyresDirtyLevelMax",   TestSignal.Constant_(80));
            Add("TyresDirtyLevelMin",   TestSignal.Sweep(0, 50, periodMs: 30000, phaseOffsetMs: 10000));
            Add("BrakesTemperatureAvg", TestSignal.Sweep(200, 500, periodMs: 8000));
            Add("BrakesTemperatureMax", TestSignal.Constant_(700));
            Add("BrakesTemperatureMin", TestSignal.Sweep(100, 350, periodMs: 8000));
            Add("BrakesTemperatureAvg&F", TestSignal.Sweep(392, 932, periodMs: 8000));
            Add("BrakesTemperatureMax&F", TestSignal.Constant_(1292));
            Add("BrakesTemperatureMin&F", TestSignal.Sweep(212, 662, periodMs: 8000));

            // --- Damage & wear ---
            Add("EngineWear",     TestSignal.Sweep(0, 100, periodMs: 60000));
            Add("GearBoxWear",    TestSignal.Sweep(0, 100, periodMs: 60000, phaseOffsetMs: 10000));
            Add("WingWearFL",     TestSignal.Sweep(0, 100, periodMs: 60000));
            Add("WingWearFR",     TestSignal.Sweep(0, 100, periodMs: 60000, phaseOffsetMs: 5000));
            Add("WingWearR",      TestSignal.Sweep(0, 100, periodMs: 60000, phaseOffsetMs: 10000));
            Add("CarDamage1",     TestSignal.Sweep(0, 100, periodMs: 30000));
            Add("CarDamage2",     TestSignal.Sweep(0, 100, periodMs: 30000, phaseOffsetMs: 6000));
            Add("CarDamage3",     TestSignal.Sweep(0, 100, periodMs: 30000, phaseOffsetMs: 12000));
            Add("CarDamage4",     TestSignal.Sweep(0, 100, periodMs: 30000, phaseOffsetMs: 18000));
            Add("CarDamage5",     TestSignal.Sweep(0, 100, periodMs: 30000, phaseOffsetMs: 24000));
            Add("CarDamagesAvg",  TestSignal.Sweep(0, 100, periodMs: 30000));
            Add("CarDamagesMax",  TestSignal.Constant_(60));
            Add("CarDamagesMin",  TestSignal.Sweep(0, 50, periodMs: 30000, phaseOffsetMs: 10000));
            Add("CargoDamage",    TestSignal.Sweep(0, 100, periodMs: 60000));

            // --- Environment / engine temps ---
            Add("AirTemp",        TestSignal.Constant_(22));
            Add("AirTemp&F",      TestSignal.Constant_(72));
            Add("TrackTemp",      TestSignal.Constant_(28));
            Add("TrackTemp&F",    TestSignal.Constant_(82));
            Add("WaterTemperature", TestSignal.Constant_(85));
            Add("OilPressure",    TestSignal.Constant_(4.5));
            Add("AirPressure",    TestSignal.Constant_(1.0));
            Add("WaterPress",     TestSignal.Constant_(1.0));
            Add("CloudCoverage",  TestSignal.Constant_(20));
            Add("TrackGripLevel", TestSignal.Sweep(80, 100, periodMs: 30000));

            // --- Vehicle dynamics ---
            Add("AccX",           TestSignal.Sweep(-2, 2, periodMs: 4000));
            Add("AccY",           TestSignal.Sweep(-2, 2, periodMs: 4000, phaseOffsetMs: 1000));
            Add("AccZ",           TestSignal.Sweep(-2, 2, periodMs: 4000, phaseOffsetMs: 2000));
            Add("Pitch",          TestSignal.Sweep(-15, 15, periodMs: 6000));
            Add("Roll",           TestSignal.Sweep(-15, 15, periodMs: 6000, phaseOffsetMs: 2000));
            Add("Heading",        TestSignal.Sweep(0, 360, periodMs: 12000));
            Add("GlobalAccelerationG", TestSignal.Sweep(0, 3, periodMs: 4000));
            Add("OrientationPitchAcceleration", TestSignal.Sweep(-5, 5, periodMs: 4000));
            Add("OrientationRollAcceleration",  TestSignal.Sweep(-5, 5, periodMs: 4000, phaseOffsetMs: 1000));
            Add("OrientationYawAcceleration",   TestSignal.Sweep(-5, 5, periodMs: 4000, phaseOffsetMs: 2000));
            Add("OrientationYawVelocity",       TestSignal.Sweep(-2, 2, periodMs: 4000));

            // --- Spotter / radar / coordinates ---
            Add("SpotterCarLeft",         TestSignal.Toggle(stepMs: 5000));
            Add("SpotterCarRight",        TestSignal.Toggle(stepMs: 5000, phaseOffsetMs: 2500));
            Add("SpotterCarLeftAngle",    TestSignal.Sweep(-90, 90, periodMs: 6000));
            Add("SpotterCarRightAngle",   TestSignal.Sweep(-90, 90, periodMs: 6000, phaseOffsetMs: 3000));
            Add("SpotterCarLeftDistance", TestSignal.Sweep(0, 30, periodMs: 6000));
            Add("SpotterCarRightDistance", TestSignal.Sweep(0, 30, periodMs: 6000, phaseOffsetMs: 3000));
            Add("CarCoordinates01", TestSignal.Sweep(-1000, 1000, periodMs: 10000));
            Add("CarCoordinates02", TestSignal.Sweep(-1000, 1000, periodMs: 10000, phaseOffsetMs: 3000));
            Add("CarCoordinates03", TestSignal.Sweep(-1000, 1000, periodMs: 10000, phaseOffsetMs: 6000));

            // Opponent track positions / radar indices — staggered around the
            // track so the visualisation looks like a real grid, not a clump.
            for (int i = 0; i < 64; i++)
            {
                int offset = (int)(i * (10000.0 / 64.0));
                Add("Location_" + i,    TestSignal.Sweep(0, 1, periodMs: 10000, phaseOffsetMs: offset));
                Add("RadarIndex" + i,   TestSignal.Constant_(i < 8 ? i : 0));
            }
            Add("Location",         TestSignal.Sweep(0, 1, periodMs: 10000));

            // --- Truck-sim oddities ---
            Add("NavigationSpeedLimit", TestSignal.Constant_(90));
            Add("JobSpeedLimitValue",   TestSignal.Constant_(90));
            Add("CruiseControlMs",      TestSignal.Constant_(25));
            Add("CruiseControlMph",     TestSignal.Constant_(55));
            Add("Odometer",             TestSignal.Constant_(125000));
            Add("SessionOdo",           TestSignal.Sweep(0, 200, periodMs: 60000));
            Add("SessionOdo&M",         TestSignal.Sweep(0, 124, periodMs: 60000));
            Add("StintOdo",             TestSignal.Sweep(0, 50, periodMs: 60000));
            Add("StintOdo&M",           TestSignal.Sweep(0, 31, periodMs: 60000));
            Add("InCome",               TestSignal.Constant_(0));
            Add("NextRestStop",         TestSignal.Constant_(120));

            // --- Configuration / track-limit penalties ---
            Add("TrackLimitsSteps",          TestSignal.Step(0, 3, stepMs: 5000));
            Add("TrackLimitsStepsPerPenalty", TestSignal.Constant_(3));
            Add("TrackLimitsStepsPerPoint",   TestSignal.Constant_(1));
            Add("CarSettings_MaxGears",       TestSignal.Constant_(7));
            Add("CarSettings_CurrentDisplayedRPMPercent", TestSignal.Sweep(0, 1, periodMs: 5000));
            Add("CarSettings",                TestSignal.Sweep(0, 1, periodMs: 5000));
            Add("PacketTime",                 TestSignal.Constant_(0));

            // --- String fields (placeholders; V2 frame layout skips them) ---
            string[] stringChannels =
            {
                "MapName", "DisplayMapName", "TrackId", "TrackCode", "TrackConfig",
                "CarId", "CarModel", "CarClass", "VehicleFilename",
                "VehicleTag1", "VehicleTag2", "VehicleTag3",
                "PlayerName", "Gamename", "SessionTypeName", "Flag_Name",
                "DestinationCity",
            };
            foreach (var name in stringChannels)
                Add(name, TestSignal.StringConstant_("DEMO"));
        }

        private static void Add(string name, TestSignal signal)
        {
            Map[name] = signal;
        }

        public static bool TryGet(string name, out TestSignal signal)
            => Map.TryGetValue(name, out signal);

        // FNV-1a 32-bit. Stable across runs (unlike string.GetHashCode in
        // some .NET versions). Used to derive a per-channel phase stagger.
        public static int StableHash(string s)
        {
            unchecked
            {
                const int prime = 16777619;
                int hash = (int)2166136261;
                foreach (char c in s ?? "")
                {
                    hash ^= c;
                    hash *= prime;
                }
                return hash & 0x7FFFFFFF;
            }
        }
    }
}
