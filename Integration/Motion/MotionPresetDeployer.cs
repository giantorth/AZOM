using System;
using System.IO;
using System.Text;
using MozaPlugin.Diagnostics;

namespace MozaPlugin.Integration.Motion
{
    /// <summary>
    /// Writes the SimHub Motion presets that make the HMA150 output selectable. SimHub's
    /// controller picker is a hard-coded list, but it also loads every
    /// <c>Motion\Presets\**\*.shmotioncontroller</c>, and the setup wizard every
    /// <c>Motion\MotionHardwarePresets\&lt;dir&gt;\</c>; both resolve the output by
    /// <c>TypeName</c> from plugin assemblies. Deployed once the controller has been
    /// seen, so users without one get no entry. SimHub reads both folders when the
    /// picker / wizard opens — no restart needed. Idempotent: rewritten only when the
    /// content differs.
    /// </summary>
    internal static class MotionPresetDeployer
    {
        private const string ControllerPresetFile = "MOZA HMA150.shmotioncontroller";
        private const string HardwarePresetDir = "MOZA_HMA150";

        // CustomName is required: the picker rejects presets without one. No OutputId, so
        // every import keeps the fresh id its constructor generates.
        private const string ControllerPreset =
@"{
  ""Output"": {
    ""TypeName"": ""MozaHma150Output"",
    ""CustomName"": ""MOZA HMA150"",
    ""ShowOriginalName"": false,
    ""Settings"": {}
  }
}
";

        private const string HardwarePresetJson =
@"{
  ""Name"": ""HMA150"",
  ""Brand"": ""MOZA"",
  ""Comment"": null,
  ""ParentPreset"": null,
  ""IsGenericPreset"": false
}
";

        // 6DOF pose bypass: the controller does its own kinematics.
        private const string HardwarePresetConfig =
@"{
  ""Geometry"": {
    ""Axis"": [],
    ""TypeName"": ""GenericGeometry6DOFsBypass"",
    ""BaseGeometry"": {
      ""Settings"": {},
      ""TypeName"": ""Geometry6DOFsBypass""
    }
  },
  ""SecondaryGeometry"": null,
  ""Output"": {
    ""AggregatedOutputs"": [
      {
        ""IsEnabled"": true,
        ""Output"": {
          ""CustomName"": null,
          ""ShowOriginalName"": true,
          ""Settings"": {},
          ""TypeName"": ""MozaHma150Output""
        }
      }
    ],
    ""AutomaticOrigin"": null,
    ""TypeName"": ""ControllersAggregatorOutput""
  }
}
";

        /// <summary>Write both presets if missing or stale. Returns true when anything was written.</summary>
        public static bool Deploy()
        {
            string motionDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Motion");
            bool wrote = false;
            wrote |= WriteIfChanged(Path.Combine(motionDir, "Presets", ControllerPresetFile), ControllerPreset);
            string hw = Path.Combine(motionDir, "MotionHardwarePresets", HardwarePresetDir);
            wrote |= WriteIfChanged(Path.Combine(hw, "preset.json"), HardwarePresetJson);
            wrote |= WriteIfChanged(Path.Combine(hw, "config.shmotionoutput"), HardwarePresetConfig);
            if (wrote) MozaLog.Info("[AZOM/Motion] Deployed the MOZA HMA150 SimHub Motion presets");
            return wrote;
        }

        private static bool WriteIfChanged(string path, string content)
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == content) return false;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM/Motion] Could not write {path}: {ex.Message}");
                return false;
            }
        }
    }
}
