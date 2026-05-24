using System.Linq;
using BookFlow.Shared.Analytics;
using Xunit;

namespace BookFlow.Tests
{
    public class L2AnalyticsWindowTests
    {
        [Fact]
        public void OnDepth_AccruesAddedAndCanceledFromDeltas()
        {
            var w = new L2AnalyticsWindow(0.25m);
            w.OnDepth(isBid: true, 100.00m, 10); // +10 added
            w.OnDepth(isBid: true, 100.00m, 15); // +5 added
            w.OnDepth(isBid: true, 100.00m, 6);  // -9 canceled
            w.OnDepth(isBid: true, 100.00m, 0);  // -6 canceled (full pull)

            var slot = w.GetVicinity(100.00m, 0).Single();
            Assert.Equal(15, slot.AddedVolume);
            Assert.Equal(15, slot.CanceledVolume);
            Assert.Equal(0, slot.BidSize);
        }

        [Fact]
        public void OnTrade_AccruesTradedVolume()
        {
            var w = new L2AnalyticsWindow(0.25m);
            w.OnTrade(100.00m, 5, hitBid: false); // aggressive buy lifts ask
            w.OnTrade(100.00m, 3, hitBid: true);  // aggressive sell hits bid

            var slot = w.GetVicinity(100.00m, 0).Single();
            Assert.Equal(8, slot.TradedVolume);
            Assert.Equal(5, slot.AskTradedVolume);
            Assert.Equal(3, slot.BidTradedVolume);
        }

        [Fact]
        public void GetVicinity_ReturnsRange_HighToLow()
        {
            var w = new L2AnalyticsWindow(0.25m);
            w.OnDepth(true, 99.75m, 1);
            w.OnDepth(true, 100.00m, 2);
            w.OnDepth(false, 100.25m, 3);

            var v = w.GetVicinity(100.00m, 1);
            Assert.Equal(3, v.Count);
            Assert.True(v[0].Price > v[1].Price && v[1].Price > v[2].Price);
        }

        [Fact]
        public void RingAliasing_ResetsSlotForDistantPrice()
        {
            var w = new L2AnalyticsWindow(0.25m);
            w.OnDepth(true, 100.00m, 10);

            // 512 ticks away (128 points) maps to the same ring index -> slot resets to new price.
            decimal distant = 100.00m + 512 * 0.25m; // 228.00
            w.OnDepth(true, distant, 7);

            Assert.Empty(w.GetVicinity(100.00m, 0));    // original price no longer occupies its slot
            var far = w.GetVicinity(distant, 0);
            Assert.Single(far);
            Assert.Equal(7, far[0].BidSize);
        }
    }
}
