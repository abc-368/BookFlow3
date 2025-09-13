using System;
using System.Globalization;
using System.Windows.Data;

namespace BookFlow.App.Converters
{
    /// <summary>
    /// Converts a long volume value to a formatted string for display in the UI.
    /// </summary>
    public class VolumeDisplayConverter : IValueConverter
    {
        /// <summary>
        /// Converts a long volume to a formatted string. If the volume is zero, an empty string is returned.
        /// </summary>
        /// <param name="value">The long volume value.</param>
        /// <param name="targetType">The type of the binding target property.</param>
        /// <param name="parameter">The converter parameter to use.</param>
        /// <param name="culture">The culture to use in the converter.</param>
        /// <returns>A formatted volume string or an empty string.</returns>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is long volume)
            {
                if (volume == 0)
                    return string.Empty;
                return volume.ToString("#####");
            }
            return string.Empty;
        }

        /// <summary>
        /// Converts a formatted volume string back to a long value.
        /// </summary>
        /// <param name="value">The formatted volume string.</param>
        /// <param name="targetType">The type of the binding target property.</param>
        /// <param name="parameter">The converter parameter to use.</param>
        /// <param name="culture">The culture to use in the converter.</param>
        /// <returns>A long volume value or 0 if the conversion fails.</returns>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string str && long.TryParse(str, out long result))
            {
                return result;
            }
            return 0L;
        }
    }
}