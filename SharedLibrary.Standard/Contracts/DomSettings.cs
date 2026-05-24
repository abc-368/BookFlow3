using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using System.Collections.Generic;

namespace BookFlow.Shared.Contracts
{
    /// <summary>
    /// Settings model for DOM configuration including grid size, fonts, and visual settings.
    /// Now shared so both NT8 engine and client can coordinate behavior.
    /// </summary>
    public class DomSettings : INotifyPropertyChanged
    {
        // Grid Configuration
        private int _maxVisibleRows = 1000;
        private int _centerPriceOffset = 500; // rows above center (remaining below)
        private CenterMode _centerMode = CenterMode.None;

        // Display Settings
        private bool _enableVolumeProfiles = true;
        private bool _showVolumeProfileColumn = true;
        private bool _showAskProfileColumn = true;
        private bool _showBidProfileColumn = true;
        private bool _showLastTrade = true;
        private bool _showPositionHighlight = true;
        private int _refreshRateMs = 16; // ~60fps

        // Font Settings
        private int _fontSize = 11;
        private bool _fontBold = false;
        private string _fontFamily = "Consolas";

        // Column Width Settings
        private int _bidVolumeWidth = 80;
        private int _bidCountWidth = 60;
        private int _priceWidth = 100;
        private int _askCountWidth = 60;
        private int _askVolumeWidth = 80;

        // Volume Profile Colors
        private string _bidVolumeColor = "#FF4CAF50";
        private string _askVolumeColor = "#FFF44336";
        private string _neutralColor = "#FF95A5A6";

        // Background Colors
        private string _bidBackgroundColor = "#FF2E7D32";
        private string _askBackgroundColor = "#FFC62828";
        private string _normalBackgroundColor = "#FF1A252F";

        // Microstructure detector thresholds (per-instrument; volume-scale-dependent, so they
        // should be tuned for the contract — ES carries far larger size than a thin future).
        private bool _enableMicrostructureSignals = true;
        private int _spoofMinChurn = 40;
        private double _spoofMaxTradedFraction = 0.15;
        private int _icebergMinRefill = 20;
        private int _withdrawalMinSize = 60;
        private int _withdrawalRadiusTicks = 5;
        private int _aggressionMinVolume = 30;
        private double _aggressionMinImbalance = 0.6;
        private int _ofiMinImbalance = 150;
        private int _ofiRadiusTicks = 3;
        private double _bookImbalanceMinRatio = 0.6;
        private int _bookImbalanceMinSize = 50;

        // Signal reliability scoring — defines "success": a win is price reaching +PredictionTargetTicks
        // before -PredictionStopTicks (asymmetric allowed, e.g. 6 / 10). Tune these to match the move
        // you actually trade, so the meter's hit-rate describes that move.
        private int _predictionTargetTicks = 4;
        private int _predictionStopTicks = 4;
        private int _reliabilityMinSamples = 5;   // resolved samples before the strength meter lights up

        // Per-type feed visibility — mute a noisy detector without disabling detection/scoring
        // (so reliability meters stay current and re-enabling is instant).
        private bool _showSpoofingSignals = true;
        private bool _showIcebergSignals = true;
        private bool _showWithdrawalSignals = true;
        private bool _showAggressionSignals = true;
        private bool _showOfiSignals = true;
        private bool _showBookImbalanceSignals = true;
        private bool _autoHideUnreliableSignals = false; // mute any type whose hit-rate stays red

        // Auto-trade: master arm (gates everything; the kill switch), the configurable "green"
        // hit-rate gate, the limit-entry auto-cancel timeout, and per-type configs.
        private bool _autoTradeArmed = false;
        private int _reliabilityGreenPercent = 60;     // a signal type at/above this % is "green" / actionable
        private int _autoTradeEntryTimeoutSeconds = 10; // cancel an unfilled limit entry after this
        private double _autoTradeMaxToxicity = 1.0;      // suppress auto-entry when flow toxicity exceeds this (1 = off)
        private readonly AutoTradeConfig _spoofingAutoTrade = new AutoTradeConfig();
        private readonly AutoTradeConfig _icebergAutoTrade = new AutoTradeConfig();
        private readonly AutoTradeConfig _withdrawalAutoTrade = new AutoTradeConfig();
        private readonly AutoTradeConfig _aggressionAutoTrade = new AutoTradeConfig { UseMarketOrder = true };

        // Per-column configuration collection
        private ObservableCollection<ColumnConfig> _columns = new ObservableCollection<ColumnConfig>();
        private readonly Dictionary<string, ColumnConfig> _columnsByKey = new Dictionary<string, ColumnConfig>();

        public DomSettings()
        {
            ResetToDefaults();
        }

        #region Properties
        public int MaxVisibleRows { get => _maxVisibleRows; set { if (_maxVisibleRows != value && value > 0 && value <= 5000) { _maxVisibleRows = value; OnPropertyChanged(); } } }
        public int CenterPriceOffset { get => _centerPriceOffset; set { if (_centerPriceOffset != value && value > 0 && value <= _maxVisibleRows / 2) { _centerPriceOffset = value; OnPropertyChanged(); } } }
        public CenterMode CenterMode { get => _centerMode; set { if (_centerMode != value) { _centerMode = value; OnPropertyChanged(); } } }
        public bool EnableVolumeProfiles { get => _enableVolumeProfiles; set { if (_enableVolumeProfiles != value) { _enableVolumeProfiles = value; OnPropertyChanged(); } } }
        public bool ShowVolumeProfileColumn { get => _showVolumeProfileColumn; set { if (_showVolumeProfileColumn != value) { _showVolumeProfileColumn = value; OnPropertyChanged(); } } }
        public bool ShowAskProfileColumn { get => _showAskProfileColumn; set { if (_showAskProfileColumn != value) { _showAskProfileColumn = value; OnPropertyChanged(); } } }
        public bool ShowBidProfileColumn { get => _showBidProfileColumn; set { if (_showBidProfileColumn != value) { _showBidProfileColumn = value; OnPropertyChanged(); } } }
        public bool ShowLastTrade { get => _showLastTrade; set { if (_showLastTrade != value) { _showLastTrade = value; OnPropertyChanged(); } } }
        public bool ShowPositionHighlight { get => _showPositionHighlight; set { if (_showPositionHighlight != value) { _showPositionHighlight = value; OnPropertyChanged(); } } }
        public int RefreshRateMs { get => _refreshRateMs; set { if (_refreshRateMs != value && value >= 8 && value <= 1000) { _refreshRateMs = value; OnPropertyChanged(); } } }
        public int FontSize { get => _fontSize; set { if (_fontSize != value && value >= 8 && value <= 24) { _fontSize = value; OnPropertyChanged(); } } }
        public bool FontBold { get => _fontBold; set { if (_fontBold != value) { _fontBold = value; OnPropertyChanged(); } } }
        public string FontFamily { get => _fontFamily; set { if (_fontFamily != value && !string.IsNullOrEmpty(value)) { _fontFamily = value; OnPropertyChanged(); } } }
        public int BidVolumeWidth { get => _bidVolumeWidth; set { if (_bidVolumeWidth != value && value >= 40 && value <= 200) { _bidVolumeWidth = value; OnPropertyChanged(); } } }
        public int BidCountWidth { get => _bidCountWidth; set { if (_bidCountWidth != value && value >= 40 && value <= 200) { _bidCountWidth = value; OnPropertyChanged(); } } }
        public int PriceWidth { get => _priceWidth; set { if (_priceWidth != value && value >= 60 && value <= 200) { _priceWidth = value; OnPropertyChanged(); } } }
        public int AskCountWidth { get => _askCountWidth; set { if (_askCountWidth != value && value >= 40 && value <= 200) { _askCountWidth = value; OnPropertyChanged(); } } }
        public int AskVolumeWidth { get => _askVolumeWidth; set { if (_askVolumeWidth != value && value >= 40 && value <= 200) { _askVolumeWidth = value; OnPropertyChanged(); } } }
        public string BidVolumeColor { get => _bidVolumeColor; set { if (_bidVolumeColor != value && !string.IsNullOrEmpty(value)) { _bidVolumeColor = value; OnPropertyChanged(); } } }
        public string AskVolumeColor { get => _askVolumeColor; set { if (_askVolumeColor != value && !string.IsNullOrEmpty(value)) { _askVolumeColor = value; OnPropertyChanged(); } } }
        public string NeutralColor { get => _neutralColor; set { if (_neutralColor != value && !string.IsNullOrEmpty(value)) { _neutralColor = value; OnPropertyChanged(); } } }
        public string BidBackgroundColor { get => _bidBackgroundColor; set { if (_bidBackgroundColor != value && !string.IsNullOrEmpty(value)) { _bidBackgroundColor = value; OnPropertyChanged(); } } }
        public string AskBackgroundColor { get => _askBackgroundColor; set { if (_askBackgroundColor != value && !string.IsNullOrEmpty(value)) { _askBackgroundColor = value; OnPropertyChanged(); } } }
        public string NormalBackgroundColor { get => _normalBackgroundColor; set { if (_normalBackgroundColor != value && !string.IsNullOrEmpty(value)) { _normalBackgroundColor = value; OnPropertyChanged(); } } }

        public ObservableCollection<ColumnConfig> Columns { get => _columns; set { if (_columns != value) { _columns = value; OnPropertyChanged(); RebuildColumnsByKey(); } } }
        public Dictionary<string, ColumnConfig> ColumnsByKey => _columnsByKey;

        // Microstructure detection settings (per-instrument)
        public bool EnableMicrostructureSignals { get => _enableMicrostructureSignals; set { if (_enableMicrostructureSignals != value) { _enableMicrostructureSignals = value; OnPropertyChanged(); } } }
        public int SpoofMinChurn { get => _spoofMinChurn; set { if (_spoofMinChurn != value && value >= 1 && value <= 1000000) { _spoofMinChurn = value; OnPropertyChanged(); } } }
        public double SpoofMaxTradedFraction { get => _spoofMaxTradedFraction; set { if (_spoofMaxTradedFraction != value && value >= 0 && value <= 1) { _spoofMaxTradedFraction = value; OnPropertyChanged(); } } }
        public int IcebergMinRefill { get => _icebergMinRefill; set { if (_icebergMinRefill != value && value >= 1 && value <= 1000000) { _icebergMinRefill = value; OnPropertyChanged(); } } }
        public int WithdrawalMinSize { get => _withdrawalMinSize; set { if (_withdrawalMinSize != value && value >= 1 && value <= 1000000) { _withdrawalMinSize = value; OnPropertyChanged(); } } }
        public int WithdrawalRadiusTicks { get => _withdrawalRadiusTicks; set { if (_withdrawalRadiusTicks != value && value >= 1 && value <= 256) { _withdrawalRadiusTicks = value; OnPropertyChanged(); } } }
        public int AggressionMinVolume { get => _aggressionMinVolume; set { if (_aggressionMinVolume != value && value >= 1 && value <= 1000000) { _aggressionMinVolume = value; OnPropertyChanged(); } } }
        public double AggressionMinImbalance { get => _aggressionMinImbalance; set { if (_aggressionMinImbalance != value && value >= 0 && value <= 1) { _aggressionMinImbalance = value; OnPropertyChanged(); } } }
        public int OfiMinImbalance { get => _ofiMinImbalance; set { if (_ofiMinImbalance != value && value >= 1 && value <= 1000000) { _ofiMinImbalance = value; OnPropertyChanged(); } } }
        public int OfiRadiusTicks { get => _ofiRadiusTicks; set { if (_ofiRadiusTicks != value && value >= 1 && value <= 256) { _ofiRadiusTicks = value; OnPropertyChanged(); } } }
        public double BookImbalanceMinRatio { get => _bookImbalanceMinRatio; set { if (_bookImbalanceMinRatio != value && value >= 0 && value <= 1) { _bookImbalanceMinRatio = value; OnPropertyChanged(); } } }
        public int BookImbalanceMinSize { get => _bookImbalanceMinSize; set { if (_bookImbalanceMinSize != value && value >= 1 && value <= 1000000) { _bookImbalanceMinSize = value; OnPropertyChanged(); } } }
        public int PredictionTargetTicks { get => _predictionTargetTicks; set { if (_predictionTargetTicks != value && value >= 1 && value <= 1000) { _predictionTargetTicks = value; OnPropertyChanged(); } } }
        public int PredictionStopTicks { get => _predictionStopTicks; set { if (_predictionStopTicks != value && value >= 1 && value <= 1000) { _predictionStopTicks = value; OnPropertyChanged(); } } }
        public int ReliabilityMinSamples { get => _reliabilityMinSamples; set { if (_reliabilityMinSamples != value && value >= 1 && value <= 1000) { _reliabilityMinSamples = value; OnPropertyChanged(); } } }
        public bool ShowSpoofingSignals { get => _showSpoofingSignals; set { if (_showSpoofingSignals != value) { _showSpoofingSignals = value; OnPropertyChanged(); } } }
        public bool ShowIcebergSignals { get => _showIcebergSignals; set { if (_showIcebergSignals != value) { _showIcebergSignals = value; OnPropertyChanged(); } } }
        public bool ShowWithdrawalSignals { get => _showWithdrawalSignals; set { if (_showWithdrawalSignals != value) { _showWithdrawalSignals = value; OnPropertyChanged(); } } }
        public bool ShowAggressionSignals { get => _showAggressionSignals; set { if (_showAggressionSignals != value) { _showAggressionSignals = value; OnPropertyChanged(); } } }
        public bool ShowOfiSignals { get => _showOfiSignals; set { if (_showOfiSignals != value) { _showOfiSignals = value; OnPropertyChanged(); } } }
        public bool ShowBookImbalanceSignals { get => _showBookImbalanceSignals; set { if (_showBookImbalanceSignals != value) { _showBookImbalanceSignals = value; OnPropertyChanged(); } } }
        public bool AutoHideUnreliableSignals { get => _autoHideUnreliableSignals; set { if (_autoHideUnreliableSignals != value) { _autoHideUnreliableSignals = value; OnPropertyChanged(); } } }

        public bool AutoTradeArmed { get => _autoTradeArmed; set { if (_autoTradeArmed != value) { _autoTradeArmed = value; OnPropertyChanged(); } } }
        public int ReliabilityGreenPercent { get => _reliabilityGreenPercent; set { if (_reliabilityGreenPercent != value && value >= 50 && value <= 95) { _reliabilityGreenPercent = value; OnPropertyChanged(); } } }
        public int AutoTradeEntryTimeoutSeconds { get => _autoTradeEntryTimeoutSeconds; set { if (_autoTradeEntryTimeoutSeconds != value && value >= 1 && value <= 120) { _autoTradeEntryTimeoutSeconds = value; OnPropertyChanged(); } } }
        public double AutoTradeMaxToxicity { get => _autoTradeMaxToxicity; set { if (_autoTradeMaxToxicity != value && value >= 0 && value <= 1) { _autoTradeMaxToxicity = value; OnPropertyChanged(); } } }
        public AutoTradeConfig SpoofingAutoTrade => _spoofingAutoTrade;
        public AutoTradeConfig IcebergAutoTrade => _icebergAutoTrade;
        public AutoTradeConfig WithdrawalAutoTrade => _withdrawalAutoTrade;
        public AutoTradeConfig AggressionAutoTrade => _aggressionAutoTrade;
        #endregion

        public void ResetToDefaults()
        {
            MaxVisibleRows = 1000;
            CenterPriceOffset = 500;
            CenterMode = CenterMode.Continuous;
            EnableVolumeProfiles = true;
            ShowVolumeProfileColumn = true;
            ShowAskProfileColumn = true;
            ShowBidProfileColumn = true;
            ShowLastTrade = true;
            ShowPositionHighlight = true;
            RefreshRateMs = 16;
            FontSize = 11;
            FontBold = false;
            FontFamily = "Consolas";
            BidVolumeWidth = 80;
            BidCountWidth = 60;
            PriceWidth = 100;
            AskCountWidth = 60;
            AskVolumeWidth = 80;
            BidVolumeColor = "#FF4CAF50";
            AskVolumeColor = "#FFF44336";
            NeutralColor = "#FF95A5A6";
            BidBackgroundColor = "#FF2E7D32";
            AskBackgroundColor = "#FFC62828";
            NormalBackgroundColor = "#FF1A252F";

            EnableMicrostructureSignals = true;
            SpoofMinChurn = 40;
            SpoofMaxTradedFraction = 0.15;
            IcebergMinRefill = 20;
            WithdrawalMinSize = 60;
            WithdrawalRadiusTicks = 5;
            AggressionMinVolume = 30;
            AggressionMinImbalance = 0.6;
            OfiMinImbalance = 150;
            OfiRadiusTicks = 3;
            BookImbalanceMinRatio = 0.6;
            BookImbalanceMinSize = 50;
            PredictionTargetTicks = 4;
            PredictionStopTicks = 4;
            ReliabilityMinSamples = 5;
            ShowSpoofingSignals = true;
            ShowIcebergSignals = true;
            ShowWithdrawalSignals = true;
            ShowAggressionSignals = true;
            ShowOfiSignals = true;
            ShowBookImbalanceSignals = true;
            AutoHideUnreliableSignals = false;

            AutoTradeArmed = false;
            ReliabilityGreenPercent = 60;
            AutoTradeEntryTimeoutSeconds = 10;
            AutoTradeMaxToxicity = 1.0;

            Columns = new ObservableCollection<ColumnConfig>
            {
                new ColumnConfig { Key = "Observations", DisplayName = "Obs", Width = 30, FontSize = 8 },
                new ColumnConfig { Key = "BidOrders", DisplayName = "Bid Ord", Width = 35, FontSize = 8 },
                new ColumnConfig { Key = "AskOrders", DisplayName = "Ask Ord", Width = 35, FontSize = 8 },
                new ColumnConfig { Key = "OpenPositionPnL", DisplayName = "P&L", Width = 45, FontSize = 8 },
                new ColumnConfig { Key = "VolumeProfile", DisplayName = "Vol Prof", Width = 50, FontSize = 8 },
                new ColumnConfig { Key = "Price", DisplayName = "Price", Width = 70, FontSize = 9, IsBold = true },
                new ColumnConfig { Key = "BidSnapshot", DisplayName = "Bid Snap", Width = 50, FontSize = 8 },
                new ColumnConfig { Key = "BidDepth", DisplayName = "Bid Dep", Width = 50, FontSize = 8 },
                new ColumnConfig { Key = "LastTradeAtBid", DisplayName = "LTB", Width = 40, FontSize = 8 },
                new ColumnConfig { Key = "LastTradeAtAsk", DisplayName = "LTA", Width = 40, FontSize = 8 },
                new ColumnConfig { Key = "AskDepth", DisplayName = "Ask Dep", Width = 50, FontSize = 8 },
                new ColumnConfig { Key = "AskSnapshot", DisplayName = "Ask Snap", Width = 50, FontSize = 8 },
                new ColumnConfig { Key = "AskProfile", DisplayName = "Ask Prof", Width = 45, FontSize = 8 },
                new ColumnConfig { Key = "BidProfile", DisplayName = "Bid Prof", Width = 45, FontSize = 8 },
                new ColumnConfig { Key = "Reserve", DisplayName = "Rsrv", Width = 30, FontSize = 8 }
            };

            RebuildColumnsByKey();
        }

        private void RebuildColumnsByKey()
        {
            _columnsByKey.Clear();
            foreach (var c in _columns)
            {
                if (!string.IsNullOrEmpty(c.Key))
                    _columnsByKey[c.Key] = c;
            }
            OnPropertyChanged(nameof(ColumnsByKey));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
