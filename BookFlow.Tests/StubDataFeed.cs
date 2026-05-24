using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using BookFlow.App.Interfaces;
using BookFlow.Shared.Contracts;
using BookFlow.Shared.Service;

namespace BookFlow.Tests
{
    /// <summary>
    /// Controllable IDataFeed for engine handshake tests: push market-data ticks on demand
    /// and gate the DOM snapshot completion so ticks can be injected during the seeding window.
    /// </summary>
    internal sealed class StubDataFeed : IDataFeed
    {
        private readonly Subject<UnifiedMarketDataMessage> _md = new();
        private readonly Subject<OrderStatusMessage> _os = new();
        private readonly Subject<PortfolioStateMessage> _pf = new();
        private readonly TaskCompletionSource<DomSnapshotResponse?> _snapTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DomSnapshotResponse? Snapshot { get; set; }
        public bool CompleteSnapshotImmediately { get; set; }

        public IObservable<UnifiedMarketDataMessage> MarketDataStream => _md;
        public IObservable<OrderStatusMessage> OrderStatusStream => _os;
        public IObservable<PortfolioStateMessage> PortfolioStream => _pf;
        public bool IsConnected => true;

#pragma warning disable 67
        public event EventHandler<bool>? ConnectionStatusChanged;
#pragma warning restore 67

        public Task<bool> ConnectAsync() => Task.FromResult(true);
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<OrderStatusMessage> SubmitOrderAsync(string instrumentName, OrderCommand orderCommand) => Task.FromResult(new OrderStatusMessage());

        public BracketOrderRequest? LastBracketRequest { get; private set; }
        public Task<OrderAck> SubmitBracketOrderAsync(BracketOrderRequest request)
        {
            LastBracketRequest = request;
            return Task.FromResult(new OrderAck { Status = BookFlowOrderStatus.Submitted, NtOrderId = "STUB-1" });
        }
        public Task<PortfolioStateMessage> RequestPortfolioStateAsync() => Task.FromResult(new PortfolioStateMessage());
        public Task<List<TickerInfo>> GetAvailableInstrumentsAsync() => Task.FromResult(new List<TickerInfo>());

        public Task<DomSnapshotResponse?> RequestDomSnapshotAsync(byte tickerId)
            => CompleteSnapshotImmediately ? Task.FromResult(Snapshot) : _snapTcs.Task;

        public void CompleteSnapshot() => _snapTcs.TrySetResult(Snapshot);
        public void Push(UnifiedMarketDataMessage m) => _md.OnNext(m);

        public void Dispose() { _md.Dispose(); _os.Dispose(); _pf.Dispose(); }
    }
}
