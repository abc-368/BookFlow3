using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BookFlow.App.Services
{
    public sealed class EventPipeClient : IDisposable
    {
        private readonly string _pipeName;
        private NamedPipeClientStream? _pipe;
        private CancellationTokenSource? _cts;
        private Task? _readerTask;

        public event Action? OrderUpdateReceived;
        public event Action? ExecutionUpdateReceived;
        public event Action? PositionUpdateReceived;
        public event Action<string>? AnyEventReceived; // raw json

        public EventPipeClient(string pipeName = "BookFlow_Event_Global")
        {
            _pipeName = pipeName;
        }

        public async Task<bool> ConnectAsync(int timeoutMs = 30000)
        {
            try
            {
                _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.In, PipeOptions.Asynchronous);
                await _pipe.ConnectAsync(timeoutMs);
                _pipe.ReadMode = PipeTransmissionMode.Message;
                _cts = new CancellationTokenSource();
                _readerTask = Task.Run(() => ReaderLoopAsync(_cts.Token));
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EventPipeClient] Connect failed: {ex.Message}");
                return false;
            }
        }

        private async Task ReaderLoopAsync(CancellationToken ct)
        {
            if (_pipe == null) return;
            var buffer = new byte[65536];
            using var ms = new MemoryStream();
            try
            {
                while (!ct.IsCancellationRequested && _pipe.IsConnected)
                {
                    int read = await _pipe.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                    if (read <= 0) { await Task.Delay(50, ct); continue; }
                    ms.Write(buffer, 0, read);
                    if (_pipe.IsMessageComplete)
                    {
                        var json = Encoding.UTF8.GetString(ms.ToArray());
                        ms.SetLength(0);
                        DispatchEvent(json);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EventPipeClient] Reader error: {ex.Message}");
            }
        }

        private void DispatchEvent(string json)
        {
            try
            {
                AnyEventReceived?.Invoke(json);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Type", out var typeEl)) return;
                var t = typeEl.GetString();
                switch (t)
                {
                    case "OrderUpdate": OrderUpdateReceived?.Invoke(); break;
                    case "ExecutionUpdate": ExecutionUpdateReceived?.Invoke(); break;
                    case "PositionUpdate": PositionUpdateReceived?.Invoke(); break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EventPipeClient] Dispatch error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                _cts?.Cancel();
                try { _readerTask?.Wait(250); } catch { }
                _pipe?.Dispose();
                _cts?.Dispose();
            }
            catch { }
        }
    }
}
