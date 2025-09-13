using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BookFlow.App.Converters
{
    /// <summary>
    /// Converter to provide ORANGE background highlighting for top of book (best bid/ask) rows.
    /// 
    /// ORANGE COLOR PURPOSE:
    /// - Instantly identifies the BEST BID and BEST ASK prices in the DOM
    /// - These are the prices where market orders will execute immediately
    /// - Critical for traders to spot executable prices at a glance
    /// - Only appears when a price level contains the current best bid or ask
    /// 
    /// COLOR SCHEME:
    /// - Orange (#FF8C00): Main cell background for top of book
    /// - Darker Orange (#CC7000): Volume profile bars within top of book cells
    /// - Blue: Regular bid side colors
    /// - Red: Regular ask side colors
    /// </summary>
    public class TopOfBookBackgroundConverter : IMultiValueConverter
    {
        private static readonly SolidColorBrush TopOfBookBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 140, 0)); // Dark orange - good contrast with white text
        private static readonly SolidColorBrush TopOfBookProfileBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(204, 112, 0)); // Darker orange for volume profile
        private static readonly SolidColorBrush BidBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 152, 219)); // Blue for bid
        private static readonly SolidColorBrush BidProfileBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 118, 184)); // Darker blue for bid profile
        private static readonly SolidColorBrush AskBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(231, 76, 60)); // Red for ask
        private static readonly SolidColorBrush AskProfileBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(197, 54, 44)); // Darker red for ask profile

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 3 && 
                values[0] is bool isTopBid && 
                values[1] is bool isTopAsk &&
                values[2] is long depth)
            {
                // Only highlight if there's actual depth
                if (depth > 0)
                {
                    if (isTopBid || isTopAsk)
                    {
                        // Check if this is for the volume profile bar or main background
                        string? elementType = parameter as string;
                        if (elementType?.Contains("profile") == true)
                        {
                            return TopOfBookProfileBrush; // Darker orange for volume profile
                        }
                        return TopOfBookBrush; // Dark orange for top of book - good contrast with white text
                    }
                }
                
                // Default colors based on side and element type
                string? param = parameter as string;
                if (param?.Contains("profile") == true)
                {
                    // Volume profile bar colors
                    if (param.Contains("bid"))
                    {
                        return BidProfileBrush;
                    }
                    else if (param.Contains("ask"))
                    {
                        return AskProfileBrush;
                    }
                }
                else
                {
                    // Main background colors
                    if (param == "bid")
                    {
                        return BidBrush;
                    }
                    else if (param == "ask")
                    {
                        return AskBrush;
                    }
                }
            }
            
            // Fallback to default blue
            return BidBrush;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}