using System.IO.Ports;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// <see cref="IMozaPort"/> backed by <see cref="System.IO.Ports.SerialPort"/>
    /// opened by COM name. Used on native Windows (and as the fallback path).
    /// Settings mirror what <see cref="MozaSerialConnection"/> used before the
    /// abstraction existed — do not change them without re-validating the wheel.
    /// </summary>
    internal sealed class SerialPortMozaPort : IMozaPort
    {
        private readonly SerialPort _sp;

        public SerialPortMozaPort(string portName, int baudRate)
        {
            _sp = new SerialPort(portName, baudRate)
            {
                ReadTimeout = 500,
                WriteTimeout = 500,
                // Larger buffers cushion Wine/tty0tty session-burst contention.
                ReadBufferSize = 65536,
                WriteBufferSize = 16384,
                // CDC ACM: DTR is the host-connected signal; Open must assert it.
                DtrEnable = true,
            };
            _sp.Open();
        }

        public bool IsOpen => _sp.IsOpen;
        // SerialPort.Read blocks until at least one byte arrives or ReadTimeout
        // (500 ms) expires, then throws — so this path genuinely needs the
        // BytesToRead gate in ReadLoop.
        public bool ReadReturnsImmediately => false;
        public int BytesToRead => _sp.BytesToRead;
        public int Read(byte[] buffer, int offset, int count) => _sp.Read(buffer, offset, count);
        public void Write(byte[] buffer, int offset, int count) => _sp.Write(buffer, offset, count);
        public void DiscardInBuffer() => _sp.DiscardInBuffer();
        public void DiscardOutBuffer() => _sp.DiscardOutBuffer();
        public void Close() => _sp.Close();
        public void Dispose() => _sp.Dispose();
    }
}
