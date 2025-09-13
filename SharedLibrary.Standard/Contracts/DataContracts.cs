using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BookFlow.Shared.Contracts
{
    // Enums and structs shared between NT8 data engine and BookFlowApp (.NET 8)
    public enum MessageCategory : byte { L1Data = 1, L2Data = 2, EventData = 3 }
    public enum EventDataType : byte { PositionUpdate = 1, OrderUpdate = 2, ExecutionUpdate = 3, StateChange = 4, SessionUpdate = 5, BarUpdate = 6, FundamentalData = 7, ConnectionStatusUpdate = 8, AccountItemUpdate = 9, RealtimeError = 10, Heartbeat = 11 }
    public enum NTOrderType : byte { Market = 1, Limit = 2, StopMarket = 3, StopLimit = 4, MIT = 5 }
    public enum NTOrderAction : byte { Buy = 1, Sell = 2, BuyToCover = 3, SellShort = 4 }
    public enum NTTimeInForce : byte { Day = 1, GTC = 2, IOC = 3 }
    public enum NTState : byte { SetDefaults = 0, Configure = 1, Active = 2, DataLoaded = 3, Historical = 4, Transition = 5, Realtime = 6, Terminated = 7, Finalized = 8 }
    public enum NTOrderState : byte { Unknown = 0, Initialized = 1, Submitted = 2, Accepted = 3, Working = 4, PartFilled = 5, Filled = 6, CancelSubmitted = 7, Cancelled = 8, Rejected = 9 }
    public enum NTMarketPosition : byte { Flat = 0, Long = 1, Short = 2 }
    public enum NTConnectionStatus : byte { Connected = 0, Connecting = 1, Disconnected = 2, ConnectionLost = 3, PriceDataError = 4 }
    public enum NTAccountItem : byte { BuyingPower = 0, CashValue = 1, RealizedProfitLoss = 2, UnrealizedProfitLoss = 3, NetLiquidation = 4, InitialMargin = 5, MaintenanceMargin = 6, ExcessEquity = 7, Commission = 8 }
    public enum NTErrorCode : byte { NoError = 0, LogOnFailed = 1, OrderRejected = 2, UnableToChangeOrder = 3, UnableToCancelOrder = 4, InsufficientBuyingPower = 5, MaxPositionExceeded = 6, UnableToSubmitOrder = 7, UserAbort = 8, Cancelled = 9, GeneralError = 10 }
    public enum L1MarketDataType : byte { Ask = 1, Bid = 2, Last = 3, DailyHigh = 4, DailyLow = 5, DailyVolume = 6, Opening = 7, LastClose = 8, Settlement = 9, OpenInterest = 10 }
    public enum L2Operation : byte { Add = 1, Update = 2, Remove = 3 }
    public enum L2MarketSide : byte { Ask = 1, Bid = 2 }
    public enum MessageType : byte { Trade = 1, Bid = 2, Ask = 3, DomSnapshotLevel = 4, InstrumentInfo = 5 }

    [StructLayout(LayoutKind.Explicit, Size = 80)]
    public struct UnifiedMarketDataMessage
    {
        [FieldOffset(0)] public MessageCategory Category;
        [FieldOffset(1)] public byte MarketDataType;
        [FieldOffset(2)] public byte Operation;
        [FieldOffset(3)] public byte TickerId;
        [FieldOffset(8)] public double Price;
        [FieldOffset(16)] public long Volume;
        [FieldOffset(24)] public double AskPrice; // L1
        [FieldOffset(32)] public double BidPrice; // L1
        [FieldOffset(24)] public int Position;    // L2 overlap
        [FieldOffset(40)] public long OriginalTimestamp;
        [FieldOffset(48)] public long NtReceiveTime;
        [FieldOffset(56)] public long IpcQueueTime;
        [FieldOffset(64)] public long Sequence;
        [FieldOffset(72)] public long Reserved1;
    }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    public struct UnifiedEventMessage
    {
        [FieldOffset(0)] public MessageCategory Category;
        [FieldOffset(1)] public EventDataType EventType;
        [FieldOffset(2)] public byte Reserved1;
        [FieldOffset(3)] public byte Reserved2;
        [FieldOffset(4)] public int SequenceNumber;
        [FieldOffset(8)] public long Timestamp;
        [FieldOffset(16)] public double PrimaryValue;

        // 64 bytes string payload
        [FieldOffset(24)] public byte StringData0;  [FieldOffset(25)] public byte StringData1;  [FieldOffset(26)] public byte StringData2;  [FieldOffset(27)] public byte StringData3;
        [FieldOffset(28)] public byte StringData4;  [FieldOffset(29)] public byte StringData5;  [FieldOffset(30)] public byte StringData6;  [FieldOffset(31)] public byte StringData7;
        [FieldOffset(32)] public byte StringData8;  [FieldOffset(33)] public byte StringData9;  [FieldOffset(34)] public byte StringData10; [FieldOffset(35)] public byte StringData11;
        [FieldOffset(36)] public byte StringData12; [FieldOffset(37)] public byte StringData13; [FieldOffset(38)] public byte StringData14; [FieldOffset(39)] public byte StringData15;
        [FieldOffset(40)] public byte StringData16; [FieldOffset(41)] public byte StringData17; [FieldOffset(42)] public byte StringData18; [FieldOffset(43)] public byte StringData19;
        [FieldOffset(44)] public byte StringData20; [FieldOffset(45)] public byte StringData21; [FieldOffset(46)] public byte StringData22; [FieldOffset(47)] public byte StringData23;
        [FieldOffset(48)] public byte StringData24; [FieldOffset(49)] public byte StringData25; [FieldOffset(50)] public byte StringData26; [FieldOffset(51)] public byte StringData27;
        [FieldOffset(52)] public byte StringData28; [FieldOffset(53)] public byte StringData29; [FieldOffset(54)] public byte StringData30; [FieldOffset(55)] public byte StringData31;
        [FieldOffset(56)] public byte StringData32; [FieldOffset(57)] public byte StringData33; [FieldOffset(58)] public byte StringData34; [FieldOffset(59)] public byte StringData35;
        [FieldOffset(60)] public byte StringData36; [FieldOffset(61)] public byte StringData37; [FieldOffset(62)] public byte StringData38; [FieldOffset(63)] public byte StringData39;
        [FieldOffset(64)] public byte StringData40; [FieldOffset(65)] public byte StringData41; [FieldOffset(66)] public byte StringData42; [FieldOffset(67)] public byte StringData43;
        [FieldOffset(68)] public byte StringData44; [FieldOffset(69)] public byte StringData45; [FieldOffset(70)] public byte StringData46; [FieldOffset(71)] public byte StringData47;
        [FieldOffset(72)] public byte StringData48; [FieldOffset(73)] public byte StringData49; [FieldOffset(74)] public byte StringData50; [FieldOffset(75)] public byte StringData51;
        [FieldOffset(76)] public byte StringData52; [FieldOffset(77)] public byte StringData53; [FieldOffset(78)] public byte StringData54; [FieldOffset(79)] public byte StringData55;
        [FieldOffset(80)] public byte StringData56; [FieldOffset(81)] public byte StringData57; [FieldOffset(82)] public byte StringData58; [FieldOffset(83)] public byte StringData59;
        [FieldOffset(84)] public byte StringData60; [FieldOffset(85)] public byte StringData61; [FieldOffset(86)] public byte StringData62; [FieldOffset(87)] public byte StringData63;

        // Overlaid unions for event specific payloads
        [FieldOffset(88)] public double AveragePrice; [FieldOffset(96)] public int Quantity; [FieldOffset(100)] public NTMarketPosition MarketPosition;
        [FieldOffset(88)] public double LimitPrice;   [FieldOffset(96)] public double StopPrice; [FieldOffset(104)] public int OrderQuantity; [FieldOffset(108)] public int FilledQuantity; [FieldOffset(112)] public double AverageFillPrice; [FieldOffset(120)] public NTOrderState OrderState; [FieldOffset(121)] public NTOrderType OrderType; [FieldOffset(122)] public NTOrderAction OrderAction; [FieldOffset(123)] public NTTimeInForce TimeInForce; [FieldOffset(124)] public NTErrorCode ErrorCode;
        [FieldOffset(88)] public double ExecutionPrice; [FieldOffset(96)] public int ExecutionQuantity; [FieldOffset(100)] public NTMarketPosition ExecutionMarketPosition;
        [FieldOffset(88)] public NTState State;
        [FieldOffset(88)] public double Open; [FieldOffset(96)] public double High; [FieldOffset(104)] public double Low; [FieldOffset(112)] public double Close; [FieldOffset(120)] public long BarVolume;
        [FieldOffset(88)] public double FundamentalValue; [FieldOffset(96)] public int FundamentalType;
        [FieldOffset(88)] public NTConnectionStatus ConnectionStatus;
        [FieldOffset(88)] public NTAccountItem AccountItem; [FieldOffset(92)] public double AccountValue;
        [FieldOffset(88)] public NTErrorCode RealtimeErrorCode;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MarketDataMessage { public MessageType MessageType; public double Price; public long Volume; public long Timestamp; }

    public class ControlMessage
    {
        public enum RequestType { RequestDomSnapshot, Disconnect, SubmitOrder, AccountStatusBroadcast, RegisterTicker, GetTickerDictionary, RequestPortfolioState, PortfolioStateUpdate, OrderUpdate, PositionUpdate, AccountUpdate }
        public RequestType Type { get; set; }
        public string InstrumentName { get; set; }
        public OrderCommand OrderCommand { get; set; }
        public AccountStatusMessage AccountStatus { get; set; }
        public string RequestId { get; set; }
        public TickerRegistrationRequest TickerRequest { get; set; }
        public PortfolioStateMessage PortfolioState { get; set; }
        public OrderUpdateMessage OrderUpdate { get; set; }
        public PositionUpdateMessage PositionUpdate { get; set; }
        public AccountUpdateMessage AccountUpdate { get; set; }
    }

    public class OrderCommand
    {
        public enum OrderAction { BuyMarket, SellMarket, BuyLimit, SellLimit, Flat, CancelAll, CancelAtPrice }
        public enum OrderStatus { Pending, Submitted, Filled, Rejected, Cancelled }
        public OrderAction Action { get; set; }
        public int Quantity { get; set; }
        public string ClientOrderId { get; set; }
        public DateTime Timestamp { get; set; }
        public double LimitPrice { get; set; }
    }

    public class OrderStatusMessage
    {
        public string ClientOrderId { get; set; }
        public OrderCommand.OrderStatus Status { get; set; }
        public string Message { get; set; }
        public double FillPrice { get; set; }
        public int FillQuantity { get; set; }
        public DateTime Timestamp { get; set; }
        public string NTOrderId { get; set; }
        public double OrderPrice { get; set; }
        public OrderCommand.OrderAction Action { get; set; }
        public string RequestId { get; set; }
    }

    public class PositionInfo { public string InstrumentName { get; set; } public int Quantity { get; set; } public double AveragePrice { get; set; } public double UnrealizedPnL { get; set; } public DateTime LastUpdateTime { get; set; } }

    public class AccountStatusMessage
    {
        public enum UpdateType { OrderUpdate, PositionUpdate, ExecutionUpdate }
        public UpdateType Type { get; set; }
        public string InstrumentName { get; set; }
        public PositionInfo Position { get; set; }
        public OrderStatusMessage OrderStatus { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class WorkingOrderInfo { public string ClientOrderId { get; set; } public string NTOrderId { get; set; } public OrderCommand.OrderAction Action { get; set; } public int Quantity { get; set; } public double OrderPrice { get; set; } public OrderCommand.OrderStatus Status { get; set; } public DateTime SubmitTime { get; set; } public DateTime LastUpdateTime { get; set; } }

    public class TickerRegistrationRequest { public string InstrumentName { get; set; } public double TickSize { get; set; } public double PointValue { get; set; } }

    public class TickerRegistrationResponse { public byte TickerId { get; set; } public bool Success { get; set; } public string Message { get; set; } public DateTime Timestamp { get; set; } public string RequestId { get; set; } }

    public class TickerDictionaryResponse { public Dictionary<byte, string> TickerDictionary { get; set; } public DateTime Timestamp { get; set; } public string RequestId { get; set; } }

    public class TickerInfo
    {
        public string InstrumentName { get; set; }
        public double TickSize { get; set; }
        public double PointValue { get; set; }
        // Added: carry server-assigned ticker id
        public byte TickerId { get; set; }
        public static TickerInfo Parse(string encoded) { var parts = encoded.Split('|'); if (parts.Length >= 3) { return new TickerInfo { InstrumentName = parts[0], TickSize = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), PointValue = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) }; } return null; }
        // Added: overload that binds id from server dictionary
        public static TickerInfo Parse(byte id, string encoded)
        {
            var ti = Parse(encoded);
            if (ti != null) ti.TickerId = id;
            return ti;
        }
        public string Encode() => $"{InstrumentName}|{TickSize}|{PointValue}";
    }

    public class PortfolioStateMessage
    {
        public AccountStateMessage Account { get; set; }
        public List<WorkingOrderMessage> Orders { get; set; } = new List<WorkingOrderMessage>();
        public List<PositionMessage> Positions { get; set; } = new List<PositionMessage>();
        public DateTime Timestamp { get; set; }
        public string RequestId { get; set; }
    }

    public class AccountStateMessage { public string AccountName { get; set; } public double BuyingPower { get; set; } public double CashValue { get; set; } public double RealizedPnL { get; set; } public double UnrealizedPnL { get; set; } public double NetLiquidation { get; set; } public double InitialMargin { get; set; } public double MaintenanceMargin { get; set; } public double ExcessEquity { get; set; } public double Commission { get; set; } }

    public class WorkingOrderMessage
    {
        public string OrderId { get; set; }
        public string NTOrderId { get; set; }
        public string ClientOrderId { get; set; }
        public string Instrument { get; set; }
        public byte Side { get; set; }
        public byte State { get; set; }
        public byte Type { get; set; }
        public byte TimeInForce { get; set; }
        public double Price { get; set; }
        public int Quantity { get; set; }
        public int FilledQuantity { get; set; }
        public double AverageFillPrice { get; set; }
        public int QueuePosition { get; set; }
        public DateTime SubmitTime { get; set; }
        public DateTime LastUpdateTime { get; set; }
    }

    public class PositionMessage { public string Instrument { get; set; } public int Quantity { get; set; } public double AveragePrice { get; set; } public double UnrealizedPnL { get; set; } public double RealizedPnL { get; set; } public DateTime LastUpdateTime { get; set; } }

    public class OrderUpdateMessage { public string OrderId { get; set; } public string NTOrderId { get; set; } public string Instrument { get; set; } public byte State { get; set; } public int FilledQuantity { get; set; } public double AverageFillPrice { get; set; } public int QueuePosition { get; set; } public DateTime Timestamp { get; set; } public string RequestId { get; set; } }

    public class PositionUpdateMessage { public string Instrument { get; set; } public int Quantity { get; set; } public double AveragePrice { get; set; } public double UnrealizedPnL { get; set; } public double RealizedPnL { get; set; } public DateTime Timestamp { get; set; } public string RequestId { get; set; } }

    public class AccountUpdateMessage { public string AccountName { get; set; } public double BuyingPower { get; set; } public double UnrealizedPnL { get; set; } public double RealizedPnL { get; set; } public double NetLiquidation { get; set; } public DateTime Timestamp { get; set; } public string RequestId { get; set; } }
}
