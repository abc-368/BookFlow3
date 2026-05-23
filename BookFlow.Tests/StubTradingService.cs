using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using BookFlow.App.Interfaces;
using BookFlow.App.Models;
using BookFlow.Shared.Contracts;

namespace BookFlow.Tests
{
    /// <summary>Minimal ITradingService for engine tests (no real connection).</summary>
    internal sealed class StubTradingService : ITradingService
    {
        private readonly ObservableCollection<WorkingOrderMessage> _orders = new();
        public StubTradingService() => WorkingOrders = new ReadOnlyObservableCollection<WorkingOrderMessage>(_orders);

        public bool IsTradingEnabled => false;
        public bool IsConnected => false;

#pragma warning disable 67
        public event EventHandler<bool>? TradingStatusChanged;
        public event Action? OrderBookChanged;
        public event Action? PortfolioChanged;
#pragma warning restore 67

        public ReadOnlyObservableCollection<WorkingOrderMessage> WorkingOrders { get; }
        public PositionSnapshot GetPositionSnapshot(string instrumentName) => new(instrumentName, 0, 0, 0, 0, 0, 0);
        public Task<OrderStatusMessage> SubmitOrderAsync(string instrumentName, OrderCommand orderCommand) => Task.FromResult(new OrderStatusMessage());
        public Task<OrderStatusMessage> CancelAllOrdersAsync(string instrumentName) => Task.FromResult(new OrderStatusMessage());
        public Task<OrderStatusMessage> CancelOrdersAtPriceAsync(string instrumentName, decimal price) => Task.FromResult(new OrderStatusMessage());
        public Task<OrderStatusMessage> FlattenPositionAsync(string instrumentName) => Task.FromResult(new OrderStatusMessage());
    }
}
