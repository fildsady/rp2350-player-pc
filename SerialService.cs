using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;

namespace PicoAudioCore
{
    public class SerialService : IDisposable
    {
        const int VID = 0x2E8A;
        const int PID = 0xC0DE;
        const int ReportLen = 64;   /* HID report payload (no report-ID byte in firmware) */

        private HidDevice?  _device;
        private HidStream?  _stream;
        private CancellationTokenSource? _cts;
        private int _disconnectFired = 0;

        public event Action<string>? LineReceived;
        public event Action? Disconnected;
        public bool IsOpen => _stream != null;

        /* Kept for API compatibility — HID auto-discovers, no port name needed. */
        public static string[] GetPortNames() => new[] { "HID" };

        public bool Open(string _ = "HID")
        {
            Close();
            _device = DeviceList.Local.GetHidDeviceOrNull(VID, PID);
            if (_device == null) return false;
            try
            {
                _stream = _device.Open();
                _stream.ReadTimeout  = Timeout.Infinite;
                _stream.WriteTimeout = 2000;
            }
            catch { _stream = null; return false; }

            _cts = new CancellationTokenSource();
            _disconnectFired = 0;
            Task.Run(() => ReadLoop(_cts.Token));
            return true;
        }

        public void Close()
        {
            _cts?.Cancel();
            try { _stream?.Close(); } catch { }
            _stream?.Dispose();
            _stream = null;
        }

        /* Send a text command — packed into a 64-byte HID OUT report.
         * HidSharp requires a leading report-ID byte (0x00), so the write
         * buffer is ReportLen+1 bytes: [0x00, cmd_bytes...]. */
        public void Send(string cmd)
        {
            if (_stream == null) return;
            try
            {
                var buf = new byte[ReportLen + 1];
                buf[0] = 0x00;   /* report ID */
                var encoded = Encoding.UTF8.GetBytes(cmd);
                int len = Math.Min(encoded.Length, ReportLen - 1);
                Array.Copy(encoded, 0, buf, 1, len);
                _stream.Write(buf);
            }
            catch { }
        }

        /* SendBytes: not supported over HID — upload feature disabled. */
        public void SendBytes(byte[] data, int offset, int count) { }

        private void ReadLoop(CancellationToken ct)
        {
            /* HidSharp read buffer: ReportLen+1 (report-ID prefix byte). */
            var buf = new byte[ReportLen + 1];
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int read = _stream!.Read(buf, 0, buf.Length);
                    if (ct.IsCancellationRequested) break;
                    if (read < 2) continue;

                    /* buf[0] = report ID (0), buf[1..] = payload text */
                    int payloadStart = buf[0] == 0 ? 1 : 0;
                    string line = Encoding.UTF8
                        .GetString(buf, payloadStart, read - payloadStart)
                        .TrimEnd('\0', '\r', '\n');

                    if (!string.IsNullOrEmpty(line))
                        LineReceived?.Invoke(line);
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    if (Interlocked.Exchange(ref _disconnectFired, 1) == 0)
                    {
                        Close();
                        Disconnected?.Invoke();
                    }
                    break;
                }
            }
        }

        public void Dispose() => Close();
    }
}
