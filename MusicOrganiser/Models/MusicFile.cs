using System;

namespace MusicOrganiser.Models;

public class MusicFile
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public int Bitrate { get; set; }
    public int SampleRate { get; set; }
    public string Genre { get; set; } = string.Empty;
    public uint Year { get; set; }

    public string DurationFormatted => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    public string BitrateFormatted => Bitrate > 0 ? $"{Bitrate} kbps" : string.Empty;

    public string SampleRateFormatted => SampleRate > 0 ? $"{SampleRate} Hz" : string.Empty;
}
