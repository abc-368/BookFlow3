using System.Collections.Generic;
using System.Linq;
using BookFlow.Shared.Analytics;
using Xunit;

namespace BookFlow.Tests
{
    public class MicrostructureDetectorTests
    {
        private const decimal Tick = 0.25m;
        private const decimal BestBid = 100.00m;
        private const decimal BestAsk = 100.25m;

        private static long Ticks(decimal price) => (long)(price / Tick);

        private static L2AnalyticsSlot Slot(decimal price, long bidSize, long askSize,
            long added, long canceled, long traded, long bidTraded, long askTraded) =>
            new L2AnalyticsSlot
            {
                PriceTicks = Ticks(price), Price = price,
                BidSize = bidSize, AskSize = askSize,
                AddedVolume = added, CanceledVolume = canceled,
                TradedVolume = traded, BidTradedVolume = bidTraded, AskTradedVolume = askTraded,
            };

        private static List<MicrostructureSignal> Run(L2AnalyticsSlot prev, L2AnalyticsSlot curr)
        {
            var det = new MicrostructureDetector();
            return det.Detect(new[] { prev }, new[] { curr }, BestBid, BestAsk, Tick, 0);
        }

        [Fact]
        public void Spoofing_HeavyChurnNoTrades_Flagged()
        {
            // A bid level 2 ticks back: added 100 then canceled 100, nothing traded.
            var prev = Slot(99.50m, bidSize: 0, askSize: 0, added: 0, canceled: 0, traded: 0, 0, 0);
            var curr = Slot(99.50m, bidSize: 0, askSize: 0, added: 100, canceled: 100, traded: 0, 0, 0);
            var s = Run(prev, curr);
            Assert.Contains(s, x => x.Type == MicrostructureSignalType.Spoofing && x.Side == MicrostructureSide.Bid
                                    && x.Price == 99.50m && x.Bias == MicrostructureBias.Down);
        }

        [Fact]
        public void Spoofing_NotFlagged_WhenTraded()
        {
            // Same churn but it mostly traded -> genuine liquidity, not a spoof.
            var prev = Slot(99.50m, 0, 0, 0, 0, 0, 0, 0);
            var curr = Slot(99.50m, 0, 0, 100, 100, 90, 90, 0);
            var s = Run(prev, curr);
            Assert.DoesNotContain(s, x => x.Type == MicrostructureSignalType.Spoofing);
        }

        [Fact]
        public void Iceberg_TradedMoreThanDisplayedDrop_Flagged()
        {
            // Ask at touch shows 5, then 5 again, but 50 traded through it -> replenished/hidden.
            var prev = Slot(100.25m, bidSize: 0, askSize: 5, added: 0, canceled: 0, traded: 0, 0, 0);
            var curr = Slot(100.25m, bidSize: 0, askSize: 5, added: 0, canceled: 0, traded: 50, 0, 50);
            var s = Run(prev, curr);
            Assert.Contains(s, x => x.Type == MicrostructureSignalType.Iceberg && x.Side == MicrostructureSide.Ask
                                    && x.Price == 100.25m && x.Bias == MicrostructureBias.Down);
        }

        [Fact]
        public void LiquidityWithdrawal_OneSidedPullNearTouch_Flagged()
        {
            // Bid one tick back: 80 canceled, none traded -> pulled liquidity.
            var prev = Slot(99.75m, bidSize: 80, askSize: 0, added: 0, canceled: 0, traded: 0, 0, 0);
            var curr = Slot(99.75m, bidSize: 0, askSize: 0, added: 0, canceled: 80, traded: 0, 0, 0);
            var s = Run(prev, curr);
            Assert.Contains(s, x => x.Type == MicrostructureSignalType.LiquidityWithdrawal && x.Side == MicrostructureSide.Bid
                                    && x.Bias == MicrostructureBias.Down && x.Price == BestBid);
        }

        [Fact]
        public void AggressionImbalance_BuyDominant_Flagged()
        {
            // Strong aggressive buying at the ask, little selling.
            var prev = Slot(100.25m, 0, 50, 0, 0, 0, 0, 0);
            var curr = Slot(100.25m, 0, 50, 0, 0, 60, 0, 60);
            var det = new MicrostructureDetector();
            var s = det.Detect(new[] { prev }, new[] { curr }, BestBid, BestAsk, Tick, 0);
            Assert.Contains(s, x => x.Type == MicrostructureSignalType.AggressionImbalance && x.Side == MicrostructureSide.Ask
                                    && x.Bias == MicrostructureBias.Up);
        }

        [Fact]
        public void Thresholds_AreConfigurable_RaisedSpoofChurnSuppresses()
        {
            var prev = Slot(99.50m, 0, 0, 0, 0, 0, 0, 0);
            var curr = Slot(99.50m, 0, 0, 100, 100, 0, 0, 0); // churn 200, normally flagged
            var det = new MicrostructureDetector { SpoofMinChurn = 500 }; // raised above the churn
            var s = det.Detect(new[] { prev }, new[] { curr }, BestBid, BestAsk, Tick, 0);
            Assert.DoesNotContain(s, x => x.Type == MicrostructureSignalType.Spoofing);
        }

        [Fact]
        public void DomSettings_DefaultsMatchDetectorDefaults()
        {
            var settings = new BookFlow.Shared.Contracts.DomSettings();
            var det = new MicrostructureDetector();
            Assert.Equal(det.SpoofMinChurn, settings.SpoofMinChurn);
            Assert.Equal(det.IcebergMinRefill, settings.IcebergMinRefill);
            Assert.Equal(det.WithdrawalMinSize, settings.WithdrawalMinSize);
            Assert.Equal(det.AggressionMinVolume, settings.AggressionMinVolume);
            Assert.True(settings.EnableMicrostructureSignals);
        }

        [Fact]
        public void ResetSlot_NegativeDelta_Ignored()
        {
            // curr counters lower than prev (ring reset) -> no false signals.
            var prev = Slot(99.50m, 0, 0, 1000, 1000, 0, 0, 0);
            var curr = Slot(99.50m, 0, 0, 5, 5, 0, 0, 0);
            var s = Run(prev, curr);
            Assert.Empty(s);
        }

        [Fact]
        public void Ofi_BidAddsAndAskCancels_FlagsBuyPressure()
        {
            // Near touch: +300 bid added, 200 ask canceled => strongly positive OFI => UP.
            var prev = new[]
            {
                Slot(100.00m, bidSize: 100, askSize: 0, added: 0, canceled: 0, traded: 0, 0, 0),
                Slot(100.25m, bidSize: 0, askSize: 100, added: 0, canceled: 0, traded: 0, 0, 0),
            };
            var curr = new[]
            {
                Slot(100.00m, bidSize: 400, askSize: 0, added: 300, canceled: 0, traded: 0, 0, 0),
                Slot(100.25m, bidSize: 0, askSize: 0, added: 0, canceled: 200, traded: 0, 0, 0),
            };
            var det = new MicrostructureDetector();
            var s = det.Detect(prev, curr, BestBid, BestAsk, Tick, 0);
            Assert.Contains(s, x => x.Type == MicrostructureSignalType.OrderFlowImbalance && x.Bias == MicrostructureBias.Up);
        }

        [Fact]
        public void BookImbalance_BidHeavyTouch_FlagsUp()
        {
            // Resting touch sizes: 150 bid vs 30 ask => ratio 0.67 => UP. No deltas, so OFI/others quiet.
            var prev = new[]
            {
                Slot(100.00m, bidSize: 150, askSize: 0, added: 0, canceled: 0, traded: 0, 0, 0),
                Slot(100.25m, bidSize: 0, askSize: 30, added: 0, canceled: 0, traded: 0, 0, 0),
            };
            var curr = new[]
            {
                Slot(100.00m, bidSize: 150, askSize: 0, added: 0, canceled: 0, traded: 0, 0, 0),
                Slot(100.25m, bidSize: 0, askSize: 30, added: 0, canceled: 0, traded: 0, 0, 0),
            };
            var det = new MicrostructureDetector();
            var s = det.Detect(prev, curr, BestBid, BestAsk, Tick, 0);
            Assert.Contains(s, x => x.Type == MicrostructureSignalType.BookImbalance && x.Bias == MicrostructureBias.Up);
        }
    }
}
