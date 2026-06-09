using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace musicpresense
{
    /// <summary>
    /// Returns Visible when the bound int equals the ConverterParameter, otherwise Collapsed.
    /// Used so each wizard step panel shows only when CurrentStep matches its index, e.g.
    /// Visibility="{Binding CurrentStep, Converter={StaticResource StepVis}, ConverterParameter=2}".
    ///
    /// This is the general shape of a custom value converter: implement IValueConverter, do the
    /// transform in Convert, and (since this one is one-way) reject ConvertBack.
    /// </summary>
    public sealed class IndexToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int current
                && parameter != null
                && int.TryParse(parameter.ToString(), out int target))
            {
                return current == target ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
