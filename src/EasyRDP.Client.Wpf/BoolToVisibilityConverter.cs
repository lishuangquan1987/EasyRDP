using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// bool → Visibility 转换器（true=Visible, false=Collapsed）。
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b) return Visibility.Visible;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility v) return v == Visibility.Visible;
            return false;
        }
    }
}
