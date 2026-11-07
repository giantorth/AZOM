using System;
using System.Drawing;
using BA63Driver;
using BA63Driver.Interfaces;
using BA63Driver.Mapper;
using SerialDash;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;
using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;

namespace MozaPlugin.Devices.Haptics
{
    /// <summary>
    /// Stand-in for SimHub's StandardProtocolConnectionDevice manager on a
    /// haptics-only definition (S12, mBooster). Reports the device's real
    /// readiness, never touches HID, and hands out the motors driver.
    ///
    /// Haptics-only definitions have no LED sub-device to keep alive, so unlike
    /// the wheelbase one gate serves both the connection and the motors driver.
    ///
    /// Implements <see cref="IConnectableLedDeviceManager"/> deliberately:
    /// without it, StandardProtocolConnectionDevice.DataUpdate calls Display()
    /// with six empty colour arrays every tick.
    ///
    /// Binds 9.12-era SimHub/BA63 members: only construct it behind
    /// <see cref="MozaBaseHapticsBridge.IsSupported"/>.
    /// </summary>
    internal sealed class MozaHapticsConnectionManager : ILedDeviceManager, IConnectableLedDeviceManager
    {
        private readonly Func<bool> _isConnected;
        private readonly IMotorsDriver _motors;
        private bool _lastConnected;

        internal MozaHapticsConnectionManager(Func<bool> isConnected, IMotorsDriver motors)
        {
            _isConnected = isConnected;
            _motors = motors;
        }

        public LedModuleSettings? LedModuleSettings { get; set; }
        public LedDeviceState? LastState { get; private set; }

#pragma warning disable CS0067 // Required by ILedDeviceManager; this device renders nothing
        public event EventHandler? BeforeDisplay;
        public event EventHandler? AfterDisplay;
        public event EventHandler? OnError;
#pragma warning restore CS0067
        public event EventHandler? OnConnect;
        public event EventHandler? OnDisconnect;

        public bool IsConnected() => _isConnected();

        /// <summary>Raise SimHub's connect/disconnect events when the state flips. Called from the device extension's DataUpdate.</summary>
        public void UpdateConnectionState()
        {
            bool now = IsConnected();
            if (now == _lastConnected) return;
            _lastConnected = now;
            if (now) OnConnect?.Invoke(this, EventArgs.Empty);
            else OnDisconnect?.Invoke(this, EventArgs.Empty);
        }

        public void EnsureConnected() { }

        public void Display(Func<Color[]> leds, Func<Color[]> buttons, Func<Color[]> encoders,
            Func<Color[]> matrix, Func<Color[]> rawState, Func<Color[]> overrideState, bool forceRefresh,
            Func<object>? extraData = null, double rpmBrightness = 1.0, double buttonsBrightness = 1.0,
            double encodersBrightness = 1.0, double matrixBrightness = 1.0)
        {
            // The device owns no pixels.
        }

        public string GetSerialNumber() => _motors.SerialNumber ?? "";
        public string GetFirmwareVersion() => _motors.FirmwareVersion ?? "";
        public object GetDriverInstance() => _motors;
        public void Close() => _motors.Clear();
        public void ResetDetection() { }
        public void SerialPortCanBeScanned(object sender, SerialDashController.ScanArgs e) { }
        public IPhysicalMapper GetPhysicalMapper() => new NeutralLedsMapper();
        public ILedDriverBase? GetLedDriver() => null;
    }
}
