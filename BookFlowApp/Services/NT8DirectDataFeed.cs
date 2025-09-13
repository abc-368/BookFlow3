using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using BookFlow.Shared.Contracts;
using BookFlow.App.Interfaces;
using BookFlow.Shared.IPC;

namespace BookFlow.App.Services
{
    public class NT8DirectDataFeed : IDataFeed, IDisposable
    {
        private readonly ControlPipeClient _controlClient;
        private readonly Subject<UnifiedMarketDataMessage> _marketDataSubject = new();
        private readonly Subject<OrderStatusMessage> _orderStatusSubject = new();
        private readonly Subject<PortfolioStateMessage> _portfolioSubject = new();
        private bool _disposed;
        private bool _connected;

        // Global market data reader (SharedRingBuffer)
        private SharedRingBuffer? _dataChannel;
        private CancellationTokenSource? _readerCts;
        private Task? _readerTask;

        public event EventHandler<bool>? ConnectionStatusChanged;
        public IObservable<UnifiedMarketDataMessage> MarketDataStream => _marketDataSubject.AsObservable();
        public IObservable<OrderStatusMessage> OrderStatusStream => _orderStatusSubject.AsObservable();
        public IObservable<PortfolioStateMessage> PortfolioStream => _portfolioSubject.AsObservable();
        public bool IsConnected => _connected;

        public NT8DirectDataFeed()
        {
            _controlClient = new ControlPipeClient("BookFlow_Control_Global");
        }

        public async Task<bool> ConnectAsync()
        {
            if (_disposed) return false;
            if (_connected) return true;

            // Connect control channel (orders/requests)
            await _controlClient.ConnectAsync();
            _connected = true;
            ConnectionStatusChanged?.Invoke(this, true);

            // Start market data reader on the global shared ring buffer
            // NOTE: Capacity must match NT8 side (BookFlowAddOn uses 1024*1024)
            _dataChannel = new SharedRingBuffer("BookFlow_Data_Global", 1024 * 1024);
            _readerCts = new CancellationTokenSource();
            _readerTask = Task.Run(() => ReaderLoop(_readerCts.Token));
            return true;
        }

        private void ReaderLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && !_disposed)
                {
                    // Wait until NT8 signals data is available
                    _dataChannel?.WaitForData();
                    if (ct.IsCancellationRequested || _disposed) break;

                    // Drain all available messages
                    UnifiedMarketDataMessage msg;
                    int safety = 0;
                    while (_dataChannel != null && _dataChannel.TryRead(out msg))
                    {
                        _marketDataSubject.OnNext(msg);
                        if (++safety > 50000) break; // safety guard
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // shutting down
            }
            catch (Exception)
            {
                // Swallow unexpected reader errors to avoid tearing down the app; user will see no data
            }
        }

        public async Task DisconnectAsync()
        {
            if (!_connected) return;
            _connected = false;
            ConnectionStatusChanged?.Invoke(this, false);

            // Stop reader
            try { _readerCts?.Cancel(); } catch { }
            try { if (_readerTask != null) await _readerTask; } catch { }
            _readerTask = null;
            _readerCts?.Dispose();
            _readerCts = null;

            _dataChannel?.Dispose();
            _dataChannel = null;
        }

        public async Task<OrderStatusMessage> SubmitOrderAsync(string instrumentName, OrderCommand orderCommand)
        {
            if (!_connected) throw new InvalidOperationException("Not connected");
            var resp = await _controlClient.SubmitOrderAsync(instrumentName, orderCommand);
            return resp ?? new OrderStatusMessage { ClientOrderId = orderCommand.ClientOrderId, Status = OrderCommand.OrderStatus.Rejected, Message = "No response", Timestamp = DateTime.UtcNow };
        }

        public async Task<PortfolioStateMessage> RequestPortfolioStateAsync()
        {
            if (!_connected) throw new InvalidOperationException("Not connected");
            var resp = await _controlClient.RequestPortfolioStateAsync();
            return resp ?? new PortfolioStateMessage { Timestamp = DateTime.UtcNow };
        }

        public async Task<List<TickerInfo>> GetAvailableInstrumentsAsync()
        {
            if (!_connected) throw new InvalidOperationException("Not connected");
            var resp = await _controlClient.GetTickerDictionaryAsync();
            var list = new List<TickerInfo>();
            if (resp?.TickerDictionary != null)
            {
                foreach (var kv in resp.TickerDictionary)
                {
                    var info = TickerInfo.Parse(kv.Key, kv.Value);
                    if (info != null) list.Add(info);
                }
            }
            return list;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _readerCts?.Cancel(); } catch { }
            try { _readerTask?.Wait(250); } catch { }
            _dataChannel?.Dispose();
            _marketDataSubject.OnCompleted();
            _orderStatusSubject.OnCompleted();
            _portfolioSubject.OnCompleted();
            _controlClient.Dispose();
        }
    }
}