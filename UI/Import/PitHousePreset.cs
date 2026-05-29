using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.UI.Import
{
    /// <summary>
    /// Parsed top-level structure of a MOZA PitHouse preset JSON file. The
    /// <see cref="DeviceParams"/> blob stays as a <see cref="JObject"/> so the
    /// per-category mappers can pull the fields they understand without locking
    /// the schema — PitHouse adds new keys over time and we don't want a strict
    /// model rejecting a future preset just because of an unknown field.
    /// </summary>
    public sealed class PitHousePreset
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string DeviceType { get; set; } = "";       // "Motor" | "Steering Wheel" | "Pedals"
        public List<string> Devices { get; set; } = new List<string>();
        public JObject DeviceParams { get; set; } = new JObject();
        public string SourcePath { get; set; } = "";       // absolute path the preset was loaded from

        /// <summary>
        /// Best-effort short label combining the user-friendly name with the
        /// device model (e.g. "R9-Forza Motorsport2023-Marty (R9)"). Used in
        /// the picker list and confirmation dialog so the user can tell two
        /// presets apart at a glance.
        /// </summary>
        public string DisplayLabel
        {
            get
            {
                var devicesLabel = (Devices != null && Devices.Count > 0)
                    ? " (" + string.Join(", ", Devices) + ")"
                    : "";
                return (string.IsNullOrEmpty(Name) ? Id : Name) + devicesLabel;
            }
        }
    }
}
