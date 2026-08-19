using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MusicOrganiser.Models;

/// <summary>One parsed line of an LRC lyrics file. <see cref="IsActive"/> is toggled by
/// the position-driven karaoke highlight in <see cref="ViewModels.MainViewModel"/>; <see cref="Time"/>
/// is user-editable via the "now"/nudge timestamp controls and <see cref="Text"/> via the inline
/// row editor for already-synced lyrics — both must notify so MainViewModel's auto-save
/// (LyricLine_PropertyChanged) picks them up.</summary>
public class LyricLine : INotifyPropertyChanged
{
    private TimeSpan _time;
    public TimeSpan Time
    {
        get => _time;
        set
        {
            if (Set(ref _time, value))
                OnPropertyChanged(nameof(TimeFormatted));
        }
    }

    public string TimeFormatted => FormatLrcTime(Time);

    /// <summary>`mm:ss.ff`, with the minutes field accumulating past 59 rather than rolling
    /// into an hours field the LRC format doesn't have — a single 83-minute track is one lyric
    /// list, and TimeSpan's own `mm` would silently wrap 1:23:15 back to `23:15`, putting every
    /// line past the hour mark an hour early. Minutes are still zero-padded to two digits, so
    /// ordinary-length songs format exactly as before.</summary>
    public static string FormatLrcTime(TimeSpan time)
        => ((int)time.TotalMinutes).ToString("00") + time.ToString(@"\:ss\.ff");

    private string _text = string.Empty;
    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    // Top spacer height (px) proportional to the time gap since the previous line — makes the
    // row list read as a time-proportional layout instead of an evenly-stacked one. Computed by
    // MainViewModel.RecomputeLyricRowGaps, not by this line itself (needs its neighbour's Time).
    private double _gapPx;
    public double GapPx
    {
        get => _gapPx;
        set => Set(ref _gapPx, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
