using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MusicOrganiser.Models;

public class MusicFile : INotifyPropertyChanged
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

    // User rating, 1..5 (null = unrated). Bound to the grid star column.
    private int? _rating;
    public int? Rating
    {
        get => _rating;
        set => Set(ref _rating, value);
    }

    // How many times this track has been played (persisted). Drives least-played shuffle.
    public int PlayCount { get; set; }

    // Comma-separated tags, bound to the grid Tags column.
    private string _tags = string.Empty;
    public string Tags
    {
        get => _tags;
        set => Set(ref _tags, value ?? string.Empty);
    }

    // Whether the lyrics table has a (non-empty) row for this track. Bound to the grid's
    // mic-icon column; flipped on load (one batch query per folder, see
    // DatabaseService.GetPathsWithLyrics) and immediately on a successful lyrics save.
    private bool _hasLyrics;
    public bool HasLyrics
    {
        get => _hasLyrics;
        set => Set(ref _hasLyrics, value);
    }

    // Change-detection fields used by the SQLite cache (kept off the grid).
    public long FileSize { get; set; }
    public DateTime FileModifiedUtc { get; set; }

    // Set only when this row is served as part of a playlist; identifies the
    // playlist_entries row so it can be removed/reordered. Null for folder views.
    public int? PlaylistEntryId { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public string DurationFormatted => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    public string BitrateFormatted => Bitrate > 0 ? $"{Bitrate} kbps" : string.Empty;

    public string SampleRateFormatted => SampleRate > 0 ? $"{SampleRate} Hz" : string.Empty;
}
