using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MusicOrganiser.Models;

public class RecentPlayedFolders
{
    private const int MaxRecentFolders = 20;
    private static readonly string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MusicOrganiser");
    private static readonly string FilePath = Path.Combine(AppDataPath, "recent_played_folders.json");

    public List<string> Folders { get; set; } = new();

    public void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Folders.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Folders.Insert(0, path);
        if (Folders.Count > MaxRecentFolders)
        {
            Folders.RemoveRange(MaxRecentFolders, Folders.Count - MaxRecentFolders);
        }
        Save();
    }

    public void Remove(string path)
    {
        if (Folders.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Save();
        }
    }

    /// Removes the folder and any of its descendants (used when a folder is deleted).
    public void RemoveTree(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var prefix = path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (Folders.RemoveAll(p =>
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Save();
        }
    }

    public void Clear()
    {
        if (Folders.Count == 0) return;
        Folders.Clear();
        Save();
    }

    public static string GetTwoLevelDisplay(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return path;
        if (parts.Length == 1) return parts[0];
        return $"{parts[parts.Length - 2]}/{parts[parts.Length - 1]}";
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataPath);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Ignore save errors
        }
    }

    public static RecentPlayedFolders Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<RecentPlayedFolders>(json) ?? new RecentPlayedFolders();
            }
        }
        catch
        {
            // Ignore load errors
        }
        return new RecentPlayedFolders();
    }
}
