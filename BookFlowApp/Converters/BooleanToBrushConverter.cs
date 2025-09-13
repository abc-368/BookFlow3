using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BookFlow.App.Converters
{
    public class BooleanToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue && boolValue && parameter is string colorString)
            {
                try
                {
                    return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorString));
                }
                catch
                {
                    return new SolidColorBrush(System.Windows.Media.Colors.Transparent);
                }
            }
            return new SolidColorBrush(System.Windows.Media.Colors.Transparent);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}