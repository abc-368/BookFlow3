using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace BookFlow.Shared.Contracts
{
    /// <summary>
    /// Enum for DOM centering behavior modes.
    /// </summary>
    public enum CenterMode
    {
        None = 0,
        Continuous = 1,
        OneTime = 2
    }

    /// <summary>
    /// Bit flags for price level state information.
    /// Uses a single byte to efficiently store multiple boolean states.
    /// </summary>
    [Flags]
    public enum BookLevelFlags : byte
    {
        None = 0,
        HasBids = 1,
        HasAsks = 2,
        IsTopOfBook = 4,
        HasRecentActivity = 8,
        IsVisible = 16,
        HasOurOrders = 32,
        IsCrossed = 64,
        IsStale = 128
    }

    /// <summary>
    /// Optimized price level structure for high-performance DOM display.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PriceLevel
    {
        // Hot fields
        public decimal Price;            // 16 bytes
        public long BidVolume;           // 8 bytes
        public long AskVolume;           // 8 bytes

        // Order information
        public int BidCount;
        public int AskCount;
        public int MyBidOrderCount;
        public int MyAskOrderCount;
        public long LastTradedVolume;

        // PnL
        public decimal UnrealizedPnL;

        // Volume tracking
        public long TotalTradedVolume;
        public long BidSideTradedVolume;
        public long AskSideTradedVolume;
        public long VolumeProfileVolume;

        // Order book dynamics
        public int AdditionsSinceDepthChange;
        public int CancellationsSinceDepthChange;
        public int CurrentDepth;
        public int QueuePosition;

        // Timestamp
        public long LastUpdateTicks;

        // State flags
        public BookLevelFlags Flags;

        public long TotalVolume => BidVolume + AskVolume;
        public int TotalOrderCount => BidCount + AskCount;
        public bool HasOrders => BidVolume > 0 || AskVolume > 0;
        public bool IsTopOfBook => (Flags & BookLevelFlags.IsTopOfBook) != 0;
        public bool HasRecentActivity => (Flags & BookLevelFlags.HasRecentActivity) != 0;
        public DateTime LastUpdateTime => new DateTime(LastUpdateTicks);

        public void UpdateBid(long volume, int orderCount, BookLevelFlags additionalFlags = BookLevelFlags.None)
        {
            BidVolume = volume;
            BidCount = orderCount;
            LastUpdateTicks = DateTime.UtcNow.Ticks;
            Flags |= BookLevelFlags.HasBids | additionalFlags;
            if (volume == 0) Flags &= ~BookLevelFlags.HasBids;
        }

        public void UpdateAsk(long volume, int orderCount, BookLevelFlags additionalFlags = BookLevelFlags.None)
        {
            AskVolume = volume;
            AskCount = orderCount;
            LastUpdateTicks = DateTime.UtcNow.Ticks;
            Flags |= BookLevelFlags.HasAsks | additionalFlags;
            if (volume == 0) Flags &= ~BookLevelFlags.HasAsks;
        }

        public void RecordTrade(long volume, bool wasBidHit)
        {
            LastTradedVolume = volume;
            TotalTradedVolume += volume;
            if (wasBidHit) BidSideTradedVolume += volume; else AskSideTradedVolume += volume;
            Flags |= BookLevelFlags.HasRecentActivity;
            LastUpdateTicks = DateTime.UtcNow.Ticks;
        }

        public void UpdatePnL(decimal pnlValue) => UnrealizedPnL = pnlValue;
        public void ResetActivityCounters() { AdditionsSinceDepthChange = 0; CancellationsSinceDepthChange = 0; }

        public static PriceLevel CreateEmpty(decimal price)
        {
            return new PriceLevel
            {
                Price = price,
                LastUpdateTicks = DateTime.UtcNow.Ticks,
                Flags = BookLevelFlags.IsVisible
            };
        }

        public override string ToString() => $"Price: {Price:F2}, Bid: {BidVolume}@{BidCount}, Ask: {AskVolume}@{AskCount}, Traded: {TotalTradedVolume}, P&L: {UnrealizedPnL:F2}, Flags: {Flags}";
    }

    /// <summary>
    /// Thread-safe snapshot of the order book state at a specific point in time.
    /// </summary>
    public class BookSnapshot
    {
        private readonly Dictionary<decimal, PriceLevel> _priceLevels;
        private readonly decimal _tickSize;

        public DateTime SnapshotTime { get; }
        public string InstrumentName { get; }
        public byte TickerId { get; }
        public decimal? BestBid { get; }
        public decimal? BestAsk { get; }
        public decimal? LastTradedPrice { get; }
        public long LastTradedVolume { get; }
        public int BidDepthLevels { get; }
        public int AskDepthLevels { get; }
        public long TotalBidVolume { get; }
        public long TotalAskVolume { get; }

        public BookSnapshot(
            Dictionary<decimal, PriceLevel> priceLevels,
            string instrumentName,
            byte tickerId,
            decimal tickSize,
            decimal? bestBid = null,
            decimal? bestAsk = null,
            decimal? lastPrice = null,
            long lastVolume = 0)
        {
            _priceLevels = new Dictionary<decimal, PriceLevel>(priceLevels);
            InstrumentName = instrumentName;
            TickerId = tickerId;
            _tickSize = tickSize;
            BestBid = bestBid;
            BestAsk = bestAsk;
            LastTradedPrice = lastPrice;
            LastTradedVolume = lastVolume;
            SnapshotTime = DateTime.UtcNow;

            var bidLevels = _priceLevels.Values.Where(p => p.BidVolume > 0).ToList();
            var askLevels = _priceLevels.Values.Where(p => p.AskVolume > 0).ToList();
            BidDepthLevels = bidLevels.Count;
            AskDepthLevels = askLevels.Count;
            try
            {
                checked
                {
                    TotalBidVolume = bidLevels.Sum(p => p.BidVolume);
                    TotalAskVolume = askLevels.Sum(p => p.AskVolume);
                }
            }
            catch (OverflowException)
            {
                TotalBidVolume = bidLevels.Aggregate(0L, (s, p) => s > long.MaxValue - p.BidVolume ? long.MaxValue : s + p.BidVolume);
                TotalAskVolume = askLevels.Aggregate(0L, (s, p) => s > long.MaxValue - p.AskVolume ? long.MaxValue : s + p.AskVolume);
            }
        }

        public PriceLevel? GetPriceLevel(decimal price) => _priceLevels.TryGetValue(price, out var lvl) ? lvl : (PriceLevel?)null;
        public IReadOnlyDictionary<decimal, PriceLevel> GetAllPriceLevels() => _priceLevels; // underlying dictionary treated as read-only externally

        public List<PriceLevel> GetVisibleLadder(int levelsAbove, int levelsBelow, CenterMode centerMode, decimal? fixedCenterPrice = null)
        {
            var ladder = new List<PriceLevel>();
            decimal centerPrice;
            switch (centerMode)
            {
                case CenterMode.None:
                    if (fixedCenterPrice.HasValue) centerPrice = fixedCenterPrice.Value;
                    else if (BestBid.HasValue && BestAsk.HasValue) centerPrice = (BestBid.Value + BestAsk.Value) / 2;
                    else if (LastTradedPrice.HasValue) centerPrice = LastTradedPrice.Value;
                    else return ladder;
                    break;
                case CenterMode.Continuous:
                case CenterMode.OneTime:
                    if (BestBid.HasValue && BestAsk.HasValue) centerPrice = (BestBid.Value + BestAsk.Value) / 2;
                    else if (BestBid.HasValue) centerPrice = BestBid.Value;
                    else if (BestAsk.HasValue) centerPrice = BestAsk.Value;
                    else if (LastTradedPrice.HasValue) centerPrice = LastTradedPrice.Value;
                    else return ladder;
                    break;
                default:
                    return ladder;
            }
            centerPrice = Math.Round(centerPrice / _tickSize) * _tickSize;
            for (int i = -levelsBelow; i <= levelsAbove; i++)
            {
                decimal price = centerPrice + (i * _tickSize);
                if (_priceLevels.TryGetValue(price, out var level)) ladder.Add(level); else ladder.Add(PriceLevel.CreateEmpty(price));
            }
            ladder.Sort((a, b) => b.Price.CompareTo(a.Price));
            return ladder;
        }

        public List<PriceLevel> GetTopBids(int count = 10) => _priceLevels.Values.Where(p => p.BidVolume > 0).OrderByDescending(p => p.Price).Take(count).ToList();
        public List<PriceLevel> GetTopAsks(int count = 10) => _priceLevels.Values.Where(p => p.AskVolume > 0).OrderBy(p => p.Price).Take(count).ToList();
        public decimal? GetSpread() => BestBid.HasValue && BestAsk.HasValue ? BestAsk.Value - BestBid.Value : null;
        public decimal? GetMidPrice() => BestBid.HasValue && BestAsk.HasValue ? (BestBid.Value + BestAsk.Value) / 2 : null;

        public double GetOrderImbalance(int levels = 5)
        {
            var bids = GetTopBids(levels);
            var asks = GetTopAsks(levels);
            long bidVol = 0, askVol = 0;
            try
            {
                checked
                {
                    bidVol = bids.Sum(p => p.BidVolume);
                    askVol = asks.Sum(p => p.AskVolume);
                }
            }
            catch
            {
                bidVol = bids.Aggregate(0L, (s, p) => s > long.MaxValue - p.BidVolume ? long.MaxValue : s + p.BidVolume);
                askVol = asks.Aggregate(0L, (s, p) => s > long.MaxValue - p.AskVolume ? long.MaxValue : s + p.AskVolume);
            }
            if (bidVol == 0 && askVol == 0) return 0;
            double total = (double)bidVol + askVol;
            if (total == 0) return 0;
            return ((double)bidVol - askVol) / total;
        }

        public decimal? GetVWAP(bool isBid, int levels = 5)
        {
            var levelsList = isBid ? GetTopBids(levels) : GetTopAsks(levels);
            if (!levelsList.Any()) return null;
            long totalVol = 0;
            decimal sum = 0;
            foreach (var lvl in levelsList)
            {
                long vol = isBid ? lvl.BidVolume : lvl.AskVolume;
                if (vol > 0) { totalVol += vol; sum += lvl.Price * vol; }
            }
            return totalVol > 0 ? sum / totalVol : null;
        }

        public bool IsCrossed() => BestBid.HasValue && BestAsk.HasValue && BestBid.Value >= BestAsk.Value;

        public BookSummary GetSummary() => new BookSummary
        {
            InstrumentName = InstrumentName,
            TickerId = TickerId,
            SnapshotTime = SnapshotTime,
            BestBid = BestBid,
            BestAsk = BestAsk,
            LastPrice = LastTradedPrice,
            LastVolume = LastTradedVolume,
            Spread = GetSpread(),
            MidPrice = GetMidPrice(),
            BidDepth = BidDepthLevels,
            AskDepth = AskDepthLevels,
            TotalBidVolume = TotalBidVolume,
            TotalAskVolume = TotalAskVolume,
            OrderImbalance = GetOrderImbalance(),
            IsCrossed = IsCrossed()
        };

        public override string ToString() => $"{InstrumentName}: Bid={BestBid:F2}@{TotalBidVolume}, Ask={BestAsk:F2}@{TotalAskVolume}, Last={LastTradedPrice:F2}@{LastTradedVolume}, Spread={GetSpread():F2}, Levels={BidDepthLevels}/{AskDepthLevels}";
    }

    public class BookSummary
    {
        public string InstrumentName { get; set; } = string.Empty;
        public byte TickerId { get; set; }
        public DateTime SnapshotTime { get; set; }
        public decimal? BestBid { get; set; }
        public decimal? BestAsk { get; set; }
        public decimal? LastPrice { get; set; }
        public long LastVolume { get; set; }
        public decimal? Spread { get; set; }
        public decimal? MidPrice { get; set; }
        public int BidDepth { get; set; }
        public int AskDepth { get; set; }
        public long TotalBidVolume { get; set; }
        public long TotalAskVolume { get; set; }
        public double OrderImbalance { get; set; }
        public bool IsCrossed { get; set; }
    }

    // Update / snapshot models
    public class LadderUpdate
    {
        public DateTime UpdateTime { get; set; } = DateTime.UtcNow;
        public string InstrumentName { get; set; } = string.Empty;
        public byte TickerId { get; set; }
        public List<PriceLevel> VisibleLevels { get; set; } = new();
        public int BestBidIndex { get; set; } = -1;
        public int BestAskIndex { get; set; } = -1;
        public decimal? BestBid { get; set; }
        public decimal? BestAsk { get; set; }
        public decimal? LastPrice { get; set; }
        public long LastVolume { get; set; }
        public decimal? Spread { get; set; }
        public long SequenceNumber { get; set; }
        public bool ShouldCenter { get; set; }
        public override string ToString() => $"Ladder[{InstrumentName}]: Bid={BestBid:F2}, Ask={BestAsk:F2}, Last={LastPrice:F2}@{LastVolume}, Levels={VisibleLevels.Count}, Seq={SequenceNumber}";
    }

    public class PositionUpdate
    {
        public DateTime UpdateTime { get; set; } = DateTime.UtcNow;
        public string InstrumentName { get; set; } = string.Empty;
        public byte TickerId { get; set; }
        public int Quantity { get; set; }
        public decimal AveragePrice { get; set; }
        public decimal UnrealizedPnL { get; set; }
        public decimal RealizedPnL { get; set; }
        public decimal CurrentPrice { get; set; }
        public decimal MaxProfit { get; set; }
        public decimal MaxDrawdown { get; set; }
        public TimeSpan Duration { get; set; }
        public string PositionDirection => Quantity > 0 ? "Long" : Quantity < 0 ? "Short" : "Flat";
        public int AbsoluteQuantity => Math.Abs(Quantity);
        public decimal TotalPnL => RealizedPnL + UnrealizedPnL;
        public override string ToString() => $"Position[{InstrumentName}]: {PositionDirection} {AbsoluteQuantity} @ {AveragePrice:F2}, P&L: {TotalPnL:F2} (U:{UnrealizedPnL:F2}, R:{RealizedPnL:F2})";
    }

    public class StatisticsUpdate
    {
        public DateTime UpdateTime { get; set; } = DateTime.UtcNow;
        public string InstrumentName { get; set; } = string.Empty;
        public byte TickerId { get; set; }
        public long TotalBidVolume { get; set; }
        public long TotalAskVolume { get; set; }
        public int BidLevels { get; set; }
        public int AskLevels { get; set; }
        public double OrderImbalance { get; set; }
        public decimal? VWABid { get; set; }
        public decimal? VWAAsk { get; set; }
        public long TotalTradedVolume { get; set; }
        public int TradeCount { get; set; }
        public double MessageRate { get; set; }
        public double AverageLatencyMicros { get; set; }
        public override string ToString() => $"Stats[{InstrumentName}]: Bid={TotalBidVolume}@{BidLevels}, Ask={TotalAskVolume}@{AskLevels}, Imbalance={OrderImbalance:F3}, Rate={MessageRate:F1}msg/s, Latency={AverageLatencyMicros:F1}?s";
    }

    public class OrderUpdate
    {
        public DateTime UpdateTime { get; set; } = DateTime.UtcNow;
        public string InstrumentName { get; set; } = string.Empty;
        public byte TickerId { get; set; }
        public string ClientOrderId { get; set; } = string.Empty;
        public string NTOrderId { get; set; } = string.Empty;
        public OrderCommand.OrderStatus Status { get; set; }
        public OrderCommand.OrderAction Action { get; set; }
        public double Price { get; set; }
        public int Quantity { get; set; }
        public int FilledQuantity { get; set; }
        public double AverageFillPrice { get; set; }
        public int QueuePosition { get; set; }
        public string Message { get; set; } = string.Empty;
        public int RemainingQuantity => Quantity - FilledQuantity;
        public bool IsWorking => Status == OrderCommand.OrderStatus.Submitted || Status == OrderCommand.OrderStatus.Pending;
        public override string ToString() => $"Order[{ClientOrderId}]: {Action} {Quantity}@{Price:F2} -> {Status}, Filled={FilledQuantity}@{AverageFillPrice:F2}, Queue={QueuePosition}";
    }

    public class PositionSnapshot
    {
        public DateTime SnapshotTime { get; }
        public string InstrumentName { get; }
        public byte TickerId { get; }
        public int Quantity { get; }
        public decimal AveragePrice { get; }
        public decimal UnrealizedPnL { get; }
        public decimal RealizedPnL { get; }
        public decimal CurrentPrice { get; }
        public decimal MaxProfit { get; }
        public decimal MaxDrawdown { get; }
        public DateTime OpenTime { get; }
        public decimal TotalPnL => RealizedPnL + UnrealizedPnL;
        public TimeSpan Duration => DateTime.UtcNow - OpenTime;
        public bool IsFlat => Quantity == 0;
        public bool IsLong => Quantity > 0;
        public bool IsShort => Quantity < 0;
        public PositionSnapshot(string instrumentName, byte tickerId, int quantity, decimal averagePrice, decimal unrealizedPnL, decimal realizedPnL, decimal currentPrice, decimal maxProfit = 0, decimal maxDrawdown = 0, DateTime openTime = default)
        {
            SnapshotTime = DateTime.UtcNow;
            InstrumentName = instrumentName;
            TickerId = tickerId;
            Quantity = quantity;
            AveragePrice = averagePrice;
            UnrealizedPnL = unrealizedPnL;
            RealizedPnL = realizedPnL;
            CurrentPrice = currentPrice;
            MaxProfit = maxProfit;
            MaxDrawdown = maxDrawdown;
            OpenTime = openTime == default ? SnapshotTime : openTime;
        }
        public override string ToString() => $"Position[{InstrumentName}]: {(IsLong ? "Long" : IsShort ? "Short" : "Flat")} {Math.Abs(Quantity)} @ {AveragePrice:F2}, P&L: {TotalPnL:F2}, Current: {CurrentPrice:F2}";
    }

    public class StatisticsSnapshot
    {
        public DateTime SnapshotTime { get; }
        public string InstrumentName { get; }
        public byte TickerId { get; }
        public long TotalBidVolume { get; }
        public long TotalAskVolume { get; }
        public int BidLevels { get; }
        public int AskLevels { get; }
        public double OrderImbalance { get; }
        public long TotalTradedVolume { get; }
        public int TradeCount { get; }
        public double MessageRate { get; }
        public double AverageLatencyMicros { get; }
        public StatisticsSnapshot(string instrumentName, byte tickerId, long totalBidVolume, long totalAskVolume, int bidLevels, int askLevels, double orderImbalance, long totalTradedVolume = 0, int tradeCount = 0, double messageRate = 0, double averageLatencyMicros = 0)
        {
            SnapshotTime = DateTime.UtcNow;
            InstrumentName = instrumentName;
            TickerId = tickerId;
            TotalBidVolume = totalBidVolume;
            TotalAskVolume = totalAskVolume;
            BidLevels = bidLevels;
            AskLevels = askLevels;
            OrderImbalance = orderImbalance;
            TotalTradedVolume = totalTradedVolume;
            TradeCount = tradeCount;
            MessageRate = messageRate;
            AverageLatencyMicros = averageLatencyMicros;
        }
        public override string ToString() => $"Stats[{InstrumentName}]: Bid={TotalBidVolume}@{BidLevels}, Ask={TotalAskVolume}@{AskLevels}, Traded={TotalTradedVolume}, Rate={MessageRate:F1}msg/s";
    }
}
