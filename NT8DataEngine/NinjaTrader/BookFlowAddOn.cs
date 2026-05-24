using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BookFlow.Shared.Contracts;
using BookFlow.Shared.IPC;
using BookFlow.Shared.Service;
using BookFlow.NT8DataEngine.Service;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.AddOns
{
    public class BookFlowAddOn : AddOnBase, IDisposable
    {
        private static BookFlowAddOn _instance;
        private static readonly object _instanceLock = new object();

        private SharedRingBuffer _globalDataChannel;

        private readonly Dictionary<string, Order> _clientOrderMap = new Dictionary<string, Order>();
        private readonly object _orderLock = new object();

        private readonly Dictionary<string, byte> _instrumentToTickerId = new Dictionary<string, byte>();
        private readonly Dictionary<byte, string> _tickerIdToInfo = new Dictionary<byte, string>();
        private int _nextTickerIdInt = 1; // widened to int to detect byte overflow before assignment
        private readonly object _tickerLock = new object();

        private readonly string _logPrefix = "[BookFlowAddOn]";

        private CancellationTokenSource _shutdownCts;
        private volatile bool _initialized;
        private volatile bool _disposed;

        // WCF duplex service: the single control + event channel for all clients.
        private BookFlowServiceHost _wcfHost;
        private long _dataMessagesSent;
        private long _dataMessagesDropped;

        // Increment 3: coalesce multi-producer market-data writes onto a single drain
        // thread so the MMF ring has exactly one writer (true SPSC). Multiple indicator
        // threads enqueue to the lock-free ConcurrentQueue; the drain thread is the sole
        // ring writer. Bounded with drop-oldest to bound memory under a slow/absent reader.
        private const int MaxPendingWrites = 65536;
        private readonly ConcurrentQueue<UnifiedMarketDataMessage> _pendingWrites = new ConcurrentQueue<UnifiedMarketDataMessage>();
        private readonly AutoResetEvent _writeSignal = new AutoResetEvent(false);
        private Thread _ringWriterThread;
        private volatile bool _ringWriterRunning;
        private int _pendingCount;

        // Q4: authoritative server-side L2 book + monotonic global sequence stamped into the
        // ring (Reserved1). Both are touched only by the single drain thread.
        private readonly ServerBookRegistry _serverBooks = new ServerBookRegistry();
        private long _globalDataSequence;

        // Event fan-out state (Increment 2b-1).
        private long _portfolioVersion;
        private long _heartbeatSeq;
        // Serializes version assignment + broadcast so clients see versions in order
        // even when NT8 raises events from different threads (Increment 4).
        private readonly object _publishLock = new object();
        private System.Threading.Timer _heartbeatTimer;
        private readonly Dictionary<string, string> _ntOrderIdToClientId = new Dictionary<string, string>();
        private readonly HashSet<Account> _subscribedAccounts = new HashSet<Account>();
        private readonly Dictionary<string, DateTime> _lastAccountBroadcastUtc = new Dictionary<string, DateTime>();
        private readonly object _accountLock = new object();

        /// <summary>
        /// Returns the singleton, lazily initializing it on first indicator drop.
        /// NT8 also instantiates this AddOn at startup (registered via OnStateChange.SetDefaults);
        /// the lazy path is a fallback for environments where that hasn't fired yet.
        /// </summary>
        public static BookFlowAddOn Instance
        {
            get
            {
                BookFlowAddOn instance;
                lock (_instanceLock)
                {
                    if (_instance == null) _instance = new BookFlowAddOn();
                    instance = _instance;
                }
                instance.EnsureInitialized();
                return instance;
            }
        }

        /// <summary>
        /// Returns the existing singleton without lazy initialization. Used by indicator
        /// teardown to avoid resurrecting a disposed AddOn while shutting down.
        /// </summary>
        public static BookFlowAddOn TryGetExistingInstance()
        {
            lock (_instanceLock) return _instance;
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "BookFlow AddOn for order management, market data fan-out, and event broadcasting.";
                Name = "BookFlowAddOn";
                lock (_instanceLock) { if (_instance == null) _instance = this; }
            }
            else if (State == State.Terminated)
            {
                // Only the active singleton handles teardown.
                BookFlowAddOn active;
                lock (_instanceLock) active = _instance;
                if (ReferenceEquals(active, this)) Shutdown();
            }
        }

        private void EnsureInitialized()
        {
            if (_initialized || _disposed) return;
            lock (_instanceLock)
            {
                if (_initialized || _disposed) return;
                try
                {
                    LogMsg("Initializing BookFlow AddOn (indicator-triggered)...");
                    _shutdownCts = new CancellationTokenSource();
                    StartGlobalChannels();
                    StartWcfService();
                    SubscribeToAccountEvents();
                    _heartbeatTimer = new System.Threading.Timer(OnHeartbeatTick, null, 1000, 1000);
                    _initialized = true;
                    LogMsg("BookFlow AddOn ready.");
                }
                catch (Exception ex)
                {
                    LogMsg(string.Format("ADDON INITIALIZATION ERROR: {0}", ex.Message));
                }
            }
        }

        private void Shutdown()
        {
            if (_disposed) return;
            lock (_instanceLock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            LogMsg("Shutting down BookFlow AddOn...");

            // 1. Signal cancellation to all loops.
            try { _shutdownCts?.Cancel(); } catch { }

            // 2. Unsubscribe from NT8 account events so no callbacks fire into disposed state.
            try { UnsubscribeFromAccountEvents(); } catch (Exception ex) { LogMsg("Account-unsubscribe error: " + ex.Message); }

            // 3. Stop the heartbeat loop and the ring drain thread.
            try { _heartbeatTimer?.Dispose(); } catch { }
            _ringWriterRunning = false;
            try { _writeSignal.Set(); } catch { }                 // wake the drain loop to exit
            try { _ringWriterThread?.Join(TimeSpan.FromSeconds(2)); } catch { }

            // 4. Dispose IPC channels.
            try { _wcfHost?.Dispose(); } catch (Exception ex) { LogMsg("WcfHost-dispose error: " + ex.Message); }
            try { _globalDataChannel?.Dispose(); } catch (Exception ex) { LogMsg("DataChannel-dispose error: " + ex.Message); }
            try { _writeSignal.Dispose(); } catch { }
            try { _shutdownCts?.Dispose(); } catch { }

            // 5. Clear maps so a subsequent re-init (NT8 recompile + reload) starts clean.
            lock (_orderLock) { _clientOrderMap.Clear(); _ntOrderIdToClientId.Clear(); }
            lock (_accountLock) { _lastAccountBroadcastUtc.Clear(); }
            lock (_tickerLock)
            {
                _instrumentToTickerId.Clear();
                _tickerIdToInfo.Clear();
                _nextTickerIdInt = 1;
            }
            try { _serverBooks.Clear(); } catch { }

            // Release the singleton slot so re-init creates a fresh instance.
            lock (_instanceLock) { if (ReferenceEquals(_instance, this)) _instance = null; }

            LogMsg("BookFlow AddOn shutdown complete.");
        }

        void IDisposable.Dispose() => Shutdown();

        private void StartGlobalChannels()
        {
            var dataChannelName = "BookFlow_Data_Global";
            _globalDataChannel = new SharedRingBuffer(dataChannelName, 1024 * 1024);

            _ringWriterRunning = true;
            _ringWriterThread = new Thread(RingWriterLoop) { IsBackground = true, Name = "BookFlowRingWriter" };
            _ringWriterThread.Start();
        }

        /// <summary>
        /// Called from any NT8 indicator thread. Enqueues onto the lock-free coalescing
        /// queue; the single drain thread performs the actual ring write. Drop-oldest
        /// when the queue is saturated (slow/absent consumer) to bound memory.
        /// </summary>
        public void WriteToGlobalChannel(byte tickerId, UnifiedMarketDataMessage data)
        {
            if (_disposed) return;

            // Drop oldest while saturated. Multiple producers may race here; each drop
            // is counted and the queue stays near the cap.
            while (Volatile.Read(ref _pendingCount) >= MaxPendingWrites)
            {
                if (_pendingWrites.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _pendingCount);
                    Interlocked.Increment(ref _dataMessagesDropped);
                }
                else break;
            }

            _pendingWrites.Enqueue(data);
            Interlocked.Increment(ref _pendingCount);
            _writeSignal.Set();
        }

        // Sole writer to the MMF ring -> the ring is single-producer/single-consumer.
        private void RingWriterLoop()
        {
            while (_ringWriterRunning)
            {
                try
                {
                    _writeSignal.WaitOne(1000);
                    if (!_ringWriterRunning) break;

                    var channel = _globalDataChannel;
                    if (channel == null) continue;

                    UnifiedMarketDataMessage msg;
                    while (_pendingWrites.TryDequeue(out msg))
                    {
                        Interlocked.Decrement(ref _pendingCount);
                        // Stamp a global monotonic sequence into the ring (Reserved1) and apply
                        // the message to the authoritative server book BEFORE publishing, so a
                        // concurrent snapshot is never newer than what the client can see.
                        var seq = ++_globalDataSequence;
                        msg.Reserved1 = seq;
                        msg.IpcQueueTime = Stopwatch.GetTimestamp();
                        _serverBooks.Apply(ref msg, seq);
                        if (channel.TryWrite(ref msg))
                        {
                            Interlocked.Increment(ref _dataMessagesSent);
                            channel.SignalDataAvailable();
                        }
                        else
                        {
                            // Ring full: the WPF consumer is behind. Drop and count.
                            Interlocked.Increment(ref _dataMessagesDropped);
                        }
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { LogMsg("RingWriterLoop error: " + ex.Message); }
            }
            LogMsg("RingWriterLoop exited.");
        }

        public byte RegisterTicker(string instrumentName, double tickSize, double pointValue)
        {
            lock (_tickerLock)
            {
                byte existing;
                if (_instrumentToTickerId.TryGetValue(instrumentName, out existing))
                    return existing; // stable: an instrument keeps the same id for the whole session

                if (_nextTickerIdInt > byte.MaxValue)
                {
                    LogMsg(string.Format("ERROR: Ticker ID space exhausted (max {0}); cannot register '{1}'", byte.MaxValue, instrumentName));
                    return 0;
                }
                byte id = (byte)_nextTickerIdInt++;

                _instrumentToTickerId[instrumentName] = id;
                _tickerIdToInfo[id] = string.Format("{0}|{1}|{2}", instrumentName, tickSize, pointValue);
                LogMsg(string.Format("Registered ticker '{0}' as ID {1} (TickSize={2}, PointValue={3})", instrumentName, id, tickSize, pointValue));
                return id;
            }
        }

        /// <summary>
        /// Called by BookFlowIndi.OnStateChange when an indicator terminates. The instrument→id
        /// mapping is intentionally KEPT for the rest of the session: ids are never recycled, so a
        /// client window holding this id can never start receiving a different instrument's data
        /// (the cause of the ES-window-showing-NQ bug), and a reopened chart reuses the same id.
        /// </summary>
        public void UnregisterTicker(byte tickerId)
        {
            // Intentionally a no-op for the id mapping (ids are stable for the session).
        }

        #region NT8 Core Event Handlers
        private void OnOrderUpdate(object sender, OrderEventArgs e)
        {
            try
            {
                if (e.Order == null || e.Order.Instrument == null) return;
                PublishOrderUpdate(e.Order);
            }
            catch (Exception ex) { LogMsg(string.Format("ERROR: OnOrderUpdate failed: {0}", ex.Message)); }
        }
        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            try
            {
                if (e.Execution == null || e.Execution.Order == null || e.Execution.Order.Instrument == null) return;
                PublishExecutionUpdate(e.Execution);
            }
            catch (Exception ex) { LogMsg(string.Format("ERROR: OnExecutionUpdate failed: {0}", ex.Message)); }
        }
        private void OnPositionUpdate(object sender, PositionEventArgs e)
        {
            try
            {
                if (e.Position == null || e.Position.Instrument == null) return;
                PublishPositionUpdate(e.Position);
            }
            catch (Exception ex) { LogMsg(string.Format("ERROR: OnPositionUpdate failed: {0}", ex.Message)); }
        }
        private void OnAccountItemUpdate(object sender, AccountItemEventArgs e)
        {
            try
            {
                var account = e.Account;
                if (account == null || account.Name == "Backtest") return;
                // Throttle: account items can fire per-tick while in a position.
                lock (_accountLock)
                {
                    if (_lastAccountBroadcastUtc.TryGetValue(account.Name, out var last) &&
                        (DateTime.UtcNow - last).TotalMilliseconds < 250) return;
                    _lastAccountBroadcastUtc[account.Name] = DateTime.UtcNow;
                }
                PublishAccountItemUpdate(account);
            }
            catch (Exception ex) { LogMsg(string.Format("ERROR: OnAccountItemUpdate failed: {0}", ex.Message)); }
        }
        #endregion

        #region Helpers
        private void LogMsg(string message)
        {
            string logEntry = string.Format("{0} {1:HH:mm:ss.fff} - {2}", _logPrefix, DateTime.Now, message);
            NinjaTrader.Code.Output.Process(logEntry, PrintTo.OutputTab1);
        }

        // Idempotent: safe to call repeatedly. The heartbeat tick re-invokes this so
        // accounts added at runtime (broker reconnect, new sim account) get hooked.
        private void SubscribeToAccountEvents()
        {
            lock (_accountLock)
            {
                foreach (var account in Account.All)
                {
                    if (account.Name == "Backtest") continue;
                    if (!_subscribedAccounts.Add(account)) continue; // already hooked
                    account.OrderUpdate += OnOrderUpdate;
                    account.ExecutionUpdate += OnExecutionUpdate;
                    account.PositionUpdate += OnPositionUpdate;
                    account.AccountItemUpdate += OnAccountItemUpdate;
                    LogMsg("Subscribed to account events: " + account.Name);
                }
            }
        }

        private void UnsubscribeFromAccountEvents()
        {
            lock (_accountLock)
            {
                foreach (var account in _subscribedAccounts)
                {
                    account.OrderUpdate -= OnOrderUpdate;
                    account.ExecutionUpdate -= OnExecutionUpdate;
                    account.PositionUpdate -= OnPositionUpdate;
                    account.AccountItemUpdate -= OnAccountItemUpdate;
                }
                _subscribedAccounts.Clear();
            }
        }
        #endregion

        private Account GetPreferredAccount()
        {
            try
            {
                var playback = Account.All.FirstOrDefault(a => a.Name != null && a.Name.StartsWith("Playback", StringComparison.OrdinalIgnoreCase));
                if (playback != null) return playback;

                var any = Account.All.FirstOrDefault(a => a.Name != "Backtest");
                return any;
            }
            catch
            {
                return null;
            }
        }

        #region WCF Service (Increment 2a)

        private void StartWcfService()
        {
            try
            {
                _wcfHost = new BookFlowServiceHost(
                    LogMsg,
                    BuildPortfolioSnapshot,
                    WcfSubmitOrder,
                    WcfCancelAllOrders,
                    WcfCancelAtPrice,
                    WcfFlattenPosition,
                    BuildAccountList,
                    BuildTickerSnapshot,
                    BuildPong,
                    BuildDomSnapshot);
                _wcfHost.Open();
            }
            catch (Exception ex)
            {
                LogMsg("ERROR: StartWcfService failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Resolves the account for a command using the Section 10.3 hybrid policy:
        /// explicit name wins; otherwise a single eligible account is auto-selected;
        /// ambiguity (multiple eligible, none specified) is rejected rather than guessed.
        /// </summary>
        private Account ResolveAccount(string requestedName, out string error)
        {
            error = null;
            try
            {
                if (!string.IsNullOrEmpty(requestedName))
                {
                    var match = Account.All.FirstOrDefault(a => string.Equals(a.Name, requestedName, StringComparison.OrdinalIgnoreCase));
                    if (match == null) { error = "Account not found: " + requestedName; return null; }
                    return match;
                }

                var eligible = Account.All.Where(a => a.Name != "Backtest").ToList();
                if (eligible.Count == 0) { error = "No eligible trading account"; return null; }
                if (eligible.Count == 1) return eligible[0];

                // Prefer a Playback account if present (sim/replay), else demand explicit selection.
                var playback = eligible.FirstOrDefault(a => a.Name != null && a.Name.StartsWith("Playback", StringComparison.OrdinalIgnoreCase));
                if (playback != null) return playback;

                error = "Multiple accounts eligible; AccountName must be specified";
                return null;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        private Pong BuildPong()
        {
            return new Pong
            {
                ServerTimestampTicks = DateTime.UtcNow.Ticks,
                DataMessagesSent = System.Threading.Interlocked.Read(ref _dataMessagesSent),
                DataMessagesDropped = System.Threading.Interlocked.Read(ref _dataMessagesDropped),
                DataRingFillRatio = _globalDataChannel?.FillRatio ?? 0.0,
                LiveCallbackCount = _wcfHost?.Callbacks.Count ?? 0,
            };
        }

        private AccountListResponse BuildAccountList()
        {
            var resp = new AccountListResponse();
            try
            {
                foreach (var a in Account.All)
                {
                    if (a.Name == "Backtest") continue;
                    resp.Accounts.Add(new AccountInfo
                    {
                        Name = a.Name,
                        IsSimulated = a.Name != null && (a.Name.StartsWith("Sim", StringComparison.OrdinalIgnoreCase) || a.Name.StartsWith("Playback", StringComparison.OrdinalIgnoreCase)),
                        IsConnected = a.Connection != null && a.Connection.Status == ConnectionStatus.Connected,
                    });
                }
                var pref = GetPreferredAccount();
                resp.PreferredAccount = pref?.Name;
            }
            catch (Exception ex) { LogMsg("BuildAccountList error: " + ex.Message); }
            return resp;
        }

        private DomSnapshotResponse BuildDomSnapshot(byte tickerId)
        {
            string instrument = null;
            lock (_tickerLock)
            {
                if (_tickerIdToInfo.TryGetValue(tickerId, out var info))
                    instrument = info.Split('|')[0];
            }
            return _serverBooks.BuildSnapshot(tickerId, instrument);
        }

        private TickerSnapshot BuildTickerSnapshot()
        {
            var snap = new TickerSnapshot { ServerUtcTime = DateTime.UtcNow };
            lock (_tickerLock)
            {
                foreach (var kvp in _tickerIdToInfo)
                {
                    var parts = kvp.Value.Split('|');
                    var entry = new TickerEntry { TickerId = kvp.Key, InstrumentName = parts.Length > 0 ? parts[0] : "" };
                    double.TryParse(parts.Length > 1 ? parts[1] : "0", System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var tickSize);
                    double.TryParse(parts.Length > 2 ? parts[2] : "0", System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pointValue);
                    entry.TickSize = tickSize;
                    entry.PointValue = pointValue;
                    snap.Entries.Add(entry);
                }
            }
            return snap;
        }

        private PortfolioSnapshot BuildPortfolioSnapshot()
        {
            var snap = new PortfolioSnapshot { ServerUtcTime = DateTime.UtcNow };
            try
            {
                foreach (var account in Account.All)
                {
                    if (account.Name == "Backtest") continue;

                    snap.Accounts.Add(new AccountState
                    {
                        AccountName = account.Name,
                        BuyingPower = SafeGet(account, AccountItem.BuyingPower),
                        CashValue = SafeGet(account, AccountItem.CashValue),
                        RealizedPnL = SafeGet(account, AccountItem.RealizedProfitLoss),
                        UnrealizedPnL = SafeGet(account, AccountItem.UnrealizedProfitLoss),
                        NetLiquidation = SafeGet(account, AccountItem.NetLiquidation),
                        InitialMargin = SafeGet(account, AccountItem.InitialMargin),
                        MaintenanceMargin = SafeGet(account, AccountItem.MaintenanceMargin),
                        ExcessEquity = SafeGet(account, AccountItem.BuyingPower) - SafeGet(account, AccountItem.MaintenanceMargin),
                        Commission = SafeGet(account, AccountItem.Commission),
                    });

                    foreach (var order in account.Orders)
                    {
                        if (order.OrderState == OrderState.Filled || order.OrderState == OrderState.Cancelled || order.OrderState == OrderState.Rejected) continue;
                        snap.Orders.Add(new WorkingOrder
                        {
                            ClientOrderId = order.Name,
                            NtOrderId = order.OrderId,
                            AccountName = account.Name,
                            InstrumentName = order.Instrument.FullName,
                            Side = order.OrderAction == OrderAction.Buy || order.OrderAction == OrderAction.BuyToCover ? BookFlowSide.Buy : BookFlowSide.Sell,
                            Status = MapOrderState(order.OrderState),
                            LimitPrice = order.LimitPrice,
                            StopPrice = order.StopPrice,
                            Quantity = order.Quantity,
                            FilledQuantity = order.Filled,
                            AverageFillPrice = order.AverageFillPrice,
                            SubmitUtcTime = DateTime.UtcNow,
                        });
                    }

                    foreach (var pos in account.Positions)
                    {
                        if (pos.Quantity == 0) continue;
                        var signed = pos.MarketPosition == MarketPosition.Short ? -Math.Abs(pos.Quantity) : Math.Abs(pos.Quantity);
                        snap.Positions.Add(new PositionState
                        {
                            AccountName = account.Name,
                            InstrumentName = pos.Instrument.FullName,
                            SignedQuantity = signed,
                            MarketPosition = MapMarketPosition(pos.MarketPosition),
                            AveragePrice = pos.AveragePrice,
                            UnrealizedPnL = pos.GetUnrealizedProfitLoss(PerformanceUnit.Currency),
                            RealizedPnL = 0,
                        });
                    }
                }
            }
            catch (Exception ex) { LogMsg("BuildPortfolioSnapshot error: " + ex.Message); }
            // Stamp after reading NT8 so the snapshot reflects state at >= this version.
            snap.Version = Interlocked.Read(ref _portfolioVersion);
            return snap;
        }

        private double SafeGet(Account account, AccountItem item)
        {
            try { return account.Get(item, Currency.UsDollar); } catch { return 0.0; }
        }

        private static BookFlowOrderStatus MapOrderState(OrderState s)
        {
            switch (s)
            {
                case OrderState.Submitted: return BookFlowOrderStatus.Submitted;
                case OrderState.Accepted: return BookFlowOrderStatus.Accepted;
                case OrderState.Working: return BookFlowOrderStatus.Working;
                case OrderState.PartFilled: return BookFlowOrderStatus.PartFilled;
                case OrderState.Filled: return BookFlowOrderStatus.Filled;
                case OrderState.CancelSubmitted: return BookFlowOrderStatus.CancelSubmitted;
                case OrderState.Cancelled: return BookFlowOrderStatus.Cancelled;
                case OrderState.Rejected: return BookFlowOrderStatus.Rejected;
                default: return BookFlowOrderStatus.Pending;
            }
        }

        private static BookFlowMarketPosition MapMarketPosition(MarketPosition p)
        {
            switch (p)
            {
                case MarketPosition.Long: return BookFlowMarketPosition.Long;
                case MarketPosition.Short: return BookFlowMarketPosition.Short;
                default: return BookFlowMarketPosition.Flat;
            }
        }

        private OrderAck WcfSubmitOrder(OrderRequest request)
        {
            var ack = new OrderAck { ClientOrderId = request?.ClientOrderId, ServerUtcTime = DateTime.UtcNow };
            try
            {
                if (request == null) { ack.Status = BookFlowOrderStatus.Rejected; ack.Message = "Null request"; return ack; }

                var account = ResolveAccount(request.AccountName, out var accErr);
                if (account == null) { ack.Status = BookFlowOrderStatus.Rejected; ack.Message = accErr; return ack; }

                var instrument = Instrument.GetInstrument(request.InstrumentName);
                if (instrument == null) { ack.Status = BookFlowOrderStatus.Rejected; ack.Message = "Instrument not found: " + request.InstrumentName; return ack; }

                var isBuy = request.Action == BookFlowOrderAction.BuyMarket || request.Action == BookFlowOrderAction.BuyLimit;
                var orderAction = isBuy ? OrderAction.Buy : OrderAction.Sell;
                var isLimit = request.Action == BookFlowOrderAction.BuyLimit || request.Action == BookFlowOrderAction.SellLimit;

                Order order = isLimit
                    ? account.CreateOrder(instrument, orderAction, OrderType.Limit, OrderEntry.Manual, TimeInForce.Day, request.Quantity, request.LimitPrice, 0, string.Empty, "BookFlow", DateTime.MinValue, null)
                    : account.CreateOrder(instrument, orderAction, OrderType.Market, OrderEntry.Manual, TimeInForce.Day, request.Quantity, 0, 0, string.Empty, "BookFlow", DateTime.MinValue, null);

                if (order == null) { ack.Status = BookFlowOrderStatus.Rejected; ack.Message = "Failed to create order object"; return ack; }

                lock (_orderLock)
                {
                    _clientOrderMap[request.ClientOrderId ?? order.OrderId] = order;
                    if (!string.IsNullOrEmpty(order.OrderId) && !string.IsNullOrEmpty(request.ClientOrderId))
                        _ntOrderIdToClientId[order.OrderId] = request.ClientOrderId;
                }
                account.Submit(new[] { order });

                ack.NtOrderId = order.OrderId;
                ack.Status = BookFlowOrderStatus.Submitted;
                ack.Message = string.Format("Submitted to {0}", account.Name);
            }
            catch (Exception ex)
            {
                ack.Status = BookFlowOrderStatus.Rejected;
                ack.Message = ex.Message;
            }
            return ack;
        }

        private OperationResult WcfCancelAllOrders(string accountName)
        {
            try
            {
                var account = ResolveAccount(accountName, out var err);
                if (account == null) return new OperationResult { Success = false, Message = err };
                var toCancel = account.Orders.Where(o => o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted || o.OrderState == OrderState.Submitted).ToList();
                int n = 0;
                foreach (var o in toCancel) { try { account.Cancel(new[] { o }); n++; } catch (Exception ex) { LogMsg("Cancel error: " + ex.Message); } }
                return new OperationResult { Success = true, AffectedCount = n, Message = string.Format("CancelAll requested ({0})", n) };
            }
            catch (Exception ex) { return new OperationResult { Success = false, Message = ex.Message }; }
        }

        private OperationResult WcfCancelAtPrice(string accountName, string instrumentName, double price)
        {
            try
            {
                var account = ResolveAccount(accountName, out var err);
                if (account == null) return new OperationResult { Success = false, Message = err };
                var toCancel = account.Orders.Where(o => o.Instrument.FullName == instrumentName &&
                                                         (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted || o.OrderState == OrderState.Submitted) &&
                                                         o.LimitPrice == price).ToList();
                int n = 0;
                foreach (var o in toCancel) { try { account.Cancel(new[] { o }); n++; } catch (Exception ex) { LogMsg("Cancel error: " + ex.Message); } }
                return new OperationResult { Success = true, AffectedCount = n, Message = string.Format("Cancelled {0} @ {1}", n, price) };
            }
            catch (Exception ex) { return new OperationResult { Success = false, Message = ex.Message }; }
        }

        private OperationResult WcfFlattenPosition(string accountName, string instrumentName)
        {
            try
            {
                var account = ResolveAccount(accountName, out var err);
                if (account == null) return new OperationResult { Success = false, Message = err };
                var instrument = Instrument.GetInstrument(instrumentName);
                if (instrument == null) return new OperationResult { Success = false, Message = "Instrument not found: " + instrumentName };
                var position = account.Positions.FirstOrDefault(p => p.Instrument == instrument);
                if (position == null || position.Quantity == 0) return new OperationResult { Success = true, AffectedCount = 0, Message = "No position to flatten" };
                var flattenAction = position.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy;
                int qty = Math.Abs(position.Quantity);
                var order = account.CreateOrder(instrument, flattenAction, OrderType.Market, OrderEntry.Manual, TimeInForce.Day, qty, 0, 0, string.Empty, "BookFlow-Flatten", DateTime.MinValue, null);
                if (order == null) return new OperationResult { Success = false, Message = "Failed to create flatten order" };
                account.Submit(new[] { order });
                return new OperationResult { Success = true, AffectedCount = 1, Message = string.Format("Flatten {0} {1}", flattenAction, qty) };
            }
            catch (Exception ex) { return new OperationResult { Success = false, Message = ex.Message }; }
        }

        #endregion

        #region WCF Event Fan-out (Increment 2b-1)

        private void OnHeartbeatTick(object state)
        {
            if (_disposed) return;
            try
            {
                // Catch accounts added at runtime (broker reconnect, new sim account).
                SubscribeToAccountEvents();

                var host = _wcfHost;
                if (host == null) return;
                var hb = new HeartbeatNotification
                {
                    Sequence = System.Threading.Interlocked.Increment(ref _heartbeatSeq),
                    ServerTimestampTicks = DateTime.UtcNow.Ticks,
                    PortfolioVersion = System.Threading.Interlocked.Read(ref _portfolioVersion),
                };
                host.Callbacks.Broadcast(cb => cb.OnHeartbeat(hb));
            }
            catch (Exception ex) { LogMsg("Heartbeat error: " + ex.Message); }
        }

        private void PublishOrderUpdate(Order order)
        {
            var host = _wcfHost;
            if (host == null) return;
            string clientId = null;
            lock (_orderLock) { if (order.OrderId != null) _ntOrderIdToClientId.TryGetValue(order.OrderId, out clientId); }
            var wo = new WorkingOrder
            {
                ClientOrderId = clientId,
                NtOrderId = order.OrderId,
                AccountName = order.Account != null ? order.Account.Name : null,
                InstrumentName = order.Instrument != null ? order.Instrument.FullName : null,
                Side = (order.OrderAction == OrderAction.Buy || order.OrderAction == OrderAction.BuyToCover) ? BookFlowSide.Buy : BookFlowSide.Sell,
                Status = MapOrderState(order.OrderState),
                LimitPrice = order.LimitPrice,
                StopPrice = order.StopPrice,
                Quantity = order.Quantity,
                FilledQuantity = order.Filled,
                AverageFillPrice = order.AverageFillPrice,
                SubmitUtcTime = DateTime.UtcNow,
            };
            var n = new OrderUpdateNotification { Order = wo };
            lock (_publishLock)
            {
                n.Version = Interlocked.Increment(ref _portfolioVersion);
                host.Callbacks.Broadcast(cb => cb.OnOrderUpdate(n));
            }
        }

        private void PublishExecutionUpdate(Execution exec)
        {
            var host = _wcfHost;
            if (host == null) return;
            var ntOrderId = exec.Order != null ? exec.Order.OrderId : null;
            string clientId = null;
            if (ntOrderId != null) lock (_orderLock) { _ntOrderIdToClientId.TryGetValue(ntOrderId, out clientId); }
            var n = new ExecutionUpdateNotification
            {
                ExecutionId = exec.ExecutionId,
                NtOrderId = ntOrderId,
                ClientOrderId = clientId,
                AccountName = exec.Account != null ? exec.Account.Name : null,
                InstrumentName = exec.Instrument != null ? exec.Instrument.FullName : null,
                MarketPosition = MapMarketPosition(exec.MarketPosition),
                Price = exec.Price,
                Quantity = exec.Quantity,
                UtcTime = DateTime.UtcNow,
            };
            lock (_publishLock)
            {
                n.Version = Interlocked.Increment(ref _portfolioVersion);
                host.Callbacks.Broadcast(cb => cb.OnExecutionUpdate(n));
            }
        }

        private void PublishPositionUpdate(Position pos)
        {
            var host = _wcfHost;
            if (host == null) return;
            var signed = pos.MarketPosition == MarketPosition.Short ? -Math.Abs(pos.Quantity) : Math.Abs(pos.Quantity);
            double pnl;
            try { pnl = pos.GetUnrealizedProfitLoss(PerformanceUnit.Currency); } catch { pnl = 0; }
            var ps = new PositionState
            {
                AccountName = pos.Account != null ? pos.Account.Name : null,
                InstrumentName = pos.Instrument != null ? pos.Instrument.FullName : null,
                SignedQuantity = signed,
                MarketPosition = MapMarketPosition(pos.MarketPosition),
                AveragePrice = pos.AveragePrice,
                UnrealizedPnL = pnl,
                RealizedPnL = 0,
            };
            var n = new PositionUpdateNotification { Position = ps };
            lock (_publishLock)
            {
                n.Version = Interlocked.Increment(ref _portfolioVersion);
                host.Callbacks.Broadcast(cb => cb.OnPositionUpdate(n));
            }
        }

        private void PublishAccountItemUpdate(Account account)
        {
            var host = _wcfHost;
            if (host == null) return;
            var state = new AccountState
            {
                AccountName = account.Name,
                BuyingPower = SafeGet(account, AccountItem.BuyingPower),
                CashValue = SafeGet(account, AccountItem.CashValue),
                RealizedPnL = SafeGet(account, AccountItem.RealizedProfitLoss),
                UnrealizedPnL = SafeGet(account, AccountItem.UnrealizedProfitLoss),
                NetLiquidation = SafeGet(account, AccountItem.NetLiquidation),
                InitialMargin = SafeGet(account, AccountItem.InitialMargin),
                MaintenanceMargin = SafeGet(account, AccountItem.MaintenanceMargin),
                ExcessEquity = SafeGet(account, AccountItem.BuyingPower) - SafeGet(account, AccountItem.MaintenanceMargin),
                Commission = SafeGet(account, AccountItem.Commission),
            };
            var n = new AccountItemUpdateNotification { AccountName = account.Name, Account = state };
            lock (_publishLock)
            {
                n.Version = Interlocked.Increment(ref _portfolioVersion);
                host.Callbacks.Broadcast(cb => cb.OnAccountItemUpdate(n));
            }
        }

        #endregion
    }
}
