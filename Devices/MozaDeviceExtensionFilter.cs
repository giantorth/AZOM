using System;
using System.Collections.Generic;
using SimHub.Plugins.Devices;
using SimHub.Plugins.Devices.DeviceExtensions;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Tells SimHub to attach MozaWheelDeviceExtension to MOZA wheel devices.
    /// SimHub discovers this via assembly scanning for IDeviceExtensionFilter implementations.
    /// </summary>
    public class MozaDeviceExtensionFilter : IDeviceExtensionFilter
    {
        public IEnumerable<Type> GetExtensionsTypes(DeviceInstance device)
        {
            var typeId = device.DeviceDescriptor.DeviceTypeID ?? "";

            MozaLog.Debug($"[AZOM] ExtensionFilter checking DeviceTypeID: {typeId}");

            // Any known wheel device ID (old template, new generic, old-protocol) → wheel extension
            if (MozaDeviceConstants.GetWheelModelPrefix(typeId) != null)
            {
                yield return typeof(MozaWheelDeviceExtension);
            }

            if (MozaDeviceConstants.IsDashDevice(typeId))
            {
                yield return typeof(MozaDashDeviceExtension);
            }

            if (MozaDeviceConstants.IsBaseAmbientDevice(typeId))
            {
                yield return typeof(MozaBaseDeviceExtension);
            }
        }
    }
}
