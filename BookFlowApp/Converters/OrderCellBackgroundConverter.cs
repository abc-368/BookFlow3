using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BookFlow.App.Converters
{
    /// <summary>
    /// Returns a background brush for Bid/Ask Orders Info cells based on order count
    /// ConverterParameter: "bid" or "ask"
    /// </summary>
    public class OrderCellBackgroundConverter : IValueConverter
    {
        private static readonly System.Windows.Media.Brush DefaultBackground = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString("#FF1A252F")!;
        private static readonly System.Windows.Media.Brush BidBackground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 152, 219)); // Blue for bid
        private static readonly System.Windows.Media.Brush AskBackground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(231, 76, 60)); // Red for ask

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                var count = 0;
                if (value is int i) count = i;
                else if (value != null) int.TryParse(value.ToString(), out count);

                var side = parameter as string;
                if (count > 0)
                {
                    return string.Equals(side, "ask", StringComparison.OrdinalIgnoreCase) ? AskBackground : BidBackground;
                }
                return DefaultBackground;
            }
            catch
            {
                return DefaultBackground;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}