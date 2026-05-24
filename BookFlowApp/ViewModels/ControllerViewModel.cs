using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using BookFlow.App.Services;
using BookFlow.App.Views;
using BookFlow.App.Interfaces;
using BookFlow.App.Engine;
using BookFlow.Shared.Contracts; // DomSettings now shared
using System.Windows.Media;
using System.Collections.Specialized;

namespace BookFlow.App.ViewModels
{
    public class ControllerViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly ObservableCollection<DomWindowInfo> _activeDomWindows;
        private readonly StringBuilder _systemLog;
        private NT8DirectDataFeed? _sharedDataFeed;
        private bool _isConnected = false;
        private bool _disposed = false;
        private TickerInfo? _selectedInstrument;

        // Status properties used by XAML
        private System.Windows.Media.Brush _connectionStatusBrush = System.Windows.Media.Brushes.Gray;
        private System.Windows.Media.Brush _dataChannelStatusBrush = System.Windows.Media.Brushes.Gray;
        private System.Windows.Media.Brush _controlPipeStatusBrush = System.Windows.Media.Brushes.Gray;
        private string _dataChannelStatusText = "Disconnected";
        private string _controlPipeStatusText = "Disconnected";

        public ObservableCollection<TickerInfo> AvailableInstruments { get; } = new();
        public ObservableCollection<DomWindowInfo> ActiveDomWindows => _activeDomWindows;

        public ControllerViewModel()
        {
            _activeDomWindows = new ObservableCollection<DomWindowInfo>();
            _systemLog = new StringBuilder();
            // Keep InstrumentCount in sync
            AvailableInstruments.CollectionChanged += OnInstrumentsChanged;
        }

        private void OnInstrumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(InstrumentCount));
            OnPropertyChanged(nameof(HasInstruments));
        }

        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                if (_isConnected != value)
                {
                    _isConnected = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ConnectionStatusText));
                    OnPropertyChanged(nameof(CanConnect));
                    OnPropertyChanged(nameof(CanDisconnect));
                    OnPropertyChanged(nameof(CanLaunchDom));
                    // Update top status brush
                    ConnectionStatusBrush = value ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)) : System.Windows.Media.Brushes.Gray;
                }
            }
        }

        public string ConnectionStatusText => IsConnected ? "Connected" : "Disconnected";
        public System.Windows.Media.Brush ConnectionStatusBrush
        {
            get => _connectionStatusBrush;
            private set { if (!Equals(_connectionStatusBrush, value)) { _connectionStatusBrush = value; OnPropertyChanged(); } }
        }

        public System.Windows.Media.Brush DataChannelStatusBrush
        {
            get => _dataChannelStatusBrush;
            private set { if (!Equals(_dataChannelStatusBrush, value)) { _dataChannelStatusBrush = value; OnPropertyChanged(); } }
        }
        public string DataChannelStatusText
        {
            get => _dataChannelStatusText;
            private set { if (_dataChannelStatusText != value) { _dataChannelStatusText = value; OnPropertyChanged(); } }
        }

        public System.Windows.Media.Brush ControlPipeStatusBrush
        {
            get => _controlPipeStatusBrush;
            private set { if (!Equals(_controlPipeStatusBrush, value)) { _controlPipeStatusBrush = value; OnPropertyChanged(); } }
        }
        public string ControlPipeStatusText
        {
            get => _controlPipeStatusText;
            private set { if (_controlPipeStatusText != value) { _controlPipeStatusText = value; OnPropertyChanged(); } }
        }

        public bool CanConnect => !IsConnected;
        public bool CanDisconnect => IsConnected;
        public int ActiveDomCount => _activeDomWindows.Count;
        public bool HasInstruments => AvailableInstruments.Count > 0;
        public bool CanLaunchDom => _selectedInstrument != null && IsConnected;
        public int InstrumentCount => AvailableInstruments.Count;

        public TickerInfo? SelectedInstrument
        {
            get => _selectedInstrument;
            set
            {
                if (!ReferenceEquals(_selectedInstrument, value))
                {
                    _selectedInstrument = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanLaunchDom));
                    OnPropertyChanged(nameof(SelectedInstrumentDetails));
                }
            }
        }

        public string SelectedInstrumentDetails => _selectedInstrument == null ? "No instrument selected" : $"Tick Size: {_selectedInstrument.TickSize:F4} | Point Value: {_selectedInstrument.PointValue:F2}";
        public string SystemLogText => _systemLog.ToString();

        public async Task ConnectAsync()
        {
            try
            {
                LogMessage("Connecting to NT8DataEngine...");
                _sharedDataFeed = new NT8DirectDataFeed();
                _sharedDataFeed.ConnectionStatusChanged += OnDataChannelStatusChanged;
                if (await _sharedDataFeed.ConnectAsync())
                {
                    IsConnected = true;
                    // Control pipe is part of direct data feed connect
                    ControlPipeStatusBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80));
                    ControlPipeStatusText = "Connected";

                    var instruments = await _sharedDataFeed.GetAvailableInstrumentsAsync();
                    AvailableInstruments.Clear();
                    foreach (var inst in instruments) AvailableInstruments.Add(inst);
                    if (AvailableInstruments.Any()) SelectedInstrument = AvailableInstruments.First();
                    OnPropertyChanged(nameof(HasInstruments));
                    OnPropertyChanged(nameof(InstrumentCount));
                    LogMessage($"Connected. Instruments: {AvailableInstruments.Count}");
                }
                else LogMessage("Connection failed.");
            }
            catch (Exception ex) { LogMessage($"Connection error: {ex.Message}"); }
        }

        /// <summary>
        /// Re-pulls the instrument→ticker dictionary without disconnecting or closing any open
        /// DOM windows. Picks up instruments whose indicators were dropped after connect, and
        /// preserves the current selection by name. (Ticker ids are stable for the session, so
        /// this never re-points an open window.)
        /// </summary>
        public async Task RefreshInstrumentsAsync()
        {
            if (!IsConnected || _sharedDataFeed == null) return;
            try
            {
                var prevName = _selectedInstrument?.InstrumentName;
                var instruments = await _sharedDataFeed.GetAvailableInstrumentsAsync();
                AvailableInstruments.Clear();
                foreach (var inst in instruments) AvailableInstruments.Add(inst);
                SelectedInstrument = (!string.IsNullOrEmpty(prevName)
                    ? AvailableInstruments.FirstOrDefault(i => i.InstrumentName == prevName)
                    : null) ?? AvailableInstruments.FirstOrDefault();
                OnPropertyChanged(nameof(HasInstruments));
                OnPropertyChanged(nameof(InstrumentCount));
                LogMessage($"Refreshed instruments: {AvailableInstruments.Count}");
            }
            catch (Exception ex) { LogMessage($"Refresh instruments error: {ex.Message}"); }
        }

        private void OnDataChannelStatusChanged(object? sender, bool isConnected)
        {
            // Update UI-friendly properties
            DataChannelStatusBrush = isConnected ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)) : System.Windows.Media.Brushes.Gray;
            DataChannelStatusText = isConnected ? "Connected" : "Disconnected";
        }

        public async Task DisconnectAsync()
        {
            try
            {
                LogMessage("Disconnecting...");
                foreach (var dom in _activeDomWindows.ToList()) { dom.Engine?.Dispose(); dom.Window?.Close(); }
                _activeDomWindows.Clear();
                if (_sharedDataFeed != null)
                {
                    _sharedDataFeed.ConnectionStatusChanged -= OnDataChannelStatusChanged;
                    await _sharedDataFeed.DisconnectAsync();
                    _sharedDataFeed.Dispose();
                    _sharedDataFeed = null;
                }
                AvailableInstruments.Clear();
                SelectedInstrument = null;
                IsConnected = false;
                DataChannelStatusBrush = System.Windows.Media.Brushes.Gray;
                DataChannelStatusText = "Disconnected";
                ControlPipeStatusBrush = System.Windows.Media.Brushes.Gray;
                ControlPipeStatusText = "Disconnected";
                OnPropertyChanged(nameof(HasInstruments));
                OnPropertyChanged(nameof(InstrumentCount));
                LogMessage("Disconnected.");
            }
            catch (Exception ex) { LogMessage($"Disconnect error: {ex.Message}"); }
        }

        public void LaunchSelectedInstrumentDom()
        {
            if (_selectedInstrument == null || !IsConnected || _sharedDataFeed == null) return;
            var existing = _activeDomWindows.FirstOrDefault(d => d.InstrumentName == _selectedInstrument.InstrumentName);
            if (existing != null) { existing.Window?.Activate(); return; }
            var tradingService = new NT8TradingService();
            // Use the TickerId provided by NT8 to filter stream correctly
            var domEngine = new DomEngine(_selectedInstrument.InstrumentName, _selectedInstrument.TickerId, tradingService, (decimal)_selectedInstrument.TickSize, (decimal)_selectedInstrument.PointValue, new DomSettings());
            _ = domEngine.StartAsync(_sharedDataFeed);
            var vm = new DomViewModel(domEngine, tradingService);
            var win = new DomGridWindow(vm) { Title = $"DOM - {_selectedInstrument.InstrumentName}" };
            var info = new DomWindowInfo { InstrumentName = _selectedInstrument.InstrumentName, TickerId = _selectedInstrument.TickerId, Window = win, ViewModel = vm, Engine = domEngine };
            _activeDomWindows.Add(info); OnPropertyChanged(nameof(ActiveDomCount));
            win.Closed += (s, e) => { domEngine.Dispose(); _activeDomWindows.Remove(info); OnPropertyChanged(nameof(ActiveDomCount)); };
            win.Show();
        }

        public void ClearLog() { _systemLog.Clear(); OnPropertyChanged(nameof(SystemLogText)); }
        private void LogMessage(string msg) { _systemLog.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}"); if (_systemLog.Length > 50000) { var lines = _systemLog.ToString().Split('\n'); _systemLog.Clear(); _systemLog.AppendLine(string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 500)))); } OnPropertyChanged(nameof(SystemLogText)); }

        public event PropertyChangedEventHandler? PropertyChanged; protected virtual void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        public void Dispose() { if (_disposed) return; _disposed = true; foreach (var dom in _activeDomWindows.ToList()) { dom.Engine?.Dispose(); dom.Window?.Close(); } _activeDomWindows.Clear(); _sharedDataFeed?.Dispose(); }
    }

    public class DomWindowInfo
    {
        public string InstrumentName { get; set; } = string.Empty; public byte TickerId { get; set; } public DomGridWindow? Window { get; set; } public DomViewModel? ViewModel { get; set; } public IDomEngine? Engine { get; set; }
    }
}
