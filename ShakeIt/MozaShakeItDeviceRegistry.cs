using System;
using System.Collections.Generic;
using SimHub.Plugins.Devices;

namespace MozaPlugin.ShakeIt
{
    /// <summary>
    /// Registers the "MOZA Wheelbase LFE" haptics device in SimHub's device
    /// catalog. SimHub discovers this class by assembly-scanning for
    /// <see cref="IDeviceDescriptorsRegistry"/> at startup — BEFORE plugin Init,
    /// so nothing here may touch plugin state (must stay public with a
    /// parameterless ctor). The device carries a full embedded ShakeIt Motors
    /// editor whose mixed channel output lands in
    /// <see cref="MozaWheelbaseLfeChannelsProvider"/>. See docs/simhub.md
    /// § ShakeIt V3 for the mechanism.
    ///
    /// The instance type is SimHub's internal ShakeItV3DeviceInstance&lt;,&gt;
    /// closed over the internal MotorsWithFrequency tone mixer — both resolved
    /// by name so a SimHub rename degrades to "device type absent", never a crash.
    /// USB detection covers every wheelbase PID: LED-less bases get no other
    /// SimHub device entry at all, so without detection those users would never
    /// discover the haptics device. ignoreForArduino keeps SimHub's Arduino
    /// scanner off the MOZA serial ports.
    /// </summary>
    public sealed class MozaShakeItDeviceRegistry : IDeviceDescriptorsRegistry
    {
        internal const string WheelbaseDeviceTypeId = "F208F60B-0050-4E83-A874-AE28DD13F7AB";

        public IEnumerable<DeviceDescriptor> GetDevices()
        {
            var factory = BuildWheelbaseFactory();
            if (factory == null) yield break;
            yield return new DeviceDescriptor
            {
                DeviceTypeID = WheelbaseDeviceTypeId,
                Name = "Wheelbase LFE haptics",
                Brand = "MOZA",
                MaximumInstances = 1,
                Factory = factory,
                DetectionDescriptors = BuildWheelbaseDetection(),
            };
        }

        private static List<USBRequest> BuildWheelbaseDetection()
        {
            var list = new List<USBRequest>();
            foreach (ushort pid in Protocol.MozaUsbIds.PidsForCategory(Protocol.MozaDeviceCategory.Wheelbase))
                list.Add(new USBRequest(Protocol.MozaPortDiscovery.MozaVid, pid, ignoreForArduino: true));
            return list;
        }

        private static Func<DeviceInstance>? BuildWheelbaseFactory()
        {
            try
            {
                var asm = typeof(DeviceDescriptor).Assembly;   // SimHub.Plugins.dll
                var mixer = asm.GetType(
                    "SimHub.Plugins.DataPlugins.ShakeItV3.Device.MotorsWithFrequency.MotorsWithFrequencyOutputManager");
                var settingsOpen = asm.GetType(
                    "SimHub.Plugins.DataPlugins.ShakeItV3.Device.ShakeitSettingsMotorsWithFrequencyOutputManagerBase`2");
                var instanceOpen = asm.GetType(
                    "SimHub.Plugins.DataPlugins.ShakeItV3.Device.ShakeItV3DeviceInstance`2");
                if (mixer == null || settingsOpen == null || instanceOpen == null)
                {
                    MozaLog.Info("[AZOM/ShakeIt] SimHub ShakeIt internals not found; wheelbase haptics device not registered");
                    return null;
                }
                var settings = settingsOpen.MakeGenericType(mixer, typeof(MozaWheelbaseLfeChannelsProvider));
                var instance = instanceOpen.MakeGenericType(mixer, settings);
                return () => (DeviceInstance)Activator.CreateInstance(instance, nonPublic: true)!;
            }
            catch (Exception ex)
            {
                MozaLog.Info($"[AZOM/ShakeIt] wheelbase haptics registration failed: {ex.Message}");
                return null;
            }
        }
    }
}
