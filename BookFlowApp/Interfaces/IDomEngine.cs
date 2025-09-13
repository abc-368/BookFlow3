using System;
using System.Threading.Tasks;
using BookFlow.Shared.Contracts; // Unified shared DOM contracts

namespace BookFlow.App.Interfaces
{
    /// <summary>
    /// Interface for a DOM (Depth of Market) engine that processes market data and manages
    /// order book state for a single instrument. This is the core business logic component
    /// that sits between the raw data feed and the UI presentation layer.
    /// </summary>
    public interface IDomEngine : IDisposable
    {
        /// <summary>
        /// The instrument this engine is processing data for.
        /// </summary>
        string InstrumentName { get; }

        /// <summary>
        /// The ticker ID used to filter messages from the global data stream.
        /// </summary>
        byte TickerId { get; }

        /// <summary>
        /// Stream of processed ladder updates suitable for UI consumption.
        /// This stream is intelligently conflated and throttled for optimal UI performance.
        /// </summary>
        IObservable<LadderUpdate> LadderUpdates { get; }

        /// <summary>
        /// Stream of statistics updates (volume, spread, etc.).
        /// </summary>
        IObservable<StatisticsUpdate> StatisticsUpdates { get; }

        /// <summary>
        /// Gets the current connection status.
        /// </summary>
        bool IsConnected { get; }
        
        /// <summary>
        /// Gets the DOM settings for this engine.
        /// </summary>
        DomSettings Settings { get; }

        /// <summary>
        /// Gets the number of decimal places for price formatting based on the instrument's tick size.
        /// This is dynamically determined from the first received trade price.
        /// </summary>
        int PriceDecimalPlaces { get; }
        
        /// <summary>
        /// Clears all DOM data structures including order books, price levels, and market data.
        /// This provides a complete reset of the DOM state.
        /// </summary>
        void ClearAllData();

        /// <summary>
        /// Event fired when connection status changes.
        /// </summary>
        event EventHandler<bool> ConnectionStatusChanged;

        /// <summary>
        /// Starts the DOM engine with the specified data feed.
        /// </summary>
        /// <param name="dataFeed">The data feed to consume data from.</param>
        /// <returns>True if started successfully, false otherwise.</returns>
        Task<bool> StartAsync(IDataFeed dataFeed);

        /// <summary>
        /// Stops the DOM engine.
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// Gets a thread-safe snapshot of the current order book state.
        /// This method uses double-buffering to provide consistent reads without blocking
        /// the data processing thread.
        /// </summary>
        /// <returns>Current order book snapshot.</returns>
        BookSnapshot GetBookSnapshot();

        /// <summary>
        /// Gets current market statistics.
        /// </summary>
        /// <returns>Current statistics snapshot.</returns>
        StatisticsSnapshot GetStatisticsSnapshot();

        /// <summary>
        /// Places a market order.
        /// </summary>
        /// <param name="action">Buy or sell action.</param>
        /// <param name="quantity">Number of contracts.</param>
        /// <returns>Order status response.</returns>
        Task<OrderStatusMessage> PlaceMarketOrderAsync(OrderCommand.OrderAction action, int quantity);

        /// <summary>
        /// Places a limit order.
        /// </summary>
        /// <param name="action">Buy or sell action.</param>
        /// <param name="quantity">Number of contracts.</param>
        /// <param name="limitPrice">Limit price for the order.</param>
        /// <returns>Order status response.</returns>
        Task<OrderStatusMessage> PlaceLimitOrderAsync(OrderCommand.OrderAction action, int quantity, double limitPrice);

        /// <summary>
        /// Cancels all working orders for this instrument.
        /// </summary>
        /// <returns>Order status response.</returns>
        Task<OrderStatusMessage> CancelAllOrdersAsync();

        /// <summary>
        /// Flattens the current position (closes all open positions).
        /// </summary>
        /// <returns>Order status response.</returns>
        Task<OrderStatusMessage> FlattenPositionAsync();

        /// <summary>
        /// Centers the DOM display around the current market price.
        /// This triggers a ladder update with the current best bid/ask centered.
        /// </summary>
        void CenterDom();

        /// <summary>
        /// Gets the current best bid and ask prices.
        /// </summary>
        /// <returns>Tuple containing best bid and best ask prices, or null if not available.</returns>
        (double? BestBid, double? BestAsk) GetBestBidAsk();

        /// <summary>
        /// Gets the current spread (difference between best bid and ask).
        /// </summary>
        /// <returns>Current spread in price units, or null if not available.</returns>
        double? GetSpread();

        /// <summary>
        /// Processes a raw market data message. This method is called by the data feed
        /// for messages that match this engine's instrument.
        /// </summary>
        /// <param name="message">The market data message to process.</param>
        void ProcessMessage(UnifiedMarketDataMessage message);

        /// <summary>
        /// Forces the engine to recalculate its state and publish a new ladder update.
        /// </summary>
        void ForceUpdate();
    }
}