using System;
using System.Collections.Generic;
using BookFlow.Shared.Contracts;     // legacy contracts the UI binds to
using BookFlow.Shared.Service;       // new WCF contracts
using BookFlow.App.Models;

namespace BookFlow.App.Services
{
    /// <summary>
    /// Translates between the new WCF service contracts and the legacy contract
    /// types the WPF UI binds to. Keeps ITradingService/IDataFeed and the view
    /// models unchanged while the transport underneath is WCF.
    /// </summary>
    internal static class WcfContractMapper
    {
        public static OrderRequest ToOrderRequest(string instrument, OrderCommand cmd) => new OrderRequest
        {
            ClientOrderId = cmd.ClientOrderId,
            AccountName = null,            // resolved server-side (single eligible / preferred)
            InstrumentName = instrument,
            Action = ToAction(cmd.Action),
            Quantity = cmd.Quantity,
            LimitPrice = cmd.LimitPrice,
            ClientUtcTime = DateTime.UtcNow,
        };

        public static BookFlowOrderAction ToAction(OrderCommand.OrderAction a)
        {
            switch (a)
            {
                case OrderCommand.OrderAction.BuyMarket: return BookFlowOrderAction.BuyMarket;
                case OrderCommand.OrderAction.SellMarket: return BookFlowOrderAction.SellMarket;
                case OrderCommand.OrderAction.BuyLimit: return BookFlowOrderAction.BuyLimit;
                case OrderCommand.OrderAction.SellLimit: return BookFlowOrderAction.SellLimit;
                case OrderCommand.OrderAction.Flat: return BookFlowOrderAction.Flat;
                case OrderCommand.OrderAction.CancelAll: return BookFlowOrderAction.CancelAll;
                case OrderCommand.OrderAction.CancelAtPrice: return BookFlowOrderAction.CancelAtPrice;
                default: return BookFlowOrderAction.BuyMarket;
            }
        }

        public static OrderStatusMessage FromAck(OrderAck ack) => new OrderStatusMessage
        {
            ClientOrderId = ack?.ClientOrderId,
            NTOrderId = ack?.NtOrderId,
            Status = ToLegacyStatus(ack?.Status ?? BookFlowOrderStatus.Rejected),
            Message = ack?.Message,
            Timestamp = ack?.ServerUtcTime ?? DateTime.UtcNow,
        };

        public static OrderStatusMessage FromOperationResult(OperationResult r, string clientOrderId, OrderCommand.OrderStatus statusOnSuccess) => new OrderStatusMessage
        {
            ClientOrderId = clientOrderId,
            Status = (r != null && r.Success) ? statusOnSuccess : OrderCommand.OrderStatus.Rejected,
            Message = r?.Message,
            Timestamp = DateTime.UtcNow,
        };

        public static OrderCommand.OrderStatus ToLegacyStatus(BookFlowOrderStatus s)
        {
            switch (s)
            {
                case BookFlowOrderStatus.Pending: return OrderCommand.OrderStatus.Pending;
                case BookFlowOrderStatus.Submitted:
                case BookFlowOrderStatus.Accepted:
                case BookFlowOrderStatus.Working: return OrderCommand.OrderStatus.Submitted;
                case BookFlowOrderStatus.PartFilled:
                case BookFlowOrderStatus.Filled: return OrderCommand.OrderStatus.Filled;
                case BookFlowOrderStatus.CancelSubmitted:
                case BookFlowOrderStatus.Cancelled: return OrderCommand.OrderStatus.Cancelled;
                default: return OrderCommand.OrderStatus.Rejected;
            }
        }

        // NT8 OrderState byte values, as the legacy WorkingOrderMessage.State carried them.
        private static byte ToNtOrderStateByte(BookFlowOrderStatus s)
        {
            switch (s)
            {
                case BookFlowOrderStatus.Pending: return 1;          // Initialized
                case BookFlowOrderStatus.Submitted: return 2;
                case BookFlowOrderStatus.Accepted: return 3;
                case BookFlowOrderStatus.Working: return 4;
                case BookFlowOrderStatus.PartFilled: return 5;
                case BookFlowOrderStatus.Filled: return 6;
                case BookFlowOrderStatus.CancelSubmitted: return 7;
                case BookFlowOrderStatus.Cancelled: return 8;
                case BookFlowOrderStatus.Rejected: return 9;
                default: return 0;
            }
        }

        public static WorkingOrderMessage ToWorkingOrderMessage(WorkingOrder o) => new WorkingOrderMessage
        {
            OrderId = o.NtOrderId,
            NTOrderId = o.NtOrderId,
            ClientOrderId = o.ClientOrderId,
            Instrument = o.InstrumentName,
            Side = (byte)o.Side,                 // BookFlowSide Buy=1/Sell=2 == legacy 1/2
            State = ToNtOrderStateByte(o.Status),
            Type = 0,
            TimeInForce = 0,
            Price = o.LimitPrice,
            Quantity = o.Quantity,
            FilledQuantity = o.FilledQuantity,
            AverageFillPrice = o.AverageFillPrice,
            QueuePosition = 0,
            SubmitTime = o.SubmitUtcTime,
            LastUpdateTime = DateTime.UtcNow,
        };

        public static PositionMessage ToPositionMessage(PositionState p) => new PositionMessage
        {
            Instrument = p.InstrumentName,
            Quantity = p.SignedQuantity,
            AveragePrice = p.AveragePrice,
            UnrealizedPnL = p.UnrealizedPnL,
            RealizedPnL = p.RealizedPnL,
            LastUpdateTime = DateTime.UtcNow,
        };

        public static AccountStateMessage ToAccountStateMessage(AccountState a)
        {
            if (a == null) return new AccountStateMessage { AccountName = string.Empty };
            return new AccountStateMessage
            {
                AccountName = a.AccountName,
                BuyingPower = a.BuyingPower,
                CashValue = a.CashValue,
                RealizedPnL = a.RealizedPnL,
                UnrealizedPnL = a.UnrealizedPnL,
                NetLiquidation = a.NetLiquidation,
                InitialMargin = a.InitialMargin,
                MaintenanceMargin = a.MaintenanceMargin,
                ExcessEquity = a.ExcessEquity,
                Commission = a.Commission,
            };
        }

        public static PortfolioStateMessage ToPortfolioStateMessage(PortfolioSnapshot snap)
        {
            var msg = new PortfolioStateMessage { Timestamp = snap?.ServerUtcTime ?? DateTime.UtcNow };
            if (snap == null) return msg;

            // Legacy UI tracks a single account; take the first (server lists eligible accounts).
            msg.Account = (snap.Accounts != null && snap.Accounts.Count > 0)
                ? ToAccountStateMessage(snap.Accounts[0])
                : new AccountStateMessage { AccountName = string.Empty };

            if (snap.Orders != null)
                foreach (var o in snap.Orders) msg.Orders.Add(ToWorkingOrderMessage(o));
            if (snap.Positions != null)
                foreach (var p in snap.Positions) msg.Positions.Add(ToPositionMessage(p));
            return msg;
        }

        public static List<TickerInfo> ToTickerInfoList(TickerSnapshot snap)
        {
            var list = new List<TickerInfo>();
            if (snap?.Entries == null) return list;
            foreach (var e in snap.Entries)
            {
                list.Add(new TickerInfo
                {
                    TickerId = e.TickerId,
                    InstrumentName = e.InstrumentName,
                    TickSize = e.TickSize,
                    PointValue = e.PointValue,
                });
            }
            return list;
        }
    }
}
