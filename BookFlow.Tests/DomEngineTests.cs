using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookFlow.App.Engine;
using BookFlow.Shared.Contracts;
using BookFlow.Shared.Analytics;
using Xunit;

namespace BookFlow.Tests
{
    public class DomEngineTests
    {
        private static UnifiedMarketDataMessage L2(L2MarketSide side, L2Operation op, double price, long vol) =>
            new UnifiedMarketDataMessage { Category = MessageCategory.L2Data, MarketDataType = (byte)side, Operation = (byte)op, Price = price, Volume = vol, TickerId = 1 };

        private static UnifiedMarketDataMessage L1Last(double price, long vol) =>
            new UnifiedMarketDataMessage { Category = MessageCategory.L1Data, MarketDataType = (byte)L1MarketDataType.Last, Price = price, Volume = vol, TickerId = 1 };

        [Fact]
        public void L2_AddsLevels_AndComputesBestPrices()
        {
            using var engine = new DomEngine("ES", 1, new StubTradingService(), 0.25m, 50m);
            engine.ProcessMessage(L2(L2MarketSide.Bid, L2Operation.Add, 100.00, 10));
            engine.ProcessMessage(L2(L2MarketSide.Ask, L2Operation.Add, 100.25, 20));
            engine.ForceUpdate();

            var (bb, ba) = engine.GetBestBidAsk();
            Assert.Equal(100.00, bb);
            Assert.Equal(100.25, ba);

            var snap = engine.GetBookSnapshot();
            Assert.Equal(10, snap.GetPriceLevel(100.00m)?.BidVolume);
            Assert.Equal(20, snap.GetPriceLevel(100.25m)?.AskVolume);
        }

        [Fact]
        public void L2_RemoveZeroVolume_RemovesLevel()
        {
            using var engine = new DomEngine("ES", 1, new StubTradingService(), 0.25m, 50m);
            engine.ProcessMessage(L2(L2MarketSide.Bid, L2Operation.Add, 100.00, 10));
            engine.ProcessMessage(L2(L2MarketSide.Bid, L2Operation.Remove, 100.00, 0));
            engine.ForceUpdate();

            var snap = engine.GetBookSnapshot();
            var lvl = snap.GetPriceLevel(100.00m);
            Assert.True(lvl == null || lvl.Value.BidVolume == 0);
        }

        [Fact]
        public void Trade_OnGrid_AttributesTradedVolumeToLevel()
        {
            using var engine = new DomEngine("ES", 1, new StubTradingService(), 0.25m, 50m);
            engine.ProcessMessage(L2(L2MarketSide.Bid, L2Operation.Add, 100.00, 10));
            engine.ProcessMessage(L1Last(100.00, 5)); // trade at the bid -> bid-side traded volume
            engine.ForceUpdate();

            var lvl = engine.GetBookSnapshot().GetPriceLevel(100.00m);
            Assert.NotNull(lvl);
            Assert.Equal(5, lvl!.Value.BidSideTradedVolume);
        }

        [Fact]
        public void Trade_AtEmptyAlignedPrice_DoesNotThrowOrAttribute()
        {
            using var engine = new DomEngine("ES", 1, new StubTradingService(), 0.25m, 50m);
            engine.ProcessMessage(L2(L2MarketSide.Bid, L2Operation.Add, 100.00, 10));
            engine.ProcessMessage(L2(L2MarketSide.Ask, L2Operation.Add, 100.50, 20));
            // Trade two ticks away from any resting level (off-grid relative to depth).
            engine.ProcessMessage(L1Last(100.25, 5));
            engine.ForceUpdate();

            // No crash; resting depth untouched.
            var snap = engine.GetBookSnapshot();
            Assert.Equal(10, snap.GetPriceLevel(100.00m)?.BidVolume);
            Assert.Equal(20, snap.GetPriceLevel(100.50m)?.AskVolume);
        }

        [Fact]
        public void Trade_HittingBid_PublishesLastBidHitPrice()
        {
            using var engine = new DomEngine("ES", 1, new StubTradingService(), 0.25m, 50m);
            engine.ProcessMessage(L2(L2MarketSide.Bid, L2Operation.Add, 100.00, 10));
            engine.ProcessMessage(L2(L2MarketSide.Ask, L2Operation.Add, 100.25, 20));

            LadderUpdate? captured = null;
            using var gate = new ManualResetEventSlim();
            using var sub = engine.LadderUpdates.Subscribe(u =>
            {
                if (u.LastBidHitPrice.HasValue) { captured = u; gate.Set(); }
            });

            engine.ProcessMessage(L1Last(100.00, 5)); // market sell hits the bid
            engine.ForceUpdate();

            Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "No ladder update with LastBidHitPrice arrived");
            Assert.Equal(100.00m, captured!.LastBidHitPrice);
        }

        [Fact]
        public void Analytics_TrackAddCancelAndTradeAroundMarket()
        {
            using var engine = new DomEngine("ES", 1, new StubTradingService(), 0.25m, 50m);
            engine.ProcessMessage(L2(L2MarketSide.Bid, L2Operation.Add, 100.00, 10));
            engine.ProcessMessage(L2(L2MarketSide.Ask, L2Operation.Add, 100.25, 20));
            engine.ProcessMessage(L2(L2MarketSide.Bid, L2Operation.Update, 100.00, 4)); // 6 canceled
            engine.ProcessMessage(L1Last(100.25, 3));                                    // trade lifts ask

            var vicinity = engine.GetVicinityAnalytics(4);
            var bid = vicinity.First(s => s.Price == 100.00m);
            var ask = vicinity.First(s => s.Price == 100.25m);

            Assert.Equal(10, bid.AddedVolume);
            Assert.Equal(6, bid.CanceledVolume);
            Assert.Equal(20, ask.AddedVolume);
            Assert.Equal(3, ask.TradedVolume);
        }

        [Fact]
        public void LockedMarket_TradeDoesNotClobberSideVolumes()
        {
            // Regression (audit P0): UpdateLastTrade merged both books and wrote the merged
            // level back to both, zeroing the bid volume on a locked market.
            using var engine = new DomEngine("ES", 1, new StubTradingService(), 0.25m, 50m);
            engine.ProcessMessage(L2(L2MarketSide.Bid, L2Operation.Add, 100.00, 10));
            engine.ProcessMessage(L2(L2MarketSide.Ask, L2Operation.Add, 100.00, 20)); // locked
            engine.ProcessMessage(L1Last(100.00, 5));                                  // trade at locked price
            engine.ForceUpdate();

            var snap = engine.GetBookSnapshot();
            Assert.Equal(10, snap.GetPriceLevel(100.00m)?.BidVolume);
            Assert.Equal(20, snap.GetPriceLevel(100.00m)?.AskVolume);
        }
    }
}
