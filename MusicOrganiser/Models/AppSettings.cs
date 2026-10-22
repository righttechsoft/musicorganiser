using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicOrganiser.Models;

/// <summary>How the transport bar draws the track: Bars = loudness bars coloured by spectral balance; the rest also show tempo/beat.</summary>
public enum WaveformStyle { Bars, Ribbons, Strands, BeatBars, Spectrum }

public class AppSettings
{
    private static readonly string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MusicOrganiser");
    private static readonly string FilePath = Path.Combine(AppDataPath, "settings.json");

    public int Volume { get; set; } = 100;

    // CoreAudio device id for playback output; null = system default endpoint.
    // Master (system) volume is intentionally not persisted — it is a live OS value.
    public string? OutputDeviceId { get; set; }

    // WASAPI output buffer in ms (100–1000). Bigger survives CPU starvation;
    // smaller gives snappier in-app volume / audible-seek response.
    public int OutputBufferMs { get; set; } = 300;

    // Seek bar drawing style (stored by name in settings.json).
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WaveformStyle WaveformStyle { get; set; } = WaveformStyle.Ribbons;

    // Last folder browsed; restored on next launch. null = nothing to restore.
    public string? LastFolderPath { get; set; }

    // Last track played; re-selected on next launch when it still lives in the restored folder,
    // so hitting Play picks up where you left off. null = nothing played yet.
    public string? LastPlayedPath { get; set; }

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

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Ignore load errors
        }
        return new AppSettings();
    }
}
