using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows.Input;
using BookFlow.Shared.Contracts;
using BookFlow.App.Interfaces;
using BookFlow.App.Models;
using System.Linq;
using System.Collections.Generic;
using DevExpress.Xpf.Grid;
using System.Windows.Data; // added for DeferRefresh

namespace BookFlow.App.ViewModels
{
    /// <summary>
    /// ViewModel for a DOM window that displays market data for a single instrument.
    /// Uses direct connection to DomEngine with intelligent UI updates.
    /// </summary>
    public class DomViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly IDomEngine _domEngine;
        private readonly ITradingService? _tradingService;
        private readonly DispatcherTimer _uiUpdateTimer;
        private volatile bool _disposed = false;
        
        // Logging integration - simple on/off
        public LogWindowViewModel? LogViewModel { get; set; }

        private string _instrumentName = "";
        private decimal? _bestBid;
        private decimal? _bestAsk;
        private decimal? _lastPrice;
        private long _lastVolume;
        private decimal? _spread;
        private int _position;
        private decimal _averagePrice;
        private decimal _unrealizedPnL;
        private decimal _realizedPnL;
        private string _positionText = "Flat";
        private string _pnlText = "$0.00";
        
        // Authoritative best bid/ask values from DomEngine - used for top-of-book highlighting
        private decimal? _authoritativeBestBid;
        private decimal? _authoritativeBestAsk;

        // UI pulse to drive time-based binding refresh (e.g., recent trade highlight decay)
        private long _uiPulseTicks;
        public long UiPulseTicks
        {
            get => _uiPulseTicks;
            private set
            {
                if (_uiPulseTicks != value)
                {
                    _uiPulseTicks = value;
                    OnPropertyChanged();
                }
            }
        }

        // Constant point value (TODO: pull from instrument metadata)
        private const decimal DefaultPointValue = 50m;

        public ObservableCollection<DomRowData> DomRows { get; }
        
        // Alias for backward compatibility
        public ObservableCollection<DomRowData> PriceLevels => DomRows;
        
        // Trading service access for diagnostics
        public ITradingService? TradingService => _tradingService;

        private bool _initialCenterDone = false;

        public DomViewModel(IDomEngine domEngine, ITradingService? tradingService = null)
        {
            _domEngine = domEngine ?? throw new ArgumentNullException(nameof(domEngine));
            _tradingService = tradingService;
            _instrumentName = domEngine.InstrumentName;

            if (_tradingService != null)
            {
                _tradingService.OrderBookChanged += OnOrderBookChanged;
                _tradingService.PortfolioChanged += OnPortfolioChanged;
                // Force initial sync so Position/PnL not left at Flat
                OnPortfolioChanged();
            }

            DomRows = new ObservableCollection<DomRowData>();

            // Subscribe to engine updates
            _domEngine.LadderUpdates.Subscribe(OnLadderUpdate);
            _domEngine.ConnectionStatusChanged += OnConnectionStatusChanged;

            // Set up UI update timer for smooth 60fps updates
            _uiUpdateTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(16) // ~60fps
            };
            _uiUpdateTimer.Tick += OnUiUpdateTick;
            _uiUpdateTimer.Start();

            // Commands: always enabled; service will auto-connect/validate
            FlattenCommand = new RelayCommand(async () => await FlattenPositionAsync());
            CancelAllOrdersCommand = new RelayCommand(async () => await CancelAllOrdersAsync());
        }

        private void OnOrderBookChanged()
        {
            OnPropertyChanged(nameof(ActiveOrdersCount));
            OnPropertyChanged(nameof(WorkingOrders));
            // Update ladder annotations for working orders
            App.Current?.Dispatcher?.BeginInvoke(UpdateOrderAnnotations, DispatcherPriority.Background);
        }

        private void OnPortfolioChanged()
        {
            if (_tradingService == null) return;

            App.Current.Dispatcher.BeginInvoke(() =>
            {
                var snapshot = _tradingService.GetPositionSnapshot(_instrumentName);
                Position = snapshot.Quantity;
                _averagePrice = snapshot.AveragePrice;

                // Recalculate UnrealizedPnL locally using latest last price to ensure responsiveness
                if (LastPrice.HasValue && Position != 0 && _averagePrice != 0)
                {
                    UnrealizedPnL = (LastPrice.Value - _averagePrice) * Position * DefaultPointValue;
                }
                else
                {
                    UnrealizedPnL = snapshot.UnrealizedPnL; // fallback
                }
                RealizedPnL = snapshot.RealizedPnL;
                UpdatePositionAnnotations();
            });
        }

        #region Properties

        public string InstrumentName
        {
            get => _instrumentName;
            private set
            {
                if (_instrumentName != value)
                {
                    _instrumentName = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(WindowTitle));
                }
            }
        }

        public string WindowTitle => $"DOM - {InstrumentName}";

        public decimal? BestBid
        {
            get => _bestBid;
            private set
            {
                if (_bestBid != value)
                {
                    _bestBid = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(BestBidText));
                }
            }
        }

        public decimal? BestAsk
        {
            get => _bestAsk;
            private set
            {
                if (_bestAsk != value)
                {
                    _bestAsk = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(BestAskText));
                }
            }
        }

        public decimal? LastPrice
        {
            get => _lastPrice;
            private set
            {
                if (_lastPrice != value)
                {
                    _lastPrice = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LastPriceText));

                    // Recalculate unrealized PnL on price change for responsiveness
                    if (value.HasValue && _averagePrice != 0 && Position != 0)
                    {
                        UnrealizedPnL = (value.Value - _averagePrice) * Position * DefaultPointValue;
                        UpdatePositionAnnotations();
                    }
                }
            }
        }

        public long LastVolume
        {
            get => _lastVolume;
            private set
            {
                if (_lastVolume != value)
                {
                    _lastVolume = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LastVolumeText));
                }
            }
        }

        public decimal? Spread
        {
            get => _spread;
            private set
            {
                if (_spread != value)
                {
                    _spread = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SpreadText));
                }
            }
        }

        public int Position
        {
            get => _position;
            private set
            {
                if (_position != value)
                {
                    _position = value;
                    OnPropertyChanged();
                    UpdatePositionText();
                }
            }
        }

        public decimal UnrealizedPnL
        {
            get => _unrealizedPnL;
            private set
            {
                if (_unrealizedPnL != value)
                {
                    _unrealizedPnL = value;
                    OnPropertyChanged();
                    UpdatePnLText();
                }
            }
        }

        public decimal RealizedPnL
        {
            get => _realizedPnL;
            private set
            {
                if (_realizedPnL != value)
                {
                    _realizedPnL = value;
                    OnPropertyChanged();
                    UpdatePnLText();
                }
            }
        }

        public string PositionText
        {
            get => _positionText;
            private set
            {
                if (_positionText != value)
                {
                    _positionText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string PnLText
        {
            get => _pnlText;
            private set
            {
                if (_pnlText != value)
                {
                    _pnlText = value;
                    OnPropertyChanged();
                }
            }
        }

        public decimal PnL => UnrealizedPnL + RealizedPnL;
        public int ActiveOrdersCount => _tradingService?.WorkingOrders.Count ?? 0;
        public ReadOnlyObservableCollection<WorkingOrderMessage>? WorkingOrders => _tradingService?.WorkingOrders;
        public DomSettings Settings => _domEngine.Settings;

        // Display text properties
        public string BestBidText => BestBid?.ToString(PriceFormatString) ?? "--";
        public string BestAskText => BestAsk?.ToString(PriceFormatString) ?? "--";
        public string LastPriceText => LastPrice?.ToString(PriceFormatString) ?? "--";
        public string LastVolumeText => LastVolume.ToString();
        public string SpreadText => Spread?.ToString(PriceFormatString) ?? "--";
        
        // Properties for volume profile calculations and bar normalization
        public long MaxVolumeProfile => DomRows.Any() ? DomRows.Max(r => r.VolumeProfile) : 1;
        public long MaxBidProfile => DomRows.Any() ? DomRows.Max(r => r.BidProfile) : 1;
        public long MaxAskProfile => DomRows.Any() ? DomRows.Max(r => r.AskProfile) : 1;
        
        // Dynamic price formatting based on instrument precision
        public string PriceFormatString => $"F{_domEngine.PriceDecimalPlaces}";
        public int PriceDecimalPlaces => _domEngine.PriceDecimalPlaces;


        #endregion

        #region Event Handlers

        private void OnLadderUpdate(LadderUpdate update)
        {
            if (LogViewModel?.IsLoggingEnabled == true)
            {
                LogViewModel.LogDomUpdate(InstrumentName, "L2_UPDATE",
                    $"Bid:{update.BestBid?.ToString(PriceFormatString) ?? "--"} Ask:{update.BestAsk?.ToString(PriceFormatString) ?? "--"} Levels:{update.VisibleLevels?.Count ?? 0} Seq:{update.SequenceNumber}");
                if ((update.VisibleLevels?.Count ?? 0) == 0)
                    LogViewModel.LogDomUpdate(InstrumentName, "L2_UPDATE", "VisibleLevels=0 - waiting for book build");
            }
            
            // Update market data (these are simple property updates, can be done on data thread)
            BestBid = update.BestBid;
            BestAsk = update.BestAsk;
            LastPrice = update.LastPrice; // triggers UnrealizedPnL recalculation
            LastVolume = update.LastVolume;
            Spread = update.Spread;
            
            // Store authoritative best bid/ask for top-of-book highlighting synchronization
            _authoritativeBestBid = update.BestBid;
            _authoritativeBestAsk = update.BestAsk;

            if (App.Current?.Dispatcher?.CheckAccess() == false)
            {
                App.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (update.VisibleLevels != null)
                        UpdatePriceLevelsOptimized(update.VisibleLevels);
                    UpdateOrderAnnotations();
                    UpdatePositionAnnotations();
                    // Perform one-time initial center after we have rows
                    TryOneTimeInitialCenter();
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            else
            {
                if (update.VisibleLevels != null)
                    UpdatePriceLevelsOptimized(update.VisibleLevels);
                UpdateOrderAnnotations();
                UpdatePositionAnnotations();
                // Perform one-time initial center after we have rows
                TryOneTimeInitialCenter();
            }
        }

        // Ensures the first time we have data and rows, we center on top-of-book in loose mode
        private void TryOneTimeInitialCenter()
        {
            if (_initialCenterDone || Settings.CenterMode != CenterMode.None)
                return;

            if (DomRows.Count == 0) return;

            var bid = BestBid.GetValueOrDefault();
            var ask = BestAsk.GetValueOrDefault();
            var last = LastPrice.GetValueOrDefault();
            bool haveMid = BestBid.HasValue && BestAsk.HasValue && bid > 0 && ask > 0;
            bool haveSide = (BestBid.HasValue && bid > 0) || (BestAsk.HasValue && ask > 0) || (LastPrice.HasValue && last > 0);
            if (haveMid || haveSide)
            {
                // Defer centering until after layout/render so rows are realized
                System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
                    new Action(() => CenterViewOnCurrentMarket()),
                    System.Windows.Threading.DispatcherPriority.Render);
                _initialCenterDone = true;
            }
        }

        private void OnConnectionStatusChanged(object? sender, bool isConnected)
        {
            // Log connection status changes only if logging is enabled
            if (LogViewModel?.IsLoggingEnabled == true)
            {
                LogViewModel.LogEngineEvent("CONNECTION", 
                    $"{InstrumentName} {(isConnected ? "Connected" : "Disconnected")}");
            }
            
            // Handle connection status changes
            App.Current?.Dispatcher?.BeginInvoke(() =>
            {
                OnPropertyChanged(nameof(WindowTitle));
            });
        }

        private void OnUiUpdateTick(object? sender, EventArgs e)
        {
            // Periodic UI pulse to refresh time-sensitive bindings (e.g., recent trade highlight decay)
            UiPulseTicks = DateTime.UtcNow.Ticks;
        }

        #endregion

        #region Private Methods

        private void UpdatePriceLevelsOptimized(System.Collections.Generic.List<PriceLevel> levels)
        {
            // Debug logging only if logging is enabled to improve performance
            if (LogViewModel?.IsLoggingEnabled == true)
            {
                System.Diagnostics.Debug.WriteLine($"[DomVM:{InstrumentName}] Updating ladder with {levels?.Count ?? 0} levels");
            }

            if (levels == null || levels.Count == 0)
            {
                // Don't clear - this could be just a sparse update with no active levels
                // The full 1000-line ladder should remain with empty levels
                if (LogViewModel?.IsLoggingEnabled == true)
                {
                    System.Diagnostics.Debug.WriteLine($"[DomVM:{InstrumentName}] No levels provided, maintaining current ladder");
                }
                return;
            }

            // If this is the first update or we need to rebuild the full ladder
            if (DomRows.Count == 0 || levels.Count > 50) // Likely full ladder update
            {
                if (LogViewModel?.IsLoggingEnabled == true)
                {
                    System.Diagnostics.Debug.WriteLine($"[DomVM:{InstrumentName}] Building full 1000-line ladder");
                }
                BuildFullLadder(levels);
            }
            else
            {
                // Sparse update - just update active levels in existing ladder
                UpdateActiveLevelsInLadder(levels);
            }
        }

        private void BuildFullLadder(System.Collections.Generic.List<PriceLevel> levels)
        {
            if (levels == null || levels.Count == 0)
            {
                if (LogViewModel?.IsLoggingEnabled == true)
                    LogViewModel.LogDomUpdate(InstrumentName, "LADDER", "BuildFullLadder called with 0 levels");
                return;
            }
            
            // IMPORTANT: Don't clear DomRows - preserve existing DomRowData objects to maintain cumulative profile data!
            // Instead, reuse existing rows and only create new ones for new price levels
            
            // Create a dictionary of existing rows by price for fast lookup
            var existingRowsByPrice = DomRows.ToDictionary(row => row.Price, row => row);
            var newRows = new List<DomRowData>();
            
            // Use the authoritative best bid/ask values from DomEngine instead of recalculating
            // This ensures synchronization between header display and DOM grid highlighting
            decimal? bestBidPrice = _authoritativeBestBid;
            decimal? bestAskPrice = _authoritativeBestAsk;
            
            if (LogViewModel?.IsLoggingEnabled == true)
            {
                System.Diagnostics.Debug.WriteLine($"[DomVM:{InstrumentName}] Using authoritative values: Bid={bestBidPrice?.ToString("F2") ?? "None"}, Ask={bestAskPrice?.ToString("F2") ?? "None"}");
            }
            
            // First, clear all existing top-of-book flags to prevent multiple orange cells
            foreach (var existingRow in existingRowsByPrice.Values)
            {
                existingRow.IsTopBid = false;
                existingRow.IsTopAsk = false;
            }
            
            // Process each level - reuse existing DomRowData or create new ones
            foreach (var level in levels)
            {
                DomRowData domRow;
                
                if (existingRowsByPrice.TryGetValue(level.Price, out var existingRow))
                {
                    // Reuse existing row - this preserves cumulative profile data!
                    domRow = existingRow;
                    domRow.UpdateFromPriceLevel(level); // Update with new market data
                }
                else
                {
                    // Create new row for new price level
                    domRow = DomRowData.FromPriceLevel(level);
                }
                
                // Set top-of-book flags using AUTHORITATIVE values from DomEngine
                // This ensures synchronization with header bid/ask display
                domRow.IsTopBid = (bestBidPrice.HasValue && level.Price == bestBidPrice.Value && level.BidVolume > 0);
                domRow.IsTopAsk = (bestAskPrice.HasValue && level.Price == bestAskPrice.Value && level.AskVolume > 0);
                
                newRows.Add(domRow);
            }
            
            // Replace the collection with the new ordered list (avoid DeferRefresh to prevent runtime errors)
            DomRows.Clear();
            foreach (var row in newRows)
            {
                DomRows.Add(row);
            }
            
            // Notify UI about changes to volume profile calculations
            OnPropertyChanged(nameof(MaxVolumeProfile));
            OnPropertyChanged(nameof(MaxBidProfile));
            OnPropertyChanged(nameof(MaxAskProfile));
            
            if (LogViewModel?.IsLoggingEnabled == true)
            {
                System.Diagnostics.Debug.WriteLine($"[DomVM:{InstrumentName}] Built ladder with {DomRows.Count} rows. Best Bid: {bestBidPrice:F2}, Best Ask: {bestAskPrice:F2}");
            }

            // Only auto-center when mode is Continuous; respect free-float when None
            if (_domEngine.Settings.CenterMode == CenterMode.Continuous)
            {
                CenterViewOnCurrentMarket();
            }
        }

        private void UpdateActiveLevelsInLadder(System.Collections.Generic.List<PriceLevel> levels)
        {
            // Build lookup table of existing rows by price for efficient updates
            var existingLookup = new Dictionary<decimal, DomRowData>();
            foreach (var row in DomRows)
            {
                existingLookup[row.Price] = row;
            }
            
            // First, clear all existing top-of-book flags to prevent multiple orange cells
            foreach (var row in DomRows)
            {
                row.IsTopBid = false;
                row.IsTopAsk = false;
            }
            
            // Update existing rows with new data
            foreach (var newLevel in levels)
            {
                if (existingLookup.TryGetValue(newLevel.Price, out var existingRow))
                {
                    // Update existing row if data has changed
                    if (existingRow.BidVolume != newLevel.BidVolume || 
                        existingRow.AskVolume != newLevel.AskVolume ||
                        existingRow.BidCount != newLevel.BidCount ||
                        existingRow.AskCount != newLevel.AskCount)
                    {
                        existingRow.UpdateFromPriceLevel(newLevel);
                    }
                }
            }
            
            // Set top-of-book flags using AUTHORITATIVE values from DomEngine
            // This ensures synchronization with header bid/ask display
            if (_authoritativeBestBid.HasValue && _authoritativeBestBid.Value > 0)
            {
                var bestBidRow = DomRows.FirstOrDefault(r => r.Price == _authoritativeBestBid.Value && r.BidVolume > 0);
                if (bestBidRow != null) bestBidRow.IsTopBid = true;
            }
            
            if (_authoritativeBestAsk.HasValue && _authoritativeBestAsk.Value > 0)
            {
                var bestAskRow = DomRows.FirstOrDefault(r => r.Price == _authoritativeBestAsk.Value && r.AskVolume > 0);
                if (bestAskRow != null) bestAskRow.IsTopAsk = true;
            }
            
            // Notify UI about changes to volume profile calculations after sparse updates
            OnPropertyChanged(nameof(MaxVolumeProfile));
            OnPropertyChanged(nameof(MaxBidProfile));
            OnPropertyChanged(nameof(MaxAskProfile));

            // Optionally re-center when top-of-book moves outside visible area
            if (_domEngine.Settings.CenterMode == CenterMode.Continuous)
            {
                CenterViewOnCurrentMarket(nonDisruptive: true);
            }
        }
        
        // Centers the GridControl view on the current mid price (between best bid/ask)
        private void CenterViewOnCurrentMarket(bool nonDisruptive = false)
        {
            if (DomRows.Count == 0) return;

            decimal? targetPrice = null;
            var bid = BestBid.GetValueOrDefault();
            var ask = BestAsk.GetValueOrDefault();
            var last = LastPrice.GetValueOrDefault();

            if (BestBid.HasValue && BestAsk.HasValue && bid > 0 && ask > 0)
                targetPrice = (bid + ask) / 2m;
            else if (BestBid.HasValue && bid > 0)
                targetPrice = bid;
            else if (BestAsk.HasValue && ask > 0)
                targetPrice = ask;
            else if (LastPrice.HasValue && last > 0)
                targetPrice = last;

            if (!targetPrice.HasValue) return;

            var target = DomRows
                .Select((r, idx) => new { Row = r, Index = idx, Diff = Math.Abs(r.Price - targetPrice.Value) })
                .OrderBy(x => x.Diff)
                .FirstOrDefault();
            if (target == null) return;

            var window = System.Windows.Application.Current?.Windows
                .OfType<BookFlow.App.Views.DomGridWindow>()
                .FirstOrDefault(w => ReferenceEquals(w.DataContext, this));
            if (window == null) return;

            var grid = window.DomGridControl;
            var view = grid?.View as TableView;
            if (view == null) return;

            // Estimate visible rows from control height and row height (18 from RowStyle)
            var gridHeight = grid.ActualHeight;
            var estimatedRowHeight = 18.0;
            var effectiveHeight = Math.Max(0, gridHeight - 4); // account for borders/padding
            int visibleRowCount = Math.Max(1, (int)(effectiveHeight / estimatedRowHeight));

            // Only center if needed (nonDisruptive)
            if (nonDisruptive)
            {
                int topHandle = view.TopRowIndex;
                int bottomHandle = topHandle + visibleRowCount - 1;
                int targetHandle = grid.GetRowHandleByListIndex(target.Index);
                if (targetHandle >= topHandle && targetHandle <= bottomHandle)
                    return; // already visible
            }

            int centerOffset = visibleRowCount / 2;
            int desiredTopListIndex = System.Math.Max(0, target.Index - centerOffset);
            int desiredTopHandle = grid.GetRowHandleByListIndex(desiredTopListIndex);

            // Ensure the row is realized and scrolled into view, then center via TopRowIndex
            view.ScrollIntoView(DomRows[target.Index]);
            view.TopRowIndex = desiredTopHandle;
        }

        private void UpdateOrderAnnotations()
        {
            if (_tradingService?.WorkingOrders == null || DomRows.Count == 0) return;
            var orders = _tradingService.WorkingOrders.Where(o => o.Instrument == InstrumentName).ToList();
            if (orders.Count == 0)
            {
                foreach (var row in DomRows)
                {
                    if (!string.IsNullOrEmpty(row.BidOrdersInfo)) row.BidOrdersInfo = string.Empty;
                    if (!string.IsNullOrEmpty(row.AskOrdersInfo)) row.AskOrdersInfo = string.Empty;
                }
                return;
            }
            var orderGroups = orders.GroupBy(o => (Price: (decimal)o.Price, Side: o.Side));
            var lookup = orderGroups.ToDictionary(g => g.Key, g => g.ToList());
            foreach (var row in DomRows)
            {
                // Bid side (Side=1)
                if (lookup.TryGetValue((row.Price, (byte)1), out var bidOrders))
                {
                    row.BidOrdersInfo = string.Join(',', bidOrders.Select(o => $"{o.Quantity - o.FilledQuantity}/{(o.QueuePosition > 0 ? o.QueuePosition : 0)}"));
                }
                else if (!string.IsNullOrEmpty(row.BidOrdersInfo))
                {
                    row.BidOrdersInfo = string.Empty;
                }
                // Ask side (Side=2)
                if (lookup.TryGetValue((row.Price, (byte)2), out var askOrders))
                {
                    row.AskOrdersInfo = string.Join(',', askOrders.Select(o => $"{o.Quantity - o.FilledQuantity}/{(o.QueuePosition > 0 ? o.QueuePosition : 0)}"));
                }
                else if (!string.IsNullOrEmpty(row.AskOrdersInfo))
                {
                    row.AskOrdersInfo = string.Empty;
                }
            }
        }

        private void UpdatePositionAnnotations()
        {
            if (DomRows.Count == 0)
                return;

            // If flat or no average price, clear PnL and overlays for every row
            if (Position == 0 || _averagePrice == 0)
            {
                foreach (var row in DomRows)
                {
                    row.OpenPositionPnL = 0;
                    row.PositionBidMarker = 0;
                    row.PositionAskMarker = 0;
                }
                return;
            }

            // Compute hypothetical PnL for fully exiting at each row's price
            foreach (var row in DomRows)
            {
                var exitPrice = row.Price;
                // (exit - avg) * signed position * point value
                var pnl = (exitPrice - _averagePrice) * Position * DefaultPointValue;
                row.OpenPositionPnL = pnl;
                // Clear markers; will set only at entry row below
                row.PositionBidMarker = 0;
                row.PositionAskMarker = 0;
            }

            // Overlay position marker at the entry/average price row (closest match)
            var entryRow = DomRows.FirstOrDefault(r => r.Price == _averagePrice) ??
                           DomRows.OrderBy(r => Math.Abs(r.Price - _averagePrice)).FirstOrDefault();
            if (entryRow != null)
            {
                if (Position > 0)
                {
                    // Long: show +N on Bid column overlay
                    entryRow.PositionBidMarker = Position;
                }
                else
                {
                    // Short: show -N on Ask column overlay (UI prefixes '-')
                    entryRow.PositionAskMarker = System.Math.Abs(Position);
                }
            }
        }

        private void UpdatePositionText()
        {
            if (Position == 0)
            {
                PositionText = "Flat";
            }
            else if (Position > 0)
            {
                PositionText = $"Long {Position}";
            }
            else
            {
                PositionText = $"Short {Math.Abs(Position)}";
            }
        }

        private void UpdatePnLText()
        {
            var totalPnL = UnrealizedPnL + RealizedPnL;
            PnLText = totalPnL.ToString("C2");
        }

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Triggers a one-time center action without changing the persistent center mode.
        /// </summary>
        public void TriggerCenterAction()
        {
            System.Diagnostics.Debug.WriteLine("[DEBUG] TriggerCenterAction called - calling _domEngine.CenterDom()");
            _domEngine.CenterDom();
            System.Diagnostics.Debug.WriteLine("[DEBUG] Called _domEngine.CenterDom()");
        }

        /// <summary>
        /// Clears all DOM data structures including price levels, volume profiles, and position information.
        /// This provides a fresh start for the DOM display.
        /// </summary>
        public void ClearAllData()
        {
            // Clear the DOM rows collection
            DomRows.Clear();
            
            // Reset market data values
            BestBid = null;
            BestAsk = null;
            LastPrice = null;
            LastVolume = 0;
            Spread = null;
            
            // Clear authoritative values for top-of-book highlighting
            _authoritativeBestBid = null;
            _authoritativeBestAsk = null;
            
            // Reset position and P&L
            Position = 0;
            UnrealizedPnL = 0;
            RealizedPnL = 0;
            
            // Notify UI of all property changes
            OnPropertyChanged(nameof(BestBidText));
            OnPropertyChanged(nameof(BestAskText));
            OnPropertyChanged(nameof(LastPriceText));
            OnPropertyChanged(nameof(LastVolumeText));
            OnPropertyChanged(nameof(SpreadText));
            OnPropertyChanged(nameof(PnL));
            OnPropertyChanged(nameof(MaxVolumeProfile));
            OnPropertyChanged(nameof(MaxBidProfile));
            OnPropertyChanged(nameof(MaxAskProfile));
            OnPropertyChanged(nameof(WindowTitle));
            
            // Clear the underlying DOM engine data
            _domEngine.ClearAllData();
        }

        /// <summary>
        /// Cancel all working orders at the specified price level.
        /// </summary>
        public async Task CancelOrdersAtPriceAsync(decimal price)
        {
            if (_tradingService == null)
            {
                LogViewModel?.LogEngineEvent("TRADE-ERROR", "Trading service not available");
                throw new InvalidOperationException("Trading service is not configured");
            }

            LogViewModel?.LogEngineEvent("TRADE-CANCEL", $"Cancelling orders at {price:F2}");

            try
            {
                var result = await _tradingService.CancelOrdersAtPriceAsync(_instrumentName, price);
                if (result.Status == OrderCommand.OrderStatus.Cancelled)
                {
                    LogViewModel?.LogEngineEvent("TRADE-SUCCESS", $"Orders cancelled at {price:F2}: {result.Message}");
                }
                else
                {
                    LogViewModel?.LogEngineEvent("TRADE-WARNING", $"Cancel result: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                LogViewModel?.LogEngineEvent("TRADE-ERROR", $"Cancel orders exception: {ex.Message}");
                throw;
            }
        }

        #endregion

        #region Trading Methods

        /// <summary>
        /// Submit a buy limit (or market if crossing) without hard pre-check; service will auto-connect and server will validate.
        /// </summary>
        public async Task SubmitBuyLimitOrderAsync(decimal price)
        {
            if (_tradingService == null)
            {
                LogViewModel?.LogEngineEvent("TRADE-ERROR", "Trading service not available");
                throw new InvalidOperationException("Trading service is not configured");
            }

            var orderCommand = new OrderCommand();

            if (BestAsk.HasValue && price >= BestAsk.Value)
            {
                orderCommand.Action = OrderCommand.OrderAction.BuyMarket;
                orderCommand.Quantity = 1;
                orderCommand.ClientOrderId = GenerateOrderId();
                orderCommand.Timestamp = DateTime.UtcNow;
                LogViewModel?.LogEngineEvent("TRADE-SUBMIT", $"BUY MARKET (from limit) {orderCommand.Quantity} @ {price:F2} (ID: {orderCommand.ClientOrderId})");
            }
            else
            {
                orderCommand.Action = OrderCommand.OrderAction.BuyLimit;
                orderCommand.LimitPrice = (double)price;
                orderCommand.Quantity = 1;
                orderCommand.ClientOrderId = GenerateOrderId();
                orderCommand.Timestamp = DateTime.UtcNow;
                LogViewModel?.LogEngineEvent("TRADE-SUBMIT", $"BUY LIMIT {orderCommand.Quantity} @ {price:F2} (ID: {orderCommand.ClientOrderId})");
            }

            try
            {
                var result = await _tradingService.SubmitOrderAsync(_instrumentName, orderCommand);
                if (result.Status == OrderCommand.OrderStatus.Submitted || result.Status == OrderCommand.OrderStatus.Pending)
                {
                    LogViewModel?.LogEngineEvent("TRADE-SUCCESS", $"Buy order submitted: {result.Message}");
                }
                else
                {
                    // Log only; do not throw on server informational responses (e.g., cancel messages)
                    LogViewModel?.LogEngineEvent("TRADE-ERROR", $"Buy order failed: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                LogViewModel?.LogEngineEvent("TRADE-ERROR", $"Buy order exception: {ex.Message}");
                // Swallow to prevent UI MessageBox from cancel path side-effects
            }
        }

        /// <summary>
        /// Submit a sell limit (or market if crossing) without hard pre-check.
        /// </summary>
        public async Task SubmitSellLimitOrderAsync(decimal price)
        {
            if (_tradingService == null)
            {
                LogViewModel?.LogEngineEvent("TRADE-ERROR", "Trading service not available");
                throw new InvalidOperationException("Trading service is not configured");
            }

            var orderCommand = new OrderCommand();

            if (BestBid.HasValue && price <= BestBid.Value)
            {
                orderCommand.Action = OrderCommand.OrderAction.SellMarket;
                orderCommand.Quantity = 1;
                orderCommand.ClientOrderId = GenerateOrderId();
                orderCommand.Timestamp = DateTime.UtcNow;
                LogViewModel?.LogEngineEvent("TRADE-SUBMIT", $"SELL MARKET (from limit) {orderCommand.Quantity} @ {price:F2} (ID: {orderCommand.ClientOrderId})");
            }
            else
            {
                orderCommand.Action = OrderCommand.OrderAction.SellLimit;
                orderCommand.LimitPrice = (double)price;
                orderCommand.Quantity = 1;
                orderCommand.ClientOrderId = GenerateOrderId();
                orderCommand.Timestamp = DateTime.UtcNow;
                LogViewModel?.LogEngineEvent("TRADE-SUBMIT", $"SELL LIMIT {orderCommand.Quantity} @ {price:F2} (ID: {orderCommand.ClientOrderId})");
            }

            try
            {
                var result = await _tradingService.SubmitOrderAsync(_instrumentName, orderCommand);
                if (result.Status == OrderCommand.OrderStatus.Submitted || result.Status == OrderCommand.OrderStatus.Pending)
                {
                    LogViewModel?.LogEngineEvent("TRADE-SUCCESS", $"Sell order submitted: {result.Message}");
                }
                else
                {
                    LogViewModel?.LogEngineEvent("TRADE-ERROR", $"Sell order failed: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                LogViewModel?.LogEngineEvent("TRADE-ERROR", $"Sell order exception: {ex.Message}");
            }
        }

        public async Task CancelAllOrdersAsync()
        {
            if (_tradingService == null)
            {
                LogViewModel?.LogEngineEvent("TRADE-ERROR", "Cannot cancel all: Trading service not available.");
                return;
            }

            LogViewModel?.LogEngineEvent("TRADE-ACTION", $"CANCEL ALL orders for {_instrumentName}");

            try
            {
                await _tradingService.CancelAllOrdersAsync(_instrumentName);
            }
            catch (Exception ex)
            {
                LogViewModel?.LogEngineEvent("TRADE-ERROR", $"Cancel all failed: {ex.Message}");
            }
        }

        public async Task FlattenPositionAsync()
        {
            if (_tradingService == null)
            {
                LogViewModel?.LogEngineEvent("TRADE-ERROR", "Cannot flatten: Trading service not available.");
                return;
            }

            LogViewModel?.LogEngineEvent("TRADE-ACTION", $"FLATTEN position for {_instrumentName}");

            try
            {
                await _tradingService.FlattenPositionAsync(_instrumentName);
            }
            catch (Exception ex)
            {
                LogViewModel?.LogEngineEvent("TRADE-ERROR", $"Flatten failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Generate a unique client order ID for tracking orders.
        /// </summary>
        private string GenerateOrderId()
        {
            var timestamp = DateTime.UtcNow.ToString("HHmmss");
            var random = new Random().Next(100, 999);
            return $"BF-{_instrumentName}-{timestamp}-{random}";
        }

        /// <summary>
        /// Gets whether trading is currently enabled.
        /// </summary>
        public bool IsTradingEnabled => _tradingService?.IsTradingEnabled ?? false;

        public ICommand FlattenCommand { get; }
        public ICommand CancelAllOrdersCommand { get; }

        #endregion // Trading Methods

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            _uiUpdateTimer?.Stop();
            _domEngine?.Dispose();
        }
    }

    public class PriceLevelViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute == null || _canExecute();
        public void Execute(object? parameter) => _execute();
    }
}