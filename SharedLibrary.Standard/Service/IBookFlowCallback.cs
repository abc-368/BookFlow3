using System.ServiceModel;

namespace BookFlow.Shared.Service
{
    /// <summary>
    /// Push channel: the BookFlow host (NT8) calls these on every connected client
    /// for every broker-side event. One-way operations — no return value, no
    /// per-call back-pressure. Slow clients get evicted by the host.
    /// </summary>
    [ServiceContract]
    public interface IBookFlowCallback
    {
        [OperationContract(IsOneWay = true)]
        void OnOrderUpdate(OrderUpdateNotification notification);

        [OperationContract(IsOneWay = true)]
        void OnExecutionUpdate(ExecutionUpdateNotification notification);

        [OperationContract(IsOneWay = true)]
        void OnPositionUpdate(PositionUpdateNotification notification);

        [OperationContract(IsOneWay = true)]
        void OnAccountItemUpdate(AccountItemUpdateNotification notification);

        [OperationContract(IsOneWay = true)]
        void OnConnectionStatus(ConnectionStatusNotification notification);

        [OperationContract(IsOneWay = true)]
        void OnHeartbeat(HeartbeatNotification notification);
    }
}
