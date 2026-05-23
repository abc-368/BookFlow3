using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace BookFlow.Shared.Service
{
    // Data contracts for the BookFlow WCF service surface. New types live here so
    // the legacy Contracts/DataContracts.cs types can be deleted in Increment 2b
    // without disturbing the wire surface that the host and client will pin against.
    //
    // Serializer: DataContractSerializer (WCF default). Zero codegen, zero third-party
    // dependency, works identically on net48 host and .NET 9 client.

    // ---- Enums kept locally so the contract doesn't bleed in legacy types -------

    [DataContract]
    public enum BookFlowOrderAction : byte
    {
        [EnumMember] BuyMarket = 0,
        [EnumMember] SellMarket = 1,
        [EnumMember] BuyLimit = 2,
        [EnumMember] SellLimit = 3,
        [EnumMember] Flat = 4,
        [EnumMember] CancelAll = 5,
        [EnumMember] CancelAtPrice = 6,
    }

    [DataContract]
    public enum BookFlowOrderStatus : byte
    {
        [EnumMember] Pending = 0,
        [EnumMember] Submitted = 1,
        [EnumMember] Accepted = 2,
        [EnumMember] Working = 3,
        [EnumMember] PartFilled = 4,
        [EnumMember] Filled = 5,
        [EnumMember] CancelSubmitted = 6,
        [EnumMember] Cancelled = 7,
        [EnumMember] Rejected = 8,
    }

    [DataContract]
    public enum BookFlowSide : byte
    {
        [EnumMember] Buy = 1,
        [EnumMember] Sell = 2,
    }

    [DataContract]
    public enum BookFlowMarketPosition : byte
    {
        [EnumMember] Flat = 0,
        [EnumMember] Long = 1,
        [EnumMember] Short = 2,
    }

    [DataContract]
    public enum BookFlowConnectionState : byte
    {
        [EnumMember] Unknown = 0,
        [EnumMember] Connected = 1,
        [EnumMember] Connecting = 2,
        [EnumMember] Disconnected = 3,
        [EnumMember] PriceDataError = 4,
    }

    // ---- Request / response DTOs ------------------------------------------------

    [DataContract]
    public class ClientInfo
    {
        [DataMember] public string ClientId { get; set; }
        [DataMember] public string ClientVersion { get; set; }
        [DataMember] public DateTime ClientUtcTime { get; set; }
    }

    [DataContract]
    public class SessionInfo
    {
        [DataMember] public string SessionToken { get; set; }
        [DataMember] public string ServerVersion { get; set; }
        [DataMember] public DateTime ServerUtcTime { get; set; }
        [DataMember] public long PortfolioVersion { get; set; }
    }

    [DataContract]
    public class Pong
    {
        [DataMember] public long ServerTimestampTicks { get; set; }
        [DataMember] public long DataMessagesSent { get; set; }
        [DataMember] public long DataMessagesDropped { get; set; }
        [DataMember] public double DataRingFillRatio { get; set; }
        [DataMember] public int LiveCallbackCount { get; set; }
    }

    [DataContract]
    public class OrderRequest
    {
        [DataMember] public string ClientOrderId { get; set; }
        [DataMember] public string AccountName { get; set; }        // optional; resolved by server if single account is eligible
        [DataMember] public string InstrumentName { get; set; }
        [DataMember] public BookFlowOrderAction Action { get; set; }
        [DataMember] public int Quantity { get; set; }
        [DataMember] public double LimitPrice { get; set; }
        [DataMember] public DateTime ClientUtcTime { get; set; }
    }

    [DataContract]
    public class OrderAck
    {
        [DataMember] public string ClientOrderId { get; set; }
        [DataMember] public string NtOrderId { get; set; }
        [DataMember] public BookFlowOrderStatus Status { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public DateTime ServerUtcTime { get; set; }
    }

    [DataContract]
    public class OperationResult
    {
        [DataMember] public bool Success { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public int AffectedCount { get; set; }
    }

    [DataContract]
    public class AccountInfo
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public bool IsSimulated { get; set; }
        [DataMember] public bool IsConnected { get; set; }
    }

    [DataContract]
    public class AccountListResponse
    {
        [DataMember] public List<AccountInfo> Accounts { get; set; } = new List<AccountInfo>();
        [DataMember] public string PreferredAccount { get; set; }
    }

    [DataContract]
    public class TickerEntry
    {
        [DataMember] public byte TickerId { get; set; }
        [DataMember] public string InstrumentName { get; set; }
        [DataMember] public double TickSize { get; set; }
        [DataMember] public double PointValue { get; set; }
    }

    [DataContract]
    public class TickerSnapshot
    {
        [DataMember] public List<TickerEntry> Entries { get; set; } = new List<TickerEntry>();
        [DataMember] public DateTime ServerUtcTime { get; set; }
    }

    // ---- Portfolio state --------------------------------------------------------

    [DataContract]
    public class AccountState
    {
        [DataMember] public string AccountName { get; set; }
        [DataMember] public double BuyingPower { get; set; }
        [DataMember] public double CashValue { get; set; }
        [DataMember] public double RealizedPnL { get; set; }
        [DataMember] public double UnrealizedPnL { get; set; }
        [DataMember] public double NetLiquidation { get; set; }
        [DataMember] public double InitialMargin { get; set; }
        [DataMember] public double MaintenanceMargin { get; set; }
        [DataMember] public double ExcessEquity { get; set; }
        [DataMember] public double Commission { get; set; }
    }

    [DataContract]
    public class WorkingOrder
    {
        [DataMember] public string ClientOrderId { get; set; }
        [DataMember] public string NtOrderId { get; set; }
        [DataMember] public string AccountName { get; set; }
        [DataMember] public string InstrumentName { get; set; }
        [DataMember] public BookFlowSide Side { get; set; }
        [DataMember] public BookFlowOrderStatus Status { get; set; }
        [DataMember] public double LimitPrice { get; set; }
        [DataMember] public double StopPrice { get; set; }
        [DataMember] public int Quantity { get; set; }
        [DataMember] public int FilledQuantity { get; set; }
        [DataMember] public double AverageFillPrice { get; set; }
        [DataMember] public DateTime SubmitUtcTime { get; set; }
    }

    [DataContract]
    public class PositionState
    {
        [DataMember] public string AccountName { get; set; }
        [DataMember] public string InstrumentName { get; set; }
        [DataMember] public int SignedQuantity { get; set; }                 // sign carries side
        [DataMember] public BookFlowMarketPosition MarketPosition { get; set; }
        [DataMember] public double AveragePrice { get; set; }
        [DataMember] public double UnrealizedPnL { get; set; }
        [DataMember] public double RealizedPnL { get; set; }
    }

    [DataContract]
    public class PortfolioSnapshot
    {
        [DataMember] public long Version { get; set; }                       // monotonic, reset on host restart
        [DataMember] public DateTime ServerUtcTime { get; set; }
        [DataMember] public List<AccountState> Accounts { get; set; } = new List<AccountState>();
        [DataMember] public List<WorkingOrder> Orders { get; set; } = new List<WorkingOrder>();
        [DataMember] public List<PositionState> Positions { get; set; } = new List<PositionState>();
    }

    // ---- Push notifications (server -> client) ----------------------------------

    [DataContract]
    public class OrderUpdateNotification
    {
        [DataMember] public long Version { get; set; }
        [DataMember] public WorkingOrder Order { get; set; }
    }

    [DataContract]
    public class ExecutionUpdateNotification
    {
        [DataMember] public long Version { get; set; }
        [DataMember] public string ExecutionId { get; set; }
        [DataMember] public string NtOrderId { get; set; }
        [DataMember] public string ClientOrderId { get; set; }
        [DataMember] public string AccountName { get; set; }
        [DataMember] public string InstrumentName { get; set; }
        [DataMember] public BookFlowMarketPosition MarketPosition { get; set; }
        [DataMember] public double Price { get; set; }
        [DataMember] public int Quantity { get; set; }
        [DataMember] public DateTime UtcTime { get; set; }
    }

    [DataContract]
    public class PositionUpdateNotification
    {
        [DataMember] public long Version { get; set; }
        [DataMember] public PositionState Position { get; set; }
    }

    [DataContract]
    public class AccountItemUpdateNotification
    {
        [DataMember] public long Version { get; set; }
        [DataMember] public string AccountName { get; set; }
        [DataMember] public AccountState Account { get; set; }
    }

    [DataContract]
    public class ConnectionStatusNotification
    {
        [DataMember] public BookFlowConnectionState State { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public DateTime UtcTime { get; set; }
    }

    [DataContract]
    public class HeartbeatNotification
    {
        [DataMember] public long Sequence { get; set; }
        [DataMember] public long ServerTimestampTicks { get; set; }
        [DataMember] public long PortfolioVersion { get; set; }
    }

    // ---- L2 depth snapshot (Q4: seed a late-joining client's ladder) ------------

    [DataContract]
    public class DomSnapshotLevel
    {
        [DataMember] public double Price { get; set; }
        [DataMember] public long Volume { get; set; }
    }

    [DataContract]
    public class DomSnapshotResponse
    {
        [DataMember] public byte TickerId { get; set; }
        [DataMember] public string InstrumentName { get; set; }
        // Global data sequence (UnifiedMarketDataMessage.Reserved1) of the last tick
        // applied to this snapshot. The client discards buffered ring ticks with
        // Reserved1 <= this value (already reflected) and applies the rest.
        [DataMember] public long LastSequence { get; set; }
        [DataMember] public List<DomSnapshotLevel> Bids { get; set; } = new List<DomSnapshotLevel>();
        [DataMember] public List<DomSnapshotLevel> Asks { get; set; } = new List<DomSnapshotLevel>();
        [DataMember] public double LastTradePrice { get; set; }
        [DataMember] public DateTime ServerUtcTime { get; set; }
    }
}
