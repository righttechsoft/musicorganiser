using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MusicOrganiser.Models;

/// <summary>
/// A saved playlist. Mirrors a row of the SQLite <c>playlists</c> table plus a live
/// track count. Observable (Name/TrackCount) so the left-pane list updates after
/// rename or add/remove without a full reload.
/// </summary>
public class Playlist : INotifyPropertyChanged
{
    public int Id { get; set; }

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set => Set(ref _name, value ?? string.Empty);
    }

    private int _trackCount;
    public int TrackCount
    {
        get => _trackCount;
        set => Set(ref _trackCount, value);
    }

    public DateTime? CreatedUtc { get; set; }
    public DateTime? ModifiedUtc { get; set; }

    // Persisted playback state (restored when the playlist is reopened).
    public bool Shuffle { get; set; }
    public int Repeat { get; set; }           // matches RepeatMode: 0=Off, 1=All, 2=One
    public string? LastFullPath { get; set; } // last track played from this playlist

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
