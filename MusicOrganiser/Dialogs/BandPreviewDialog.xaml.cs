using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.Wave;
using MusicOrganiser.Services;

namespace MusicOrganiser.Dialogs;

/// <summary>Modeless per-band audio preview opened from BulkTagDialog: lists every supported
/// audio file under a band folder (recursively) and plays it via its OWN NAudio chain —
/// deliberately not AudioPlayerService, so auditioning a candidate track never touches the
/// main app's NowPlaying/queue/output-device state.</summary>
public partial class BandPreviewDialog : Window
{
    private const int MaxFiles = 500;

    private readonly List<string> _files;
    private readonly DispatcherTimer _positionTimer;
    private bool _scrubbing;

    private AudioFileReader? _reader;
    private WaveOutEvent? _output;

    public BandPreviewDialog(string bandName, string folderPath)
    {
        InitializeComponent();
        Title = $"Preview — {bandName}";

        List<string> all;
        try { all = MusicMetadataService.EnumerateSupportedFilesRecursive(folderPath).ToList(); }
        catch { all = new List<string>(); }

        var capped = all.Count > MaxFiles;
        _files = capped ? all.Take(MaxFiles).ToList() : all;

        FilesList.ItemsSource = _files.Select(f => Path.GetRelativePath(folderPath, f)).ToList();
        CountText.Text = capped
            ? $"{_files.Count} file(s) (showing first {MaxFiles})"
            : $"{_files.Count} file(s)";

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _positionTimer.Tick += PositionTimer_Tick;

        Closed += (_, _) => StopPlayback();
    }

    private void FilesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesList.SelectedIndex >= 0)
            PlayIndex(FilesList.SelectedIndex);
    }

    private void PlaySelected_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedIndex >= 0)
            PlayIndex(FilesList.SelectedIndex);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_output == null)
        {
            if (FilesList.SelectedIndex >= 0) PlayIndex(FilesList.SelectedIndex);
            return;
        }

        if (_output.PlaybackState == PlaybackState.Playing)
        {
            _output.Pause();
            PlayPauseButton.Content = "▶";
        }
        else
        {
            _output.Play();
            PlayPauseButton.Content = "⏸";
        }
    }

    private void PlayIndex(int index)
    {
        if (index < 0 || index >= _files.Count) return;

        StopPlayback();
        StatusText.Text = string.Empty;

        try
        {
            var reader = new AudioFileReader(_files[index]);
            var output = new WaveOutEvent();
            output.Init(reader);
            output.PlaybackStopped += Output_PlaybackStopped;
            output.Play();

            _reader = reader;
            _output = output;

            FilesList.SelectedIndex = index;
            PlayPauseButton.Content = "⏸";
            PositionSlider.Maximum = reader.TotalTime.TotalSeconds;
            _positionTimer.Start();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not play: {ex.Message}";
            StopPlayback();
        }
    }

    private void Output_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (e.Exception != null)
            {
                StatusText.Text = $"Playback error: {e.Exception.Message}";
                return;
            }

            // Natural end (StopPlayback always unsubscribes before a manual stop/switch,
            // so this handler only ever fires for genuine end-of-track).
            var idx = FilesList.SelectedIndex;
            if (idx >= 0 && idx + 1 < _files.Count)
                PlayIndex(idx + 1);
        });
    }

    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (_reader == null || _scrubbing) return;
        PositionSlider.Value = _reader.CurrentTime.TotalSeconds;
        TimeText.Text = $"{FormatTime(_reader.CurrentTime)} / {FormatTime(_reader.TotalTime)}";
    }

    private void PositionSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _scrubbing = true;

    private void PositionSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _scrubbing = false;
        if (_reader != null)
            _reader.CurrentTime = TimeSpan.FromSeconds(PositionSlider.Value);
    }

    private void StopPlayback()
    {
        _positionTimer.Stop();
        try
        {
            if (_output != null)
            {
                _output.PlaybackStopped -= Output_PlaybackStopped;
                _output.Stop();
                _output.Dispose();
                _output = null;
            }
            _reader?.Dispose();
            _reader = null;
        }
        catch { }
        PlayPauseButton.Content = "▶";
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
