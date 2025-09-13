using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BookFlow.App.Converters
{
    /// <summary>
    /// A multi-value converter that determines the visibility of the volume profile.
    /// It takes a column-specific setting and a global setting as input.
    /// The volume profile is only visible if both settings are true.
    /// </summary>
    public class VolumeProfileVisibilityConverter : IMultiValueConverter
    {
        /// <summary>
        /// Converts boolean flags to a `Visibility` value.
        /// </summary>
        /// <param name="values">An array of values. Expected values are: {bool showVolumeProfile, bool enableVolumeProfiles}.</param>
        /// <param name="targetType">The type of the binding target property.</param>
        /// <param name="parameter">The converter parameter to use.</param>
        /// <param name="culture">The culture to use in the converter.</param>
        /// <returns>A `Visibility` value (Visible or Collapsed).</returns>
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2)
                return Visibility.Collapsed;
                
            // First value is the column's ShowVolumeProfile setting
            var showVolumeProfile = values[0] as bool? ?? false;
            
            // Second value is the global EnableVolumeProfiles setting
            var enableVolumeProfiles = values[1] as bool? ?? false;
            
            // The volume profile is only visible if both the column-specific and global settings are enabled.
            return (showVolumeProfile && enableVolumeProfiles) ? Visibility.Visible : Visibility.Collapsed;
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