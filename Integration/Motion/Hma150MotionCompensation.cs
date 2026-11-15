using System;
using MozaPlugin.Devices.Motion;
using SimHub.Plugins.Motion.Geometry;
using SimHub.Plugins.Motion.Models;

namespace MozaPlugin.Integration.Motion
{
    /// <summary>
    /// What the HMA150 output tells SimHub's VR motion compensation, which runs on the same
    /// <see cref="GeometryResult"/> right after the output's update (<c>MotionDevice.Update</c>:
    /// geometry → <c>Output.Update</c> → <c>IMotionCompensation.Update</c>). The 6DOF bypass geometry
    /// hard-codes the pivot at (0, 0, 0) and has no rig settings, so the output fills in:
    /// <list type="bullet">
    /// <item>the pivot, from the rig measurements entered in the output settings, in the convention of
    /// SimHub's own "3 DOFs / 4 corners" geometry (<c>Geometry3Dof4Linear</c>): surge = seat-from-front
    /// actuators − length / 2, sway 0, heave = seat height above the floor with the actuators fully
    /// retracted + the rise to the centre position. The MC places it at
    /// (sway, surge, −(<c>SeatToPivotHeightMm</c> + heave)) from the seat; the bypass geometry's
    /// <c>SeatToPivotHeightMm</c> has no UI and stays 0.</item>
    /// <item>in AI mode, the pitch, roll and heave the controller is actually driven to (after the plugin's
    /// tilt share and limiter, and the controller's soft limits), in place of SimHub's pose in
    /// <c>MotionState</c>, which is what the MC estimates from. The <c>MotionCompensationExternal*</c>
    /// fields are not used: the MC reads them only with a platform sensor enabled, which its UI offers
    /// only for geometries with a hardware MC report.</item>
    /// </list>
    /// See docs/simhub.md § Motion compensation with the HMA150.
    /// </summary>
    internal static class Hma150MotionCompensation
    {
        /// <summary>
        /// Rise of the platform from parked to the centre (ready) position: the controller logs every
        /// actuator at −80 mm in standby and 0 at the ready position (docs/protocol/motion/firmware-log-0x0E.md).
        /// SimHub's 4-corner geometry uses stroke / 2 here.
        /// </summary>
        public const double ParkToCentreMm = 80.0;

        /// <summary>A driven pose older than this is not used (the lane streams every 30 ms).</summary>
        public const int DrivenPoseMaxAgeMs = 200;

        private const double RadToDeg = 180.0 / Math.PI;

        /// <summary>True when the measurements the pivot needs have been entered.</summary>
        public static bool HasPivot(MozaHma150OutputSettings? s)
            => s != null && s.McRigLengthMm > 0 && s.McSeatFromFrontMm > 0 && s.McSeatHeightMm > 0;

        /// <summary>The pivot relative to the seat, as <c>MotionCompensationRotationCenter</c> takes it (mm).</summary>
        public static void Pivot(double rigLengthMm, double seatFromFrontMm, double seatHeightMm,
            out double surge, out double sway, out double heave)
        {
            surge = seatFromFrontMm - rigLengthMm / 2.0;
            sway = 0.0;
            heave = seatHeightMm + ParkToCentreMm;
        }

        /// <summary>
        /// Platform angles (SimHub pose convention, degrees) for the travel the controller was driven to,
        /// on the physical actuator spacing. The controller turns an angle into travel with its own stored
        /// dimensions; the rig tilts by that travel over its real spacing. A spacing of 0 or less falls
        /// back to the controller's. The frame carries −pitch and −roll (<see cref="MotionGameFeed.PoseFrame"/>).
        /// </summary>
        public static void DrivenAngles(MotionDrivenPose pose, double rigLengthMm, double rigWidthMm,
            out double pitchDegrees, out double rollDegrees)
        {
            double halfLength = rigLengthMm > 0 ? rigLengthMm / 2.0 : pose.ControllerHalfLengthMm;
            double halfWidth = rigWidthMm > 0 ? rigWidthMm / 2.0 : pose.ControllerHalfWidthMm;
            pitchDegrees = -Angle(pose.PitchTravelMm, halfLength) * RadToDeg;
            rollDegrees = -Angle(pose.RollTravelMm, halfWidth) * RadToDeg;
        }

        private static double Angle(double travelMm, double halfSpacingMm)
            => halfSpacingMm > 0 ? Math.Asin(Math.Max(-1.0, Math.Min(1.0, travelMm / halfSpacingMm))) : 0.0;

        /// <summary>
        /// Write the pivot and, in AI mode, the driven pose into the primary result. Safe with a null
        /// result or settings; leaves the result untouched when nothing has been entered.
        /// </summary>
        public static void Apply(CompositeGeometryResult? geometryResult, MozaHma150OutputSettings? settings,
            MozaMotionController? lane)
        {
            var primary = geometryResult?.PrimaryGeometryResult;
            if (primary == null || settings == null) return;

            if (HasPivot(settings))
            {
                Pivot(settings.McRigLengthMm, settings.McSeatFromFrontMm, settings.McSeatHeightMm,
                    out double surge, out double sway, out double heave);
                primary.MotionCompensationRotationCenter = new RotationCenter { Surge = surge, Sway = sway, Heave = heave };
            }

            if (!settings.McUseDrivenPose || primary.MotionState == null) return;
            var driven = lane?.LatestDrivenPose(DrivenPoseMaxAgeMs);
            if (driven == null) return;
            DrivenAngles(driven, settings.McRigLengthMm, settings.McRigWidthMm, out double pitch, out double roll);
            // A copy: the output has already streamed from this state, and FinalMotionState is separate.
            var state = new MotionState(primary.MotionState)
            {
                PitchDegrees = pitch,
                RollDegrees = roll,
                HeaveMm = driven.HeaveMm,
            };
            primary.MotionState = state;
        }
    }
}
