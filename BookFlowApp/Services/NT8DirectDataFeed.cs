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
    /// <summary>
    /// Market data feed + control plane bridge. High-frequency L1/L2 still flows over
    /// the shared-memory ring (fastest path). Control operations (orders, portfolio,
    /// ticker dictionary) now go over the WCF service via <see cref="BookFlowServiceClient"/>.
    /// </summary>
    public class NT8DirectDataFeed : IDataFeed, IDisposable
    {
        private readonly BookFlowServiceClient _serviceClient;
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
            _serviceClient = new BookFlowServiceClient("NT8DirectDataFeed");
        }

        public async Task<bool> ConnectAsync()
        {
            if (_disposed) return false;
            if (_connected) return true;

            // Connect the WCF control channel.
            if (!await _serviceClient.ConnectAsync())
                return false;
            _connected = true;
            ConnectionStatusChanged?.Invoke(this, true);

            // Start market data reader on the global shared ring buffer.
            // NOTE: Capacity must match NT8 side (BookFlowAddOn uses 1024*1024).
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
                    _dataChannel?.WaitForData();
                    if (ct.IsCancellationRequested || _disposed) break;

                    UnifiedMarketDataMessage msg;
                    int safety = 0;
                    while (_dataChannel != null && _dataChannel.TryRead(out msg))
                    {
                        _marketDataSubject.OnNext(msg);
                        if (++safety > 50000) break; // safety guard
                    }
                }
            }
            catch (ObjectDisposedException) { /* shutting down */ }
            catch (Exception) { /* swallow to keep app alive; user sees no data */ }
        }

        public async Task DisconnectAsync()
        {
            if (!_connected) return;
            _connected = false;
            ConnectionStatusChanged?.Invoke(this, false);

            try { _readerCts?.Cancel(); } catch { }
            try { if (_readerTask != null) await _readerTask; } catch { }
            _readerTask = null;
            _readerCts?.Dispose();
            _readerCts = null;

            _dataChannel?.Dispose();
            _dataChannel = null;

            _serviceClient.Disconnect();
        }

        public async Task<OrderStatusMessage> SubmitOrderAsync(string instrumentName, OrderCommand orderCommand)
        {
            if (!_connected) throw new InvalidOperationException("Not connected");
            switch (orderCommand.Action)
            {
                case OrderCommand.OrderAction.CancelAll:
                    return WcfContractMapper.FromOperationResult(
                        await _serviceClient.CancelAllOrdersAsync(string.Empty), orderCommand.ClientOrderId, OrderCommand.OrderStatus.Cancelled);
                case OrderCommand.OrderAction.CancelAtPrice:
                    return WcfContractMapper.FromOperationResult(
                        await _serviceClient.CancelAtPriceAsync(string.Empty, instrumentName, orderCommand.LimitPrice), orderCommand.ClientOrderId, OrderCommand.OrderStatus.Cancelled);
                case OrderCommand.OrderAction.Flat:
                    return WcfContractMapper.FromOperationResult(
                        await _serviceClient.FlattenPositionAsync(string.Empty, instrumentName), orderCommand.ClientOrderId, OrderCommand.OrderStatus.Submitted);
                default:
                    return WcfContractMapper.FromAck(
                        await _serviceClient.SubmitOrderAsync(WcfContractMapper.ToOrderRequest(instrumentName, orderCommand)));
            }
        }

        public async Task<BookFlow.Shared.Service.OrderAck> SubmitBracketOrderAsync(BookFlow.Shared.Service.BracketOrderRequest request)
        {
            if (!_connected) throw new InvalidOperationException("Not connected");
            return await _serviceClient.SubmitBracketOrderAsync(request);
        }

        public async Task<PortfolioStateMessage> RequestPortfolioStateAsync()
        {
            if (!_connected) throw new InvalidOperationException("Not connected");
            var snap = await _serviceClient.RequestPortfolioStateAsync();
            return WcfContractMapper.ToPortfolioStateMessage(snap);
        }

        public async Task<List<TickerInfo>> GetAvailableInstrumentsAsync()
        {
            if (!_connected) throw new InvalidOperationException("Not connected");
            var snap = await _serviceClient.GetTickerSnapshotAsync();
            return WcfContractMapper.ToTickerInfoList(snap);
        }

        public async Task<BookFlow.Shared.Service.DomSnapshotResponse?> RequestDomSnapshotAsync(byte tickerId)
        {
            if (!_connected) return null;
            try { return await _serviceClient.RequestDomSnapshotAsync(tickerId); }
            catch { return null; }
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
            _serviceClient.Dispose();
        }
    }
}
