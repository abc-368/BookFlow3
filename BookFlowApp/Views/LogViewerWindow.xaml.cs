using System.Windows;
using BookFlow.App.ViewModels;

namespace BookFlow.App.Views
{
    /// <summary>
    /// Detached system-log viewer. Shares the controller's view model so it shows the same
    /// live log; opened on demand so the controller window stays compact.
    /// </summary>
    public partial class LogViewerWindow : Window
    {
        public LogViewerWindow(ControllerViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void TxtSystemLog_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (chkAutoScroll.IsChecked == true)
                txtSystemLog.ScrollToEnd();
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            (DataContext as ControllerViewModel)?.ClearLog();
        }

        private void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Clipboard.SetText(
                txtSystemLog.SelectedText.Length > 0 ? txtSystemLog.SelectedText : txtSystemLog.Text);
        }

        private void SelectAllLog_Click(object sender, RoutedEventArgs e)
        {
            txtSystemLog.SelectAll();
        }
    }
}
