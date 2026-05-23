using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookFlow.Shared.Contracts;
using BookFlow.Shared.Service;
using BookFlow.App.Interfaces;
using BookFlow.App.Models;

namespace BookFlow.App.Services
{
    /// <summary>
    /// Trading + portfolio service backed by the BookFlow WCF duplex channel.
    ///
    /// Reconciliation model (NT8 is source of truth): on connect the full
    /// <see cref="PortfolioSnapshot"/> is pulled and its Version cached. Each broker
    /// event arrives as a versioned delta and is applied incrementally (immediate
    /// sync, no full re-pull). If a version gap is detected — or a heartbeat reports a
    /// version ahead of what we've applied, or the channel reconnects — a debounced
    /// full resync runs to converge back to NT8's authoritative state.
    /// </summary>
    public class NT8TradingService : ITradingService, IDisposable
    {
        private readonly BookFlowServiceClient _client;
        private readonly ObservableCollection<WorkingOrderMessage> _workingOrders;
        public ReadOnlyObservableCollection<WorkingOrderMessage> WorkingOrders { get; }

        private bool _isTradingEnabled;
        private bool _isConnected;
        private bool _disposed;

        public bool IsTradingEnabled => _isTradingEnabled && _isConnected;
        public bool IsConnected => _isConnected;
        public event EventHandler<bool>? TradingStatusChanged;
        public event Action<string>? LogMessage;
        public event Action? OrderBookChanged;
        public event Action? PortfolioChanged;

        private readonly ConcurrentDictionary<string, PositionSnapshot> _positions = new();
        private readonly SynchronizationContext? _uiContext;

        // Reconciliation state. Touched only on the UI context to stay serialized.
        private long _lastAppliedVersion;
        private readonly object _resyncLock = new object();
        private System.Threading.Timer? _resyncTimer;

        private enum DeltaAction { Apply, Ignore, Resync }

        public NT8TradingService(SynchronizationContext? uiContext = null)
        {
            _client = new BookFlowServiceClient("NT8TradingService");
            _workingOrders = new ObservableCollection<WorkingOrderMessage>();
            WorkingOrders = new ReadOnlyObservableCollection<WorkingOrderMessage>(_workingOrders);
            _uiContext = uiContext ?? SynchronizationContext.Current;

            _client.OrderUpdated += OnOrderDelta;
            _client.ExecutionUpdated += OnExecutionDelta;
            _client.PositionUpdated += OnPositionDelta;
            _client.AccountItemUpdated += OnAccountDelta;
            _client.HeartbeatReceived += OnHeartbeat;
            _client.ConnectionChanged += OnConnectionChanged;

            _ = ConnectAsync();
        }

        public async Task<bool> ConnectAsync(int timeoutMs = 30000)
        {
            if (_disposed) return false;
            if (_isConnected) return true;
            if (!await _client.ConnectAsync()) return false;
            _isConnected = true;
            await RefreshPortfolioAsync();
            ExecuteOnUi(() => TradingStatusChanged?.Invoke(this, IsTradingEnabled));
            return true;
        }

        private void OnConnectionChanged(bool connected)
        {
            _isConnected = connected;
            if (connected) ScheduleResync(); // missed events while disconnected -> converge
            ExecuteOnUi(() => TradingStatusChanged?.Invoke(this, IsTradingEnabled));
        }

        // ---- Full resync (authoritative) -------------------------------------------

        private void ScheduleResync()
        {
            lock (_resyncLock)
            {
                if (_disposed) return;
                if (_resyncTimer == null)
                    _resyncTimer = new System.Threading.Timer(_ => { _ = RefreshPortfolioAsync(); }, null, 150, Timeout.Infinite);
                else
                    _resyncTimer.Change(150, Timeout.Infinite);
            }
        }

        private async Task RefreshPortfolioAsync()
        {
            try
            {
                var wcfSnap = await _client.RequestPortfolioStateAsync();
                if (wcfSnap == null) return;
                var snapshot = WcfContractMapper.ToPortfolioStateMessage(wcfSnap);
                ExecuteOnUi(() =>
                {
                    _lastAppliedVersion = wcfSnap.Version;

                    _workingOrders.Clear();
                    foreach (var o in snapshot.Orders) _workingOrders.Add(o);

                    foreach (var pos in snapshot.Positions)
                    {
                        var snap = new PositionSnapshot(pos.Instrument, 0, pos.Quantity, (decimal)pos.AveragePrice, (decimal)pos.UnrealizedPnL, (decimal)pos.RealizedPnL, 0, 0, 0, pos.LastUpdateTime);
                        _positions.AddOrUpdate(pos.Instrument, snap, (key, old) => snap);
                    }
                    var present = snapshot.Positions.Select(p => p.Instrument).ToHashSet();
                    foreach (var key in _positions.Keys.ToList())
                        if (!present.Contains(key)) _positions.TryRemove(key, out _);

                    _isTradingEnabled = snapshot.Account != null && !string.IsNullOrEmpty(snapshot.Account.AccountName);
                    OrderBookChanged?.Invoke();
                    PortfolioChanged?.Invoke();
                });
            }
            catch (Exception ex) { OnLogMessage($"Portfolio resync failed: {ex.Message}"); }
        }

        // ---- Versioned delta application (UI-thread serialized) --------------------

        private DeltaAction Classify(long version)
        {
            // Caller is on the UI context, so _lastAppliedVersion access is serialized.
            if (version <= _lastAppliedVersion) return DeltaAction.Ignore;     // already reflected
            if (version == _lastAppliedVersion + 1) { _lastAppliedVersion = version; return DeltaAction.Apply; }
            return DeltaAction.Resync;                                          // gap -> missed events
        }

        private void OnOrderDelta(OrderUpdateNotification n)
        {
            if (n?.Order == null) return;
            ExecuteOnUi(() =>
            {
                switch (Classify(n.Version))
                {
                    case DeltaAction.Ignore: return;
                    case DeltaAction.Resync: ScheduleResync(); return;
                }
                var wo = WcfContractMapper.ToWorkingOrderMessage(n.Order);
                var key = wo.NTOrderId ?? wo.OrderId;
                bool terminal = n.Order.Status == BookFlowOrderStatus.Filled
                             || n.Order.Status == BookFlowOrderStatus.Cancelled
                             || n.Order.Status == BookFlowOrderStatus.Rejected;
                int idx = IndexOfOrder(key);
                if (terminal) { if (idx >= 0) _workingOrders.RemoveAt(idx); }
                else if (idx >= 0) _workingOrders[idx] = wo;
                else _workingOrders.Add(wo);
                OrderBookChanged?.Invoke();
            });
        }

        private void OnPositionDelta(PositionUpdateNotification n)
        {
            if (n?.Position == null) return;
            ExecuteOnUi(() =>
            {
                switch (Classify(n.Version))
                {
                    case DeltaAction.Ignore: return;
                    case DeltaAction.Resync: ScheduleResync(); return;
                }
                var p = n.Position;
                if (p.SignedQuantity == 0)
                {
                    _positions.TryRemove(p.InstrumentName, out _);
                }
                else
                {
                    var snap = new PositionSnapshot(p.InstrumentName, 0, p.SignedQuantity, (decimal)p.AveragePrice, (decimal)p.UnrealizedPnL, (decimal)p.RealizedPnL, 0, 0, 0, DateTime.UtcNow);
                    _positions.AddOrUpdate(p.InstrumentName, snap, (k, old) => snap);
                }
                PortfolioChanged?.Invoke();
            });
        }

        private void OnAccountDelta(AccountItemUpdateNotification n)
        {
            if (n?.Account == null) return;
            ExecuteOnUi(() =>
            {
                switch (Classify(n.Version))
                {
                    case DeltaAction.Ignore: return;
                    case DeltaAction.Resync: ScheduleResync(); return;
                }
                _isTradingEnabled = !string.IsNullOrEmpty(n.Account.AccountName);
                PortfolioChanged?.Invoke();
                TradingStatusChanged?.Invoke(this, IsTradingEnabled);
            });
        }

        private void OnExecutionDelta(ExecutionUpdateNotification n)
        {
            if (n == null) return;
            // Executions carry a version too; they must advance the sequence even though
            // working-order/position state arrives via their own deltas.
            ExecuteOnUi(() =>
            {
                if (Classify(n.Version) == DeltaAction.Resync) ScheduleResync();
            });
        }

        private void OnHeartbeat(HeartbeatNotification hb)
        {
            if (hb == null) return;
            ExecuteOnUi(() =>
            {
                // Server is ahead of us with no delta closing the gap -> we missed events.
                if (hb.PortfolioVersion > _lastAppliedVersion) ScheduleResync();
            });
        }

        private int IndexOfOrder(string? key)
        {
            if (key == null) return -1;
            for (int i = 0; i < _workingOrders.Count; i++)
            {
                var o = _workingOrders[i];
                if ((o.NTOrderId ?? o.OrderId) == key) return i;
            }
            return -1;
        }

        public PositionSnapshot GetPositionSnapshot(string instrumentName)
            => _positions.TryGetValue(instrumentName, out var p)
                ? p
                : new PositionSnapshot(instrumentName, 0, 0, 0, 0, 0, 0, 0, 0, DateTime.UtcNow);

        // ---- Order entry -----------------------------------------------------------

        public async Task<OrderStatusMessage> SubmitOrderAsync(string instrumentName, OrderCommand orderCommand)
        {
            if (!_isConnected) await ConnectAsync();
            try
            {
                switch (orderCommand.Action)
                {
                    case OrderCommand.OrderAction.CancelAll:
                        return WcfContractMapper.FromOperationResult(await _client.CancelAllOrdersAsync(string.Empty), orderCommand.ClientOrderId, OrderCommand.OrderStatus.Cancelled);
                    case OrderCommand.OrderAction.CancelAtPrice:
                        return WcfContractMapper.FromOperationResult(await _client.CancelAtPriceAsync(string.Empty, instrumentName, orderCommand.LimitPrice), orderCommand.ClientOrderId, OrderCommand.OrderStatus.Cancelled);
                    case OrderCommand.OrderAction.Flat:
                        return WcfContractMapper.FromOperationResult(await _client.FlattenPositionAsync(string.Empty, instrumentName), orderCommand.ClientOrderId, OrderCommand.OrderStatus.Submitted);
                    default:
                        return WcfContractMapper.FromAck(await _client.SubmitOrderAsync(WcfContractMapper.ToOrderRequest(instrumentName, orderCommand)));
                }
            }
            catch (Exception ex)
            {
                return new OrderStatusMessage { ClientOrderId = orderCommand.ClientOrderId, Status = OrderCommand.OrderStatus.Rejected, Message = ex.Message, Timestamp = DateTime.UtcNow };
            }
        }

        public Task<OrderStatusMessage> CancelAllOrdersAsync(string instrumentName)
            => SubmitOrderAsync(instrumentName, new OrderCommand { Action = OrderCommand.OrderAction.CancelAll, ClientOrderId = Guid.NewGuid().ToString() });

        public Task<OrderStatusMessage> CancelOrdersAtPriceAsync(string instrumentName, decimal price)
            => SubmitOrderAsync(instrumentName, new OrderCommand { Action = OrderCommand.OrderAction.CancelAtPrice, LimitPrice = (double)price, ClientOrderId = Guid.NewGuid().ToString() });

        public Task<OrderStatusMessage> FlattenPositionAsync(string instrumentName)
            => SubmitOrderAsync(instrumentName, new OrderCommand { Action = OrderCommand.OrderAction.Flat, ClientOrderId = Guid.NewGuid().ToString() });

        private void OnLogMessage(string msg)
        {
            System.Diagnostics.Debug.WriteLine(msg);
            BookFlow.App.Diagnostics.BookFlowLog.Info("NT8TradingService", msg);
            LogMessage?.Invoke(msg);
        }

        private void ExecuteOnUi(Action a)
        {
            if (_uiContext != null) _uiContext.Post(_ => a(), null);
            else System.Windows.Application.Current?.Dispatcher?.BeginInvoke(a);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_resyncLock) { _resyncTimer?.Dispose(); _resyncTimer = null; }
            _client.Dispose();
        }
    }
}
