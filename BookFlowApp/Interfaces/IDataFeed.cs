using System;
using System.Threading.Tasks;
using BookFlow.Shared.Contracts;
using BookFlow.Shared.Service;

namespace BookFlow.App.Interfaces
{
    /// <summary>
    /// Interface for data feeds that provide market data and order management capabilities.
    /// This abstraction allows the DomEngine to work with different data sources without
    /// coupling to specific implementations.
    /// </summary>
    public interface IDataFeed : IDisposable
    {
        /// <summary>
        /// Stream of unified market data messages from the data source.
        /// This is a high-frequency stream that should be consumed on a background thread.
        /// </summary>
        IObservable<UnifiedMarketDataMessage> MarketDataStream { get; }

        /// <summary>
        /// Stream of order status updates for submitted orders.
        /// </summary>
        IObservable<OrderStatusMessage> OrderStatusStream { get; }

        /// <summary>
        /// Stream of portfolio state updates (positions and account information).
        /// </summary>
        IObservable<PortfolioStateMessage> PortfolioStream { get; }

        /// <summary>
        /// Gets the current connection status of the data feed.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Event fired when connection status changes.
        /// </summary>
        event EventHandler<bool> ConnectionStatusChanged;

        /// <summary>
        /// Connects to the data source asynchronously.
        /// </summary>
        /// <returns>True if connection was successful, false otherwise.</returns>
        Task<bool> ConnectAsync();

        /// <summary>
        /// Disconnects from the data source.
        /// </summary>
        Task DisconnectAsync();

        /// <summary>
        /// Submits an order command to the data source.
        /// </summary>
        /// <param name="instrumentName">The instrument to trade.</param>
        /// <param name="orderCommand">The order command details.</param>
        /// <returns>Order status response from the data source.</returns>
        Task<OrderStatusMessage> SubmitOrderAsync(string instrumentName, OrderCommand orderCommand);

        /// <summary>
        /// Requests current portfolio state from the data source.
        /// </summary>
        /// <returns>Current portfolio state including positions and orders.</returns>
        Task<PortfolioStateMessage> RequestPortfolioStateAsync();

        /// <summary>
        /// Gets available instruments from the data source.
        /// </summary>
        /// <returns>List of available instruments with their properties.</returns>
        Task<System.Collections.Generic.List<TickerInfo>> GetAvailableInstrumentsAsync();

        /// <summary>
        /// Requests the authoritative L2 depth snapshot for a ticker (Q4), used to seed the
        /// ladder on engine start. Returns null if unavailable.
        /// </summary>
        Task<DomSnapshotResponse?> RequestDomSnapshotAsync(byte tickerId);
    }
}