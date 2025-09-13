using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using BookFlow.Shared.Contracts;
using BookFlow.App.Interfaces;
using BookFlow.App.Models;

namespace BookFlow.App.Services
{
    /// <summary>
    /// Mock trading service for demonstration and testing purposes.
    /// In production, this would be replaced with a real trading service that 
    /// communicates with NinjaTrader 8 or other trading platform.
    /// </summary>
    public class MockTradingService : ITradingService
    {
        private bool _isTradingEnabled = false;
        private readonly ObservableCollection<WorkingOrderMessage> _workingOrders = new();

        public bool IsTradingEnabled => _isTradingEnabled;
        public bool IsConnected => true; // Mock service is always "connected"

        public ReadOnlyObservableCollection<WorkingOrderMessage> WorkingOrders { get; }

        public event EventHandler<bool>? TradingStatusChanged;
        public event Action? OrderBookChanged;
        public event Action? PortfolioChanged;

        private int _position;
        private decimal _averagePrice;
        private decimal _realizedPnL;
        private decimal _unrealizedPnL;

        public MockTradingService()
        {
            WorkingOrders = new ReadOnlyObservableCollection<WorkingOrderMessage>(_workingOrders);
        }

        /// <summary>
        /// Enable or disable trading for demonstration purposes.
        /// In production, this would be controlled by connection status and account validation.
        /// </summary>
        public void SetTradingEnabled(bool enabled)
        {
            if (_isTradingEnabled != enabled)
            {
                _isTradingEnabled = enabled;
                TradingStatusChanged?.Invoke(this, enabled);
            }
        }

        public async Task<OrderStatusMessage> SubmitOrderAsync(string instrumentName, OrderCommand orderCommand)
        {
            // Simulate network latency
            await Task.Delay(50);

            if (!_isTradingEnabled)
            {
                return new OrderStatusMessage
                {
                    ClientOrderId = orderCommand.ClientOrderId,
                    Status = OrderCommand.OrderStatus.Rejected,
                    Message = "Trading is currently disabled",
                    Timestamp = DateTime.UtcNow,
                    Action = orderCommand.Action,
                    OrderPrice = orderCommand.LimitPrice,
                    RequestId = Guid.NewGuid().ToString()
                };
            }

            // Simulate order validation and submission
            if (orderCommand.Quantity <= 0)
            {
                return new OrderStatusMessage
                {
                    ClientOrderId = orderCommand.ClientOrderId,
                    Status = OrderCommand.OrderStatus.Rejected,
                    Message = "Invalid quantity: must be greater than zero",
                    Timestamp = DateTime.UtcNow,
                    Action = orderCommand.Action,
                    OrderPrice = orderCommand.LimitPrice,
                    RequestId = Guid.NewGuid().ToString()
                };
            }

            if (orderCommand.LimitPrice <= 0 && 
                (orderCommand.Action == OrderCommand.OrderAction.BuyLimit || 
                 orderCommand.Action == OrderCommand.OrderAction.SellLimit))
            {
                return new OrderStatusMessage
                {
                    ClientOrderId = orderCommand.ClientOrderId,
                    Status = OrderCommand.OrderStatus.Rejected,
                    Message = "Invalid limit price: must be greater than zero",
                    Timestamp = DateTime.UtcNow,
                    Action = orderCommand.Action,
                    OrderPrice = orderCommand.LimitPrice,
                    RequestId = Guid.NewGuid().ToString()
                };
            }

            // Simulate successful order submission
            var ntOrderId = $"NT-{DateTime.UtcNow:HHmmssff}-{new Random().Next(1000, 9999)}";

            if (orderCommand.Action == OrderCommand.OrderAction.BuyMarket || orderCommand.Action == OrderCommand.OrderAction.SellMarket)
            {
                var fillPrice = orderCommand.Action == OrderCommand.OrderAction.BuyMarket ? 4520.25 : 4520.00;
                var orderStatus = new OrderStatusMessage
                {
                    ClientOrderId = orderCommand.ClientOrderId,
                    NTOrderId = ntOrderId,
                    Status = OrderCommand.OrderStatus.Filled,
                    Message = $"Order filled successfully for {instrumentName}",
                    Timestamp = DateTime.UtcNow,
                    Action = orderCommand.Action,
                    OrderPrice = orderCommand.LimitPrice,
                    FillPrice = fillPrice,
                    FillQuantity = orderCommand.Quantity,
                    RequestId = Guid.NewGuid().ToString()
                };
                UpdatePosition(orderStatus);
                PortfolioChanged?.Invoke();
                return orderStatus;
            }

            var newOrder = new WorkingOrderMessage
            {
                Instrument = instrumentName,
                ClientOrderId = orderCommand.ClientOrderId,
                NTOrderId = ntOrderId,
                Price = orderCommand.LimitPrice,
                Quantity = orderCommand.Quantity,
                FilledQuantity = 0,
                Side = (byte)((orderCommand.Action == OrderCommand.OrderAction.BuyLimit || orderCommand.Action == OrderCommand.OrderAction.BuyMarket) ? 1 : 2),
                State = (byte)NTOrderState.Working,
                Type = (byte)NTOrderType.Limit,
                TimeInForce = (byte)NTTimeInForce.Day,
                SubmitTime = DateTime.UtcNow,
                LastUpdateTime = DateTime.UtcNow
            };
            _workingOrders.Add(newOrder);
            OrderBookChanged?.Invoke();
            
            return new OrderStatusMessage
            {
                ClientOrderId = orderCommand.ClientOrderId,
                NTOrderId = ntOrderId,
                Status = OrderCommand.OrderStatus.Submitted,
                Message = $"Order submitted successfully for {instrumentName}",
                Timestamp = DateTime.UtcNow,
                Action = orderCommand.Action,
                OrderPrice = orderCommand.LimitPrice,
                RequestId = Guid.NewGuid().ToString()
            };
        }

        public async Task<OrderStatusMessage> CancelAllOrdersAsync(string instrumentName)
        {
            // Simulate network latency
            await Task.Delay(30);

            var ordersToRemove = _workingOrders.Where(o => o.Instrument == instrumentName).ToList();
            foreach (var order in ordersToRemove)
            {
                _workingOrders.Remove(order);
            }
            if (ordersToRemove.Any())
            {
                OrderBookChanged?.Invoke();
            }

            return new OrderStatusMessage
            {
                ClientOrderId = "CANCEL_ALL",
                Status = OrderCommand.OrderStatus.Cancelled,
                Message = $"All orders cancelled for {instrumentName}",
                Timestamp = DateTime.UtcNow,
                RequestId = Guid.NewGuid().ToString()
            };
        }

        public async Task<OrderStatusMessage> CancelOrdersAtPriceAsync(string instrumentName, decimal price)
        {
            // Simulate network latency
            await Task.Delay(30);

            var ordersToRemove = _workingOrders.Where(o => o.Instrument == instrumentName && (decimal)o.Price == price).ToList();
            foreach (var order in ordersToRemove)
            {
                _workingOrders.Remove(order);
            }
            if (ordersToRemove.Any())
            {
                OrderBookChanged?.Invoke();
            }

            return new OrderStatusMessage
            {
                ClientOrderId = $"CANCEL_AT_{price:F2}",
                Status = OrderCommand.OrderStatus.Cancelled,
                Message = $"Orders cancelled at {price:F2} for {instrumentName}",
                Timestamp = DateTime.UtcNow,
                OrderPrice = (double)price,
                RequestId = Guid.NewGuid().ToString()
            };
        }

        public async Task<OrderStatusMessage> FlattenPositionAsync(string instrumentName)
        {
            // Simulate network latency
            await Task.Delay(50);

            return new OrderStatusMessage
            {
                ClientOrderId = "FLATTEN",
                Status = OrderCommand.OrderStatus.Submitted,
                Message = $"Flatten position order submitted for {instrumentName}",
                Timestamp = DateTime.UtcNow,
                RequestId = Guid.NewGuid().ToString()
            };
        }

        public PositionSnapshot GetPositionSnapshot(string instrumentName)
        {
            // In a real application, you would look up the position for the given instrument.
            // For this example, we are only tracking one instrument.
            return new PositionSnapshot(
                instrumentName,
                0, // TickerId is not available here
                _position,
                _averagePrice,
                _unrealizedPnL,
                _realizedPnL,
                0, // CurrentPrice is not available here
                0,
                0,
                DateTime.UtcNow);
        }

        private void UpdatePosition(OrderStatusMessage orderStatus)
        {
            int fillSize = orderStatus.FillQuantity;
            if (orderStatus.Action == OrderCommand.OrderAction.SellMarket || 
                orderStatus.Action == OrderCommand.OrderAction.SellLimit)
                fillSize = -fillSize;
                
            if (_position == 0)
            {
                // Opening position
                _position = fillSize;
                _averagePrice = (decimal)orderStatus.FillPrice;
            }
            else if (Math.Sign(_position) == Math.Sign(fillSize))
            {
                // Adding to position
                decimal totalCost = (_position * _averagePrice) + (fillSize * (decimal)orderStatus.FillPrice);
                _position += fillSize;
                _averagePrice = totalCost / _position;
            }
            else
            {
                // Reducing or reversing position
                int absOldPosition = Math.Abs(_position);
                int absFillSize = Math.Abs(fillSize);
                
                if (absFillSize >= absOldPosition)
                {
                    // Closing and potentially reversing
                    decimal realizedPnLFromClose = absOldPosition * ((decimal)orderStatus.FillPrice - _averagePrice) * 
                                                  Math.Sign(_position) * 50; // Assuming $50 per point
                    _realizedPnL += realizedPnLFromClose;
                    
                    if (absFillSize > absOldPosition)
                    {
                        // Reversing position
                        _position = fillSize + _position; // Net new position
                        _averagePrice = (decimal)orderStatus.FillPrice;
                    }
                    else
                    {
                        // Flat
                        _position = 0;
                        _averagePrice = 0;
                        _unrealizedPnL = 0;
                    }
                }
                else
                {
                    // Partial close
                    decimal realizedPnLFromPartial = absFillSize * ((decimal)orderStatus.FillPrice - _averagePrice) * 
                                                   Math.Sign(_position) * 50; // Assuming $50 per point
                    _realizedPnL += realizedPnLFromPartial;
                    _position += fillSize; // Reduce position
                }
            }
        }
    }
}