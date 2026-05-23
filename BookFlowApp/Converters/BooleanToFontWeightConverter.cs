using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BookFlow.App.Converters
{
    public class BooleanToFontWeightConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b)
                return b ? FontWeights.Bold : FontWeights.Normal;
            return FontWeights.Normal;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is FontWeight fw)
                return fw == FontWeights.Bold;
            return false;
        }
    }
}
