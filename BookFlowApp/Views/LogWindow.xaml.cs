using System;
using System.IO;
using System.Windows;
using BookFlow.App.ViewModels;
using Microsoft.Win32;

namespace BookFlow.App.Views
{
    public partial class LogWindow : Window
    {
        public LogWindowViewModel? ViewModel { get; }
        private bool _autoScroll = true;

        public LogWindow()
        {
            InitializeComponent();
        }

        public LogWindow(LogWindowViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = viewModel;

            // Set up auto-scroll monitoring
            LogScrollViewer.ScrollChanged += OnScrollChanged;
            
            // Monitor text changes for auto-scroll
            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(LogWindowViewModel.LogText) && _autoScroll)
                {
                    Dispatcher.BeginInvoke(() => LogScrollViewer.ScrollToEnd());
                }
            };
        }

        protected override void OnClosed(EventArgs e)
        {
            ViewModel?.Dispose();
            base.OnClosed(e);
        }

        private void OnScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
        {
            // Detect if user manually scrolled away from bottom
            if (e.ExtentHeightChange == 0)
            {
                _autoScroll = Math.Abs(LogScrollViewer.VerticalOffset - LogScrollViewer.ScrollableHeight) < 1.0;
                AutoScrollCheckBox.IsChecked = _autoScroll;
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ClearLogs();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;

            try
            {
                var saveDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save Log File",
                    Filter = "Text Files (*.txt)|*.txt|Log Files (*.log)|*.log|All Files (*.*)|*.*",
                    DefaultExt = ".txt",
                    FileName = $"BookFlow_Logs_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    File.WriteAllText(saveDialog.FileName, ViewModel.LogText);
                    System.Windows.MessageBox.Show($"Log saved successfully to:\n{saveDialog.FileName}", 
                        "Log Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error saving log file:\n{ex.Message}", 
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}