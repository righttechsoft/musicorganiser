using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MusicOrganiser.Models;

/// <summary>One line in the karaoke big-view (see <see cref="ViewModels.MainViewModel.KaraokeWindow"/>).
/// One of these exists per <see cref="LyricLine"/> for the whole track — the collection is built
/// once (MainViewModel.RebuildKaraokeWindow) and then <see cref="Offset"/>/<see cref="IsCurrent"/>
/// are updated in place as playback advances, never Clear+Add'd again, so MainWindow's
/// code-behind can smoothly pan a stable set of item containers instead of them being
/// regenerated every tick. Offset is the signed distance from the current line (0 = current,
/// clamped to a small range for styling purposes — see MainViewModel.KaraokeMaxOffset); IsCurrent
/// is independent of Offset==0 so the "anchor at line 0, nothing marked current" state (before
/// playback reaches the first timestamp) can style normally without looking highlighted.</summary>
public class KaraokeWindowLine : INotifyPropertyChanged
{
    public string Text { get; set; } = string.Empty;

    private int _offset;
    public int Offset
    {
        get => _offset;
        set => Set(ref _offset, value);
    }

    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
