using System;
using System.Globalization;
using System.Windows.Data;

namespace BookFlow.App.Converters
{
    /// <summary>
    /// Converter for P&L display that shows empty string when value is 0,
    /// and formatted currency when there's an actual position P&L.
    /// </summary>
    public class PnLDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is decimal pnl)
            {
                // Show nothing if P&L is exactly 0 (flat position)
                if (pnl == 0m)
                    return string.Empty;
                
                // Show formatted P&L with + for positive, - for negative
                return pnl > 0 ? $"+{pnl:F0}" : pnl.ToString("F0");
            }
            
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}