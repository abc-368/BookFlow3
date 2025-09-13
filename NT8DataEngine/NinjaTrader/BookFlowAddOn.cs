using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BookFlow.Shared.Contracts;
using BookFlow.Shared.IPC;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.AddOns
{
    public class BookFlowAddOn : AddOnBase
    {
        private static BookFlowAddOn _instance;
        private static readonly object _instanceLock = new object();

        private ControlPipeServer _controlPipeServer;
        private SharedRingBuffer _globalDataChannel;
        private NamedPipeServerStream _eventPipeServer;
        private Thread _eventPipeThread;
        private volatile bool _eventPipeRunning = false;
        private DateTime _lastEventPipeActivity = DateTime.MinValue;

        private readonly Dictionary<string, Order> _clientOrderMap = new Dictionary<string, Order>();
        private readonly object _orderLock = new object();

        private readonly Dictionary<string, byte> _instrumentToTickerId = new Dictionary<string, byte>();
        private readonly Dictionary<byte, string> _tickerIdToInfo = new Dictionary<byte, string>();
        private byte _nextTickerId = 1;
        private readonly object _tickerLock = new object();

        private readonly string _logPrefix = "[BookFlowAddOn]";

        private readonly ConcurrentQueue<string> _eventQueue = new ConcurrentQueue<string>();
        private readonly AutoResetEvent _eventSignal = new AutoResetEvent(false);
        private Thread _eventWriterThread;

        public static BookFlowAddOn Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null)
                        {
                            _instance = new BookFlowAddOn();
                            _instance.InitializeAddOn();
                        }
                    }
                }
                return _instance;
            }
        }

        private void InitializeAddOn()
        {
            try
            {
                LogMsg("Starting BookFlow AddOn...");
                StartGlobalChannels();
                SubscribeToAccountEvents();
                LogMsg("BookFlow AddOn ready.");
            }
            catch (Exception ex)
            {
                LogMsg($"ADDON INITIALIZATION ERROR: {ex.Message}");
            }
        }

        private void StartGlobalChannels()
        {
            ControlPipeServer.Logger = s => LogMsg(s);

            var controlChannelName = "BookFlow_Control_Global";
            _controlPipeServer = new ControlPipeServer(controlChannelName);
            _controlPipeServer.MessageReceived += OnControlMessageReceived;
            _controlPipeServer.ClientDisconnected += OnControlPipeClientDisconnected;

            var dataChannelName = "BookFlow_Data_Global";
            _globalDataChannel = new SharedRingBuffer(dataChannelName, 1024 * 1024);

            var eventChannelName = "BookFlow_Event_Global";
            _eventPipeRunning = true;
            _eventWriterThread = new Thread(EventWriterLoop) { IsBackground = true, Name = "EventPipeWriter" };
            _eventWriterThread.Start();
            _eventPipeThread = new Thread(() => RunEventPipeServer(eventChannelName)) { IsBackground = true, Name = "EventPipeServer" };
            _eventPipeThread.Start();
        }

        private void EventWriterLoop()
        {
            while (_eventPipeRunning)
            {
                try
                {
                    _eventSignal.WaitOne(1000);
                    while (_eventQueue.TryDequeue(out var json))
                    {
                        try
                        {
                            var pipe = _eventPipeServer;
                            if (pipe == null || !pipe.IsConnected) continue;
                            var buf = Encoding.UTF8.GetBytes(json);
                            pipe.Write(buf, 0, buf.Length);
                            pipe.Flush();
                            _lastEventPipeActivity = DateTime.UtcNow;
                        }
                        catch (IOException) { /* drop and continue */ }
                        catch (ObjectDisposedException) { /* drop and continue */ }
                        catch (Exception ex) { LogMsg($"ERROR: EventWriterLoop write failed: {ex.Message}"); }
                    }
                }
                catch (ThreadInterruptedException) { }
                catch (Exception ex) { LogMsg($"ERROR: EventWriterLoop: {ex.Message}"); }
            }
        }

        public void WriteToGlobalChannel(byte tickerId, UnifiedMarketDataMessage data)
        {
            try
            {
                if (_globalDataChannel != null)
                {
                    data.IpcQueueTime = Stopwatch.GetTimestamp();
                    if (!_globalDataChannel.TryWrite(ref data))
                        LogMsg($"WARNING: Global data buffer full, dropping message for ticker {tickerId}");
                    else
                        _globalDataChannel.SignalDataAvailable();
                }
            }
            catch (Exception ex)
            {
                LogMsg($"ERROR: WriteToGlobalChannel failed: {ex.Message}");
            }
        }

        public byte RegisterTicker(string instrumentName, double tickSize, double pointValue)
        {
            lock (_tickerLock)
            {
                if (_instrumentToTickerId.TryGetValue(instrumentName, out var existing))
                    return existing;
                var id = _nextTickerId++;
                _instrumentToTickerId[instrumentName] = id;
                _tickerIdToInfo[id] = $"{instrumentName}|{tickSize}|{pointValue}";
                LogMsg($"Registered ticker '{instrumentName}' as ID {id} (TickSize={tickSize}, PointValue={pointValue})");
                return id;
            }
        }

        #region NT8 Core Event Handlers
        private void OnOrderUpdate(object sender, OrderEventArgs e)
        {
            try
            {
                if (e.Order?.Instrument == null) return;
                BroadcastOrderUpdateEvent(e.Order);
            }
            catch (Exception ex) { LogMsg($"ERROR: OnOrderUpdate failed: {ex.Message}"); }
        }
        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            try
            {
                if (e.Execution?.Order?.Instrument == null) return;
                BroadcastExecutionUpdateEvent(e.Execution);
            }
            catch (Exception ex) { LogMsg($"ERROR: OnExecutionUpdate failed: {ex.Message}"); }
        }
        private void OnPositionUpdate(object sender, PositionEventArgs e)
        {
            try
            {
                if (e.Position?.Instrument == null) return;
                BroadcastPositionUpdateEvent(e.Position);
            }
            catch (Exception ex) { LogMsg($"ERROR: OnPositionUpdate failed: {ex.Message}"); }
        }
        #endregion

        #region Event Pipe
        private void RunEventPipeServer(string pipeName)
        {
            while (_eventPipeRunning)
            {
                try
                {
                    LogMsg($"Event Pipe Server waiting for client on '{pipeName}'...");
                    var pipeSecurity = new PipeSecurity();
                    pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                    using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous, 0, 0, pipeSecurity))
                    {
                        _eventPipeServer = pipe;
                        pipe.WaitForConnection();
                        _lastEventPipeActivity = DateTime.UtcNow;
                        LogMsg("Event Pipe client connected.");
                        while (_eventPipeRunning)
                        {
                            try
                            {
                                if (!pipe.IsConnected) { LogMsg("Event Pipe client disconnected (IsConnected=false)"); break; }
                                if ((DateTime.UtcNow - _lastEventPipeActivity).TotalMinutes > 5) { LogMsg("Event Pipe stale - forcing reconnection"); break; }
                                Thread.Sleep(1000);
                            }
                            catch (IOException) { LogMsg("Event Pipe client disconnected (IOException)"); break; }
                            catch (ObjectDisposedException) { LogMsg("Event Pipe disposed"); break; }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_eventPipeRunning) { LogMsg($"ERROR: Event Pipe Server failed: {ex.Message}"); Thread.Sleep(1000); }
                }
                finally
                {
                    _eventPipeServer = null;
                    LogMsg("Event Pipe Server ready for new client connection...");
                    if (_eventPipeRunning) Thread.Sleep(1000);
                }
            }
            LogMsg("Event Pipe Server stopped.");
        }

        private void RestartEventPipe()
        {
            try
            {
                LogMsg("Restarting event pipe...");
                if (_eventPipeServer != null)
                {
                    try { _eventPipeServer.Close(); _eventPipeServer.Dispose(); } catch (Exception ex) { LogMsg($"Event pipe close during restart: {ex.Message}"); } finally { _eventPipeServer = null; }
                }
                Thread.Sleep(100);
                LogMsg("Event pipe restart initiated.");
            }
            catch (Exception ex) { LogMsg($"ERROR: RestartEventPipe failed: {ex.Message}"); }
        }

        private bool BroadcastEventMessage(string json)
        {
            // Enqueue to be written by background writer to avoid blocking NT8 event threads
            _eventQueue.Enqueue(json);
            _eventSignal.Set();
            return true;
        }

        private void BroadcastOrderUpdateEvent(Order order)
        {
            var evtJson = $"{{\"Type\":\"OrderUpdate\",\"OrderId\":\"{order.OrderId}\",\"State\":{(int)order.OrderState},\"Filled\":{order.Filled},\"AverageFillPrice\":{order.AverageFillPrice}}}";
            BroadcastEventMessage(evtJson);
        }
        private void BroadcastExecutionUpdateEvent(Execution execution)
        {
            var evtJson = $"{{\"Type\":\"ExecutionUpdate\",\"OrderId\":\"{execution.OrderId}\",\"Price\":{execution.Price},\"Quantity\":{execution.Quantity}}}";
            BroadcastEventMessage(evtJson);
        }
        private void BroadcastPositionUpdateEvent(Position position)
        {
            double pnl = position.GetUnrealizedProfitLoss(PerformanceUnit.Currency);
            var evtJson = $"{{\"Type\":\"PositionUpdate\",\"Instrument\":\"{position.Instrument.FullName}\",\"Quantity\":{position.Quantity},\"AveragePrice\":{position.AveragePrice},\"UnrealizedPnL\":{pnl}}}";
            BroadcastEventMessage(evtJson);
        }
        #endregion

        #region Control Pipe
        private void OnControlPipeClientDisconnected()
        {
            try { LogMsg("Control pipe client disconnected -> syncing event pipe"); RestartEventPipe(); }
            catch (Exception ex) { LogMsg($"ERROR: OnControlPipeClientDisconnected: {ex.Message}"); }
        }

        private Task<object> OnControlMessageReceived(ControlMessage message)
        {
            try
            {
                LogMsg($"Control message: {message.Type}");
                switch (message.Type)
                {
                    case ControlMessage.RequestType.Disconnect:
                        HandleDisconnectRequest(message); break;
                    case ControlMessage.RequestType.GetTickerDictionary:
                        return Task.FromResult<object>(HandleGetTickerDictionary(message));
                    case ControlMessage.RequestType.SubmitOrder:
                        return Task.FromResult<object>(HandleSubmitOrder(message));
                    case ControlMessage.RequestType.AccountStatusBroadcast:
                        return Task.FromResult<object>(HandleGetAccountStatus(message));
                    case ControlMessage.RequestType.RequestPortfolioState:
                        return Task.FromResult<object>(HandleGetPortfolioState(message));
                    default:
                        LogMsg($"Unhandled message type: {message.Type}"); break;
                }
            }
            catch (Exception ex) { LogMsg($"ERROR: OnControlMessageReceived failed: {ex.Message}"); }
            return Task.FromResult<object>(null);
        }

        private TickerDictionaryResponse HandleGetTickerDictionary(ControlMessage message)
        {
            try
            {
                var resp = new TickerDictionaryResponse { RequestId = message.RequestId, Timestamp = DateTime.UtcNow, TickerDictionary = new Dictionary<byte, string>() };
                lock (_tickerLock)
                    foreach (var kvp in _tickerIdToInfo)
                        resp.TickerDictionary[kvp.Key] = kvp.Value;
                return resp;
            }
            catch (Exception ex)
            {
                LogMsg($"ERROR: HandleGetTickerDictionary: {ex.Message}");
                return new TickerDictionaryResponse { RequestId = message.RequestId, Timestamp = DateTime.UtcNow, TickerDictionary = new Dictionary<byte, string>() };
            }
        }

        private OrderStatusMessage HandleSubmitOrder(ControlMessage message)
        {
            try
            {
                var account = GetPreferredAccount();
                if (account == null)
                    return Reject(message, "No trading account available");

                switch (message.OrderCommand.Action)
                {
                    case OrderCommand.OrderAction.CancelAll: return HandleCancelAllOrders(message, account);
                    case OrderCommand.OrderAction.CancelAtPrice: return HandleCancelAtPrice(message, account);
                    case OrderCommand.OrderAction.Flat: return HandleFlattenPositionInternal(message, account);
                }

                var instrument = Instrument.GetInstrument(message.InstrumentName);
                if (instrument == null)
                    return Reject(message, $"Instrument not found: {message.InstrumentName}");

                var orderAction = (message.OrderCommand.Action == OrderCommand.OrderAction.BuyMarket || message.OrderCommand.Action == OrderCommand.OrderAction.BuyLimit)
                    ? OrderAction.Buy : OrderAction.Sell;

                Order order;
                if (message.OrderCommand.LimitPrice > 0)
                {
                    order = account.CreateOrder(instrument, orderAction, OrderType.Limit, OrderEntry.Manual, TimeInForce.Day,
                        message.OrderCommand.Quantity, message.OrderCommand.LimitPrice, 0, string.Empty, "BookFlow", DateTime.MinValue, null);
                }
                else
                {
                    order = account.CreateOrder(instrument, orderAction, OrderType.Market, OrderEntry.Manual, TimeInForce.Day,
                        message.OrderCommand.Quantity, 0, 0, string.Empty, "BookFlow", DateTime.MinValue, null);
                }

                if (order == null)
                    return Reject(message, "Failed to create order object");

                lock (_orderLock)
                    _clientOrderMap[message.OrderCommand.ClientOrderId] = order;

                account.Submit(new[] { order });
                return new OrderStatusMessage
                {
                    ClientOrderId = message.OrderCommand.ClientOrderId,
                    Status = OrderCommand.OrderStatus.Submitted,
                    Message = $"Order submitted (NT8 OrderId={order.OrderId}, Account={account.Name})",
                    Timestamp = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return Reject(message, ex.Message);
            }
        }

        private OrderStatusMessage HandleCancelAtPrice(ControlMessage message, Account account)
        {
            try
            {
                double price = message.OrderCommand.LimitPrice;
                var toCancel = account.Orders.Where(o => o.Instrument.FullName == message.InstrumentName &&
                                                          (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted || o.OrderState == OrderState.Submitted) &&
                                                          o.LimitPrice == price).ToList();
                foreach (var o in toCancel)
                    try { account.Cancel(new[] { o }); } catch (Exception ex) { LogMsg($"Cancel error: {ex.Message}"); }
                var msg = toCancel.Count == 0 ? $"No working orders at {price}" : $"Requested cancel of {toCancel.Count} orders @ {price}";
                return new OrderStatusMessage { ClientOrderId = message.OrderCommand.ClientOrderId, Status = OrderCommand.OrderStatus.Cancelled, Message = msg, Timestamp = DateTime.UtcNow };
            }
            catch (Exception ex) { return Reject(message, $"CancelAtPrice failed: {ex.Message}"); }
        }

        private OrderStatusMessage HandleCancelAllOrders(ControlMessage message, Account account)
        {
            try
            {
                var toCancel = account.Orders.Where(o => o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted || o.OrderState == OrderState.Submitted).ToList();
                int count = 0;
                foreach (var o in toCancel)
                {
                    try { account.Cancel(new[] { o }); count++; } catch (Exception ex) { LogMsg($"Cancel error: {ex.Message}"); }
                }
                return new OrderStatusMessage { ClientOrderId = message.OrderCommand.ClientOrderId, Status = OrderCommand.OrderStatus.Cancelled, Message = $"CancelAll requested ({count} orders)", Timestamp = DateTime.UtcNow };
            }
            catch (Exception ex) { return Reject(message, $"CancelAll failed: {ex.Message}"); }
        }

        private OrderStatusMessage HandleFlattenPositionInternal(ControlMessage message, Account account)
        {
            try
            {
                var instrument = Instrument.GetInstrument(message.InstrumentName);
                if (instrument == null) return Reject(message, $"Instrument not found: {message.InstrumentName}");
                var position = account.Positions.FirstOrDefault(p => p.Instrument == instrument);
                if (position == null || position.Quantity == 0)
                    return new OrderStatusMessage { ClientOrderId = message.OrderCommand.ClientOrderId, Status = OrderCommand.OrderStatus.Submitted, Message = "No position to flatten", Timestamp = DateTime.UtcNow };
                var flattenAction = position.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy;
                int qty = Math.Abs(position.Quantity);
                var order = account.CreateOrder(instrument, flattenAction, OrderType.Market, OrderEntry.Manual, TimeInForce.Day, qty, 0, 0, string.Empty, "BookFlow-Flatten", DateTime.MinValue, null);
                if (order == null) return Reject(message, "Failed to create flatten order");
                lock (_orderLock) _clientOrderMap[message.OrderCommand.ClientOrderId] = order;
                account.Submit(new[] { order });
                return new OrderStatusMessage { ClientOrderId = message.OrderCommand.ClientOrderId, Status = OrderCommand.OrderStatus.Submitted, Message = $"Flatten submitted ({flattenAction} {qty})", Timestamp = DateTime.UtcNow };
            }
            catch (Exception ex) { return Reject(message, $"Flatten failed: {ex.Message}"); }
        }

        private void HandleDisconnectRequest(ControlMessage message)
        {
            try
            {
                LogMsg($"Client disconnect (RequestId={message.RequestId})");
                if (_eventPipeServer != null)
                {
                    try { _eventPipeServer.Close(); _eventPipeServer.Dispose(); } catch (Exception ex) { LogMsg($"Event pipe close error: {ex.Message}"); } finally { _eventPipeServer = null; }
                }
            }
            catch (Exception ex) { LogMsg($"ERROR: HandleDisconnectRequest: {ex.Message}"); }
        }

        private AccountStateMessage HandleGetAccountStatus(ControlMessage message)
        {
            try
            {
                var account = GetPreferredAccount();
                if (account == null)
                    return new AccountStateMessage { AccountName = "No Account" };
                return new AccountStateMessage
                {
                    AccountName = account.Name,
                    BuyingPower = account.Get(AccountItem.BuyingPower, Currency.UsDollar),
                    CashValue = account.Get(AccountItem.CashValue, Currency.UsDollar),
                    RealizedPnL = account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar),
                    UnrealizedPnL = account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar),
                    NetLiquidation = account.Get(AccountItem.NetLiquidation, Currency.UsDollar),
                    InitialMargin = account.Get(AccountItem.InitialMargin, Currency.UsDollar),
                    MaintenanceMargin = account.Get(AccountItem.MaintenanceMargin, Currency.UsDollar),
                    ExcessEquity = 0.0,
                    Commission = account.Get(AccountItem.Commission, Currency.UsDollar)
                };
            }
            catch (Exception ex)
            {
                LogMsg($"ERROR: HandleGetAccountStatus: {ex.Message}");
                return new AccountStateMessage { AccountName = "Error" };
            }
        }

        private PortfolioStateMessage HandleGetPortfolioState(ControlMessage message)
        {
            try
            {
                var response = new PortfolioStateMessage { RequestId = message.RequestId, Timestamp = DateTime.UtcNow };
                var account = GetPreferredAccount();
                if (account == null)
                {
                    response.Account = new AccountStateMessage { AccountName = "No Account" };
                    return response;
                }
                response.Account = new AccountStateMessage
                {
                    AccountName = account.Name,
                    BuyingPower = account.Get(AccountItem.BuyingPower, Currency.UsDollar),
                    CashValue = account.Get(AccountItem.CashValue, Currency.UsDollar),
                    RealizedPnL = account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar),
                    UnrealizedPnL = account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar),
                    NetLiquidation = account.Get(AccountItem.NetLiquidation, Currency.UsDollar),
                    InitialMargin = account.Get(AccountItem.InitialMargin, Currency.UsDollar),
                    MaintenanceMargin = account.Get(AccountItem.MaintenanceMargin, Currency.UsDollar),
                    ExcessEquity = account.Get(AccountItem.BuyingPower, Currency.UsDollar) - account.Get(AccountItem.MaintenanceMargin, Currency.UsDollar),
                    Commission = account.Get(AccountItem.Commission, Currency.UsDollar)
                };
                foreach (var order in account.Orders)
                {
                    if (order.OrderState == OrderState.Filled || order.OrderState == OrderState.Cancelled || order.OrderState == OrderState.Rejected) continue;
                    response.Orders.Add(new WorkingOrderMessage
                    {
                        OrderId = order.OrderId,
                        NTOrderId = order.OrderId,
                        ClientOrderId = order.Name,
                        Instrument = order.Instrument.FullName,
                        Side = (byte)(order.OrderAction == OrderAction.Buy ? 1 : 2),
                        State = (byte)order.OrderState,
                        Type = (byte)order.OrderType,
                        TimeInForce = (byte)order.TimeInForce,
                        Price = order.LimitPrice,
                        Quantity = order.Quantity,
                        FilledQuantity = order.Filled,
                        AverageFillPrice = order.AverageFillPrice
                    });
                }
                foreach (var pos in account.Positions)
                {
                    if (pos.Quantity == 0) continue;
                    response.Positions.Add(new PositionMessage
                    {
                        Instrument = pos.Instrument.FullName,
                        Quantity = pos.Quantity,
                        AveragePrice = pos.AveragePrice,
                        UnrealizedPnL = pos.GetUnrealizedProfitLoss(PerformanceUnit.Currency),
                        RealizedPnL = 0,
                        LastUpdateTime = DateTime.UtcNow
                    });
                }
                return response;
            }
            catch (Exception ex)
            {
                LogMsg($"ERROR: HandleGetPortfolioState: {ex.Message}");
                return new PortfolioStateMessage { RequestId = message.RequestId, Timestamp = DateTime.UtcNow, Account = new AccountStateMessage { AccountName = "Error" } };
            }
        }
        #endregion

        #region Helpers
        private OrderStatusMessage Reject(ControlMessage message, string reason) => new OrderStatusMessage
        {
            ClientOrderId = message.OrderCommand?.ClientOrderId,
            Status = OrderCommand.OrderStatus.Rejected,
            Message = reason,
            Timestamp = DateTime.UtcNow
        };

        private void LogMsg(string message)
        {
            Trace.WriteLine($"{_logPrefix} {DateTime.Now:HH:mm:ss.fff} - {message}");
        }

        private void SubscribeToAccountEvents()
        {
            foreach (var account in Account.All)
            {
                if (account.Name == "Backtest") continue;
                account.OrderUpdate += OnOrderUpdate;
                account.ExecutionUpdate += OnExecutionUpdate;
                account.PositionUpdate += OnPositionUpdate;
            }
        }
        #endregion

        private Account GetPreferredAccount()
        {
            try
            {
                // Prefer Playback account when Market Replay is active
                var playback = Account.All.FirstOrDefault(a => a.Name != null && a.Name.StartsWith("Playback", StringComparison.OrdinalIgnoreCase));
                if (playback != null) return playback;

                // Fallback: any non-Backtest account
                var any = Account.All.FirstOrDefault(a => a.Name != "Backtest");
                return any;
            }
            catch
            {
                return null;
            }
        }
    }
}
