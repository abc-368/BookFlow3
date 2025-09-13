using System;
using System.Globalization;
using System.Windows.Data;

namespace BookFlow.App.Converters
{
    /// <summary>
    /// Multi-value converter that formats a price with dynamic decimal places.
    /// Takes the price value and decimal places count as inputs.
    /// </summary>
    public class DynamicPriceDisplayConverter : IMultiValueConverter
    {
        /// <summary>
        /// Converts price and decimal places to a formatted price string.
        /// </summary>
        /// <param name="values">Array containing [0] price value and [1] decimal places count</param>
        /// <param name="targetType">The type of the binding target property</param>
        /// <param name="parameter">The converter parameter to use</param>
        /// <param name="culture">The culture to use in the converter</param>
        /// <returns>A formatted price string or an empty string if inputs are invalid</returns>
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2)
                return string.Empty;

            // Extract price value
            decimal price;
            if (values[0] is decimal decimalPrice)
            {
                price = decimalPrice;
            }
            else if (values[0] is double doublePrice)
            {
                price = (decimal)doublePrice;
            }
            else
            {
                return string.Empty;
            }

            // Extract decimal places count
            int decimalPlaces = 2; // Default
            if (values[1] is int intPlaces)
            {
                decimalPlaces = Math.Max(0, Math.Min(intPlaces, 6)); // Clamp between 0-6
            }

            // Format the price
            string format = $"F{decimalPlaces}";
            return price.ToString(format);
        }

        /// <summary>
        /// ConvertBack is not supported for this multi-value converter.
        /// </summary>
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException("ConvertBack is not supported for DynamicPriceDisplayConverter");
        }
    }
}