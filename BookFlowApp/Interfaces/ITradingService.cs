using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using BookFlow.Shared.Contracts;
using BookFlow.App.Models;

namespace BookFlow.App.Interfaces
{
    /// <summary>
    /// Trading service interface for executing orders through BookFlow.
    /// Provides methods for order placement, cancellation, and position management.
    /// </summary>
    public interface ITradingService
    {
        /// <summary>
        /// Submit an order for a specific instrument.
        /// </summary>
        /// <param name="instrumentName">The name of the instrument (e.g., "ES 12-24")</param>
        /// <param name="orderCommand">The order command to execute</param>
        /// <returns>Order status response indicating success or failure</returns>
        Task<OrderStatusMessage> SubmitOrderAsync(string instrumentName, OrderCommand orderCommand);
        
        /// <summary>
        /// Cancel all working orders for a specific instrument.
        /// </summary>
        /// <param name="instrumentName">The name of the instrument</param>
        /// <returns>Order status response</returns>
        Task<OrderStatusMessage> CancelAllOrdersAsync(string instrumentName);
        
        /// <summary>
        /// Cancel working orders at a specific price level for an instrument.
        /// </summary>
        /// <param name="instrumentName">The name of the instrument</param>
        /// <param name="price">The price level to cancel orders at</param>
        /// <returns>Order status response</returns>
        Task<OrderStatusMessage> CancelOrdersAtPriceAsync(string instrumentName, decimal price);
        
        /// <summary>
        /// Flatten position for a specific instrument (close all positions).
        /// </summary>
        /// <param name="instrumentName">The name of the instrument</param>
        /// <returns>Order status response</returns>
        Task<OrderStatusMessage> FlattenPositionAsync(string instrumentName);
        
        /// <summary>
        /// Check if trading is enabled and the service is ready to accept orders.
        /// </summary>
        bool IsTradingEnabled { get; }
        
        /// <summary>
        /// Check if the service is connected to the trading backend.
        /// </summary>
        bool IsConnected { get; }
        
        /// <summary>
        /// Event fired when trading status changes (enabled/disabled).
        /// </summary>
        event EventHandler<bool> TradingStatusChanged;

        /// <summary>
        /// Event fired when the collection of working orders changes.
        /// </summary>
        event Action? OrderBookChanged;

        /// <summary>
        /// Event fired when the portfolio state changes (position, P&L, etc.).
        /// </summary>
        event Action? PortfolioChanged;

        /// <summary>
        /// Gets the current position for a specific instrument.
        /// </summary>
        PositionSnapshot GetPositionSnapshot(string instrumentName);

        /// <summary>
        /// A collection of all current working orders.
        /// </summary>
        System.Collections.ObjectModel.ReadOnlyObservableCollection<WorkingOrderMessage> WorkingOrders { get; }
    }
}