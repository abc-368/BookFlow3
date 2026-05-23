using System.Collections.Generic;
using BookFlow.App.Models;
using BookFlow.Shared.Contracts;
using Xunit;

namespace BookFlow.Tests
{
    public class DomRowDataTests
    {
        [Fact]
        public void UpdateFromPriceLevel_RaisesSingleBatchedNotification()
        {
            var row = new DomRowData(100m);
            var names = new List<string?>();
            row.PropertyChanged += (s, e) => names.Add(e.PropertyName);

            var level = PriceLevel.CreateEmpty(100m);
            level.UpdateBid(10, 2);
            level.UpdateAsk(20, 3);
            row.UpdateFromPriceLevel(level);

            // Batching collapses ~20 per-property notifications into one "all properties" event.
            Assert.Single(names);
            Assert.True(string.IsNullOrEmpty(names[0]));
        }

        [Fact]
        public void UpdateFromPriceLevel_AppliesValues()
        {
            var row = new DomRowData(100m);
            var level = PriceLevel.CreateEmpty(100m);
            level.UpdateBid(10, 2);
            level.UpdateAsk(20, 3);
            row.UpdateFromPriceLevel(level);

            Assert.Equal(10, row.BidVolume);
            Assert.Equal(20, row.AskVolume);
            Assert.Equal(2, row.BidCount);
            Assert.Equal(3, row.AskCount);
        }

        [Fact]
        public void IndividualSetter_StillRaisesImmediately()
        {
            var row = new DomRowData(100m);
            int count = 0;
            row.PropertyChanged += (s, e) => count++;
            row.BidVolume = 5;
            Assert.True(count >= 1); // BidVolume (+ HasBids)
        }

        [Fact]
        public void DisplayVolume_IsSuppressedOutsideItsZone()
        {
            var row = new DomRowData(100m);
            var lvl = PriceLevel.CreateEmpty(100m);
            lvl.UpdateBid(10, 1);
            lvl.UpdateAsk(20, 1);
            row.UpdateFromPriceLevel(lvl);

            // Outside both zones: nothing renders (Q3 stale-data suppression).
            row.IsBidZone = false; row.IsAskZone = false;
            Assert.Equal(0, row.DisplayBidDepth);
            Assert.Equal(0, row.DisplayAskDepth);

            // Bid zone: bid renders, ask stays suppressed.
            row.IsBidZone = true;
            Assert.Equal(10, row.DisplayBidDepth);
            Assert.Equal(0, row.DisplayAskDepth);

            // Ask zone: ask renders.
            row.IsAskZone = true;
            Assert.Equal(20, row.DisplayAskDepth);
        }

        [Fact]
        public void Profiles_AreCumulative()
        {
            var row = new DomRowData(100m);
            var l1 = PriceLevel.CreateEmpty(100m);
            l1.RecordTrade(5, wasBidHit: false); // ask side traded 5
            row.UpdateFromPriceLevel(l1);
            var firstAsk = row.AskProfile;

            var l2 = PriceLevel.CreateEmpty(100m);
            l2.RecordTrade(8, wasBidHit: false); // cumulative ask side 8 (>5)
            row.UpdateFromPriceLevel(l2);

            Assert.True(row.AskProfile >= firstAsk);
        }
    }
}
