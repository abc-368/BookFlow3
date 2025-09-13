using System.ComponentModel;
using System.Runtime.CompilerServices;

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
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
