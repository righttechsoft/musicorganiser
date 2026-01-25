using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MusicOrganiser.Models;

public class ArtistInfoCache
{
    private static readonly string CacheFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MusicOrganiser",
        "artist_info_cache.json");

    public Dictionary<string, ArtistInfoEntry> Artists { get; set; } = new();

    public static ArtistInfoCache Load()
    {
        try
        {
            if (File.Exists(CacheFilePath))
            {
                var json = File.ReadAllText(CacheFilePath);
                return JsonSerializer.Deserialize<ArtistInfoCache>(json) ?? new ArtistInfoCache();
            }
        }
        catch
        {
            // If loading fails, return empty cache
        }
        return new ArtistInfoCache();
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(CacheFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CacheFilePath, json);
        }
        catch
        {
            // Ignore save errors
        }
    }

    public ArtistInfoEntry? Get(string artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName)) return null;

        var key = NormalizeKey(artistName);
        return Artists.TryGetValue(key, out var entry) ? entry : null;
    }

    public void Set(string artistName, string summary, bool isConfident)
    {
        if (string.IsNullOrWhiteSpace(artistName) || !isConfident) return;

        var key = NormalizeKey(artistName);
        Artists[key] = new ArtistInfoEntry
        {
            ArtistName = artistName,
            Summary = summary,
            LastUpdated = DateTime.UtcNow
        };
        Save();
    }

    public void Remove(string artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName)) return;

        var key = NormalizeKey(artistName);
        if (Artists.Remove(key))
        {
            Save();
        }
    }

    private static string NormalizeKey(string artistName)
    {
        return artistName.Trim().ToLowerInvariant();
    }
}

public class ArtistInfoEntry
{
    public string ArtistName { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; }
}
