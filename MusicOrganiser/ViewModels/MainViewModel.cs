using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using MusicOrganiser.Models;
using MusicOrganiser.Services;

namespace MusicOrganiser.ViewModels;

public class MainViewModel : ViewModelBase, IDisposable
{
    private readonly MusicMetadataService _metadataService;
    private readonly AudioPlayerService _audioPlayer;
    private readonly FileOperationsService _fileOperations;
    private readonly ArtistInfoService _artistInfoService;

    private string _currentFolderPath = string.Empty;
    private MusicFile? _selectedFile;
    private MusicFile? _nowPlaying;
    private TimeSpan _currentPosition;
    private TimeSpan _totalDuration;
    private bool _isPlaying;
    private double _sliderValue;
    private bool _isSliderDragging;
    private bool _disposed;
    private bool _stoppedByUser;
    private CancellationTokenSource? _loadingCts;
    private string _artistSummary = string.Empty;
    private bool _isLoadingArtistInfo;

    public FolderTreeViewModel FolderTree { get; }
    public RecentFolders RecentFolders { get; }
    public ObservableCollection<MusicFile> MusicFiles { get; } = new();

    public string CurrentFolderPath
    {
        get => _currentFolderPath;
        set => SetProperty(ref _currentFolderPath, value);
    }

    public MusicFile? SelectedFile
    {
        get => _selectedFile;
        set => SetProperty(ref _selectedFile, value);
    }

    public MusicFile? NowPlaying
    {
        get => _nowPlaying;
        set => SetProperty(ref _nowPlaying, value);
    }

    public TimeSpan CurrentPosition
    {
        get => _currentPosition;
        set
        {
            if (SetProperty(ref _currentPosition, value) && !_isSliderDragging && TotalDuration.TotalSeconds > 0)
            {
                _sliderValue = value.TotalSeconds / TotalDuration.TotalSeconds * 100;
                OnPropertyChanged(nameof(SliderValue));
            }
        }
    }

    public TimeSpan TotalDuration
    {
        get => _totalDuration;
        set => SetProperty(ref _totalDuration, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set => SetProperty(ref _isPlaying, value);
    }

    public double Volume
    {
        get => _audioPlayer.Volume * 100;
        set
        {
            _audioPlayer.Volume = (float)(value / 100);
            OnPropertyChanged(nameof(Volume));
        }
    }

    public double SliderValue
    {
        get => _sliderValue;
        set
        {
            if (SetProperty(ref _sliderValue, value) && _isSliderDragging && TotalDuration.TotalSeconds > 0)
            {
                var newPosition = TimeSpan.FromSeconds(value / 100 * TotalDuration.TotalSeconds);
                _currentPosition = newPosition;
                OnPropertyChanged(nameof(CurrentPosition));
            }
        }
    }

    public bool IsSliderDragging
    {
        get => _isSliderDragging;
        set
        {
            if (SetProperty(ref _isSliderDragging, value) && !value && TotalDuration.TotalSeconds > 0)
            {
                var newPosition = TimeSpan.FromSeconds(SliderValue / 100 * TotalDuration.TotalSeconds);
                _audioPlayer.Seek(newPosition);
            }
        }
    }

    public string CurrentPositionFormatted => FormatTime(CurrentPosition);
    public string TotalDurationFormatted => FormatTime(TotalDuration);

    public string ArtistSummary
    {
        get => _artistSummary;
        set => SetProperty(ref _artistSummary, value);
    }

    public bool IsLoadingArtistInfo
    {
        get => _isLoadingArtistInfo;
        set => SetProperty(ref _isLoadingArtistInfo, value);
    }

    public ICommand PlayPauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PlayFileCommand { get; }
    public ICommand RefreshArtistInfoCommand { get; }

    public AudioPlayerService AudioPlayer => _audioPlayer;
    public FileOperationsService FileOperations => _fileOperations;

    public MainViewModel()
    {
        _metadataService = new MusicMetadataService();
        _audioPlayer = new AudioPlayerService();
        _fileOperations = new FileOperationsService(_audioPlayer);
        _artistInfoService = new ArtistInfoService();

        FolderTree = new FolderTreeViewModel();
        RecentFolders = RecentFolders.Load();

        _audioPlayer.PositionChanged += OnPositionChanged;
        _audioPlayer.PlaybackStopped += OnPlaybackStopped;

        PlayPauseCommand = new RelayCommand(PlayPause);
        StopCommand = new RelayCommand(Stop);
        PreviousCommand = new RelayCommand(Previous);
        NextCommand = new RelayCommand(Next);
        PlayFileCommand = new RelayCommand(PlayFile);
        RefreshArtistInfoCommand = new RelayCommand(RefreshArtistInfo);
    }

    private void RefreshArtistInfo()
    {
        if (NowPlaying != null)
        {
            _ = FetchArtistInfoAsync(NowPlaying, forceRefresh: true);
        }
    }

    public void LoadFolder(string path)
    {
        // Cancel any previous loading operation
        _loadingCts?.Cancel();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;

        CurrentFolderPath = path;
        MusicFiles.Clear();

        _ = LoadFolderAsync(path, token);
    }

    private async Task LoadFolderAsync(string path, CancellationToken token)
    {
        await Task.Run(() =>
        {
            foreach (var file in _metadataService.GetMusicFiles(path))
            {
                if (token.IsCancellationRequested) return;

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        MusicFiles.Add(file);
                        if (MusicFiles.Count == 1)
                        {
                            SelectedFile = file;
                        }
                    }
                });
            }
        }, token);
    }

    public void PlayFile(object? parameter)
    {
        var file = parameter as MusicFile ?? SelectedFile;
        if (file == null) return;

        try
        {
            _audioPlayer.Play(file.FullPath);
            NowPlaying = file;
            TotalDuration = _audioPlayer.TotalDuration;
            IsPlaying = true;

            // Fetch artist info asynchronously
            _ = FetchArtistInfoAsync(file);
        }
        catch
        {
            // Handle playback error
            NowPlaying = null;
            IsPlaying = false;
        }
    }

    private async Task FetchArtistInfoAsync(MusicFile file, bool forceRefresh = false)
    {
        var artistName = _artistInfoService.DetectArtistName(file.Artist, file.FullPath);

        IsLoadingArtistInfo = true;
        ArtistSummary = forceRefresh
            ? "Refreshing artist info..."
            : (!string.IsNullOrWhiteSpace(artistName)
                ? $"Loading info for {artistName}..."
                : "Identifying artist...");

        try
        {
            // Clear cache if forcing refresh
            if (forceRefresh)
            {
                _artistInfoService.ClearCache(artistName);
            }

            var result = await _artistInfoService.GetArtistSummaryAsync(
                artistName,
                file.Title,
                file.Album,
                file.FileName,
                forceRefresh);

            ArtistSummary = result.Summary;
        }
        catch
        {
            ArtistSummary = "Could not load artist info";
        }
        finally
        {
            IsLoadingArtistInfo = false;
        }
    }

    private void PlayPause()
    {
        if (_audioPlayer.IsPlaying)
        {
            _audioPlayer.Pause();
            IsPlaying = false;
        }
        else if (_audioPlayer.IsPaused)
        {
            _audioPlayer.Resume();
            IsPlaying = true;
        }
        else if (SelectedFile != null)
        {
            PlayFile(SelectedFile);
        }
    }

    private void Stop()
    {
        _stoppedByUser = true;
        _audioPlayer.Stop();
        IsPlaying = false;
        CurrentPosition = TimeSpan.Zero;
    }

    private void Previous()
    {
        if (NowPlaying == null || MusicFiles.Count == 0) return;

        var index = MusicFiles.IndexOf(NowPlaying);
        if (index > 0)
        {
            var prevFile = MusicFiles[index - 1];
            SelectedFile = prevFile;
            PlayFile(prevFile);
        }
    }

    private void Next()
    {
        if (NowPlaying == null || MusicFiles.Count == 0) return;

        var index = MusicFiles.IndexOf(NowPlaying);
        if (index < MusicFiles.Count - 1)
        {
            var nextFile = MusicFiles[index + 1];
            SelectedFile = nextFile;
            PlayFile(nextFile);
        }
    }

    private void OnPositionChanged(object? sender, TimeSpan position)
    {
        if (!_isSliderDragging)
        {
            CurrentPosition = position;
            OnPropertyChanged(nameof(CurrentPositionFormatted));
        }
    }

    private void OnPlaybackStopped(object? sender, EventArgs e)
    {
        IsPlaying = false;

        // Don't auto-play next if user clicked Stop
        if (_stoppedByUser)
        {
            _stoppedByUser = false;
            return;
        }

        // Auto-play next track when current track ends naturally
        if (NowPlaying != null)
        {
            var index = MusicFiles.IndexOf(NowPlaying);
            if (index < MusicFiles.Count - 1)
            {
                var nextFile = MusicFiles[index + 1];
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    SelectedFile = nextFile;
                    PlayFile(nextFile);
                });
            }
        }
    }

    private static string FormatTime(TimeSpan time)
    {
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _audioPlayer.PositionChanged -= OnPositionChanged;
        _audioPlayer.PlaybackStopped -= OnPlaybackStopped;
        _audioPlayer.Dispose();
    }
}
