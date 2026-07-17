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
    /// Finds album art candidates for a folder. Priority: the current track's own embedded
    /// pictures, then image files in the folder, then embedded picture in a music file,
    /// then image files in an immediate subfolder.
    /// Returns every candidate within the winning tier (used for cover cycling).
    /// </summary>
    public List<byte[]> GetAlbumArts(string folderPath, string? currentFilePath = null)
    {
        // ponytail: caps at 12 images so a folder of 200 scans doesn't get decoded into RAM.
        const int MaxImages = 12;

        var result = new List<byte[]>();
        if (!Directory.Exists(folderPath))
            return result;

        // 0. The current track's own embedded pictures beat any folder image.
        if (!string.IsNullOrEmpty(currentFilePath))
        {
            var own = GetEmbeddedArts(currentFilePath, MaxImages);
            if (own.Count > 0)
                return own;
        }

        // 1. Image files in the folder itself
        foreach (var imgPath in GetFolderImagePaths(folderPath))
        {
            if (result.Count >= MaxImages)
                break;
            try { result.Add(System.IO.File.ReadAllBytes(imgPath)); }
            catch { /* skip files that fail to read */ }
        }
        if (result.Count > 0)
            return result;

        // 2. Embedded picture from a music file in the folder
        var embedded = GetEmbeddedArt(folderPath);
        if (embedded != null)
            return new List<byte[]> { embedded };

        // 3. Image files in immediate subfolders
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(folderPath))
            {
                foreach (var imgPath in GetFolderImagePaths(sub))
                {
                    if (result.Count >= MaxImages)
                        break;
                    try { result.Add(System.IO.File.ReadAllBytes(imgPath)); }
                    catch { /* skip files that fail to read */ }
                }
                if (result.Count >= MaxImages)
                    break;
            }
        }
        catch
        {
            // ignore enumeration errors
        }

        return result;
    }

    /// <summary>Returns all image files directly in a folder, preferred cover names first
    /// (e.g. "cover", "folder"), then the rest — each group ordered by filename.</summary>
    public static List<string> GetFolderImagePaths(string folderPath)
    {
        try
        {
            var images = Directory.EnumerateFiles(folderPath)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            var preferred = images
                .Where(f => PreferredCoverNames.Contains(Path.GetFileNameWithoutExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            var rest = images
                .Where(f => !PreferredCoverNames.Contains(Path.GetFileNameWithoutExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

            return preferred.Concat(rest).ToList();
        }
        catch
        {
            return new List<string>();
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

    /// <summary>All embedded pictures in one music file (empty if none / unreadable).</summary>
    private static List<byte[]> GetEmbeddedArts(string filePath, int max)
    {
        var result = new List<byte[]>();
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            foreach (var pic in tagFile.Tag.Pictures)
            {
                if (result.Count >= max)
                    break;
                if (pic.Data.Count > 0)
                    result.Add(pic.Data.Data);
            }
        }
        catch
        {
            // skip files that fail to read
        }

        return result;
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
