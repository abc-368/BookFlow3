using System;
using System.Collections.Generic;
using System.ServiceModel;
using System.Threading;
using BookFlow.Shared.Service;

namespace BookFlow.NT8DataEngine.Service
{
    /// <summary>
    /// Single-instance, multi-threaded WCF service implementation. One service
    /// object fans out to all connected callback channels.
    ///
    /// Increment 2a deliberately keeps method bodies thin: the host runs alongside
    /// the existing TCP event server, and order/portfolio plumbing into NT8's
    /// Account API is wired in Increment 2b. The contract surface is locked here so
    /// the WPF client side can be coded against it in parallel.
    /// </summary>
    [ServiceBehavior(
        InstanceContextMode = InstanceContextMode.Single,
        ConcurrencyMode = ConcurrencyMode.Multiple,
        IncludeExceptionDetailInFaults = true)]
    public class BookFlowServiceImpl : IBookFlowService
    {
        private readonly CallbackRegistry _callbacks;
        private readonly Func<PortfolioSnapshot> _portfolioProvider;
        private readonly Func<OrderRequest, OrderAck> _submitOrderHandler;
        private readonly Func<string, OperationResult> _cancelAllHandler;
        private readonly Func<string, string, double, OperationResult> _cancelAtPriceHandler;
        private readonly Func<string, string, OperationResult> _flattenHandler;
        private readonly Func<AccountListResponse> _accountsProvider;
        private readonly Func<TickerSnapshot> _tickerProvider;
        private readonly Func<Pong> _pingHandler;
        private readonly Func<byte, DomSnapshotResponse> _domSnapshotProvider;

        // 2a stub returns a session token without persistence; 2b adds reconnect-by-token.
        private long _sessionCounter;

        public BookFlowServiceImpl(
            CallbackRegistry callbacks,
            Func<PortfolioSnapshot> portfolioProvider,
            Func<OrderRequest, OrderAck> submitOrderHandler,
            Func<string, OperationResult> cancelAllHandler,
            Func<string, string, double, OperationResult> cancelAtPriceHandler,
            Func<string, string, OperationResult> flattenHandler,
            Func<AccountListResponse> accountsProvider,
            Func<TickerSnapshot> tickerProvider,
            Func<Pong> pingHandler,
            Func<byte, DomSnapshotResponse> domSnapshotProvider)
        {
            _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
            _portfolioProvider = portfolioProvider ?? throw new ArgumentNullException(nameof(portfolioProvider));
            _submitOrderHandler = submitOrderHandler ?? throw new ArgumentNullException(nameof(submitOrderHandler));
            _cancelAllHandler = cancelAllHandler ?? throw new ArgumentNullException(nameof(cancelAllHandler));
            _cancelAtPriceHandler = cancelAtPriceHandler ?? throw new ArgumentNullException(nameof(cancelAtPriceHandler));
            _flattenHandler = flattenHandler ?? throw new ArgumentNullException(nameof(flattenHandler));
            _accountsProvider = accountsProvider ?? throw new ArgumentNullException(nameof(accountsProvider));
            _tickerProvider = tickerProvider ?? throw new ArgumentNullException(nameof(tickerProvider));
            _pingHandler = pingHandler ?? throw new ArgumentNullException(nameof(pingHandler));
            _domSnapshotProvider = domSnapshotProvider ?? throw new ArgumentNullException(nameof(domSnapshotProvider));
        }

        public SessionInfo RegisterClient(ClientInfo clientInfo)
        {
            var callback = OperationContext.Current?.GetCallbackChannel<IBookFlowCallback>();
            var token = "BF-" + Interlocked.Increment(ref _sessionCounter).ToString("D6");
            if (callback != null)
                _callbacks.Add(token, callback, clientInfo?.ClientId ?? "anonymous");

            return new SessionInfo
            {
                SessionToken = token,
                ServerVersion = typeof(BookFlowServiceImpl).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                ServerUtcTime = DateTime.UtcNow,
                PortfolioVersion = 0, // wired to PortfolioStateTracker in Increment 4
            };
        }

        public Pong Ping() => _pingHandler();

        public PortfolioSnapshot RequestPortfolioState() => _portfolioProvider();

        public OrderAck SubmitOrder(OrderRequest request) => _submitOrderHandler(request);

        public OperationResult CancelAllOrders(string accountName) => _cancelAllHandler(accountName);

        public OperationResult CancelAtPrice(string accountName, string instrumentName, double price)
            => _cancelAtPriceHandler(accountName, instrumentName, price);

        public OperationResult FlattenPosition(string accountName, string instrumentName)
            => _flattenHandler(accountName, instrumentName);

        public AccountListResponse ListAccounts() => _accountsProvider();

        public TickerSnapshot GetTickerSnapshot() => _tickerProvider();

        public DomSnapshotResponse RequestDomSnapshot(byte tickerId) => _domSnapshotProvider(tickerId);

        public void Disconnect()
        {
            var callback = OperationContext.Current?.GetCallbackChannel<IBookFlowCallback>();
            if (callback != null) _callbacks.RemoveByChannel(callback);
        }
    }

    /// <summary>
    /// Thread-safe registry of live callback channels. Broadcast methods iterate
    /// a snapshot of the list and prune any callback that throws or whose channel
    /// is no longer in <see cref="CommunicationState.Opened"/>.
    /// </summary>
    public sealed class CallbackRegistry
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _byToken = new Dictionary<string, Entry>();
        private readonly Action<string> _log;

        public CallbackRegistry(Action<string> log) { _log = log ?? (_ => { }); }

        public int Count { get { lock (_gate) return _byToken.Count; } }

        public void Add(string token, IBookFlowCallback callback, string clientId)
        {
            lock (_gate)
            {
                // Replace any prior entry for the same token (reconnect).
                _byToken[token] = new Entry(token, callback, clientId);
            }
            _log("Callback registered: " + token + " (" + clientId + ")");
        }

        public void RemoveByChannel(IBookFlowCallback callback)
        {
            string removed = null;
            lock (_gate)
            {
                foreach (var kv in _byToken)
                {
                    if (ReferenceEquals(kv.Value.Callback, callback)) { removed = kv.Key; break; }
                }
                if (removed != null) _byToken.Remove(removed);
            }
            if (removed != null) _log("Callback removed (Disconnect): " + removed);
        }

        public void Broadcast(Action<IBookFlowCallback> action)
        {
            List<Entry> snapshot;
            lock (_gate) snapshot = new List<Entry>(_byToken.Values);

            List<string> dead = null;
            foreach (var entry in snapshot)
            {
                try
                {
                    var commObject = entry.Callback as ICommunicationObject;
                    if (commObject != null && commObject.State != CommunicationState.Opened)
                    {
                        (dead ?? (dead = new List<string>())).Add(entry.Token);
                        continue;
                    }
                    action(entry.Callback);
                }
                catch (Exception ex)
                {
                    (dead ?? (dead = new List<string>())).Add(entry.Token);
                    _log("Callback broadcast failed for " + entry.Token + ": " + ex.Message);
                }
            }

            if (dead != null)
            {
                lock (_gate)
                {
                    foreach (var t in dead) _byToken.Remove(t);
                }
                _log("Pruned " + dead.Count + " dead callback(s).");
            }
        }

        private sealed class Entry
        {
            public string Token { get; }
            public IBookFlowCallback Callback { get; }
            public string ClientId { get; }
            public Entry(string token, IBookFlowCallback cb, string clientId)
            {
                Token = token; Callback = cb; ClientId = clientId;
            }
        }
    }
}
