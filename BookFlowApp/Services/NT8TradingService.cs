using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using BookFlow.Shared.Contracts; // Shared contracts now include PositionSnapshot etc.
using BookFlow.App.Interfaces;
using System.Linq;
using System.Threading;
using BookFlow.Shared.IPC;
using BookFlow.App.Services;

namespace BookFlow.App.Services
{
    public class NT8TradingService : ITradingService, IDisposable
    {
        private readonly ControlPipeClient _controlPipeClient;
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
        private readonly ConcurrentDictionary<string, WorkingOrderMessage> _orderIndex = new();
        private readonly SynchronizationContext? _uiContext;

        // Event pipe for live order/position updates
        private readonly EventTcpClient _eventClient = new EventTcpClient();
        private bool _eventClientConnected;
        private DateTime _lastRefreshUtc = DateTime.MinValue; // simple throttle for burst events

        public NT8TradingService(string controlPipeName = "BookFlow_Control_Global", SynchronizationContext? uiContext = null)
        {
            _controlPipeClient = new ControlPipeClient(controlPipeName);
            _workingOrders = new ObservableCollection<WorkingOrderMessage>();
            WorkingOrders = new ReadOnlyObservableCollection<WorkingOrderMessage>(_workingOrders);
            _uiContext = uiContext ?? SynchronizationContext.Current;

            _ = ConnectAsync();
        }

        public async Task<bool> ConnectAsync(int timeoutMs = 30000)
        {
            if (_disposed) return false;
            if (_isConnected) return true;
            await _controlPipeClient.ConnectAsync(timeoutMs);
            _isConnected = true;
            await SynchronizeAccountStatusAsync();
            // Try to connect event pipe
            _eventClientConnected = await _eventClient.ConnectAsync(2000);
            if (_eventClientConnected)
            {
                _eventClient.AnyEventReceived += __ => { var _ = RefreshPortfolioAsync(); };
            }
            TradingStatusChanged?.Invoke(this, _isTradingEnabled);
            return true;
        }

        private async Task SynchronizeAccountStatusAsync()
        {
            try
            {
                var snapshot = await _controlPipeClient.RequestPortfolioStateAsync();
                if (snapshot == null) return;
                ExecuteOnUi(() =>
                {
                    _workingOrders.Clear(); _orderIndex.Clear();
                    if (snapshot.Orders != null)
                    {
                        foreach (var o in snapshot.Orders)
                        {
                            _orderIndex[o.OrderId] = o;
                            _workingOrders.Add(o);
                        }
                    }
                    _positions.Clear();
                    if (snapshot.Positions != null)
                    {
                        foreach (var pos in snapshot.Positions)
                        {
                            var snap = new PositionSnapshot(pos.Instrument, 0, pos.Quantity, (decimal)pos.AveragePrice, (decimal)pos.UnrealizedPnL, (decimal)pos.RealizedPnL, 0, 0, 0, pos.LastUpdateTime);
                            _positions.TryAdd(pos.Instrument, snap);
                        }
                    }
                    _isTradingEnabled = snapshot.Account != null && !string.IsNullOrEmpty(snapshot.Account.AccountName);
                    OrderBookChanged?.Invoke(); PortfolioChanged?.Invoke();
                });
                _lastRefreshUtc = DateTime.UtcNow;
            }
            catch (Exception ex) { OnLogMessage($"Sync failed: {ex.Message}"); }
        }

        private async Task RefreshPortfolioAsync()
        {
            try
            {
                var snapshot = await _controlPipeClient.RequestPortfolioStateAsync();
                if (snapshot == null) return;
                ExecuteOnUi(() =>
                {
                    _workingOrders.Clear(); _orderIndex.Clear();
                    if (snapshot.Orders != null)
                    {
                        foreach (var o in snapshot.Orders)
                        {
                            _orderIndex[o.OrderId] = o;
                            _workingOrders.Add(o);
                        }
                    }

                    // Update positions using AddOrUpdate for thread safety and to avoid clearing
                    if (snapshot.Positions != null)
                    {
                        foreach (var pos in snapshot.Positions)
                        {
                            var snap = new PositionSnapshot(pos.Instrument, 0, pos.Quantity, (decimal)pos.AveragePrice, (decimal)pos.UnrealizedPnL, (decimal)pos.RealizedPnL, 0, 0, 0, pos.LastUpdateTime);
                            _positions.AddOrUpdate(pos.Instrument, snap, (key, old) => snap);
                        }
                    }

                    // Clear positions that are no longer in the snapshot
                    var instrumentsInSnapshot = snapshot.Positions?.Select(p => p.Instrument).ToList() ?? new List<string>();
                    foreach (var key in _positions.Keys)
                    {
                        if (!instrumentsInSnapshot.Contains(key))
                        {
                            _positions.TryRemove(key, out _);
                        }
                    }

                    _isTradingEnabled = snapshot.Account != null && !string.IsNullOrEmpty(snapshot.Account.AccountName);
                    OrderBookChanged?.Invoke(); 
                    PortfolioChanged?.Invoke();
                });
            }
            catch { }
        }

        public PositionSnapshot GetPositionSnapshot(string instrumentName) => _positions.TryGetValue(instrumentName, out var p) ? p : new PositionSnapshot(instrumentName, 0, 0, 0, 0, 0, 0, 0, 0, System.DateTime.UtcNow);

        public async Task<OrderStatusMessage> SubmitOrderAsync(string instrumentName, OrderCommand orderCommand)
        {
            if (!_isConnected) await ConnectAsync();
            var resp = await _controlPipeClient.SubmitOrderAsync(instrumentName, orderCommand) ?? new OrderStatusMessage { ClientOrderId = orderCommand.ClientOrderId, Status = OrderCommand.OrderStatus.Rejected, Message = "No response" };
            _ = RefreshPortfolioAsync();
            return resp;
        }
        public async Task<OrderStatusMessage> CancelAllOrdersAsync(string instrumentName)
        {
            var resp = await SubmitOrderAsync(instrumentName, new OrderCommand { Action = OrderCommand.OrderAction.CancelAll, ClientOrderId = System.Guid.NewGuid().ToString() });
            _ = RefreshPortfolioAsync();
            return resp;
        }
        public async Task<OrderStatusMessage> CancelOrdersAtPriceAsync(string instrumentName, decimal price)
        {
            var resp = await SubmitOrderAsync(instrumentName, new OrderCommand { Action = OrderCommand.OrderAction.CancelAtPrice, LimitPrice = (double)price, ClientOrderId = System.Guid.NewGuid().ToString() });
            _ = RefreshPortfolioAsync();
            return resp;
        }
        public async Task<OrderStatusMessage> FlattenPositionAsync(string instrumentName)
        {
            var resp = await SubmitOrderAsync(instrumentName, new OrderCommand { Action = OrderCommand.OrderAction.Flat, ClientOrderId = System.Guid.NewGuid().ToString() });
            _ = RefreshPortfolioAsync();
            return resp;
        }

        private void OnLogMessage(string msg) => System.Diagnostics.Debug.WriteLine(msg);
        private void ExecuteOnUi(Action a)
        {
            if (_uiContext != null)
            {
                _uiContext.Post(_ => a(), null);
            }
            else
            {
                System.Windows.Application.Current?.Dispatcher?.BeginInvoke(a);
            }
        }

        public void Dispose()
        {
            if (_disposed) return; _disposed = true; _controlPipeClient.Dispose(); _eventClient.Dispose();
        }
    }
}