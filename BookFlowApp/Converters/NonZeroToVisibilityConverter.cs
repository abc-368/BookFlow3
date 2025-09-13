using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BookFlow.App.Converters
{
    /// <summary>
    /// Converts any numeric value to Visible when non-zero, otherwise Collapsed.
    /// Supports int, long, double, decimal.
    /// </summary>
    public class NonZeroToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return Visibility.Collapsed;
            try
            {
                var d = System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return d != 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch
            {
                return Visibility.Collapsed;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
