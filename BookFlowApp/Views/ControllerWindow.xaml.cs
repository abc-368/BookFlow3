using System.Windows;
using BookFlow.App.ViewModels;

namespace BookFlow.App.Views
{
    /// <summary>
    /// Interaction logic for ControllerWindow.xaml
    /// The main controller window that acts as a manager (not a broker) for DOM windows.
    /// </summary>
    public partial class ControllerWindow : Window
    {
        public ControllerViewModel ViewModel { get; }
        private LogViewerWindow? _logViewer;

        public ControllerWindow()
        {
            InitializeComponent();
            ViewModel = new ControllerViewModel();
            DataContext = ViewModel;
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.ConnectAsync();
        }

        private async void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.DisconnectAsync();
        }

        private async void BtnRefreshSystem_Click(object sender, RoutedEventArgs e)
        {
            // RefreshSystemAsync removed in simplified architecture; re-run Connect to refresh instruments
            if (!ViewModel.IsConnected)
                await ViewModel.ConnectAsync();
            else
            {
                await ViewModel.DisconnectAsync();
                await ViewModel.ConnectAsync();
            }
        }

        private async void BtnRefreshInstruments_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.RefreshInstrumentsAsync();
        }

        private void BtnLaunchDom_Click(object sender, RoutedEventArgs e)
        {
            System.Console.WriteLine($"🎯🎯🎯 [CONTROLLER WINDOW] BtnLaunchDom_Click CALLED! 🎯🎯🎯");
            ViewModel.LaunchSelectedInstrumentDom();
            System.Console.WriteLine($"🎯🎯🎯 [CONTROLLER WINDOW] ViewModel.LaunchSelectedInstrumentDom() completed! 🎯🎯🎯");
        }

        private void BtnWipeSymbolHistory_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.WipeHistoryForSelected();
        }

        private void BtnWipeAllHistory_Click(object sender, RoutedEventArgs e)
        {
            var r = System.Windows.MessageBox.Show("Delete ALL persisted signal history for every symbol? (Takes effect next session.)",
                "Wipe All History", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes) ViewModel.WipeAllHistory();
        }

        private void BtnSystemLog_Click(object sender, RoutedEventArgs e)
        {
            if (_logViewer == null || !_logViewer.IsVisible)
            {
                _logViewer = new LogViewerWindow(ViewModel) { Owner = this };
                _logViewer.Left = this.Left + this.Width + 10;
                _logViewer.Top = this.Top;
                _logViewer.Closed += (s, args) => _logViewer = null;
                _logViewer.Show();
            }
            else
            {
                _logViewer.Activate();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _logViewer?.Close();
            ViewModel?.Dispose();
            base.OnClosed(e);
        }
    }
}