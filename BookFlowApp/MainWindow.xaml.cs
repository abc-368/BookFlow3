using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using BookFlow.App.ViewModels;
using BookFlow.App.Views;
using BookFlow.App.Engine;
using BookFlow.App.Services;
using BookFlow.Shared.Contracts;
using BookFlow.App.Models;

namespace BookFlow.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void TestDomButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Create a mock trading service for the test window
            var mockTradingService = new MockTradingService();

            // Create a test DomEngine with sample data
            var domEngine = new DomEngine("ES", 1, mockTradingService, 0.25m); // ES, tickerId=1, tickSize=0.25

            // Create test data to populate the DOM
            CreateTestMarketData(domEngine);
            
            // Debug: Verify DOM has data
            Console.WriteLine($"[DEBUG] Created DOM engine for {domEngine.InstrumentName}");
            
            // Create ViewModel with the mock trading service
            var viewModel = new DomViewModel(domEngine, mockTradingService);
            
            // Debug: Monitor ladder updates
            System.Diagnostics.Debug.WriteLine($"[DEBUG] ViewModel created with {viewModel.DomRows.Count} initial rows");
            
            // Subscribe to ladder updates for debugging
            domEngine.LadderUpdates.Subscribe(update =>
            {
                System.Diagnostics.Debug.WriteLine($"[DEBUG] Ladder update: {update.VisibleLevels?.Count ?? 0} levels");
            });

            // Create and show DOM window
            var domWindow = new DomGridWindow(viewModel);
            domWindow.Show();
            
            System.Diagnostics.Debug.WriteLine($"[DEBUG] DOM window shown, ViewModel rows: {viewModel.DomRows.Count}");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Error launching DOM window: {ex.Message}", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CreateTestMarketData(DomEngine domEngine)
    {
        // Create test market data for ES (E-mini S&P 500)
        var testPrices = new decimal[]
        {
            4521.50m, 4521.25m, 4521.00m, 4520.75m, 4520.50m,  // Ask side
            4520.25m, 4520.00m,  // Best Bid/Ask
            4519.75m, 4519.50m, 4519.25m, 4519.00m, 4518.75m   // Bid side
        };

        var random = new Random();

        foreach (var price in testPrices)
        {
            var bidVolume = price <= 4520.00m ? random.Next(1, 50) : 0;
            var askVolume = price >= 4520.25m ? random.Next(1, 50) : 0;

            // Create L2 market data messages
            if (bidVolume > 0)
            {
                var bidMessage = new UnifiedMarketDataMessage
                {
                    Category = MessageCategory.L2Data,
                    MarketDataType = 1, // Bid
                    Operation = 1, // Insert/Update
                    TickerId = 1,
                    Price = (double)price,
                    Volume = bidVolume,
                    OriginalTimestamp = DateTime.UtcNow.Ticks
                };
                domEngine.ProcessMessage(bidMessage);
                System.Diagnostics.Debug.WriteLine($"[DEBUG] Added bid data: {price:F2} vol={bidVolume}");
            }

            if (askVolume > 0)
            {
                var askMessage = new UnifiedMarketDataMessage
                {
                    Category = MessageCategory.L2Data,
                    MarketDataType = 2, // Ask
                    Operation = 1, // Insert/Update
                    TickerId = 1,
                    Price = (double)price,
                    Volume = askVolume,
                    OriginalTimestamp = DateTime.UtcNow.Ticks
                };
                domEngine.ProcessMessage(askMessage);
                System.Diagnostics.Debug.WriteLine($"[DEBUG] Added ask data: {price:F2} vol={askVolume}");
            }
        }
    }

    private void CreateDirectTestData(DomViewModel viewModel)
    {
        // Add test data directly to the ObservableCollection for debugging
        var testRows = new[]
        {
            new DomRowData
            {
                Price = 4521.00m,
                BidVolume = 0,
                AskVolume = 25,
                BidCount = 0,
                AskCount = 3,
                AskDepth = 25,
                BidDepth = 0,
                VolumeProfile = 150,
                BidOrdersInfo = "",
                AskOrdersInfo = "3 ord",
                OpenPositionPnL = 0m,
                Observations = "",
                BidSnapshot = 0,
                AskSnapshot = 43,
                LastTradeAtBid = 0,
                LastTradeAtAsk = 12,
                Reserve = "R1"
            },
            new DomRowData
            {
                Price = 4520.75m,
                BidVolume = 0,
                AskVolume = 18,
                BidCount = 0,
                AskCount = 2,
                AskDepth = 18,
                BidDepth = 0,
                VolumeProfile = 89,
                BidOrdersInfo = "",
                AskOrdersInfo = "2 ord",
                OpenPositionPnL = 0m,
                Observations = "",
                BidSnapshot = 0,
                AskSnapshot = 31,
                LastTradeAtBid = 0,
                LastTradeAtAsk = 8,
                Reserve = ""
            },
            new DomRowData
            {
                Price = 4520.50m,
                BidVolume = 35,
                AskVolume = 0,
                BidCount = 4,
                AskCount = 0,
                AskDepth = 0,
                BidDepth = 35,
                VolumeProfile = 245,
                BidOrdersInfo = "4 ord",
                AskOrdersInfo = "",
                OpenPositionPnL = 125m,
                Observations = "TOP",
                IsTopOfBook = true,
                BidSnapshot = 52,
                AskSnapshot = 0,
                LastTradeAtBid = 15,
                LastTradeAtAsk = 0,
                Reserve = "TOB"
            },
            new DomRowData
            {
                Price = 4520.25m,
                BidVolume = 42,
                AskVolume = 0,
                BidCount = 5,
                AskCount = 0,
                AskDepth = 0,
                BidDepth = 42,
                VolumeProfile = 312,
                BidOrdersInfo = "5 ord",
                AskOrdersInfo = "",
                OpenPositionPnL = 87m,
                Observations = "SUPP",
                BidSnapshot = 77,
                AskSnapshot = 0,
                LastTradeAtBid = 22,
                LastTradeAtAsk = 0,
                Reserve = ""
            },
            new DomRowData
            {
                Price = 4520.00m,
                BidVolume = 28,
                AskVolume = 0,
                BidCount = 3,
                AskCount = 0,
                AskDepth = 0,
                BidDepth = 28,
                VolumeProfile = 187,
                BidOrdersInfo = "3 ord",
                AskOrdersInfo = "",
                OpenPositionPnL = 45m,
                Observations = "",
                BidSnapshot = 39,
                AskSnapshot = 0,
                LastTradeAtBid = 7,
                LastTradeAtAsk = 0,
                Reserve = ""
            }
        };

        // Clear and add test data
        viewModel.DomRows.Clear();
        foreach (var row in testRows)
        {
            viewModel.DomRows.Add(row);
        }
        
        System.Diagnostics.Debug.WriteLine($"[DEBUG] Added {testRows.Length} direct test rows to DomRows collection");
    }
}