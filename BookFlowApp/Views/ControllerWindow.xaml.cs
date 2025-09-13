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

        private void BtnLaunchDom_Click(object sender, RoutedEventArgs e)
        {
            System.Console.WriteLine($"🎯🎯🎯 [CONTROLLER WINDOW] BtnLaunchDom_Click CALLED! 🎯🎯🎯");
            ViewModel.LaunchSelectedInstrumentDom();
            System.Console.WriteLine($"🎯🎯🎯 [CONTROLLER WINDOW] ViewModel.LaunchSelectedInstrumentDom() completed! 🎯🎯🎯");
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ClearLog();
        }

        private void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            if (txtSystemLog.SelectedText.Length > 0)
            {
                System.Windows.Clipboard.SetText(txtSystemLog.SelectedText);
            }
            else
            {
                System.Windows.Clipboard.SetText(txtSystemLog.Text);
            }
        }

        private void SelectAllLog_Click(object sender, RoutedEventArgs e)
        {
            txtSystemLog.SelectAll();
        }

        protected override void OnClosed(EventArgs e)
        {
            ViewModel?.Dispose();
            base.OnClosed(e);
        }
    }
}