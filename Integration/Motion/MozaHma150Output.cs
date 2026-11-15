using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Controls;
using MozaPlugin.Devices.Motion;
using MozaPlugin.Resources;
using Newtonsoft.Json;
using SimHub.Plugins.Motion.Contracts;
using SimHub.Plugins.Motion.Geometry;
using SimHub.Plugins.Motion.Outputs;
using Simhub.Plugins.Motion.Models;

namespace MozaPlugin.Integration.Motion
{
    /// <summary>What the output streams to the controller.</summary>
    public enum MozaHma150Source
    {
        /// <summary>Raw game physics, as Motion Manager sends; the controller's effects do the cueing.</summary>
        GameTelemetry = 0,
        /// <summary>SimHub Motion's pose (its effects profile) as attitude and heave.</summary>
        SimHubPose = 1,
    }

    /// <summary>Persisted output settings; the controller stores its own setup.</summary>
    public class MozaHma150OutputSettings : OutputSettingsBase
    {
        private MozaHma150Source _source;
        private bool _includeCornerHaptics = true;
        private int _crestBoost;
        private bool _slowManualControl = true;

        public MozaHma150Source Source
        {
            get => _source;
            set
            {
                if (_source == value) return;
                _source = value;
                // What the base properties' change hook does: re-clone the running settings.
                Output?.ApplySettings();
            }
        }

        /// <summary>
        /// Pose source only: stream <c>FinalMotionState</c> (corner haptics folded in) rather than
        /// <c>MotionState</c>. Haptics that write heave directly are in both.
        /// </summary>
        public bool IncludeCornerHaptics
        {
            get => _includeCornerHaptics;
            set
            {
                if (_includeCornerHaptics == value) return;
                _includeCornerHaptics = value;
                Output?.ApplySettings();
            }
        }

        /// <summary>Pose source only, 0–100 (0 = off): share of <see cref="MotionGameFeed.CrestPush"/> taken off f6.</summary>
        public int CrestBoost
        {
            get => _crestBoost;
            set
            {
                value = Math.Max(0, Math.Min(100, value));
                if (_crestBoost == value) return;
                _crestBoost = value;
                Output?.ApplySettings();
            }
        }

        private int _engineVibrationPitch = 50;
        private int _engineTickover;
        private bool _aiPoseMode;
        private bool _aiMaxPerformance;

        /// <summary>
        /// AI mode only, off by default. Off: the pose is kept inside game mode's behaviour (tilt
        /// scaled to what game mode gives, every actuator within game mode's travel, speed and
        /// acceleration capped). On: the pose's true angles with no speed or acceleration cap, which
        /// can make the rig hop or shift.
        /// </summary>
        public bool AiMaxPerformance
        {
            get => _aiMaxPerformance;
            set
            {
                if (_aiMaxPerformance == value) return;
                _aiMaxPerformance = value;
                Output?.ApplySettings();
            }
        }

        /// <summary>
        /// Pose source only, off by default: run the controller in its AI mode (mode 11), where the pose
        /// is followed as positions (heave holds, pitch and roll are true angles), instead of game mode.
        /// </summary>
        public bool AiPoseMode
        {
            get => _aiPoseMode;
            set
            {
                if (_aiPoseMode == value) return;
                _aiPoseMode = value;
                Output?.ApplySettings();
            }
        }

        /// <summary>
        /// Pose source only, 0–100 (0 = off): a heave tick-over pulse of up to 3 mm at idle that fades
        /// out as the controller's engine vibration fades in (use instead of the profile's engine effect).
        /// </summary>
        public int EngineTickover
        {
            get => _engineTickover;
            set
            {
                value = Math.Max(0, Math.Min(100, value));
                if (_engineTickover == value) return;
                _engineTickover = value;
                Output?.ApplySettings();
            }
        }

        /// <summary>
        /// Pose source only, 0–100, 50 = Motion Manager's frequencies (25–60 Hz): pitch of the controller's
        /// engine vibration, a factor of 2^((value − 50) / 25), so 100 is four times higher and 25 half.
        /// </summary>
        public int EngineVibrationPitch
        {
            get => _engineVibrationPitch;
            set
            {
                value = Math.Max(0, Math.Min(100, value));
                if (_engineVibrationPitch == value) return;
                _engineVibrationPitch = value;
                Output?.ApplySettings();
            }
        }

        /// <summary>
        /// While SimHub's Manual control or Hop-in position moves the platform, use the controller's
        /// Manual Motion Test (mode 2, <c>0x1E/07</c> setpoints) instead of game mode.
        /// </summary>
        public bool SlowManualControl
        {
            get => _slowManualControl;
            set
            {
                if (_slowManualControl == value) return;
                _slowManualControl = value;
                Output?.ApplySettings();
            }
        }
    }

    /// <summary>
    /// SimHub Motion output for the MOZA HMA150 platform. Not in SimHub's hard-coded
    /// controller list; reached through the presets the plugin deploys (resolved by
    /// <c>TypeName</c> from this assembly). Pairs with the 6DOF pose-bypass geometry, which
    /// has no corner channels to map.
    ///
    /// By default, like Motion Manager, it streams the game's raw telemetry (<see cref="MotionGameFeed"/>)
    /// and leaves cueing and strength to the controller's own Motion Effects sliders; with
    /// <see cref="MozaHma150Source.SimHubPose"/> it streams SimHub Motion's pose instead
    /// (<see cref="MotionGameFeed.PoseFrame"/>). SimHub Motion supplies the lifecycle: start,
    /// idle, stop and failure.
    ///
    /// The port belongs to the plugin's motion lane (<see cref="MozaMotionController"/>),
    /// which this output holds while started:
    /// <list type="bullet">
    /// <item>Start — connect if needed, engage (self-check, game mode); returns at once, and
    /// <see cref="IsReadyForLiveMotionData"/> stays false ("Starting") until the
    /// controller reports game mode.</item>
    /// <item>Idle — stay in game mode, neutral frames with the in-game flag clear.</item>
    /// <item>Stop — run state stop, standby, then the port is released.</item>
    /// <item>Lane fault or disconnect — <c>ConnectionState.Failure</c>; SimHub freezes the pose.</item>
    /// </list>
    /// Reference: docs/simhub.md § Motion Plugin — Custom Outputs.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class MozaHma150Output : MotionOutputBase<MozaHma150OutputSettings>
    {
        public const string DisplayNameText = "MOZA HMA150";

        // Motion Manager streams 0x22/00 at ~33 Hz.
        private const int FrameIntervalMs = 30;
        private const int FeedMaxAgeMs = 500;

        // Manual mode: SimHub's Manual control full scale (its sliders reach ±20° and ±200 mm on the
        // 6DOF bypass geometry, capture 2026-11-11 19-07-06) maps to Motion Manager's ±100 %.
        private const double ManualFullScaleDegrees = 20.0, ManualFullScaleHeaveMm = 200.0;

        private int _counted;
        private readonly System.Diagnostics.Stopwatch _manualPoll = new System.Diagnostics.Stopwatch();
        private const int ManualPollMs = 200;

        [JsonIgnore]
        public override string Name => DisplayNameText;

        [JsonIgnore]
        public override string PreviewIcon => null!;

        public override int RefreshRateMs => 10;

        public override Control SettingsControl => new MozaHma150SettingsControl(this);

        private static MozaMotionController? Lane => MozaPlugin.SharedMotionController;

        public override IEnumerable<string> GetReservedSerialPorts()
        {
            string? port = Lane?.LastPortName;
            if (port != null && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                yield return port;
        }

        protected override IEnumerable<string> GetOutputConfigurationErrorsInternal(bool ignoreAxisAssignment)
        {
            var lane = Lane;
            if (lane == null)
            {
                yield return Strings.Motion_Error_PluginUnavailable;
                yield break;
            }
            foreach (var issue in lane.GetConfigurationErrors())
                yield return FormatIssue(issue);
        }

        internal static string FormatIssue(MotionConfigIssue issue) => issue.Kind switch
        {
            MotionConfigIssueKind.NotConnected => Strings.Motion_Error_NotConnected,
            MotionConfigIssueKind.Enrolling => Strings.Motion_Error_Enrolling,
            MotionConfigIssueKind.PortUnassigned => string.Format(Strings.Motion_Error_PortUnassignedFmt, issue.Value),
            MotionConfigIssueKind.PortDuplicate => string.Format(Strings.Motion_Error_PortDuplicateFmt, issue.Value, CornerLabel(issue.Corner)),
            MotionConfigIssueKind.ActuatorsMissing => string.Format(Strings.Motion_Error_ActuatorsMissingFmt, issue.Value, issue.Expected),
            MotionConfigIssueKind.PortConfigPending => Strings.Motion_Error_PortConfigPending,
            _ => issue.ToString(),
        };

        internal static string CornerLabel(Protocol.MotionCorner corner) => corner switch
        {
            Protocol.MotionCorner.FrontLeft => Strings.Motion_Corner_FrontLeft,
            Protocol.MotionCorner.FrontRight => Strings.Motion_Corner_FrontRight,
            Protocol.MotionCorner.RearLeft => Strings.Motion_Corner_RearLeft,
            Protocol.MotionCorner.RearRight => Strings.Motion_Corner_RearRight,
            _ => Strings.Motion_Corner_None,
        };

        protected override void StartInternal()
        {
            var lane = Lane;
            if (lane == null || !lane.IsPresent)
            {
                LastErrorMessage = Strings.Motion_Error_NotConnected;
                ConnectionState = OutputConnectionState.NotFound;
                return;
            }
            var issues = lane.GetConfigurationErrors();
            if (issues.Count > 0)
            {
                LastErrorMessage = FormatIssue(issues[0]);
                ConnectionState = OutputConnectionState.Failure;
                return;
            }
            if (Interlocked.Exchange(ref _counted, 1) == 0)
                lane.Acquire();
            lane.SetIdle(false);
            PollManualControl(lane);
            lane.Engage(new MotionEngageConfig { FrameIntervalMs = FrameIntervalMs });
            ConnectionState = OutputConnectionState.Connected;
        }

        public override void StopInternal()
        {
            var lane = Lane;
            lane?.SetManualWanted(false);
            lane?.Disengage();
            if (Interlocked.Exchange(ref _counted, 0) == 1)
                lane?.Release();
        }

        public override bool IsReadyForLiveMotionData()
            => base.IsReadyForLiveMotionData()
               && (Lane?.Phase == MotionLanePhase.Game || (Lane?.ManualDriven == true && Lane.ManualTestActive));

        protected override void UpdateInternal(CompositeGeometryResult geometryResult, MozaHma150OutputSettings currentSettings)
        {
            var lane = Lane;
            PollManualControl(lane);
            if (!CheckLane(lane)) return;
            if (lane!.ManualDriven)
            {
                // Manual control and Hop-in poses carry no effects, so either state reads the same.
                var primary = geometryResult?.PrimaryGeometryResult;
                var pose = primary?.MotionState ?? primary?.FinalMotionState;
                if (pose != null)
                    lane.PostManualPose(
                        (float)(pose.PitchDegrees / ManualFullScaleDegrees),
                        (float)(pose.RollDegrees / ManualFullScaleDegrees),
                        (float)(pose.HeaveMm / ManualFullScaleHeaveMm));
                return;
            }
            var feed = MozaPlugin.Instance?.MotionFeed;
            var game = feed?.Latest(FeedMaxAgeMs);
            if (currentSettings?.Source == MozaHma150Source.SimHubPose)
            {
                // Final state: the bypass geometry folds corner haptics into it, as SimHub's <PoseAxis> token reads.
                var primary = geometryResult?.PrimaryGeometryResult;
                var pose = currentSettings!.IncludeCornerHaptics
                    ? primary?.FinalMotionState ?? primary?.MotionState
                    : primary?.MotionState;
                if (pose != null)
                {
                    // The crest signal belongs to the running game: none without a fresh game frame.
                    double crestPush = game != null ? currentSettings.CrestBoost / 100.0 * feed!.CrestPush : 0.0;
                    // AI mode follows the pose as positions. The lane says which shape it streams, and
                    // drops any frame whose own shape (IsPosition) is not that one.
                    if (lane!.StreamsAiPose)
                    {
                        // A held drop needs speed: none below 5 m/s, full from 15 m/s. A car being placed
                        // on the track reads as "light" and held the full drop for 10 s (capture 13 Nov 2026).
                        if (game != null)
                            crestPush *= Math.Max(0.0, Math.Min(1.0, (Math.Abs(game.Fields[7]) - 5.0) / 10.0));
                        // Max performance: the pose's true angles. Otherwise the share game mode turns into tilt.
                        double tiltScale = currentSettings.AiMaxPerformance ? 1.0 : AiPoseLimiter.DefaultTiltShare;
                        lane.PostFrame(MotionGameFeed.PoseFrameAsPosition(pose.PitchDegrees, pose.RollDegrees,
                            pose.HeaveMm, game, crestPush, tiltScale));
                    }
                    else
                        lane.PostFrame(MotionGameFeed.PoseFrame(pose.PitchDegrees, pose.RollDegrees, pose.HeaveMm, game,
                            crestPush));
                }
                return;
            }
            if (game != null) lane!.PostFrame(game);
        }

        protected override void KeepAliveInternal(CompositeGeometryResult geometryResult)
        {
            PollManualControl(Lane);
            CheckLane(Lane);
        }

        /// <summary>
        /// Tell the lane, a few times a second, whether SimHub's Manual control or Hop-in position is
        /// the motion source; the lane then swaps game mode for the slow manual mode and back on its own.
        /// </summary>
        private void PollManualControl(MozaMotionController? lane)
        {
            if (lane == null) return;
            // The SimHub pose source runs the controller in AI mode (pose followed as positions).
            lane.SetAiPoseWanted(Settings?.Source == MozaHma150Source.SimHubPose && Settings.AiPoseMode);
            lane.SetEngineVibrationPitch((float)Math.Pow(2.0, ((Settings?.EngineVibrationPitch ?? 50) - 50) / 25.0));
            lane.SetEngineTickover((Settings?.EngineTickover ?? 0) / 100f * 3f);
            lane.SetAiMaxPerformance(Settings?.AiMaxPerformance == true);
            if (_manualPoll.IsRunning && _manualPoll.ElapsedMilliseconds < ManualPollMs) return;
            // Between plugin instances (SimHub game switch) there is nothing to ask: keep the last answer.
            if (MozaPlugin.Instance == null) return;
            _manualPoll.Restart();
            lane.SetManualWanted(Settings?.SlowManualControl != false
                && MozaPlugin.Instance?.MotionManualControlActive == true);
        }

        protected override void BeforeStartingIdle(CompositeGeometryResult geometryResult) => Lane?.SetIdle(true);

        protected override void BeforeStoppingIdle() => Lane?.SetIdle(false);

        // The controller ramps itself in and out; SimHub's park/unpark transitions need
        // actuator roles, which a pose-bypass output does not have.
        protected override void Park(MozaHma150OutputSettings settings) { }

        protected override void Unpark(CompositeGeometryResult geometryResult, MozaHma150OutputSettings settings) { }

        private bool CheckLane(MozaMotionController? lane)
        {
            if (lane == null)
            {
                Fail(Strings.Motion_Error_NotConnected);
                return false;
            }
            var phase = lane.Phase;
            if (!lane.IsConnected || phase == MotionLanePhase.Enrolling || phase == MotionLanePhase.Disconnected)
            {
                // Started with the port closed: the lane is opening and enrolling it. The port
                // can read open a tick before the phase leaves Disconnected.
                if (lane.EngagePending) return false;
                Fail(lane.FaultReason != null
                    ? string.Format(Strings.Motion_Error_FaultFmt, lane.FaultReason)
                    : Strings.Motion_Error_NotConnected);
                return false;
            }
            if (phase == MotionLanePhase.Faulted)
            {
                Fail(string.Format(Strings.Motion_Error_FaultFmt, lane.FaultReason ?? phase.ToString()));
                return false;
            }
            return true;
        }

        private void Fail(string message)
        {
            if (ConnectionState == OutputConnectionState.Failure) return;
            LastErrorMessage = message;
            ConnectionState = OutputConnectionState.Failure;
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _counted, 0) == 1)
                Lane?.Release();
            base.Dispose();
        }
    }
}
