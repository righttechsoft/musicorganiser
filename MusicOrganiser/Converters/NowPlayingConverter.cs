using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MusicOrganiser.Converters;

public class NowPlayingConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2 && values[0] != null && values[0] == values[1])
        {
            return new SolidColorBrush(Color.FromRgb(200, 230, 255));
        }
        return Brushes.Transparent;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class NowPlayingBoolConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        return values.Length == 2 && values[0] != null && values[0] == values[1];
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
