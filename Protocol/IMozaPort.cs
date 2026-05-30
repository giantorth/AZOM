using System;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Transport abstraction for the wheelbase serial link, so the read/write
    /// loops in <see cref="MozaSerialConnection"/> are independent of HOW the
    /// port was opened. Current implementation: <see cref="SerialPortMozaPort"/>
    /// (System.IO.Ports.SerialPort by COM name). The surface mirrors exactly the
    /// <c>SerialPort</c> members the connection uses (IsConnected, ReadLoop,
    /// WriteLoop, Disconnect).
    /// </summary>
    internal interface IMozaPort : IDisposable
    {
        bool IsOpen { get; }
        int BytesToRead { get; }
        int Read(byte[] buffer, int offset, int count);
        void Write(byte[] buffer, int offset, int count);
        void DiscardInBuffer();
        void DiscardOutBuffer();
        void Close();
    }
}
