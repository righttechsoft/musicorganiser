using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;

namespace MusicOrganiser.Converters;

public class PlayingFolderFontWeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return FontWeights.Normal;

        string? folderPath = values[0] as string;
        string? nowPlayingPath = values[1] as string;

        if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(nowPlayingPath))
            return FontWeights.Normal;

        string? playingDir;
        try
        {
            playingDir = Path.GetDirectoryName(nowPlayingPath);
        }
        catch
        {
            return FontWeights.Normal;
        }

        if (string.IsNullOrEmpty(playingDir)) return FontWeights.Normal;

        string a = folderPath!.TrimEnd('\\', '/');
        string b = playingDir!.TrimEnd('\\', '/');

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            ? FontWeights.Bold
            : FontWeights.Normal;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
