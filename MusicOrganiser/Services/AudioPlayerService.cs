using System;
using System.Timers;
using NAudio.Wave;

namespace MusicOrganiser.Services;

public class AudioPlayerService : IDisposable
{
    private WaveOutEvent? _waveOut;
    private AudioFileReader? _audioFile;
    private readonly System.Timers.Timer _positionTimer;
    private bool _disposed;
    private float _volume = 1.0f;

    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? PlaybackStopped;

    public string? CurrentFilePath { get; private set; }
    public TimeSpan CurrentPosition => _audioFile?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan TotalDuration => _audioFile?.TotalTime ?? TimeSpan.Zero;
    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _waveOut?.PlaybackState == PlaybackState.Paused;
    public bool IsStopped => _waveOut == null || _waveOut.PlaybackState == PlaybackState.Stopped;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_audioFile != null)
            {
                _audioFile.Volume = _volume;
            }
        }
    }

    public AudioPlayerService()
    {
        _positionTimer = new System.Timers.Timer(100);
        _positionTimer.Elapsed += (s, e) => PositionChanged?.Invoke(this, CurrentPosition);
    }

    public void Play(string filePath)
    {
        StopAndReleaseFile();

        try
        {
            _audioFile = new AudioFileReader(filePath);
            _audioFile.Volume = _volume;
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_audioFile);
            _waveOut.PlaybackStopped += OnPlaybackStopped;
            _waveOut.Play();
            CurrentFilePath = filePath;
            _positionTimer.Start();
        }
        catch (Exception ex)
        {
            StopAndReleaseFile();
            throw new InvalidOperationException($"Failed to play file: {ex.Message}", ex);
        }
    }

    public void Resume()
    {
        if (_waveOut?.PlaybackState == PlaybackState.Paused)
        {
            _waveOut.Play();
            _positionTimer.Start();
        }
    }

    public void Pause()
    {
        if (_waveOut?.PlaybackState == PlaybackState.Playing)
        {
            _waveOut.Pause();
            _positionTimer.Stop();
        }
    }

    public void Stop()
    {
        _positionTimer.Stop();
        _waveOut?.Stop();
        if (_audioFile != null)
        {
            _audioFile.Position = 0;
        }
        PositionChanged?.Invoke(this, TimeSpan.Zero);
    }

    public void Seek(TimeSpan position)
    {
        if (_audioFile != null)
        {
            _audioFile.CurrentTime = position;
            PositionChanged?.Invoke(this, position);
        }
    }

    public void StopAndReleaseFile()
    {
        _positionTimer.Stop();

        if (_waveOut != null)
        {
            _waveOut.PlaybackStopped -= OnPlaybackStopped;
            _waveOut.Stop();
            _waveOut.Dispose();
            _waveOut = null;
        }

        if (_audioFile != null)
        {
            _audioFile.Dispose();
            _audioFile = null;
        }

        CurrentFilePath = null;
    }

    public bool IsPlayingFile(string filePath)
    {
        return CurrentFilePath != null &&
               CurrentFilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        _positionTimer.Stop();
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _positionTimer.Stop();
        _positionTimer.Dispose();
        StopAndReleaseFile();
    }
}
