using System.Windows;
using BookFlow.App.ViewModels;

namespace BookFlow.App.Views
{
    public partial class GridSettingsWindow : Window
    {
        public DomViewModel ViewModel { get; }
        public GridSettingsWindow(DomViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = viewModel;
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.Settings.ResetToDefaults();
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            // Close to apply - grid binds live to Settings
            DialogResult = true;
            Close();
        }
    }
}
