using System;
using System.Threading.Tasks;
using BookFlow.Shared.Contracts;
using BookFlow.Shared.IPC;

namespace BookFlow.NT8DataEngine.NinjaTrader
{
    /// <summary>
    /// Manages all communication and data handling for a single instrument.
    /// This class is instantiated for each chart the indicator is applied to.
    /// </summary>
    public class InstrumentDataManager : IDisposable
    {
        private SharedRingBuffer _ringBuffer;
        private ControlPipeServer _controlPipe;
        private readonly string _instrumentName;
        private bool _isInitialized;
        private bool _isDisposed;
        private readonly object _initLock = new object();
        private int _messagesSent;
        private int _messagesDropped;
        private DateTime _lastMessageTime;
        private int _eventSequenceNumber;
        private double _lastPrice = 0;
        
        public bool IsConnected => _isInitialized && _ringBuffer != null;
        public int MessagesSent => _messagesSent;
        public int MessagesDropped => _messagesDropped;
        public DateTime LastMessageTime => _lastMessageTime;
        
        // Delegate for message logging
        public Action<string> MessageLogger { get; set; }
        
        // Delegate for order execution
        public Func<OrderCommand, OrderStatusMessage> OrderExecutor { get; set; }
        
        // Strategy heartbeat interval (injected from strategy)
        public int StrategyHeartbeatIntervalMs { get; set; }
        
        // Helper method to validate price values
        private static bool IsValidPrice(double price)
        {
            return !double.IsNaN(price) && !double.IsInfinity(price) && price != double.MinValue && price != double.MaxValue && price > 0;
        }

        public InstrumentDataManager(string instrumentName, double tickSize, double pointValue)
            : this(instrumentName, tickSize, pointValue, 
                   $"DOMDataRingBuffer_{instrumentName.Replace(' ', '_')}", 
                   $"DOMControlPipe_{instrumentName.Replace(' ', '_')}")
        {
        }

        /// <summary>
        /// Constructor for AddOn scenarios where control pipe is managed externally
        /// </summary>
        public InstrumentDataManager(string instrumentName, double tickSize, double pointValue, 
                                   string sharedMemoryName, bool useExternalControlPipe = false)
        {
            _instrumentName = instrumentName;
            
            try
            {
                _ringBuffer = new SharedRingBuffer(sharedMemoryName, 1024 * 1024); // 1MB buffer
                
                if (!useExternalControlPipe)
                {
                    var controlPipeName = $"DOMControlPipe_{instrumentName.Replace(' ', '_')}";
                    _controlPipe = new ControlPipeServer(controlPipeName);
                    _controlPipe.MessageReceived += OnControlMessageReceived;
                    LogInfo($"Created Control Pipe: {controlPipeName}");
                }
                else
                {
                    LogInfo("Using external control pipe managed by AddOn");
                }

                SendInstrumentInfo(tickSize, pointValue);
                _isInitialized = true;
                
                LogInfo($"InstrumentDataManager initialized for {instrumentName}");
                LogInfo($"Created Shared Memory: {sharedMemoryName}");
                LogInfo("Communication channels ready for client connections");
            }
            catch (Exception ex)
            {
                LogError($"Failed to initialize InstrumentDataManager for {instrumentName}: {ex.Message}");
                Dispose();
                throw;
            }
        }

        public InstrumentDataManager(string instrumentName, double tickSize, double pointValue, 
                                   string sharedMemoryName, string controlPipeName)
        {
            _instrumentName = instrumentName;
            
            try
            {
                _ringBuffer = new SharedRingBuffer(sharedMemoryName, 1024 * 1024); // 1MB buffer
                _controlPipe = new ControlPipeServer(controlPipeName);
                _controlPipe.MessageReceived += OnControlMessageReceived;

                SendInstrumentInfo(tickSize, pointValue);
                _isInitialized = true;
                
                LogInfo($"InstrumentDataManager initialized for {instrumentName}");
                LogInfo($"Created Shared Memory: {sharedMemoryName}");
                LogInfo($"Created Control Pipe: {controlPipeName}");
                LogInfo("Communication channels ready for client connections");
            }
            catch (Exception ex)
            {
                LogError($"Failed to initialize InstrumentDataManager for {instrumentName}: {ex.Message}");
                Dispose();
                throw;
            }
        }
        
        private void LogInfo(string message)
        {
            System.Diagnostics.Trace.WriteLine($"[NT8DataEngine-{_instrumentName}] {message}");
        }
        
        private void LogError(string message)
        {
            System.Diagnostics.Trace.WriteLine($"[NT8DataEngine-{_instrumentName}] ERROR: {message}");
        }

        private Task<object> OnControlMessageReceived(ControlMessage message)
        {
            switch (message.Type)
            {
                case ControlMessage.RequestType.RequestDomSnapshot:
                    return Task.FromResult<object>(null);
                case ControlMessage.RequestType.Disconnect:
                    LogInfo("Client disconnect");
                    return Task.FromResult<object>(null);
                case ControlMessage.RequestType.SubmitOrder:
                    if (message.OrderCommand != null && OrderExecutor != null)
                    {
                        LogInfo("Forwarding order to strategy");
                        var result = OrderExecutor(message.OrderCommand);
                        return Task.FromResult<object>(result);
                    }
                    else
                    {
                        return Task.FromResult<object>(new OrderStatusMessage
                        {
                            ClientOrderId = message.OrderCommand?.ClientOrderId ?? "UNKNOWN",
                            Status = OrderCommand.OrderStatus.Rejected,
                            Message = "Order executor not available",
                            Timestamp = DateTime.Now
                        });
                    }
                default:
                    return Task.FromResult<object>(null);
            }
        }

        // L1 data
        public void OnL1MarketData(L1MarketDataType dataType, double price, long volume, double askPrice, double bidPrice, long time)
        {
            if (!_isInitialized || _ringBuffer == null)
            {
                LogError("OnL1MarketData called but manager not initialized");
                return;
            }
            
            var msg = new UnifiedMarketDataMessage
            {
                Category = MessageCategory.L1Data,
                MarketDataType = (byte)dataType,
                Operation = 0,
                Price = price,
                Volume = volume,
                AskPrice = IsValidPrice(askPrice) ? askPrice : 0.0,
                BidPrice = IsValidPrice(bidPrice) ? bidPrice : 0.0,
                OriginalTimestamp = time
            };

            if (_ringBuffer.TryWrite(ref msg))
            {
                _ringBuffer.SignalDataAvailable();
                _messagesSent++;
                _lastMessageTime = DateTime.Now;
                if (dataType == L1MarketDataType.Last && IsValidPrice(price)) _lastPrice = price;
                if (MessageLogger != null)
                {
                    var askDisplay = IsValidPrice(askPrice) ? $"{askPrice:F2}" : "N/A";
                    var bidDisplay = IsValidPrice(bidPrice) ? $"{bidPrice:F2}" : "N/A";
                    var msgText = $"L1→ Type:{dataType} Price:{price:F2} Ask:{askDisplay} Bid:{bidDisplay} Vol:{volume} Time:{new DateTime(time):HH:mm:ss.fff}";
                    MessageLogger(msgText);
                }
                if (_messagesSent % 1000 == 0)
                    LogInfo($"Status: Sent={_messagesSent}, Dropped={_messagesDropped}, LastMsg={_lastMessageTime:HH:mm:ss.fff}");
            }
            else
            {
                _messagesDropped++;
                LogError($"Ring buffer full - dropped L1 {dataType} @ {price}");
            }
        }

        // L2 data
        public void OnL2MarketDepth(L2MarketSide side, L2Operation operation, double price, long volume, int position, long time)
        {
            if (!_isInitialized || _ringBuffer == null)
            {
                LogError("OnL2MarketDepth called but manager not initialized");
                return;
            }
            
            var msg = new UnifiedMarketDataMessage
            {
                Category = MessageCategory.L2Data,
                MarketDataType = (byte)side,
                Operation = (byte)operation,
                Price = price,
                Volume = volume,
                Position = position,
                OriginalTimestamp = time
            };

            if (_ringBuffer.TryWrite(ref msg))
            {
                _ringBuffer.SignalDataAvailable();
                _messagesSent++;
                _lastMessageTime = DateTime.Now;
                if (MessageLogger != null)
                {
                    var msgText = $"L2→ Side:{side} Op:{operation} Price:{price:F2} Vol:{volume} Pos:{position} Time:{new DateTime(time):HH:mm:ss.fff}";
                    MessageLogger(msgText);
                }
            }
            else
            {
                _messagesDropped++;
                LogError($"Ring buffer full - dropped L2 {operation} @ {price}");
            }
        }

        private void WriteEventMessage(UnifiedEventMessage eventMsg, string debugMessage)
        {
            if (!_isInitialized || _ringBuffer == null)
            {
                LogError($"WriteEventMessage called but manager not initialized for {eventMsg.EventType}");
                return;
            }
            var marketMsg = new UnifiedMarketDataMessage
            {
                Category = MessageCategory.EventData,
                MarketDataType = (byte)eventMsg.EventType,
                Operation = 0,
                Price = eventMsg.PrimaryValue,
                Volume = eventMsg.SequenceNumber,
                OriginalTimestamp = eventMsg.Timestamp
            };
            if (_ringBuffer.TryWrite(ref marketMsg))
            {
                _ringBuffer.SignalDataAvailable();
                _messagesSent++;
                _lastMessageTime = DateTime.Now;
                MessageLogger?.Invoke(debugMessage);
            }
            else
            {
                _messagesDropped++;
                LogError($"Ring buffer full - dropped {eventMsg.EventType} event");
            }
        }

        private void SetStringData(ref UnifiedEventMessage eventMsg, string value, int maxLength = 64)
        {
            if (string.IsNullOrEmpty(value))
            {
                var eventMsgType = typeof(UnifiedEventMessage);
                for (int i = 0; i < 64; i++)
                {
                    var field = eventMsgType.GetField($"StringData{i}");
                    if (field != null) field.SetValueDirect(__makeref(eventMsg), (byte)0);
                }
                return;
            }
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            var length = Math.Min(bytes.Length, maxLength - 1);
            var type = typeof(UnifiedEventMessage);
            for (int i = 0; i < 64; i++)
            {
                var field = type.GetField($"StringData{i}");
                if (field != null)
                {
                    byte byteValue = (i < length) ? bytes[i] : (byte)0;
                    field.SetValueDirect(__makeref(eventMsg), byteValue);
                }
            }
        }

        private void SetAccountName(ref UnifiedEventMessage eventMsg, string accountName)
        {
            if (string.IsNullOrEmpty(accountName)) { eventMsg.Reserved1 = 0; eventMsg.Reserved2 = 0; return; }
            var accountBytes = System.Text.Encoding.UTF8.GetBytes(accountName);
            if (accountBytes.Length > 0) eventMsg.Reserved1 = accountBytes[0];
            if (accountBytes.Length > 1) eventMsg.Reserved2 = accountBytes[1];
        }

        public void OnPositionUpdate(string instrumentName, double averagePrice, int quantity, int marketPosition, string accountName = "")
        {
            var eventMsg = new UnifiedEventMessage
            {
                Category = MessageCategory.EventData,
                EventType = EventDataType.PositionUpdate,
                SequenceNumber = ++_eventSequenceNumber,
                Timestamp = DateTime.Now.Ticks,
                PrimaryValue = averagePrice,
                AveragePrice = averagePrice,
                Quantity = quantity,
                MarketPosition = (NTMarketPosition)marketPosition
            };
            SetStringData(ref eventMsg, instrumentName);
            SetAccountName(ref eventMsg, accountName);
            var accountDisplay = !string.IsNullOrEmpty(accountName) ? $"Account:{accountName} " : "";
            var debugMsg = $"POSITION→ {accountDisplay}{instrumentName} Avg:{averagePrice:F2} Qty:{quantity} Pos:{(NTMarketPosition)marketPosition}";
            WriteEventMessage(eventMsg, debugMsg);
        }

        public void OnOrderUpdate(string orderId, double limitPrice, double stopPrice, int quantity, int filled, 
                                       double averageFillPrice, int orderState, long time, int errorCode, string comment,
                                       int orderType = 1, int orderAction = 1, int timeInForce = 1)
        {
            var eventMsg = new UnifiedEventMessage
            {
                Category = MessageCategory.EventData,
                EventType = EventDataType.OrderUpdate,
                SequenceNumber = ++_eventSequenceNumber,
                Timestamp = time,
                PrimaryValue = limitPrice,
                LimitPrice = limitPrice,
                StopPrice = stopPrice,
                OrderQuantity = quantity,
                FilledQuantity = filled,
                AverageFillPrice = averageFillPrice,
                OrderState = (NTOrderState)orderState,
                OrderType = (NTOrderType)orderType,
                OrderAction = (NTOrderAction)orderAction,
                TimeInForce = (NTTimeInForce)timeInForce,
                ErrorCode = (NTErrorCode)errorCode
            };
            SetStringData(ref eventMsg, orderId);
            var debugMsg = $"ORDER→ ID:{orderId} State:{(NTOrderState)orderState} Qty:{quantity} Filled:{filled} Limit:{limitPrice:F2} AvgFill:{averageFillPrice:F2}";
            WriteEventMessage(eventMsg, debugMsg);
        }

        public void OnExecutionUpdate(string executionId, string orderId, double price, int quantity, 
                                           int marketPosition, long time, string accountName = "")
        {
            var eventMsg = new UnifiedEventMessage
            {
                Category = MessageCategory.EventData,
                EventType = EventDataType.ExecutionUpdate,
                SequenceNumber = ++_eventSequenceNumber,
                Timestamp = time,
                PrimaryValue = price,
                ExecutionPrice = price,
                ExecutionQuantity = quantity,
                ExecutionMarketPosition = (NTMarketPosition)marketPosition
            };
            SetStringData(ref eventMsg, $"{executionId}|{orderId}");
            SetAccountName(ref eventMsg, accountName);
            var accountDisplay = !string.IsNullOrEmpty(accountName) ? $"Account:{accountName} " : "";
            var debugMsg = $"EXECUTION→ {accountDisplay}Exec:{executionId} Order:{orderId} Price:{price:F2} Qty:{quantity} Pos:{(NTMarketPosition)marketPosition}";
            WriteEventMessage(eventMsg, debugMsg);
        }

        public void OnStateChange(int state)
        {
            var eventMsg = new UnifiedEventMessage
            {
                Category = MessageCategory.EventData,
                EventType = EventDataType.StateChange,
                SequenceNumber = ++_eventSequenceNumber,
                Timestamp = DateTime.Now.Ticks,
                PrimaryValue = state,
                State = (NTState)state
            };
            SetStringData(ref eventMsg, _instrumentName);
            var debugMsg = $"STATE→ {_instrumentName} State:{(NTState)state}";
            WriteEventMessage(eventMsg, debugMsg);
        }

        public void OnBarUpdate(double open, double high, double low, double close, long volume, long time)
        {
            var eventMsg = new UnifiedEventMessage
            {
                Category = MessageCategory.EventData,
                EventType = EventDataType.BarUpdate,
                SequenceNumber = ++_eventSequenceNumber,
                Timestamp = time,
                PrimaryValue = close,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                BarVolume = volume
            };
            SetStringData(ref eventMsg, _instrumentName);
            var debugMsg = $"BAR→ {_instrumentName} O:{open:F2} H:{high:F2} L:{low:F2} C:{close:F2} V:{volume}";
            WriteEventMessage(eventMsg, debugMsg);
        }

        public void OnFundamentalData(int fundamentalDataType, double value)
        {
            var eventMsg = new UnifiedEventMessage
            {
                Category = MessageCategory.EventData,
                EventType = EventDataType.FundamentalData,
                SequenceNumber = ++_eventSequenceNumber,
                Timestamp = DateTime.Now.Ticks,
                PrimaryValue = value,
                FundamentalValue = value,
                FundamentalType = fundamentalDataType
            };
            SetStringData(ref eventMsg, _instrumentName);
            var debugMsg = $"FUNDAMENTAL→ {_instrumentName} Type:{fundamentalDataType} Value:{value:F2}";
            WriteEventMessage(eventMsg, debugMsg);
        }

        public void OnConnectionStatusUpdate(int connectionStatus)
        {
            var eventMsg = new UnifiedEventMessage
            {
                Category = MessageCategory.EventData,
                EventType = EventDataType.ConnectionStatusUpdate,
                SequenceNumber = ++_eventSequenceNumber,
                Timestamp = DateTime.Now.Ticks,
                PrimaryValue = connectionStatus,
                ConnectionStatus = (NTConnectionStatus)connectionStatus
            };
            SetStringData(ref eventMsg, _instrumentName);
            var debugMsg = $"CONNECTION→ {_instrumentName} Status:{(NTConnectionStatus)connectionStatus}";
            WriteEventMessage(eventMsg, debugMsg);
        }

        public void OnAccountItemUpdate(string accountName, int accountItem, double value)
        {
            var eventMsg = new UnifiedEventMessage
            {
                Category = MessageCategory.EventData,
                EventType = EventDataType.AccountItemUpdate,
                SequenceNumber = ++_eventSequenceNumber,
                Timestamp = DateTime.Now.Ticks,
                PrimaryValue = value,
                AccountItem = (NTAccountItem)accountItem,
                AccountValue = value
            };
            SetStringData(ref eventMsg, accountName);
            var debugMsg = $"ACCOUNT→ {accountName} Item:{(NTAccountItem)accountItem} Value:{value:F2}";
            WriteEventMessage(eventMsg, debugMsg);
        }

        public void OnRealtimeError(int errorCode, string errorMessage)
        {
            var eventMsg = new UnifiedEventMessage
            {
                Category = MessageCategory.EventData,
                EventType = EventDataType.RealtimeError,
                SequenceNumber = ++_eventSequenceNumber,
                Timestamp = DateTime.Now.Ticks,
                PrimaryValue = errorCode,
                RealtimeErrorCode = (NTErrorCode)errorCode
            };
            SetStringData(ref eventMsg, errorMessage ?? "Unknown Error");
            var debugMsg = $"ERROR→ Code:{(NTErrorCode)errorCode} Message:{errorMessage}";
            WriteEventMessage(eventMsg, debugMsg);
        }

        public void OnSessionUpdate(string sessionInfo, long sessionStartTime, long sessionEndTime)
        {
            var eventMsg = new UnifiedEventMessage
            {
                Category = MessageCategory.EventData,
                EventType = EventDataType.SessionUpdate,
                SequenceNumber = ++_eventSequenceNumber,
                Timestamp = DateTime.Now.Ticks,
                PrimaryValue = sessionStartTime
            };
            SetStringData(ref eventMsg, $"{sessionInfo}|{sessionStartTime}|{sessionEndTime}");
            var debugMsg = $"SESSION→ {sessionInfo} Start:{new DateTime(sessionStartTime):HH:mm:ss} End:{new DateTime(sessionEndTime):HH:mm:ss}";
            WriteEventMessage(eventMsg, debugMsg);
        }

        public void OnHeartbeat(long timestamp)
        {
            var eventMsg = new UnifiedEventMessage
            {
                Category = MessageCategory.EventData,
                EventType = EventDataType.Heartbeat,
                SequenceNumber = ++_eventSequenceNumber,
                Timestamp = timestamp,
                PrimaryValue = timestamp
            };
            SetStringData(ref eventMsg, _instrumentName);
            var debugMsg = $"HEARTBEAT→ {_instrumentName} Time:{new DateTime(timestamp):HH:mm:ss.fff}";
            WriteEventMessage(eventMsg, debugMsg);
        }

        public void OnMarketData(double price, long volume, MessageType type, long time)
        {
            L1MarketDataType dataType = type switch
            {
                MessageType.Ask => L1MarketDataType.Ask,
                MessageType.Bid => L1MarketDataType.Bid,
                MessageType.Trade => L1MarketDataType.Last,
                _ => L1MarketDataType.Last
            };
            OnL1MarketData(dataType, price, volume, price, price, time);
        }

        private void SendInstrumentInfo(double tickSize, double pointValue)
        {
            _tickSize = tickSize;
            _pointValue = pointValue;
        }
        
        private double _tickSize;
        private double _pointValue;

        public bool RecreateRingBuffer(string newSharedMemoryName)
        {
            lock (_initLock)
            {
                try
                {
                    LogInfo($"Recreating ring buffer for {_instrumentName}");
                    if (_ringBuffer != null)
                    {
                        _ringBuffer.Dispose();
                        _ringBuffer = null;
                    }
                    _ringBuffer = new SharedRingBuffer(newSharedMemoryName, 1024 * 1024);
                    _messagesSent = 0;
                    _messagesDropped = 0;
                    _lastMessageTime = DateTime.Now;
                    LogInfo("Ring buffer recreation complete");
                    return true;
                }
                catch (Exception ex)
                {
                    LogError($"Failed to recreate ring buffer: {ex.Message}");
                    return false;
                }
            }
        }

        public void Dispose()
        {
            lock (_initLock)
            {
                if (_isDisposed) return;
                try
                {
                    _isDisposed = true;
                    if (_controlPipe != null)
                    {
                        try { _controlPipe.MessageReceived -= OnControlMessageReceived; _controlPipe.Dispose(); } catch { }
                        _controlPipe = null;
                    }
                    if (_ringBuffer != null)
                    {
                        try { _ringBuffer.Dispose(); } catch { }
                        _ringBuffer = null;
                    }
                    _isInitialized = false;
                }
                catch (Exception ex)
                {
                    LogError($"Dispose error: {ex.Message}");
                }
            }
        }
    }
}