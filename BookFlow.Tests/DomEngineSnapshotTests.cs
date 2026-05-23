using System.Threading.Tasks;
using BookFlow.App.Engine;
using BookFlow.Shared.Contracts;
using BookFlow.Shared.Service;
using Xunit;

namespace BookFlow.Tests
{
    public class DomEngineSnapshotTests
    {
        private static UnifiedMarketDataMessage L2(L2MarketSide side, L2Operation op, double price, long vol, long seq) =>
            new UnifiedMarketDataMessage
            {
                Category = MessageCategory.L2Data,
                MarketDataType = (byte)side,
                Operation = (byte)op,
                Price = price,
                Volume = vol,
                TickerId = 1,
                Reserved1 = seq,
            };

        [Fact]
        public async Task StartAsync_SeedsLadderFromSnapshot()
        {
            var feed = new StubDataFeed
            {
                CompleteSnapshotImmediately = true,
                Snapshot = new DomSnapshotResponse
                {
                    TickerId = 1,
                    LastSequence = 5,
                    Bids = { new DomSnapshotLevel { Price = 100.00, Volume = 10 } },
                    Asks = { new DomSnapshotLevel { Price = 100.25, Volume = 20 } },
                },
            };

            using var engine = new DomEngine("ES", 1, new StubTradingService(), 0.25m, 50m);
            await engine.StartAsync(feed);

            var snap = engine.GetBookSnapshot();
            Assert.Equal(10, snap.GetPriceLevel(100.00m)?.BidVolume);
            Assert.Equal(20, snap.GetPriceLevel(100.25m)?.AskVolume);
        }

        [Fact]
        public async Task StartAsync_DedupsBufferedTicksAgainstSnapshotSequence()
        {
            var feed = new StubDataFeed
            {
                Snapshot = new DomSnapshotResponse
                {
                    TickerId = 1,
                    LastSequence = 5,
                    Bids = { new DomSnapshotLevel { Price = 100.00, Volume = 10 } },
                },
            };

            using var engine = new DomEngine("ES", 1, new StubTradingService(), 0.25m, 50m);

            // StartAsync parks awaiting the (gated) snapshot, with the subscription already buffering.
            var start = engine.StartAsync(feed);

            // Inject during the seeding window: one tick already in the snapshot, one newer.
            feed.Push(L2(L2MarketSide.Bid, L2Operation.Update, 100.00, 99, seq: 3)); // stale (<= 5) -> ignore
            feed.Push(L2(L2MarketSide.Bid, L2Operation.Add, 99.75, 30, seq: 7));     // new (> 5) -> apply

            feed.CompleteSnapshot();
            await start;

            var snap = engine.GetBookSnapshot();
            // Stale update ignored: bid@100 keeps the snapshot value (10), not 99.
            Assert.Equal(10, snap.GetPriceLevel(100.00m)?.BidVolume);
            // Newer tick applied on top of the snapshot.
            Assert.Equal(30, snap.GetPriceLevel(99.75m)?.BidVolume);
        }
    }
}
