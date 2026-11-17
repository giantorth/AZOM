using System;
using System.Collections.Generic;
using System.Threading;
using MozaPlugin.Devices.Motion;
using MozaPlugin.Diagnostics;
using MozaPlugin.Settings;

namespace MozaPlugin
{
    /// <summary>
    /// MOZA Motion Controller (HMA150 platform) lane. It opens the port itself while the SimHub
    /// Motion output or its settings panel holds it; the 5 s reconnect tick only persists the
    /// port and deploys presets. The SimHub Motion output attaches to it through
    /// <see cref="SharedMotionController"/>.
    ///
    /// The lane outlives the plugin instance while something holds it. SimHub restarts every
    /// plugin when it switches game, but its Motion setup, and so the started HMA150 output,
    /// carries on across that restart. Disposing the lane there dropped a running platform to
    /// standby (motors off) under the user and left the output on a new, never-engaged lane
    /// ("Output connection failure"). A held lane is handed to the next instance instead and
    /// keeps its heartbeat and stream running; it is disposed on process exit.
    /// </summary>
    public partial class MozaPlugin
    {
        private MozaMotionController? _motionController;
        private static MozaMotionController? s_persistentMotionController;
        private readonly MotionGameFeed _motionFeed = new MotionGameFeed();
        private bool _motionPresetsDeployed;

        internal MozaMotionController? MotionController => _motionController;

        /// <summary>
        /// The lane for callers that outlive a plugin instance (the SimHub Motion output and its
        /// panel): the current instance's, or the kept one while SimHub is between instances.
        /// </summary>
        internal static MozaMotionController? SharedMotionController
            => Instance?._motionController ?? s_persistentMotionController;

        /// <summary>Latest game telemetry as a 0x22/00 frame, fed from DataUpdate.</summary>
        internal MotionGameFeed MotionFeed => _motionFeed;

        /// <summary>Whether SimHub Motion has the HMA150 output in its active setup (status banner).</summary>
        internal Integration.Motion.MotionSetupStatus MotionSetupStatus
            => Integration.Motion.MotionIntegration.GetSetupStatus(_pluginManager);

        /// <summary>Whether SimHub's Manual control (or its automated test cycles) or Hop-in position is moving the active setup.</summary>
        internal bool MotionManualControlActive
            => Integration.Motion.MotionIntegration.IsManualControlActive(_pluginManager);

        private void InitMotionLane()
        {
            var kept = s_persistentMotionController;
            if (kept != null && !kept.IsDisposed)
            {
                _motionController = kept;
                MozaLog.Info($"[AZOM/Motion] Reusing the motion lane from the prior plugin instance ({kept.Phase})");
                return;
            }
            _motionController = new MozaMotionController(() => IsShuttingDown);
            s_persistentMotionController = _motionController;
            if (!string.IsNullOrEmpty(_settings.LastMotionPort))
                _motionController.LastPortName = _settings.LastMotionPort;
            if (!string.IsNullOrEmpty(_settings.LastMotionDeviceId))
                _motionController.LastDeviceId = _settings.LastMotionDeviceId;        }

        /// <summary>Reconnect-tick hook; same authoritative-device-source gate as the other dedicated lanes.</summary>
        private void TryConnectMotion()
        {
            var lane = _motionController;
            if (lane == null || IsShuttingDown || !lane.IsPresent) return;

            // Presets only once a controller has been seen, so other users get no entry.
            if (!_motionPresetsDeployed)
            {
                Integration.Motion.MotionPresetDeployer.Deploy();
                _motionPresetsDeployed = true;
            }

            // Once per SimHub Motion setup: start mode "Last" -> "Off", so SimHub doesn't lift
            // the platform at launch just because motion was enabled when it last closed.
            if (Integration.Motion.MotionIntegration.DefaultStartModeToOff(
                    _pluginManager, _settings.MotionStartModeDefaultedSetups))
                ScheduleSave();

            if (!lane.IsConnected) return;
            bool dirty = false;
            string port = lane.LastPortName ?? "";
            if (port.Length != 0 && _settings.LastMotionPort != port) { _settings.LastMotionPort = port; dirty = true; }
            string id = lane.LastDeviceId ?? "";
            if (_settings.LastMotionDeviceId != id) { _settings.LastMotionDeviceId = id; dirty = true; }
            if (dirty) ScheduleSave();
        }

        /// <summary>Profile effect percentages → the lane's desired values (null = leave the controller's).</summary>
        internal static MotionEffect?[] ToDesiredEffects(MotionEffectsSettings? s)
        {
            var desired = new MotionEffect?[MotionEffectsSettings.Count];
            if (s == null) return desired;
            for (int i = 0; i < desired.Length; i++)
            {
                var (intensity, smoothness) = s.Get(i);
                if (intensity >= 0 && smoothness >= 0)
                    desired[i] = new MotionEffect(intensity / 100f, smoothness / 100f);
            }
            return desired;
        }

        private static float? Fraction(int percent) => percent >= 0 ? percent / 100f : (float?)null;

        /// <summary>Hand a profile's motion effects to the lane (null fields leave the controller's).</summary>
        internal static void PushMotionEffects(MozaMotionController? lane, MotionEffectsSettings? s)
            => lane?.SetDesiredEffects(ToDesiredEffects(s),
                Fraction(s?.EngineVibrationStrength ?? -1), Fraction(s?.TactileIntensity ?? -1));

        /// <summary>
        /// Store effect values (index → intensity/smoothness %) and the engine vibration /
        /// tactile percentages (null = unchanged) in the active profile, then push them.
        /// </summary>
        internal void SetMotionEffects(IReadOnlyDictionary<int, (int Intensity, int Smoothness)> values,
            int? engineStrength, int? tactile)
        {
            var profile = _settings?.ProfileStore?.CurrentProfile;
            if (profile == null) return;
            var effects = profile.MotionEffects ??= new MotionEffectsSettings();
            foreach (var kv in values) effects.Set(kv.Key, kv.Value.Intensity, kv.Value.Smoothness);
            if (engineStrength.HasValue) effects.EngineVibrationStrength = engineStrength.Value;
            if (tactile.HasValue) effects.TactileIntensity = tactile.Value;
            SaveSettings();
            PushMotionEffects(_motionController, effects);
        }

        /// <summary>
        /// Plugin teardown: a lane nothing holds is disposed; a held one (started output, open
        /// settings panel) stays up for the next instance, so a game switch never moves the platform.
        /// </summary>
        private void DisposeMotionLane()
        {
            var lane = _motionController;
            _motionController = null;
            if (lane == null) return;
            if (lane.InUse && !lane.IsDisposed)
            {
                MozaLog.Info($"[AZOM/Motion] End: keeping the motion lane alive across plugin reload ({lane.Phase})");
                return;
            }
            try { lane.Dispose(); } catch { }
            if (ReferenceEquals(s_persistentMotionController, lane)) s_persistentMotionController = null;
        }

        /// <summary>Process exit: a platform still running goes to standby before the port closes.</summary>
        private static void DisposePersistentMotionLane()
        {
            var lane = Interlocked.Exchange(ref s_persistentMotionController, null);
            try { lane?.Dispose(); } catch { }
        }
    }
}
