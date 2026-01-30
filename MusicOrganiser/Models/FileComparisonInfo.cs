using System;
using System.IO;
using TagLib;

namespace MusicOrganiser.Models;

public class FileComparisonInfo
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime ModifiedDate { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public int Bitrate { get; set; }

    public string FileSizeFormatted => FormatFileSize(FileSize);
    public string DurationFormatted => Duration.TotalSeconds > 0
        ? (Duration.TotalHours >= 1 ? Duration.ToString(@"h\:mm\:ss") : Duration.ToString(@"m\:ss"))
        : string.Empty;
    public string BitrateFormatted => Bitrate > 0 ? $"{Bitrate} kbps" : string.Empty;
    public string ModifiedDateFormatted => ModifiedDate.ToString("yyyy-MM-dd HH:mm:ss");

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    public static FileComparisonInfo FromPath(string path)
    {
        var fileInfo = new FileInfo(path);
        var info = new FileComparisonInfo
        {
            FileName = fileInfo.Name,
            FullPath = path,
            FileSize = fileInfo.Length,
            ModifiedDate = fileInfo.LastWriteTime
        };

        try
        {
            using var tagFile = TagLib.File.Create(path);
            info.Title = tagFile.Tag.Title ?? string.Empty;
            info.Artist = tagFile.Tag.FirstPerformer ?? string.Empty;
            info.Album = tagFile.Tag.Album ?? string.Empty;
            info.Duration = tagFile.Properties.Duration;
            info.Bitrate = tagFile.Properties.AudioBitrate;
        }
        catch
        {
            // If tag reading fails, we still have basic file info
        }

        return info;
    }
}

public class FolderComparisonInfo
{
    public string FolderName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalSize { get; set; }
    public DateTime ModifiedDate { get; set; }

    public string TotalSizeFormatted => FormatFileSize(TotalSize);
    public string ModifiedDateFormatted => ModifiedDate.ToString("yyyy-MM-dd HH:mm:ss");

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    public static FolderComparisonInfo FromPath(string path)
    {
        var dirInfo = new DirectoryInfo(path);
        var files = dirInfo.GetFiles("*", SearchOption.AllDirectories);

        return new FolderComparisonInfo
        {
            FolderName = dirInfo.Name,
            FullPath = path,
            FileCount = files.Length,
            TotalSize = files.Sum(f => f.Length),
            ModifiedDate = dirInfo.LastWriteTime
        };
    }
}

public class FolderFileEntry
{
    public string FileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public TimeSpan Duration { get; set; }
    public string Status { get; set; } = string.Empty;

    public string FileSizeFormatted => FormatFileSize(FileSize);
    public string DurationFormatted => Duration.TotalSeconds > 0
        ? (Duration.TotalHours >= 1 ? Duration.ToString(@"h\:mm\:ss") : Duration.ToString(@"m\:ss"))
        : string.Empty;

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
