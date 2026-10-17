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
    /// <summary>Persisted output settings. The controller stores its own setup, so nothing here yet.</summary>
    public class MozaHma150OutputSettings : OutputSettingsBase
    {
    }

    /// <summary>
    /// SimHub Motion output for the MOZA HMA150 platform. Not in SimHub's hard-coded
    /// controller list; reached through the presets the plugin deploys (resolved by
    /// <c>TypeName</c> from this assembly). Pairs with the 6DOF pose-bypass geometry, which
    /// has no corner channels to map.
    ///
    /// Like Motion Manager, it streams the game's raw telemetry (<see cref="MotionGameFeed"/>)
    /// and leaves cueing and strength to the controller's own Motion Effects sliders; SimHub
    /// Motion supplies the lifecycle: start, idle, stop and failure.
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

        private static int s_activeInstances;

        /// <summary>True while an HMA150 output is started in SimHub Motion.</summary>
        internal static bool AnyStarted => Volatile.Read(ref s_activeInstances) > 0;

        private int _counted;

        [JsonIgnore]
        public override string Name => DisplayNameText;

        [JsonIgnore]
        public override string PreviewIcon => null!;

        public override int RefreshRateMs => 10;

        public override Control SettingsControl => new MozaHma150SettingsControl();

        private static MozaMotionController? Lane => MozaPlugin.Instance?.MotionController;

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
            {
                Interlocked.Increment(ref s_activeInstances);
                lane.Acquire();
            }
            lane.SetIdle(false);
            lane.Engage(new MotionEngageConfig { FrameIntervalMs = FrameIntervalMs });
            ConnectionState = OutputConnectionState.Connected;
        }

        public override void StopInternal()
        {
            var lane = Lane;
            lane?.Disengage();
            if (Interlocked.Exchange(ref _counted, 0) == 1)
            {
                Interlocked.Decrement(ref s_activeInstances);
                lane?.Release();
            }
        }

        public override bool IsReadyForLiveMotionData()
            => base.IsReadyForLiveMotionData() && Lane?.Phase == MotionLanePhase.Game;

        protected override void UpdateInternal(CompositeGeometryResult geometryResult, MozaHma150OutputSettings currentSettings)
        {
            var lane = Lane;
            if (!CheckLane(lane)) return;
            var frame = MozaPlugin.Instance?.MotionFeed.Latest(FeedMaxAgeMs);
            if (frame != null) lane!.PostFrame(frame);
        }

        protected override void KeepAliveInternal(CompositeGeometryResult geometryResult)
            => CheckLane(Lane);

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
            {
                Interlocked.Decrement(ref s_activeInstances);
                Lane?.Release();
            }
            base.Dispose();
        }
    }
}
