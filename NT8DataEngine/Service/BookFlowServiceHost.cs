using System;
using System.ServiceModel;
using System.ServiceModel.Description;
using BookFlow.Shared.Service;

namespace BookFlow.NT8DataEngine.Service
{
    /// <summary>
    /// Owns the WCF <see cref="ServiceHost"/> lifecycle and the singleton
    /// <see cref="BookFlowServiceImpl"/>. Created by <c>BookFlowAddOn.EnsureInitialized</c>,
    /// disposed by <c>BookFlowAddOn.Shutdown</c>. Safe to construct + dispose
    /// repeatedly across NT8 F5 recompile cycles.
    /// </summary>
    public sealed class BookFlowServiceHost : IDisposable
    {
        private readonly Action<string> _log;
        private readonly BookFlowServiceImpl _impl;
        private ServiceHost _host;
        private int _disposed;

        public CallbackRegistry Callbacks { get; }

        public BookFlowServiceHost(
            Action<string> log,
            Func<PortfolioSnapshot> portfolioProvider,
            Func<OrderRequest, OrderAck> submitOrderHandler,
            Func<string, OperationResult> cancelAllHandler,
            Func<string, string, double, OperationResult> cancelAtPriceHandler,
            Func<string, string, OperationResult> flattenHandler,
            Func<AccountListResponse> accountsProvider,
            Func<TickerSnapshot> tickerProvider,
            Func<Pong> pingHandler,
            Func<byte, DomSnapshotResponse> domSnapshotProvider)
        {
            _log = log ?? (_ => { });
            Callbacks = new CallbackRegistry(_log);
            _impl = new BookFlowServiceImpl(
                Callbacks,
                portfolioProvider,
                submitOrderHandler,
                cancelAllHandler,
                cancelAtPriceHandler,
                flattenHandler,
                accountsProvider,
                tickerProvider,
                pingHandler,
                domSnapshotProvider);
        }

        public void Open()
        {
            if (_host != null) return;
            try
            {
                _host = new ServiceHost(_impl, new Uri(BookFlowEndpoint.BaseAddress));

                // Suppress the WSDL/metadata endpoint — we ship the contract assembly to clients,
                // so there's nothing for ?wsdl to add and exposing it would be needless surface.
                var debug = _host.Description.Behaviors.Find<ServiceDebugBehavior>();
                if (debug == null)
                {
                    debug = new ServiceDebugBehavior();
                    _host.Description.Behaviors.Add(debug);
                }
                debug.IncludeExceptionDetailInFaults = true;

                _host.AddServiceEndpoint(typeof(IBookFlowService), CreateBinding(), BookFlowEndpoint.ServicePath);

                _host.Faulted += OnHostFaulted;
                _host.Open();
                _log("BookFlow WCF service listening on " + BookFlowEndpoint.FullAddress);
            }
            catch (AddressAlreadyInUseException ex)
            {
                _log("ERROR: Service endpoint already in use (another NT8 instance still holding the pipe?): " + ex.Message);
                CloseHostQuietly();
            }
            catch (Exception ex)
            {
                _log("ERROR: ServiceHost.Open failed: " + ex.Message);
                CloseHostQuietly();
            }
        }

        /// <summary>Server-side binding. Mirrors the client binding in BookFlowServiceClient.</summary>
        internal static NetNamedPipeBinding CreateBinding()
        {
            var binding = new NetNamedPipeBinding
            {
                MaxReceivedMessageSize = BookFlowEndpoint.MaxMessageBytes,
                MaxBufferSize = BookFlowEndpoint.MaxMessageBytes,
                MaxBufferPoolSize = BookFlowEndpoint.MaxMessageBytes,
                ReceiveTimeout = TimeSpan.MaxValue,
                SendTimeout = TimeSpan.FromSeconds(BookFlowEndpoint.SendTimeoutSeconds),
                OpenTimeout = TimeSpan.FromSeconds(BookFlowEndpoint.OpenTimeoutSeconds),
                CloseTimeout = TimeSpan.FromSeconds(BookFlowEndpoint.CloseTimeoutSeconds),
            };
            binding.Security.Mode = NetNamedPipeSecurityMode.None;
            binding.ReaderQuotas.MaxStringContentLength = BookFlowEndpoint.MaxMessageBytes;
            binding.ReaderQuotas.MaxArrayLength = BookFlowEndpoint.MaxMessageBytes;
            binding.ReaderQuotas.MaxBytesPerRead = BookFlowEndpoint.MaxMessageBytes;
            return binding;
        }

        private void OnHostFaulted(object sender, EventArgs e)
        {
            _log("WARNING: ServiceHost entered Faulted state; aborting.");
            CloseHostQuietly();
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
            CloseHostQuietly();
        }

        private void CloseHostQuietly()
        {
            var host = System.Threading.Interlocked.Exchange(ref _host, null);
            if (host == null) return;
            try
            {
                if (host.State == CommunicationState.Faulted) host.Abort();
                else host.Close(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _log("ServiceHost close error: " + ex.Message);
                try { host.Abort(); } catch { }
            }
        }
    }
}
