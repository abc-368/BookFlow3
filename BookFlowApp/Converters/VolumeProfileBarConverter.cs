using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BookFlow.App.Converters
{
    /// <summary>
    /// A multi-value converter that calculates the width of a volume profile bar.
    /// It takes the volume for the current price level and the maximum volume in the visible DOM
    /// as input, and returns a width proportional to the maximum volume.
    /// </summary>
    public class VolumeProfileBarConverter : IMultiValueConverter
    {
        /// <summary>
        /// Converts volume and max volume to a bar width.
        /// </summary>
        /// <param name="values">An array of values. Expected values are: {long volume, long maxVolume}.</param>
        /// <param name="targetType">The type of the binding target property.</param>
        /// <param name="parameter">The converter parameter to use.</param>
        /// <param name="culture">The culture to use in the converter.</param>
        /// <returns>A double representing the calculated width of the volume profile bar.</returns>
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2)
                return 0.0;
                
            var volume = values[0] as long? ?? 0;
            var maxVolume = values[1] as long? ?? 1; // Avoid divide by zero
            
            if (volume <= 0 || maxVolume <= 0)
                return 0.0;
                
            // Calculate the percentage of the current volume relative to the maximum volume.
            double percentage = Math.Min((double)volume / maxVolume, 1.0);
            
            // The width of the bar is the percentage of the available column width (assumed to be 53 pixels).
            return 53.0 * percentage;
        }

        /// <summary>
        /// This method is not implemented as it is not needed for one-way binding.
        /// </summary>
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}