using System;
using MozaPlugin.Devices.Motion;
using SimHub.Plugins.Motion.Contracts;
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
    /// <item>the pivot, from the controller's front-to-rear actuator spacing and the seat measurements in
    /// the output settings, in the convention of SimHub's own "3 DOFs / 4 corners" geometry
    /// (<c>Geometry3Dof4Linear</c>): surge = seat-from-front actuators − length / 2, sway 0,
    /// heave = seat height above the floor with the actuators fully retracted + the rise to the centre
    /// position. The MC places it at (sway, surge, −(<c>SeatToPivotHeightMm</c> + heave)) from the seat;
    /// the bypass geometry's <c>SeatToPivotHeightMm</c> has no UI and stays 0.</item>
    /// <item>with the pose source (full control mode), the pitch, roll and heave the controller is
    /// actually driven to (after the plugin's tilt share and limiter, and the controller's soft
    /// limits), in place of SimHub's pose in <c>MotionState</c>, which is what the MC estimates from.
    /// Yaw, surge and sway are zeroed: the platform does not move on them. The
    /// <c>MotionCompensationExternal*</c> fields are not used: the MC reads them only with a platform
    /// sensor enabled, which its UI offers only for geometries with a hardware MC report.</item>
    /// </list>
    /// One instance per output, used only from its update thread.
    /// See docs/simhub.md § Motion compensation with the HMA150.
    /// </summary>
    internal sealed class Hma150MotionCompensation
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

        private RotationCenter? _pivot;
        // The copy last written into a result, and SimHub's state it replaced.
        private MotionState? _drivenState;
        private IMotionState? _simHubState;

        /// <summary>
        /// The pivot relative to the seat, as <c>MotionCompensationRotationCenter</c> takes it (mm);
        /// false until the controller's spacing has been read and both seat measurements are set.
        /// </summary>
        public static bool TryPivot(double? rigLengthMm, double seatFromFrontMm, double seatHeightMm,
            out double surge, out double heave)
        {
            surge = heave = 0.0;
            if (rigLengthMm == null || seatFromFrontMm <= 0 || seatHeightMm <= 0) return false;
            surge = seatFromFrontMm - rigLengthMm.Value / 2.0;
            heave = seatHeightMm + ParkToCentreMm;
            return true;
        }

        /// <summary>
        /// Platform angles (SimHub pose convention, degrees) for the travel the controller was driven to,
        /// over the spacing it was computed with. The frame carries −pitch and −roll
        /// (<see cref="MotionGameFeed.PoseFrame"/>).
        /// </summary>
        public static void DrivenAngles(in MotionDrivenPose pose, out double pitchDegrees, out double rollDegrees)
        {
            pitchDegrees = -Angle(pose.PitchTravelMm, pose.ControllerHalfLengthMm) * RadToDeg;
            rollDegrees = -Angle(pose.RollTravelMm, pose.ControllerHalfWidthMm) * RadToDeg;
        }

        private static double Angle(double travelMm, double halfSpacingMm)
            => halfSpacingMm > 0 ? Math.Asin(Math.Max(-1.0, Math.Min(1.0, travelMm / halfSpacingMm))) : 0.0;

        /// <summary>
        /// SimHub's own pose for this result. SimHub can hand the same result back after
        /// <see cref="Apply"/> replaced its state (Manual control, Hop-in), so streaming reads through this.
        /// </summary>
        public IMotionState? SimHubState(GeometryResult? primary)
        {
            var state = primary?.MotionState;
            return state != null && ReferenceEquals(state, _drivenState) ? _simHubState : state;
        }

        /// <summary>
        /// Write the pivot and, in full control mode, the driven pose into the primary result. Safe with
        /// a null result or settings; leaves the pivot untouched when it is not set.
        /// </summary>
        public void Apply(CompositeGeometryResult? geometryResult, MozaHma150OutputSettings? settings,
            MozaMotionController? lane)
        {
            var primary = geometryResult?.PrimaryGeometryResult;
            if (primary == null || settings == null) return;

            if (TryPivot(lane?.RigLengthReadMm, settings.McSeatFromFrontMm, settings.McSeatHeightMm,
                    out double surge, out double heave))
            {
                if (_pivot == null || _pivot.Surge != surge || _pivot.Heave != heave)
                    _pivot = new RotationCenter { Surge = surge, Sway = 0.0, Heave = heave };
                primary.MotionCompensationRotationCenter = _pivot;
            }

            var simHub = SimHubState(primary);
            if (simHub == null) return;
            if (!settings.McUseDrivenPose || lane == null
                || !lane.TryGetDrivenPose(DrivenPoseMaxAgeMs, out var driven))
            {
                // A result this already changed goes back to SimHub's pose.
                if (!ReferenceEquals(primary.MotionState, simHub)) primary.MotionState = simHub;
                return;
            }

            DrivenAngles(driven, out double pitch, out double roll);
            var state = ReferenceEquals(primary.MotionState, _drivenState) ? _drivenState! : null;
            if (state == null)
            {
                // A copy: the output has already streamed from SimHub's state, and FinalMotionState is separate.
                state = new MotionState(simHub);
                _drivenState = state;
                _simHubState = simHub;
            }
            state.PitchDegrees = pitch;
            state.RollDegrees = roll;
            state.HeaveMm = driven.HeaveMm;
            state.YawDegrees = 0.0;
            state.SurgeMm = 0.0;
            state.SwayMm = 0.0;
            primary.MotionState = state;
        }
    }
}
