using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MusicOrganiser.Models;
using TagLib;

namespace MusicOrganiser.Services;

public class MusicMetadataService
{
    private static readonly string[] SupportedExtensions =
    {
        ".mp3", ".flac", ".wav", ".wma", ".aac", ".ogg", ".m4a"
    };

    public static bool IsSupportedFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return SupportedExtensions.Contains(ext);
    }

    public static string GetFileFilter()
    {
        return string.Join(";", SupportedExtensions.Select(e => $"*{e}"));
    }

    public IEnumerable<MusicFile> GetMusicFiles(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            yield break;

        foreach (var ext in SupportedExtensions)
        {
            foreach (var file in Directory.EnumerateFiles(folderPath, $"*{ext}", SearchOption.TopDirectoryOnly))
            {
                var musicFile = ReadMetadata(file);
                if (musicFile != null)
                    yield return musicFile;
            }
        }
    }

    public MusicFile? ReadMetadata(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var musicFile = new MusicFile
            {
                FileName = fileInfo.Name,
                FullPath = filePath
            };

            try
            {
                using var tagFile = TagLib.File.Create(filePath);
                musicFile.Title = tagFile.Tag.Title ?? string.Empty;
                musicFile.Artist = tagFile.Tag.FirstPerformer ?? string.Empty;
                musicFile.Album = tagFile.Tag.Album ?? string.Empty;
                musicFile.Genre = tagFile.Tag.FirstGenre ?? string.Empty;
                musicFile.Year = tagFile.Tag.Year;
                musicFile.Duration = tagFile.Properties.Duration;
                musicFile.Bitrate = tagFile.Properties.AudioBitrate;
                musicFile.SampleRate = tagFile.Properties.AudioSampleRate;
            }
            catch
            {
                // If tag reading fails, we still have the basic file info
            }

            return musicFile;
        }
        catch
        {
            return null;
        }
    }
}
