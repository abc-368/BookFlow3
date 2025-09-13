using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BookFlow.App.Services
{
    public sealed class EventTcpClient : IDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private TcpClient? _client;
        private CancellationTokenSource? _cts;
        private Task? _readerTask;

        public event Action? OrderUpdateReceived;
        public event Action? ExecutionUpdateReceived;
        public event Action? PositionUpdateReceived;
        public event Action<string>? AnyEventReceived; // raw json

        public EventTcpClient(string host = "127.0.0.1", int port = 38755)
        {
            _host = host;
            _port = port;
        }

        public async Task<bool> ConnectAsync(int timeoutMs = 5000)
        {
            try
            {
                _client = new TcpClient();
                var connectTask = _client.ConnectAsync(_host, _port);
                if (await Task.WhenAny(connectTask, Task.Delay(timeoutMs)) == connectTask)
                {
                    await connectTask; // Propagate exceptions
                    _cts = new CancellationTokenSource();
                    _readerTask = Task.Run(() => ReaderLoopAsync(_cts.Token));
                    System.Diagnostics.Debug.WriteLine("[EventTcpClient] Successfully connected.");
                    return true;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[EventTcpClient] Connection timed out.");
                    _client.Dispose();
                    _client = null;
                    return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EventTcpClient] Connect failed: {ex.Message}");
                if (_client != null) { _client.Dispose(); _client = null; }
                return false;
            }
        }

        private async Task ReaderLoopAsync(CancellationToken ct)
        {
            if (_client == null) return;
            var stream = _client.GetStream();
            var lengthBuffer = new byte[4];

            try
            {
                while (!ct.IsCancellationRequested && _client.Connected)
                {
                    // 1. Read the 4-byte length prefix
                    int bytesRead = 0;
                    while (bytesRead < lengthBuffer.Length)
                    {
                        var read = await stream.ReadAsync(lengthBuffer, bytesRead, lengthBuffer.Length - bytesRead, ct);
                        if (read == 0) throw new IOException("Socket closed.");
                        bytesRead += read;
                    }
                    var messageLength = BitConverter.ToInt32(lengthBuffer, 0);

                    // 2. Read the message content
                    var messageBuffer = new byte[messageLength];
                    bytesRead = 0;
                    while (bytesRead < messageLength)
                    {
                        var read = await stream.ReadAsync(messageBuffer, bytesRead, messageLength - bytesRead, ct);
                        if (read == 0) throw new IOException("Socket closed.");
                        bytesRead += read;
                    }

                    var json = Encoding.UTF8.GetString(messageBuffer);
                    DispatchEvent(json);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { System.Diagnostics.Debug.WriteLine("[EventTcpClient] Disconnected from server."); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EventTcpClient] Reader error: {ex.Message}");
            }
            finally
            {
                Dispose();
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
                System.Diagnostics.Debug.WriteLine($"[EventTcpClient] Dispatch error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                _cts?.Cancel();
                _client?.Close();
                _client?.Dispose();
                _cts?.Dispose();
                _client = null;
            }
            catch { }
        }
    }
}
