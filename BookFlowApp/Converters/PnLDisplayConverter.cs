using System;
using System.Globalization;
using System.Windows.Data;

namespace BookFlow.App.Converters
{
    /// <summary>
    /// Converter for P&L display that shows empty string when value is 0,
    /// and formatted numeric text when there's an actual position P&L.
    /// Uses two decimal places and a leading '+' for positives.
    /// </summary>
    public class PnLDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is decimal pnl)
            {
                if (pnl == 0m)
                    return string.Empty;

                // Two decimals, include leading '+' for positives
                return pnl > 0 ? $"+{pnl:F2}" : pnl.ToString("F2");
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}