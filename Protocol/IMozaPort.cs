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
        /// <summary>True when <see cref="Read"/> returns whatever is buffered
        /// right now (possibly zero bytes) instead of blocking until the
        /// requested count arrives or a timeout expires. Ports that return
        /// immediately must NOT have their reads gated on
        /// <see cref="BytesToRead"/> — see the ReadLoop comment for why that
        /// gate loses data on the Wine device path.</summary>
        bool ReadReturnsImmediately { get; }
        int Read(byte[] buffer, int offset, int count);
        void Write(byte[] buffer, int offset, int count);
        void DiscardInBuffer();
        void DiscardOutBuffer();
        void Close();
    }
}
