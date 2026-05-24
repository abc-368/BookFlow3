using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using BookFlow.App.ViewModels;
using BookFlow.Shared.Contracts; // for CenterMode
using BookFlow.App.Models; // for DomRowData

namespace BookFlow.App.Views
{
    /// <summary>
    /// Interaction logic for DomGridWindow.xaml
    /// High-performance DOM window with direct data connection.
    /// </summary>
    public partial class DomGridWindow : Window
    {
        public DomViewModel? ViewModel { get; }
        private LogWindow? _logWindow;
        private LogWindowViewModel? _logViewModel;
        private DateTime _lastRightClickUtc = DateTime.MinValue;

        public DomGridWindow()
        {
            InitializeComponent();
        }

        public DomGridWindow(DomViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = viewModel;
            
            // Start in loose mode (no auto center)
            if (ViewModel?.Settings != null)
            {
                ViewModel.Settings.CenterMode = CenterMode.None;
            }
            _logViewModel = null;
            UpdateCenterButtonAppearance();
        }

        protected override void OnClosed(EventArgs e)
        {
            _logWindow?.Close();
            _logViewModel?.Dispose();
            ViewModel?.Dispose();
            base.OnClosed(e);
        }
        
        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null)
            {
                System.Windows.MessageBox.Show("No ViewModel available.", "DOM Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var win = new GridSettingsWindow(ViewModel)
            {
                Owner = this
            };
            win.ShowDialog();
        }
        
        private void CenterButton_MouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[DEBUG] Right-click detected - triggering center action");
            
            if (ViewModel == null) 
            {
                System.Windows.MessageBox.Show("ViewModel is null!", "Debug Error", MessageBoxButton.OK);
                return;
            }
            
            // Instead of generating new data, find and focus on the best bid/ask rows in current data
            ScrollToTopOfBook();
            
            // Prevent context menu
            e.Handled = true;
        }

        private void CenterToggle_Checked(object sender, RoutedEventArgs e)
        {
            // Pressed => free-float (no auto center)
            if (ViewModel?.Settings == null) return;
            ViewModel.Settings.CenterMode = CenterMode.None;
            UpdateCenterButtonAppearance();
        }

        private void CenterToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            // Unpressed => auto-center continuously
            if (ViewModel?.Settings == null) return;
            ViewModel.Settings.CenterMode = CenterMode.Continuous;
            UpdateCenterButtonAppearance();
        }

        private void BottomCenterButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            // Use same logic as right-click on the top-left center button
            ScrollToTopOfBook();
            // Re-post once after layout to ensure it's centered visually
            Dispatcher.BeginInvoke(new Action(ScrollToTopOfBook), System.Windows.Threading.DispatcherPriority.Render);
        }
        
        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;

            // Clear all DOM data structures immediately (no confirmation dialog)
            ViewModel.ClearAllData();
            System.Diagnostics.Debug.WriteLine("[Clear] DOM data structures cleared");
        }
        
        private void ScrollToTopOfBook()
        {
            if (ViewModel?.DomRows == null || ViewModel.DomRows.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[DOM Center] No DOM rows available");
                return;
            }

            // Find the best bid or ask row to center on
            decimal? targetPrice = null;
            var bid = ViewModel.BestBid.GetValueOrDefault();
            var ask = ViewModel.BestAsk.GetValueOrDefault();
            var last = ViewModel.LastPrice.GetValueOrDefault();

            if (ViewModel.BestBid.HasValue && ViewModel.BestAsk.HasValue && bid > 0 && ask > 0)
                targetPrice = (bid + ask) / 2m;
            else if (ViewModel.BestBid.HasValue && bid > 0)
                targetPrice = bid;
            else if (ViewModel.BestAsk.HasValue && ask > 0)
                targetPrice = ask;
            else if (ViewModel.LastPrice.HasValue && last > 0)
                targetPrice = last;

            if (!targetPrice.HasValue)
            {
                System.Diagnostics.Debug.WriteLine("[DOM Center] No target price found");
                return;
            }
            
            System.Diagnostics.Debug.WriteLine($"[DOM Center] Looking for price {targetPrice.Value} in {ViewModel.DomRows.Count} rows");
            
            // Find the row closest to this price
            DomRowData? targetRow = null;
            decimal closestDiff = decimal.MaxValue;
            
            foreach (var row in ViewModel.DomRows)
            {
                var diff = Math.Abs(row.Price - targetPrice.Value);
                if (diff < closestDiff)
                {
                    closestDiff = diff;
                    targetRow = row;
                }
            }
            
            if (targetRow != null)
            {
                System.Diagnostics.Debug.WriteLine($"[DOM Center] Found target row at price {targetRow.Price}");
                
                try
                {
                    // Try focusing on the row using DevExpress grid methods
                    var view = DomGridControl.View as DevExpress.Xpf.Grid.TableView;
                    if (view != null)
                    {
                        // Find the row index/handle in the grid by comparing prices
                        int rowIndex = -1;
                        for (int i = 0; i < ViewModel.DomRows.Count; i++)
                        {
                            if (ViewModel.DomRows[i].Price == targetRow.Price)
                            {
                                rowIndex = i;
                                break;
                            }
                        }
                        
                        System.Diagnostics.Debug.WriteLine($"[DOM Center] Found row at index {rowIndex}");
                        
                        if (rowIndex >= 0)
                        {
                            // Calculate the center of the visible area using DevExpress properties
                            int visibleRowCount = 0;
                            int currentTopRowIndex = 0;
                            
                            try 
                            {
                                // Calculate visible rows from grid height and row height
                                var gridHeight = DomGridControl.ActualHeight;
                                var estimatedRowHeight = 18.0; // From XAML row style Height="18"
                                
                                // Account for any internal padding/margins in the DevExpress grid
                                var effectiveHeight = gridHeight - 4; // Account for borders/padding
                                visibleRowCount = (int)(effectiveHeight / estimatedRowHeight);
                                currentTopRowIndex = view.TopRowIndex;
                                
                                System.Diagnostics.Debug.WriteLine($"[DOM Center] Grid height: {gridHeight:F0}px, Effective: {effectiveHeight:F0}px, Visible rows: {visibleRowCount}, Current top: {currentTopRowIndex}");
                            }
                            catch (Exception)
                            {
                                // Final fallback
                                visibleRowCount = 25; // Reasonable default
                                System.Diagnostics.Debug.WriteLine($"[DOM Center] Using fallback visible rows: {visibleRowCount}");
                            }
                            
                            // Put the target row dead-center: top row = target - half a viewport.
                            var centerOffset = visibleRowCount / 2;
                            int targetTopRowIndex = Math.Max(0, rowIndex - centerOffset);
                            System.Diagnostics.Debug.WriteLine($"[DOM Center] Target row {rowIndex}, visible rows {visibleRowCount}, centering at top row {targetTopRowIndex}");
                            
                            try 
                            {
                                // Set focused row handle first
                                view.FocusedRowHandle = rowIndex;
                                
                                // Then scroll to center the target row in the visible area
                                view.TopRowIndex = targetTopRowIndex;
                                
                                System.Diagnostics.Debug.WriteLine($"[DOM Center] Successfully centered row {rowIndex} in visible area");
                            }
                            catch (Exception ex3)
                            {
                                System.Diagnostics.Debug.WriteLine($"[DOM Center] Centering failed: {ex3.Message}");
                            }
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[DOM Center] Could not get TableView");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DOM Center] Error focusing row: {ex.Message}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[DOM Center] Could not find target row");
            }
        }
        
        private void UpdateCenterButtonAppearance()
        {
            if (ViewModel?.Settings == null) return;

            // Update visual background/border colors
            switch (ViewModel.Settings.CenterMode)
            {
                case CenterMode.None:
                    CenterButton.Background = System.Windows.Media.Brushes.Transparent;
                    CenterButton.BorderBrush = System.Windows.Media.Brushes.Gray;
                    break;

                case CenterMode.Continuous:
                    CenterButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 144, 226));
                    CenterButton.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(91, 160, 242));
                    break;

                case CenterMode.OneTime:
                    CenterButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 152, 0));
                    CenterButton.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 193, 7));
                    break;
            }

            // Sync ToggleButton check state: Checked means free-float (None)
            if (CenterButton is ToggleButton tb)
            {
                tb.IsChecked = ViewModel.Settings.CenterMode == CenterMode.None;
            }
        }
        
        private void LogsToggle_Checked(object sender, RoutedEventArgs e)
        {
            // Initialize LogViewModel on first use (lazy initialization)
            if (_logViewModel == null)
            {
                InitializeLogging();
            }

            // Enable logging and connect to ViewModel
            if (_logViewModel != null)
            {
                // Ensure queue is completely clear before enabling
                _logViewModel.ClearLogs();
                
                ConnectLoggingToViewModel(); // Connect LogViewModel to DomViewModel FIRST
                _logViewModel.IsLoggingEnabled = true; // Then enable (this starts the timer)
                _logViewModel.LogEngineEvent("LogWindow", $"Logging enabled for {ViewModel?.InstrumentName}");
            }

            if (_logWindow == null || !_logWindow.IsVisible)
            {
                _logWindow = new LogWindow(_logViewModel!);
                _logWindow.Owner = this;
                _logWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                
                // Position log window to the right of DOM window
                _logWindow.Left = this.Left + this.Width + 10;
                _logWindow.Top = this.Top;
                
                _logWindow.Closed += (s, args) =>
                {
                    LogsToggle.IsChecked = false;
                    _logWindow = null;
                };
                
                _logWindow.Show();
            }
            else
            {
                _logWindow.Activate();
            }
        }
        
        private void LogsToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            // Disable logging to stop data collection and free memory
            if (_logViewModel != null)
            {
                _logViewModel.LogEngineEvent("LogWindow", "Logging disabled by user");
                _logViewModel.IsLoggingEnabled = false;
                
                // Disconnect LogViewModel from DomViewModel to prevent any further logging
                if (ViewModel != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[DisconnectLogging] DISCONNECTING LogViewModel from {ViewModel.InstrumentName}");
                    ViewModel.LogViewModel = null;
                }
                
                // Completely destroy the LogViewModel to prevent any lingering references
                _logViewModel.Dispose();
                _logViewModel = null;
            }
            
            if (_logWindow != null)
            {
                _logWindow.Close();
                _logWindow = null;
            }
        }
        
        private void InitializeLogging()
        {
            if (_logViewModel != null || ViewModel == null) return;
            
            // Create log view model for this DOM instance - starts with logging disabled
            _logViewModel = new LogWindowViewModel();
            
            // Don't connect it to the ViewModel yet - wait until logging is actually enabled
            // This prevents any race condition where market data updates start logging
            // before we explicitly enable it
        }
        
        private void ConnectLoggingToViewModel()
        {
            if (_logViewModel != null && ViewModel != null && ViewModel.LogViewModel == null)
            {
                // Connect logging to DOM view model only when logging is enabled
                System.Diagnostics.Debug.WriteLine($"[ConnectLogging] CONNECTING LogViewModel to {ViewModel.InstrumentName}");
                ViewModel.LogViewModel = _logViewModel;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectLogging] NOT CONNECTING - LogViewModel null: {_logViewModel == null}, ViewModel null: {ViewModel == null}, Already connected: {ViewModel?.LogViewModel != null}");
            }
        }

        #region Trading Click Handlers

        /// <summary>
        /// Handle left click on Bid Depth column - Submit Buy Limit Order
        /// </summary>
        private async void BidDepth_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Suppress accidental left-click after a right-click (context operation)
            if ((DateTime.UtcNow - _lastRightClickUtc).TotalMilliseconds < 120)
            {
                e.Handled = true;
                return;
            }

            if (ViewModel is null) return;
            try
            {
                if (sender is FrameworkElement element && element.DataContext is DevExpress.Xpf.Grid.GridCellData cellData)
                {
                    if (cellData.RowData?.Row is DomRowData rowData)
                    {
                        decimal price = rowData.Price;
                        System.Diagnostics.Debug.WriteLine($"[TRADE] Left click on Bid Depth - Submitting BUY LIMIT at {price:F2}");
                        await ViewModel.SubmitBuyLimitOrderAsync(price);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TRADE ERROR] BidDepth_MouseLeftButtonDown: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle right click on Bid Depth column - Cancel orders at this price
        /// </summary>
        private async void BidDepth_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _lastRightClickUtc = DateTime.UtcNow;
            if (ViewModel is null) { e.Handled = true; return; }
            try
            {
                if (sender is FrameworkElement element && element.DataContext is DevExpress.Xpf.Grid.GridCellData cellData)
                {
                    if (cellData.RowData?.Row is DomRowData rowData)
                    {
                        decimal price = rowData.Price;
                        
                        System.Diagnostics.Debug.WriteLine($"[TRADE] Right click on Bid Depth - Cancelling orders at {price:F2}");
                        
                        // Cancel orders at this price level
                        await ViewModel.CancelOrdersAtPriceAsync(price);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TRADE ERROR] BidDepth_MouseRightButtonDown: {ex.Message}");
                System.Windows.MessageBox.Show($"Error canceling orders: {ex.Message}", "Cancel Error", 
                               MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                // Prevent any additional mouse handlers (like left) from firing after right-click
                e.Handled = true;
            }
        }

        /// <summary>
        /// Handle left click on Ask Depth column - Submit Sell Limit Order
        /// </summary>
        private async void AskDepth_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((DateTime.UtcNow - _lastRightClickUtc).TotalMilliseconds < 120)
            {
                e.Handled = true;
                return;
            }

            if (ViewModel is null) return;
            try
            {
                if (sender is FrameworkElement element && element.DataContext is DevExpress.Xpf.Grid.GridCellData cellData)
                {
                    if (cellData.RowData?.Row is DomRowData rowData)
                    {
                        decimal price = rowData.Price;
                        System.Diagnostics.Debug.WriteLine($"[TRADE] Left click on Ask Depth - Submitting SELL LIMIT at {price:F2}");
                        await ViewModel.SubmitSellLimitOrderAsync(price);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TRADE ERROR] AskDepth_MouseLeftButtonDown: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle right click on Ask Depth column - Cancel orders at this price
        /// </summary>
        private async void AskDepth_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _lastRightClickUtc = DateTime.UtcNow;
            if (ViewModel is null) { e.Handled = true; return; }
            try
            {
                if (sender is FrameworkElement element && element.DataContext is DevExpress.Xpf.Grid.GridCellData cellData)
                {
                    if (cellData.RowData?.Row is DomRowData rowData)
                    {
                        decimal price = rowData.Price;
                        
                        System.Diagnostics.Debug.WriteLine($"[TRADE] Right click on Ask Depth - Cancelling orders at {price:F2}");
                        
                        // Cancel orders at this price level
                        await ViewModel.CancelOrdersAtPriceAsync(price);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TRADE ERROR] AskDepth_MouseRightButtonDown: {ex.Message}");
                System.Windows.MessageBox.Show($"Error canceling orders: {ex.Message}", "Cancel Error", 
                               MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                e.Handled = true;
            }
        }

        private void Depth_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Pure time-based suppression to avoid left-submit right after a right-cancel
            if ((DateTime.UtcNow - _lastRightClickUtc).TotalMilliseconds < 120)
            {
                e.Handled = true;
            }
        }

        #endregion
    }
}
