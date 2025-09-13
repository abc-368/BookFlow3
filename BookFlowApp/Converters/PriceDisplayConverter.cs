using System;
using System.Globalization;
using System.Windows.Data;

namespace BookFlow.App.Converters
{
    /// <summary>
    /// Converts a decimal price value to a formatted string for display in the UI.
    /// </summary>
    public class PriceDisplayConverter : IValueConverter
    {
        /// <summary>
        /// Converts a decimal price to a string formatted with dynamic decimal places.
        /// </summary>
        /// <param name="value">The decimal price value.</param>
        /// <param name="targetType">The type of the binding target property.</param>
        /// <param name="parameter">The converter parameter specifying decimal places (defaults to 2).</param>
        /// <param name="culture">The culture to use in the converter.</param>
        /// <returns>A formatted price string or an empty string if the input is not a valid decimal.</returns>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int decimalPlaces = 2; // Default
            
            // Parse decimal places from parameter
            if (parameter is string paramStr && int.TryParse(paramStr, out int places))
            {
                decimalPlaces = Math.Max(0, Math.Min(places, 6)); // Clamp between 0-6
            }
            else if (parameter is int intParam)
            {
                decimalPlaces = Math.Max(0, Math.Min(intParam, 6)); // Clamp between 0-6
            }
            
            string format = $"F{decimalPlaces}";
            
            if (value is decimal price)
            {
                return price.ToString(format);
            }
            if (value is double doublePrice)
            {
                return doublePrice.ToString(format);
            }
            return string.Empty;
        }

        /// <summary>
        /// Converts a formatted price string back to a decimal value.
        /// </summary>
        /// <param name="value">The formatted price string.</param>
        /// <param name="targetType">The type of the binding target property.</param>
        /// <param name="parameter">The converter parameter to use.</param>
        /// <param name="culture">The culture to use in the converter.</param>
        /// <returns>A decimal price value or 0 if the conversion fails.</returns>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string str && decimal.TryParse(str, out decimal result))
            {
                return result;
            }
            return 0m;
        }
    }
}