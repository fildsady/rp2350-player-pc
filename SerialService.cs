using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace RP2350Player
{
    public class SerialService : IDisposable
    {
        private SerialPort? _port;
        private CancellationTokenSource? _cts;
        private int _disconnectFired = 0;   /* guard — fire Disconnected only once */

        public event Action<string>? LineReceived;
        public event Action? Disconnected;
        public bool IsOpen => _port?.IsOpen == true;

        public static string[] GetPortNames() => SerialPort.GetPortNames();

        public bool Open(string portName)
        {
            Close();   /* always clean up previous port before opening a new one */
            try
            {
                _port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout  = 500,
                    WriteTimeout = 500,
                    NewLine      = "\n",  /* firmware sends \n (most lines) and \r\n (ACKs) */
                    DtrEnable    = true,  /* Pico SDK checks DTR via tud_cdc_connected() before printf — must be true or board drops all responses */
                };
                _port.Open();
                _cts = new CancellationTokenSource();
                _disconnectFired = 0;
                Task.Run(() => ReadLoop(_cts.Token));
                return true;
            }
            catch { return false; }
        }

        public void Close()
        {
            _cts?.Cancel();
            try { _port?.Close(); } catch { }
            _port?.Dispose();
            _port = null;
        }

        public void Send(string cmd)
        {
            if (_port?.IsOpen != true) return;
            try { _port.WriteLine(cmd); } catch { }
        }

        public void SendBytes(byte[] data, int offset, int count)
        {
            if (_port?.IsOpen != true) return;
            try { _port.BaseStream.Write(data, offset, count); _port.BaseStream.Flush(); } catch { }
        }

        private void ReadLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    /* cache _port — it can be nulled by Close() on another thread */
                    var p = _port;
                    if (p == null || !p.IsOpen) break;

                    string line = p.ReadLine().TrimEnd('\r', '\n');
                    LineReceived?.Invoke(line);
                }
                catch (TimeoutException) { }
                catch
                {
                    /* fire Disconnected exactly once, then exit loop */
                    if (Interlocked.Exchange(ref _disconnectFired, 1) == 0)
                    {
                        Close();             /* release COM port immediately */
                        Disconnected?.Invoke();
                    }
                    break;
                }
            }
        }

        public void Dispose() => Close();
    }
}
