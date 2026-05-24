using System;
using System.Collections.Generic;

namespace BookFlow.Shared.Analytics
{
    /// <summary>
    /// One price level's microstructure accumulators (Q5). A value type so the window is a
    /// flat, cache-resident array with no per-level heap objects.
    /// </summary>
    public struct L2AnalyticsSlot
    {
        public long PriceTicks;        // tick index this slot currently represents (ring tag)
        public decimal Price;
        public long BidSize;           // current resting bid depth
        public long AskSize;           // current resting ask depth
        public long AddedVolume;       // cumulative size added at this level (within window lifetime)
        public long CanceledVolume;    // cumulative size withdrawn (pulled limit orders)
        public long TradedVolume;      // aggressive trade volume executed here (both sides)
        public long BidTradedVolume;   // aggressive sells that hit the bid here
        public long AskTradedVolume;   // aggressive buys that lifted the ask here
        public long LastUpdateTicks;
    }

    /// <summary>
    /// Fixed-size sliding window of per-level L2 analytics centered on the market (Q5,
    /// per CLAUDE-VERIFICATION §Q5). 512 tick-slots (~128 points at a 0.25 tick) addressed by
    /// <c>priceTicks % 512</c> — a ring keyed by tick index, so the window follows the market
    /// with O(1) updates and no recenter/memmove. A slot reused for a price ≥512 ticks away
    /// resets itself (that old price is far outside the vicinity of interest).
    ///
    /// Purpose: relate top-of-book movement to add/cancel bursts in nearby limit orders
    /// (spoofing/iceberg/liquidity-withdrawal inference). All ~40 KB fits in L1/L2 cache.
    ///
    /// NOT internally synchronized: the owning engine serializes access under its book lock.
    /// </summary>
    public sealed class L2AnalyticsWindow
    {
        public const int WindowSize = 512;
        private readonly L2AnalyticsSlot[] _slots = new L2AnalyticsSlot[WindowSize];
        private readonly decimal _tickSize;

        public L2AnalyticsWindow(decimal tickSize)
        {
            _tickSize = tickSize > 0 ? tickSize : 0.25m;
        }

        private long ToTicks(decimal price) => (long)Math.Round(price / _tickSize, MidpointRounding.AwayFromZero);

        private static int IndexOf(long priceTicks) => (int)(((priceTicks % WindowSize) + WindowSize) % WindowSize);

        private ref L2AnalyticsSlot SlotFor(long priceTicks, decimal price)
        {
            ref var slot = ref _slots[IndexOf(priceTicks)];
            if (slot.PriceTicks != priceTicks)
            {
                slot = default;            // a different price now occupies this ring index
                slot.PriceTicks = priceTicks;
                slot.Price = price;
            }
            return ref slot;
        }

        /// <summary>Records the current resting depth at a level and accrues the add/cancel delta.</summary>
        public void OnDepth(bool isBid, decimal price, long newSize)
        {
            if (newSize < 0) newSize = 0;
            ref var s = ref SlotFor(ToTicks(price), price);
            long prev = isBid ? s.BidSize : s.AskSize;
            long delta = newSize - prev;
            if (delta > 0) s.AddedVolume += delta;
            else if (delta < 0) s.CanceledVolume += -delta;
            if (isBid) s.BidSize = newSize; else s.AskSize = newSize;
            s.LastUpdateTicks = DateTime.UtcNow.Ticks;
        }

        /// <summary>Records aggressive trade volume executed at a level.
        /// <paramref name="hitBid"/> = an aggressive sell hit the bid; otherwise a buy lifted the ask.</summary>
        public void OnTrade(decimal price, long volume, bool hitBid)
        {
            if (volume <= 0) return;
            ref var s = ref SlotFor(ToTicks(price), price);
            s.TradedVolume += volume;
            if (hitBid) s.BidTradedVolume += volume; else s.AskTradedVolume += volume;
            s.LastUpdateTicks = DateTime.UtcNow.Ticks;
        }

        /// <summary>
        /// Returns the live slots within ±<paramref name="radiusTicks"/> of the center price,
        /// ordered high → low price. Only slots currently representing those prices are returned.
        /// </summary>
        public List<L2AnalyticsSlot> GetVicinity(decimal centerPrice, int radiusTicks)
        {
            if (radiusTicks < 0) radiusTicks = 0;
            if (radiusTicks > WindowSize / 2) radiusTicks = WindowSize / 2;
            long center = ToTicks(centerPrice);
            var result = new List<L2AnalyticsSlot>(radiusTicks * 2 + 1);
            for (long t = center + radiusTicks; t >= center - radiusTicks; t--)
            {
                var slot = _slots[IndexOf(t)];
                if (slot.PriceTicks == t) result.Add(slot);
            }
            return result;
        }

        public void Clear() => Array.Clear(_slots, 0, _slots.Length);
    }
}
