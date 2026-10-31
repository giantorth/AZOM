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
        public string Name { get; set; } = "";
        public string DeviceType { get; set; } = "";       // "Motor" | "Steering Wheel" | "Pedals"
        public List<string> Devices { get; set; } = new List<string>();
        public JObject DeviceParams { get; set; } = new JObject();
    }
}
