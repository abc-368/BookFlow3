using System.ServiceModel;

namespace BookFlow.Shared.Service
{
    /// <summary>
    /// Duplex service contract for control + portfolio. Hosted by the NT8 AddOn,
    /// consumed by every BookFlow WPF window. The callback contract is
    /// <see cref="IBookFlowCallback"/>; the host invokes it whenever a broker-side
    /// event occurs, so the wire is bidirectional over a single named pipe.
    /// </summary>
    [ServiceContract(
        SessionMode = SessionMode.Required,
        CallbackContract = typeof(IBookFlowCallback))]
    public interface IBookFlowService
    {
        /// <summary>
        /// Registers the calling client and returns a session token. Required before
        /// any other operation. Resubmitting an existing token is idempotent and
        /// resets the callback channel to the current caller's instance context.
        /// </summary>
        [OperationContract(IsInitiating = true)]
        SessionInfo RegisterClient(ClientInfo clientInfo);

        /// <summary>Cheap round-trip + diagnostic counters.</summary>
        [OperationContract]
        Pong Ping();

        /// <summary>
        /// Full authoritative portfolio snapshot. Client caches the returned Version
        /// and resyncs by re-calling this method whenever a notification arrives with
        /// a non-sequential Version (gap = dropped frames).
        /// </summary>
        [OperationContract]
        PortfolioSnapshot RequestPortfolioState();

        /// <summary>Submits a new order. Reply is the immediate broker ack; subsequent
        /// state changes arrive via <see cref="IBookFlowCallback.OnOrderUpdate"/>.</summary>
        [OperationContract]
        OrderAck SubmitOrder(OrderRequest request);

        /// <summary>Cancels every working order on the given account. Empty = server resolves the eligible account.</summary>
        [OperationContract]
        OperationResult CancelAllOrders(string accountName);

        /// <summary>Cancels all working orders on the given account+instrument at the given price. Empty account = server resolves.</summary>
        [OperationContract]
        OperationResult CancelAtPrice(string accountName, string instrumentName, double price);

        /// <summary>Submits a flatten (market order opposite to current position) for account+instrument. Empty account = server resolves.</summary>
        [OperationContract]
        OperationResult FlattenPosition(string accountName, string instrumentName);

        /// <summary>Returns every account NT8 can see, plus the preferred default (if any).</summary>
        [OperationContract]
        AccountListResponse ListAccounts();

        /// <summary>Current ticker registrations (instrument → ticker id in the data ring).</summary>
        [OperationContract]
        TickerSnapshot GetTickerSnapshot();

        /// <summary>
        /// Authoritative L2 depth snapshot for a ticker, used to seed a client's ladder on
        /// connect (Q4). Carries the global data sequence of the last applied tick so the
        /// client can align it with the live MMF stream without gaps or double-application.
        /// </summary>
        [OperationContract]
        DomSnapshotResponse RequestDomSnapshot(byte tickerId);

        /// <summary>Voluntary clean disconnect; lets the host drop the callback immediately
        /// instead of waiting for the channel to fault.</summary>
        [OperationContract(IsTerminating = true)]
        void Disconnect();
    }
}
