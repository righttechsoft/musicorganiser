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

    private static readonly string[] ImageExtensions =
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"
    };

    private static readonly string[] PreferredCoverNames =
    {
        "cover", "folder", "front", "album", "albumart"
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

        foreach (var file in EnumerateSupportedFiles(folderPath))
        {
            var musicFile = ReadMetadata(file);
            if (musicFile != null)
                yield return musicFile;
        }
    }

    /// <summary>Enumerates supported music file paths in a folder (top level only), no tag reading.
    /// Used by the cache layer to diff the filesystem before deciding what to re-parse.</summary>
    public static IEnumerable<string> EnumerateSupportedFiles(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            yield break;

        foreach (var ext in SupportedExtensions)
        {
            foreach (var file in Directory.EnumerateFiles(folderPath, $"*{ext}", SearchOption.TopDirectoryOnly))
                yield return file;
        }
    }

    /// <summary>
    /// Finds album art for a folder. Priority: image file in folder,
    /// embedded picture in a music file, image file in an immediate subfolder.
    /// </summary>
    public byte[]? GetAlbumArt(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return null;

        // 1. Image file in the folder itself
        var art = FindImageInFolder(folderPath);
        if (art != null)
            return art;

        // 2. Embedded picture from a music file in the folder
        art = GetEmbeddedArt(folderPath);
        if (art != null)
            return art;

        // 3. Image file in an immediate subfolder
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(folderPath))
            {
                var subArt = FindImageInFolder(sub);
                if (subArt != null)
                    return subArt;
            }
        }
        catch
        {
            // ignore enumeration errors
        }

        return null;
    }

    private static byte[]? FindImageInFolder(string folderPath)
    {
        try
        {
            var images = Directory.EnumerateFiles(folderPath)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            if (images.Count == 0)
                return null;

            var preferred = images.FirstOrDefault(f =>
                PreferredCoverNames.Contains(Path.GetFileNameWithoutExtension(f).ToLowerInvariant()));

            return System.IO.File.ReadAllBytes(preferred ?? images[0]);
        }
        catch
        {
            return null;
        }
    }

    private byte[]? GetEmbeddedArt(string folderPath)
    {
        try
        {
            foreach (var ext in SupportedExtensions)
            {
                foreach (var file in Directory.EnumerateFiles(folderPath, $"*{ext}", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        using var tagFile = TagLib.File.Create(file);
                        var pic = tagFile.Tag.Pictures.FirstOrDefault();
                        if (pic != null && pic.Data.Count > 0)
                            return pic.Data.Data;
                    }
                    catch
                    {
                        // skip files that fail to read
                    }
                }
            }
        }
        catch
        {
            // ignore enumeration errors
        }

        return null;
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
