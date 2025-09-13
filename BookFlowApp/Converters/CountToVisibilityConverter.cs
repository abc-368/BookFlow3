using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BookFlow.App.Converters
{
    /// <summary>
    /// Converts an integer count to a Visibility value.
    /// Returns Visible if count > 0, otherwise Collapsed.
    /// </summary>
    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count && count > 0)
            {
                return Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
