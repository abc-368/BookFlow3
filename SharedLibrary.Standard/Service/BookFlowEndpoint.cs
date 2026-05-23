namespace BookFlow.Shared.Service
{
    /// <summary>
    /// Shared endpoint constants. The binding itself is constructed on each side
    /// (host = net48 framework WCF, client = System.ServiceModel.NetNamedPipe) because
    /// NetNamedPipeBinding has no netstandard2.0 build. Keeping the values here means
    /// both sides stay in lockstep even though the binding object is built twice.
    /// </summary>
    public static class BookFlowEndpoint
    {
        // net.pipe address space is local-only. No firewall, no port collision.
        public const string BaseAddress = "net.pipe://localhost/bookflow";
        public const string ServicePath = "control";
        public const string FullAddress = BaseAddress + "/" + ServicePath;

        // Mirrors Jigsaw's daytradr setting; localhost named pipe so this only bounds
        // worst-case portfolio snapshots, not network traffic.
        public const int MaxMessageBytes = 20 * 1024 * 1024;

        public const int OpenTimeoutSeconds = 10;
        public const int SendTimeoutSeconds = 30;
        public const int CloseTimeoutSeconds = 5;
    }
}
