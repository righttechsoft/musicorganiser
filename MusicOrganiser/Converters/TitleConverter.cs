using System;
using System.Globalization;
using System.Windows.Data;
using MusicOrganiser.Models;

namespace MusicOrganiser.Converters;

public class TitleConverter : IMultiValueConverter
{
    private const string AppName = "Music Organiser";

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        string? folder = values.Length > 0 ? values[0] as string : null;
        var nowPlaying = values.Length > 1 ? values[1] as MusicFile : null;

        bool hasFolder = !string.IsNullOrWhiteSpace(folder);
        bool hasPlaying = nowPlaying != null && !string.IsNullOrWhiteSpace(nowPlaying.FileName);

        if (hasFolder && hasPlaying)
        {
            return $"{AppName} \u2014 {folder} \u2014 \u25B6 {nowPlaying!.FileName}";
        }
        if (hasFolder)
        {
            return $"{AppName} \u2014 {folder}";
        }
        return AppName;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
