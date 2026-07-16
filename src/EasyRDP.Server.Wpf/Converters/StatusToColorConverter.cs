using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using EasyRDP.Core.Transport;

namespace EasyRDP.Server.Wpf.Converters
{
    public class BoolToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value is bool && (bool)value) ? Brushes.Green : Brushes.Red;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) { throw new NotImplementedException(); }
    }

    public class LogLevelToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is LogLevel)
            {
                switch ((LogLevel)value)
                {
                    case LogLevel.Error: return Brushes.Red;
                    case LogLevel.Warning: return Brushes.Orange;
                    default: return Brushes.Black;
                }
            }
            return Brushes.Black;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) { throw new NotImplementedException(); }
    }
}
