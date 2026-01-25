using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MusicOrganiser.Models;

public class RecentFolders
{
    private const int MaxRecentFolders = 10;
    private static readonly string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MusicOrganiser");
    private static readonly string FilePath = Path.Combine(AppDataPath, "recent_folders.json");

    public List<string> CopyFolders { get; set; } = new();
    public List<string> MoveFolders { get; set; } = new();

    public void AddCopyFolder(string path)
    {
        AddToList(CopyFolders, path);
        Save();
    }

    public void AddMoveFolder(string path)
    {
        AddToList(MoveFolders, path);
        Save();
    }

    private static void AddToList(List<string> list, string path)
    {
        list.Remove(path);
        list.Insert(0, path);
        if (list.Count > MaxRecentFolders)
        {
            list.RemoveAt(list.Count - 1);
        }
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

    public static RecentFolders Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<RecentFolders>(json) ?? new RecentFolders();
            }
        }
        catch
        {
            // Ignore load errors
        }
        return new RecentFolders();
    }
}
