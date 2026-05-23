using BookFlow.App.Services;
using BookFlow.Shared.Contracts;
using BookFlow.Shared.Service;
using Xunit;

namespace BookFlow.Tests
{
    public class WcfContractMapperTests
    {
        [Fact]
        public void ToWorkingOrderMessage_MapsSideAndState()
        {
            var wo = new WorkingOrder
            {
                NtOrderId = "X1", InstrumentName = "ES", Side = BookFlowSide.Sell,
                Status = BookFlowOrderStatus.Working, LimitPrice = 5000.25, Quantity = 3,
                FilledQuantity = 1, AverageFillPrice = 5000.0
            };
            var m = WcfContractMapper.ToWorkingOrderMessage(wo);
            Assert.Equal((byte)2, m.Side);   // Sell == 2
            Assert.Equal((byte)4, m.State);  // Working maps to NT OrderState byte 4
            Assert.Equal("ES", m.Instrument);
            Assert.Equal(5000.25, m.Price);
            Assert.Equal(3, m.Quantity);
            Assert.Equal(1, m.FilledQuantity);
        }

        [Fact]
        public void ToPositionMessage_PreservesSignedQuantity()
        {
            var p = new PositionState { InstrumentName = "NQ", SignedQuantity = -5, AveragePrice = 20000, UnrealizedPnL = -123.5 };
            var m = WcfContractMapper.ToPositionMessage(p);
            Assert.Equal(-5, m.Quantity);   // sign carries short
            Assert.Equal("NQ", m.Instrument);
            Assert.Equal(20000, m.AveragePrice);
        }

        [Fact]
        public void ToPortfolioStateMessage_TakesFirstAccount_AndMapsCollections()
        {
            var snap = new PortfolioSnapshot { Version = 7 };
            snap.Accounts.Add(new AccountState { AccountName = "Sim101", NetLiquidation = 100000 });
            snap.Accounts.Add(new AccountState { AccountName = "Other" });
            snap.Orders.Add(new WorkingOrder { NtOrderId = "O1", InstrumentName = "ES", Side = BookFlowSide.Buy, Status = BookFlowOrderStatus.Working, LimitPrice = 5000 });
            snap.Positions.Add(new PositionState { InstrumentName = "ES", SignedQuantity = 2, AveragePrice = 5000 });

            var m = WcfContractMapper.ToPortfolioStateMessage(snap);

            Assert.Equal("Sim101", m.Account.AccountName);
            Assert.Single(m.Orders);
            Assert.Single(m.Positions);
            Assert.Equal(2, m.Positions[0].Quantity);
            Assert.Equal((byte)1, m.Orders[0].Side); // Buy == 1
        }

        [Theory]
        [InlineData(BookFlowOrderStatus.Submitted, OrderCommand.OrderStatus.Submitted)]
        [InlineData(BookFlowOrderStatus.Working, OrderCommand.OrderStatus.Submitted)]
        [InlineData(BookFlowOrderStatus.Filled, OrderCommand.OrderStatus.Filled)]
        [InlineData(BookFlowOrderStatus.Cancelled, OrderCommand.OrderStatus.Cancelled)]
        [InlineData(BookFlowOrderStatus.Rejected, OrderCommand.OrderStatus.Rejected)]
        public void FromAck_MapsStatus(BookFlowOrderStatus src, OrderCommand.OrderStatus expected)
        {
            var ack = new OrderAck { ClientOrderId = "C1", NtOrderId = "N1", Status = src, Message = "m" };
            var m = WcfContractMapper.FromAck(ack);
            Assert.Equal(expected, m.Status);
            Assert.Equal("N1", m.NTOrderId);
            Assert.Equal("C1", m.ClientOrderId);
        }

        [Fact]
        public void FromOperationResult_SuccessAndFailure()
        {
            var ok = WcfContractMapper.FromOperationResult(new OperationResult { Success = true, Message = "ok" }, "C1", OrderCommand.OrderStatus.Cancelled);
            Assert.Equal(OrderCommand.OrderStatus.Cancelled, ok.Status);
            var bad = WcfContractMapper.FromOperationResult(new OperationResult { Success = false, Message = "no" }, "C1", OrderCommand.OrderStatus.Cancelled);
            Assert.Equal(OrderCommand.OrderStatus.Rejected, bad.Status);
        }

        [Fact]
        public void ToOrderRequest_MapsActionPriceAndInstrument()
        {
            var cmd = new OrderCommand { Action = OrderCommand.OrderAction.BuyLimit, Quantity = 2, LimitPrice = 99.5, ClientOrderId = "C9" };
            var r = WcfContractMapper.ToOrderRequest("ES", cmd);
            Assert.Equal(BookFlowOrderAction.BuyLimit, r.Action);
            Assert.Equal(99.5, r.LimitPrice);
            Assert.Equal("ES", r.InstrumentName);
            Assert.Equal(2, r.Quantity);
            Assert.Equal("C9", r.ClientOrderId);
        }
    }
}
