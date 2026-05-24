using System;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using BookFlow.Shared.Service;

namespace BookFlow.App.Services
{
    /// <summary>
    /// Duplex WCF client for the BookFlow control + event channel. Hosts the
    /// <see cref="IBookFlowCallback"/> implementation, manages the channel lifecycle,
    /// and recreates the channel on fault (per CLAUDE-ENHANCEMENTS §10.2).
    ///
    /// Used by NT8TradingService (orders + portfolio + live events) and
    /// NT8DirectDataFeed (control plane; market data still rides the MMF ring).
    /// </summary>
    [CallbackBehavior(ConcurrencyMode = ConcurrencyMode.Multiple, UseSynchronizationContext = false)]
    public sealed class BookFlowServiceClient : IBookFlowCallback, IDisposable
    {
        private readonly string _clientId;
        private readonly object _gate = new object();
        private DuplexChannelFactory<IBookFlowService>? _factory;
        private IBookFlowService? _channel;
        private CancellationTokenSource? _cts;
        private Task? _watchdog;
        private long _lastHeartbeatTicks;
        private volatile bool _connected;
        private int _disposed;

        public bool IsConnected => _connected;
        public string? SessionToken { get; private set; }

        // Surfaced to the app. Marshal to the UI thread at the subscription site.
        public event Action<OrderUpdateNotification>? OrderUpdated;
        public event Action<ExecutionUpdateNotification>? ExecutionUpdated;
        public event Action<PositionUpdateNotification>? PositionUpdated;
        public event Action<AccountItemUpdateNotification>? AccountItemUpdated;
        public event Action<ConnectionStatusNotification>? ConnectionStatusChanged;
        public event Action<HeartbeatNotification>? HeartbeatReceived;
        public event Action<bool>? ConnectionChanged; // true = connected, false = lost

        public BookFlowServiceClient(string? clientId = null)
        {
            _clientId = clientId ?? ("BookFlowApp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        public async Task<bool> ConnectAsync()
        {
            if (_disposed != 0) return false;
            try
            {
                lock (_gate)
                {
                    CloseChannelNoLock();
                    var binding = CreateBinding();
                    var address = new EndpointAddress(BookFlowEndpoint.FullAddress);
                    _factory = new DuplexChannelFactory<IBookFlowService>(new InstanceContext(this), binding, address);
                    _channel = _factory.CreateChannel();
                    ((ICommunicationObject)_channel).Faulted += OnChannelFaulted;
                    ((ICommunicationObject)_factory).Faulted += OnChannelFaulted;
                }

                var session = await Task.Run(() => _channel.RegisterClient(new ClientInfo
                {
                    ClientId = _clientId,
                    ClientVersion = typeof(BookFlowServiceClient).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                    ClientUtcTime = DateTime.UtcNow,
                })).ConfigureAwait(false);

                SessionToken = session?.SessionToken;
                Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
                _connected = true;
                BookFlow.App.Diagnostics.BookFlowLog.Info("WcfClient", $"Connected ({_clientId}), session={SessionToken}");
                ConnectionChanged?.Invoke(true);

                StartWatchdog();
                return true;
            }
            catch (Exception ex)
            {
                _connected = false;
                BookFlow.App.Diagnostics.BookFlowLog.Error("WcfClient", $"Connect failed ({_clientId})", ex);
                ConnectionChanged?.Invoke(false);
                return false;
            }
        }

        public Task<PortfolioSnapshot> RequestPortfolioStateAsync()
            => InvokeAsync(c => c.RequestPortfolioState());

        public Task<OrderAck> SubmitOrderAsync(OrderRequest request)
            => InvokeAsync(c => c.SubmitOrder(request));

        public Task<OrderAck> SubmitBracketOrderAsync(BracketOrderRequest request)
            => InvokeAsync(c => c.SubmitBracketOrder(request));

        public Task<OperationResult> CancelAllOrdersAsync(string accountName)
            => InvokeAsync(c => c.CancelAllOrders(accountName));

        public Task<OperationResult> CancelAtPriceAsync(string accountName, string instrument, double price)
            => InvokeAsync(c => c.CancelAtPrice(accountName, instrument, price));

        public Task<OperationResult> FlattenPositionAsync(string accountName, string instrument)
            => InvokeAsync(c => c.FlattenPosition(accountName, instrument));

        public Task<AccountListResponse> ListAccountsAsync()
            => InvokeAsync(c => c.ListAccounts());

        public Task<TickerSnapshot> GetTickerSnapshotAsync()
            => InvokeAsync(c => c.GetTickerSnapshot());

        public Task<DomSnapshotResponse> RequestDomSnapshotAsync(byte tickerId)
            => InvokeAsync(c => c.RequestDomSnapshot(tickerId));

        public Task<Pong> PingAsync()
            => InvokeAsync(c => c.Ping());

        private Task<T> InvokeAsync<T>(Func<IBookFlowService, T> call)
        {
            return Task.Run(() =>
            {
                IBookFlowService? channel;
                lock (_gate) channel = _channel;
                if (channel == null) throw new InvalidOperationException("Not connected");
                return call(channel);
            });
        }

        // ---- IBookFlowCallback (invoked by the NT8 host) ------------------------

        public void OnOrderUpdate(OrderUpdateNotification n) => OrderUpdated?.Invoke(n);
        public void OnExecutionUpdate(ExecutionUpdateNotification n) => ExecutionUpdated?.Invoke(n);
        public void OnPositionUpdate(PositionUpdateNotification n) => PositionUpdated?.Invoke(n);
        public void OnAccountItemUpdate(AccountItemUpdateNotification n) => AccountItemUpdated?.Invoke(n);
        public void OnConnectionStatus(ConnectionStatusNotification n) => ConnectionStatusChanged?.Invoke(n);

        public void OnHeartbeat(HeartbeatNotification n)
        {
            Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
            HeartbeatReceived?.Invoke(n);
        }

        // ---- Fault handling & reconnect -----------------------------------------

        private void OnChannelFaulted(object? sender, EventArgs e)
        {
            _connected = false;
            BookFlow.App.Diagnostics.BookFlowLog.Error("WcfClient", $"Channel faulted ({_clientId}); watchdog will reconnect");
            ConnectionChanged?.Invoke(false);
            // Reconnect attempts run in the watchdog loop.
        }

        private void StartWatchdog()
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _watchdog = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(1000, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }

                    var lastBeat = new DateTime(Interlocked.Read(ref _lastHeartbeatTicks), DateTimeKind.Utc);
                    var stale = (DateTime.UtcNow - lastBeat) > TimeSpan.FromSeconds(5);
                    var faulted = !ChannelUsable();

                    if (stale || faulted)
                    {
                        _connected = false;
                        ConnectionChanged?.Invoke(false);
                        var ok = await ConnectAsync().ConfigureAwait(false);
                        if (ok) break; // ConnectAsync starts a fresh watchdog
                    }
                }
            }, token);
        }

        private bool ChannelUsable()
        {
            lock (_gate)
            {
                if (_channel is ICommunicationObject co)
                    return co.State == CommunicationState.Opened;
                return false;
            }
        }

        private void CloseChannelNoLock()
        {
            try { _cts?.Cancel(); } catch { }
            if (_channel is ICommunicationObject co)
            {
                try { if (co.State == CommunicationState.Faulted) co.Abort(); else co.Close(TimeSpan.FromSeconds(2)); }
                catch { try { co.Abort(); } catch { } }
            }
            try { _factory?.Abort(); } catch { }
            _channel = null;
            _factory = null;
        }

        public void Disconnect()
        {
            lock (_gate)
            {
                try { _channel?.Disconnect(); } catch { }
                CloseChannelNoLock();
            }
            _connected = false;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _cts?.Cancel(); } catch { }
            lock (_gate) CloseChannelNoLock();
            try { _cts?.Dispose(); } catch { }
        }

        private static NetNamedPipeBinding CreateBinding()
        {
            var binding = new NetNamedPipeBinding
            {
                MaxReceivedMessageSize = BookFlowEndpoint.MaxMessageBytes,
                MaxBufferSize = BookFlowEndpoint.MaxMessageBytes,
                MaxBufferPoolSize = BookFlowEndpoint.MaxMessageBytes,
                ReceiveTimeout = TimeSpan.MaxValue,
                SendTimeout = TimeSpan.FromSeconds(BookFlowEndpoint.SendTimeoutSeconds),
                OpenTimeout = TimeSpan.FromSeconds(BookFlowEndpoint.OpenTimeoutSeconds),
                CloseTimeout = TimeSpan.FromSeconds(BookFlowEndpoint.CloseTimeoutSeconds),
            };
            binding.Security.Mode = NetNamedPipeSecurityMode.None;
            binding.ReaderQuotas.MaxStringContentLength = BookFlowEndpoint.MaxMessageBytes;
            binding.ReaderQuotas.MaxArrayLength = BookFlowEndpoint.MaxMessageBytes;
            binding.ReaderQuotas.MaxBytesPerRead = BookFlowEndpoint.MaxMessageBytes;
            return binding;
        }
    }
}
