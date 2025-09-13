using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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

        private TcpListener _eventListener;
        private TcpClient _eventClient;
        private Thread _eventServerThread;
        private volatile bool _eventServerRunning = false;
        private DateTime _lastEventActivity = DateTime.MinValue;
        private const int EVENT_PORT = 38755;

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
                LogMsg(string.Format("ADDON INITIALIZATION ERROR: {0}", ex.Message));
            }
        }

        private void StartGlobalChannels()
        {
            ControlPipeServer.Logger = delegate(string s) { LogMsg(s); };

            var controlChannelName = "BookFlow_Control_Global";
            _controlPipeServer = new ControlPipeServer(controlChannelName);
            _controlPipeServer.MessageReceived += OnControlMessageReceived;
            _controlPipeServer.ClientDisconnected += OnControlPipeClientDisconnected;

            var dataChannelName = "BookFlow_Data_Global";
            _globalDataChannel = new SharedRingBuffer(dataChannelName, 1024 * 1024);

            _eventServerRunning = true;
            _eventWriterThread = new Thread(EventWriterLoop) { IsBackground = true, Name = "EventWriterThread" };
            _eventWriterThread.Start();
            _eventServerThread = new Thread(delegate() { RunEventTcpServer(); }) { IsBackground = true, Name = "EventServerThread" };
            _eventServerThread.Start();
        }

        private void EventWriterLoop()
        {
            while (_eventServerRunning)
            {
                try
                {
                    _eventSignal.WaitOne(1000);
                    string json;
                    while (_eventQueue.TryDequeue(out json))
                    {
                        try
                        {
                            var client = _eventClient;
                            if (client == null || !client.Connected) continue;

                            var contentBytes = Encoding.UTF8.GetBytes(json);
                            var lengthBytes = BitConverter.GetBytes(contentBytes.Length);
                            var buffer = new byte[4 + contentBytes.Length];
                            lengthBytes.CopyTo(buffer, 0);
                            contentBytes.CopyTo(buffer, 4);

                            var stream = client.GetStream();
                            stream.Write(buffer, 0, buffer.Length);
                            stream.Flush();
                            _lastEventActivity = DateTime.UtcNow;
                        }
                        catch (IOException) { CloseEventClient(); } 
                        catch (ObjectDisposedException) { }
                        catch (Exception ex) { LogMsg(string.Format("ERROR: EventWriterLoop write failed: {0}", ex.Message)); }
                    }
                }
                catch (ThreadInterruptedException) { } 
                catch (Exception ex) { LogMsg(string.Format("ERROR: EventWriterLoop: {0}", ex.Message)); }
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
                        LogMsg(string.Format("WARNING: Global data buffer full, dropping message for ticker {0}", tickerId));
                    else
                        _globalDataChannel.SignalDataAvailable();
                }
            }
            catch (Exception ex)
            {
                LogMsg(string.Format("ERROR: WriteToGlobalChannel failed: {0}", ex.Message));
            }
        }

        public byte RegisterTicker(string instrumentName, double tickSize, double pointValue)
        {
            lock (_tickerLock)
            {
                byte existing;
                if (_instrumentToTickerId.TryGetValue(instrumentName, out existing))
                    return existing;
                var id = _nextTickerId++;
                _instrumentToTickerId[instrumentName] = id;
                _tickerIdToInfo[id] = string.Format("{0}|{1}|{2}", instrumentName, tickSize, pointValue);
                LogMsg(string.Format("Registered ticker '{0}' as ID {1} (TickSize={2}, PointValue={3})", instrumentName, id, tickSize, pointValue));
                return id;
            }
        }

        #region NT8 Core Event Handlers
        private void OnOrderUpdate(object sender, OrderEventArgs e)
        {
            try
            {
                if (e.Order == null || e.Order.Instrument == null) return;
                BroadcastOrderUpdateEvent(e.Order);
            }
            catch (Exception ex) { LogMsg(string.Format("ERROR: OnOrderUpdate failed: {0}", ex.Message)); }
        }
        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            try
            {
                if (e.Execution == null || e.Execution.Order == null || e.Execution.Order.Instrument == null) return;
                BroadcastExecutionUpdateEvent(e.Execution);
            }
            catch (Exception ex) { LogMsg(string.Format("ERROR: OnExecutionUpdate failed: {0}", ex.Message)); }
        }
        private void OnPositionUpdate(object sender, PositionEventArgs e)
        {
            try
            {
                if (e.Position == null || e.Position.Instrument == null) return;
                BroadcastPositionUpdateEvent(e.Position);
            }
            catch (Exception ex) { LogMsg(string.Format("ERROR: OnPositionUpdate failed: {0}", ex.Message)); }
        }
        #endregion

        #region Event Channel
        private void RunEventTcpServer()
        {
            try
            {
                _eventListener = new TcpListener(IPAddress.Loopback, EVENT_PORT);
                _eventListener.Start();
                _eventServerRunning = true;
                LogMsg(string.Format("Event TCP Server listening on port {0}...", EVENT_PORT));
            }
            catch (Exception ex)
            {
                LogMsg(string.Format("FATAL: Event TCP Server failed to start: {0}", ex.Message));
                _eventServerRunning = false;
                return;
            }

            while (_eventServerRunning)
            {
                try
                {
                    LogMsg("Event TCP Server waiting for a client...");
                    var client = _eventListener.AcceptTcpClient();
                    LogMsg("Event TCP client connected.");
                    
                    CloseEventClient(); 

                    _eventClient = client;
                    _lastEventActivity = DateTime.UtcNow;
                }
                catch (SocketException)
                {
                    if (_eventServerRunning) LogMsg("Event TCP listener socket closed.");
                }
                catch (Exception ex)
                {
                    if (_eventServerRunning)
                    {
                        LogMsg(string.Format("ERROR: Event TCP Server failed: {0}", ex.Message));
                        Thread.Sleep(1000);
                    }
                }
            }
            _eventListener.Stop();
            LogMsg("Event TCP Server stopped.");
        }

        private void CloseEventClient()
        {
            if (_eventClient != null)
            {
                try { _eventClient.Close(); } catch { } 
                _eventClient = null;
                LogMsg("Event TCP client disconnected and cleaned up.");
            }
        }

        private void RestartEventChannel()
        {
            try
            {
                LogMsg("Restarting event channel...");
                CloseEventClient();
                LogMsg("Event channel ready for new client.");
            }
            catch (Exception ex) { LogMsg(string.Format("ERROR: RestartEventChannel failed: {0}", ex.Message)); }
        }

        private bool BroadcastEventMessage(string json)
        {
            _eventQueue.Enqueue(json);
            _eventSignal.Set();
            return true;
        }

        private void BroadcastOrderUpdateEvent(Order order)
        {
            var evtJson = string.Format("{{\"Type\":\"OrderUpdate\",\"OrderId\":\"{0}\",\"State\":{1},\"Filled\":{2},\"AverageFillPrice\":{3}}}", 
                order.OrderId, (int)order.OrderState, order.Filled, order.AverageFillPrice);
            BroadcastEventMessage(evtJson);
        }
        private void BroadcastExecutionUpdateEvent(Execution execution)
        {
            var evtJson = string.Format("{{\"Type\":\"ExecutionUpdate\",\"OrderId\":\"{0}\",\"Price\":{1},\"Quantity\":{2}}}", 
                execution.OrderId, execution.Price, execution.Quantity);
            BroadcastEventMessage(evtJson);
        }
        private void BroadcastPositionUpdateEvent(Position position)
        {
            double pnl = position.GetUnrealizedProfitLoss(PerformanceUnit.Currency);
            var evtJson = string.Format("{{\"Type\":\"PositionUpdate\",\"Instrument\":\"{0}\",\"Quantity\":{1},\"AveragePrice\":{2},\"UnrealizedPnL\":{3}}}", 
                position.Instrument.FullName, position.Quantity, position.AveragePrice, pnl);
            BroadcastEventMessage(evtJson);
        }
        #endregion

        #region Control Pipe
        private void OnControlPipeClientDisconnected()
        {
            try { LogMsg("Control pipe client disconnected -> syncing event channel"); RestartEventChannel(); }
            catch (Exception ex) { LogMsg(string.Format("ERROR: OnControlPipeClientDisconnected: {0}", ex.Message)); }
        }

        private Task<object> OnControlMessageReceived(ControlMessage message)
        {
            try
            {
                LogMsg(string.Format("Control message: {0}", message.Type));
                switch (message.Type)
                {
                    case ControlMessage.RequestType.Disconnect:
                        HandleDisconnectRequest(message); 
                        break;
                    case ControlMessage.RequestType.GetTickerDictionary:
                        return Task.FromResult<object>(HandleGetTickerDictionary(message));
                    case ControlMessage.RequestType.SubmitOrder:
                        return Task.FromResult<object>(HandleSubmitOrder(message));
                    case ControlMessage.RequestType.AccountStatusBroadcast:
                        return Task.FromResult<object>(HandleGetAccountStatus(message));
                    case ControlMessage.RequestType.RequestPortfolioState:
                        return Task.FromResult<object>(HandleGetPortfolioState(message));
                    default:
                        LogMsg(string.Format("Unhandled message type: {0}", message.Type)); 
                        break;
                }
            }
            catch (Exception ex) { LogMsg(string.Format("ERROR: OnControlMessageReceived failed: {0}", ex.Message)); }
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
                LogMsg(string.Format("ERROR: HandleGetTickerDictionary: {0}", ex.Message));
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
                    return Reject(message, string.Format("Instrument not found: {0}", message.InstrumentName));

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
                    Message = string.Format("Order submitted (NT8 OrderId={0}, Account={1})", order.OrderId, account.Name),
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
                    try { account.Cancel(new[] { o }); } catch (Exception ex) { LogMsg(string.Format("Cancel error: {0}", ex.Message)); }
                var msg = toCancel.Count == 0 ? string.Format("No working orders at {0}", price) : string.Format("Requested cancel of {0} orders @ {1}", toCancel.Count, price);
                return new OrderStatusMessage { ClientOrderId = message.OrderCommand.ClientOrderId, Status = OrderCommand.OrderStatus.Cancelled, Message = msg, Timestamp = DateTime.UtcNow };
            }
            catch (Exception ex) { return Reject(message, string.Format("CancelAtPrice failed: {0}", ex.Message)); }
        }

        private OrderStatusMessage HandleCancelAllOrders(ControlMessage message, Account account)
        {
            try
            {
                var toCancel = account.Orders.Where(o => o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted || o.OrderState == OrderState.Submitted).ToList();
                int count = 0;
                foreach (var o in toCancel)
                {
                    try { account.Cancel(new[] { o }); count++; } catch (Exception ex) { LogMsg(string.Format("Cancel error: {0}", ex.Message)); }
                }
                return new OrderStatusMessage { ClientOrderId = message.OrderCommand.ClientOrderId, Status = OrderCommand.OrderStatus.Cancelled, Message = string.Format("CancelAll requested ({0} orders)", count), Timestamp = DateTime.UtcNow };
            }
            catch (Exception ex) { return Reject(message, string.Format("CancelAll failed: {0}", ex.Message)); }
        }

        private OrderStatusMessage HandleFlattenPositionInternal(ControlMessage message, Account account)
        {
            try
            {
                var instrument = Instrument.GetInstrument(message.InstrumentName);
                if (instrument == null) return Reject(message, string.Format("Instrument not found: {0}", message.InstrumentName));
                var position = account.Positions.FirstOrDefault(p => p.Instrument == instrument);
                if (position == null || position.Quantity == 0)
                    return new OrderStatusMessage { ClientOrderId = message.OrderCommand.ClientOrderId, Status = OrderCommand.OrderStatus.Submitted, Message = "No position to flatten", Timestamp = DateTime.UtcNow };
                var flattenAction = position.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy;
                int qty = Math.Abs(position.Quantity);
                var order = account.CreateOrder(instrument, flattenAction, OrderType.Market, OrderEntry.Manual, TimeInForce.Day, qty, 0, 0, string.Empty, "BookFlow-Flatten", DateTime.MinValue, null);
                if (order == null) return Reject(message, "Failed to create flatten order");
                lock (_orderLock) _clientOrderMap[message.OrderCommand.ClientOrderId] = order;
                account.Submit(new[] { order });
                return new OrderStatusMessage { ClientOrderId = message.OrderCommand.ClientOrderId, Status = OrderCommand.OrderStatus.Submitted, Message = string.Format("Flatten submitted ({0} {1})", flattenAction, qty), Timestamp = DateTime.UtcNow };
            }
            catch (Exception ex) { return Reject(message, string.Format("Flatten failed: {0}", ex.Message)); }
        }

        private void HandleDisconnectRequest(ControlMessage message)
        {
            try
            {
                LogMsg(string.Format("Client disconnect (RequestId={0})", message.RequestId));
                CloseEventClient();
            }
            catch (Exception ex) { LogMsg(string.Format("ERROR: HandleDisconnectRequest: {0}", ex.Message)); }
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
                LogMsg(string.Format("ERROR: HandleGetAccountStatus: {0}", ex.Message));
                return new AccountStateMessage { AccountName = "Error" };
            }
        }

        private PortfolioStateMessage HandleGetPortfolioState(ControlMessage message)
        {
            try
            {
                LogMsg("Handling GetPortfolioState request...");
                var response = new PortfolioStateMessage { RequestId = message.RequestId, Timestamp = DateTime.UtcNow };
                var account = GetPreferredAccount();
                if (account == null)
                {
                    LogMsg("No account found for portfolio state.");
                    response.Account = new AccountStateMessage { AccountName = "No Account" };
                    return response;
                }
                LogMsg(string.Format("Found account: {0}. Total orders: {1}, Total positions: {2}", account.Name, account.Orders.Count, account.Positions.Count));

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

                int workingOrderCount = 0;
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
                    workingOrderCount++;
                }
                LogMsg(string.Format("Found {0} working orders.", workingOrderCount));

                int activePositionCount = 0;
                foreach (var pos in account.Positions)
                {
                    if (pos.Quantity == 0) continue;
                    var signedQty = pos.MarketPosition == MarketPosition.Short ? -System.Math.Abs(pos.Quantity) : System.Math.Abs(pos.Quantity);
                    response.Positions.Add(new PositionMessage
                    {
                        Instrument = pos.Instrument.FullName,
                        Quantity = signedQty,
                        AveragePrice = pos.AveragePrice,
                        UnrealizedPnL = pos.GetUnrealizedProfitLoss(PerformanceUnit.Currency),
                        RealizedPnL = 0,
                        LastUpdateTime = DateTime.UtcNow
                    });
                    activePositionCount++;
                }
                LogMsg(string.Format("Found {0} active positions.", activePositionCount));

                LogMsg("Finished handling GetPortfolioState request.");
                return response;
            }
            catch (Exception ex)
            {
                LogMsg(string.Format("ERROR: HandleGetPortfolioState: {0}", ex.Message));
                return new PortfolioStateMessage { RequestId = message.RequestId, Timestamp = DateTime.UtcNow, Account = new AccountStateMessage { AccountName = "Error" } };
            }
        }
        #endregion

        #region Helpers
        private OrderStatusMessage Reject(ControlMessage message, string reason)
        {
            return new OrderStatusMessage
            {
                ClientOrderId = message.OrderCommand != null ? message.OrderCommand.ClientOrderId : null,
                Status = OrderCommand.OrderStatus.Rejected,
                Message = reason,
                Timestamp = DateTime.UtcNow
            };
        }

        private void LogMsg(string message)
        {
            string logEntry = string.Format("{0} {1:HH:mm:ss.fff} - {2}", _logPrefix, DateTime.Now, message);
            NinjaTrader.Code.Output.Process(logEntry, PrintTo.OutputTab1);
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
    }
}
