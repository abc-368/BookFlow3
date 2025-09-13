using System;
using System.Globalization;
using System.Windows.Data;
using Media = System.Windows.Media;

namespace BookFlow.App.Converters
{
    /// <summary>
    /// Returns blue brush for positive P&L, red for negative, and neutral (white) for zero/empty.
    /// </summary>
    public class PnLForegroundConverter : IValueConverter
    {
        private static Media.SolidColorBrush FromHex(string hex)
        {
            // Expect #RRGGBB or #AARRGGBB
            if (hex.StartsWith("#")) hex = hex.Substring(1);
            byte a = 0xFF, r, g, b;
            if (hex.Length == 8)
            {
                a = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                r = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                g = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
                b = byte.Parse(hex.Substring(6, 2), NumberStyles.HexNumber);
            }
            else
            {
                r = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                g = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                b = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
            }
            return new Media.SolidColorBrush(Media.Color.FromArgb(a, r, g, b));
        }

        private static readonly Media.SolidColorBrush Blue = FromHex("FF4A90E2");
        private static readonly Media.SolidColorBrush Red = FromHex("FFE74C3C");
        private static readonly Media.SolidColorBrush White = Media.Brushes.White;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is decimal d)
            {
                if (d > 0m) return Blue;
                if (d < 0m) return Red;
                return White;
            }
            return White;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
