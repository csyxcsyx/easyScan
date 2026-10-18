using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EasyScan.Desktop.Services;

public sealed class VisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (parameter as string == "Invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is Visibility.Visible) != (parameter as string == "Invert");
}
