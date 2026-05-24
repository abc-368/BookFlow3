using System.Threading.Tasks;
using BookFlow.App.Engine;
using BookFlow.Shared.Service;
using Xunit;

namespace BookFlow.Tests
{
    public class DomEngineBracketTests
    {
        private static StubDataFeed ConnectedFeed() =>
            new StubDataFeed { CompleteSnapshotImmediately = true, Snapshot = new DomSnapshotResponse { TickerId = 1 } };

        [Fact]
        public async Task SubmitBracketOrder_BuyLimit_BuildsRequest()
        {
            var feed = ConnectedFeed();
            using var engine = new DomEngine("ES", 1, new StubTradingService(), 0.25m, 50m);
            await engine.StartAsync(feed);

            var ack = await engine.SubmitBracketOrderAsync(
                isBuy: true, entryIsLimit: true, entryLimitPrice: 5000.25, quantity: 2, targetTicks: 8, stopTicks: 4);

            Assert.Equal(BookFlowOrderStatus.Submitted, ack.Status);

            var req = feed.LastBracketRequest;
            Assert.NotNull(req);
            Assert.Equal("ES", req!.InstrumentName);
            Assert.Equal(BookFlowSide.Buy, req.Side);
            Assert.True(req.EntryIsLimit);
            Assert.Equal(5000.25, req.EntryLimitPrice);
            Assert.Equal(2, req.Quantity);
            Assert.Equal(8, req.TargetTicks);
            Assert.Equal(4, req.StopTicks);
            Assert.False(string.IsNullOrEmpty(req.ClientOrderId));
        }

        [Fact]
        public async Task SubmitBracketOrder_SellMarket_MapsSideAndType()
        {
            var feed = ConnectedFeed();
            using var engine = new DomEngine("NQ", 1, new StubTradingService(), 0.25m, 20m);
            await engine.StartAsync(feed);

            await engine.SubmitBracketOrderAsync(
                isBuy: false, entryIsLimit: false, entryLimitPrice: 0, quantity: 1, targetTicks: 10, stopTicks: 5);

            var req = feed.LastBracketRequest;
            Assert.NotNull(req);
            Assert.Equal(BookFlowSide.Sell, req!.Side);
            Assert.False(req.EntryIsLimit);
            Assert.Equal(10, req.TargetTicks);
            Assert.Equal(5, req.StopTicks);
        }
    }
}
