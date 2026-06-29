using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Converts a hex color string (e.g. "#2D6CDF") into a SolidColorBrush for swatch
    /// previews. Blank or invalid input falls back to transparent so an in-progress edit
    /// never throws. One-way only.
    /// </summary>
    public sealed class ColorStringToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var hex = value as string;
            if (!string.IsNullOrWhiteSpace(hex))
            {
                try
                {
                    if (ColorConverter.ConvertFromString(hex.Trim()) is Color c)
                    {
                        var brush = new SolidColorBrush(c);
                        brush.Freeze();
                        return brush;
                    }
                }
                catch { }
            }
            return Brushes.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
