using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MusicOrganiser.Models;
using MusicOrganiser.Services;

namespace MusicOrganiser.ViewModels;

public class MainViewModel : ViewModelBase, IDisposable
{
    private readonly MusicMetadataService _metadataService;
    private readonly AudioPlayerService _audioPlayer;
    private readonly FileOperationsService _fileOperations;
    private readonly ArtistInfoService _artistInfoService;
    private readonly AppSettings _appSettings;

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
    private ImageSource? _albumCover;

    public FolderTreeViewModel FolderTree { get; }
    public RecentFolders RecentFolders { get; }
    public RecentPlayedFolders RecentPlayedStore { get; }
    public ObservableCollection<RecentPlayedFolderItem> RecentPlayedFolders { get; } = new();
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
            _appSettings.Volume = (int)value;
            _appSettings.Save();
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

    public ImageSource? AlbumCover
    {
        get => _albumCover;
        set => SetProperty(ref _albumCover, value);
    }

    public ICommand PlayPauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PlayFileCommand { get; }
    public ICommand RefreshArtistInfoCommand { get; }

    public AudioPlayerService AudioPlayer => _audioPlayer;
    public FileOperationsService FileOperations => _fileOperations;
    public MusicMetadataService MetadataService => _metadataService;

    public MainViewModel()
    {
        // Initialize the SQLite cache and start the background refresh worker before any
        // folder loading kicks off.
        DatabaseService.Instance.Initialize();
        LibraryCacheService.Instance.Start();

        _metadataService = new MusicMetadataService();
        _audioPlayer = new AudioPlayerService();
        _fileOperations = new FileOperationsService(_audioPlayer);
        _artistInfoService = new ArtistInfoService();
        _appSettings = AppSettings.Load();

        FolderTree = new FolderTreeViewModel();
        RecentFolders = RecentFolders.Load();
        RecentPlayedStore = Models.RecentPlayedFolders.Load();
        RefreshRecentPlayedFolders();

        // Restore volume from settings
        _audioPlayer.Volume = _appSettings.Volume / 100f;

        _audioPlayer.PositionChanged += OnPositionChanged;
        _audioPlayer.PlaybackStopped += OnPlaybackStopped;

        PlayPauseCommand = new RelayCommand(PlayPause);
        StopCommand = new RelayCommand(Stop);
        PreviousCommand = new RelayCommand(Previous);
        NextCommand = new RelayCommand(Next);
        PlayFileCommand = new RelayCommand(PlayFile);
        RefreshArtistInfoCommand = new RelayCommand(RefreshArtistInfo);
    }

    private void AddRecentPlayedFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        RecentPlayedStore.Add(folderPath);
        RefreshRecentPlayedFolders();
    }

    public void RemoveRecentPlayedFolder(string folderPath)
    {
        RecentPlayedStore.Remove(folderPath);
        RefreshRecentPlayedFolders();
    }

    private void RefreshRecentPlayedFolders()
    {
        RecentPlayedFolders.Clear();
        foreach (var path in RecentPlayedStore.Folders)
        {
            if (!Directory.Exists(path)) continue;
            RecentPlayedFolders.Add(new RecentPlayedFolderItem(path));
        }
    }

    private void RefreshArtistInfo()
    {
        if (NowPlaying != null)
        {
            _ = FetchArtistInfoAsync(NowPlaying);
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
        AlbumCover = null;

        _ = LoadFolderAsync(path, token);
        _ = LoadAlbumCoverAsync(path, token);
    }

    private async Task LoadAlbumCoverAsync(string path, CancellationToken token)
    {
        var bytes = await Task.Run(() => _metadataService.GetAlbumArt(path), token);
        if (token.IsCancellationRequested || bytes == null)
            return;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (token.IsCancellationRequested)
                return;

            try
            {
                var img = new BitmapImage();
                using var ms = new MemoryStream(bytes);
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
                img.Freeze();
                AlbumCover = img;
            }
            catch
            {
                AlbumCover = null;
            }
        });
    }

    private async Task LoadFolderAsync(string path, CancellationToken token)
    {
        // 1. Cache-first: show whatever the SQLite cache already has, instantly.
        IReadOnlyList<MusicFile> cached;
        try { cached = LibraryCacheService.Instance.GetCachedFiles(path); }
        catch { cached = Array.Empty<MusicFile>(); }

        if (cached.Count > 0 && !token.IsCancellationRequested)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (token.IsCancellationRequested) return;
                foreach (var file in cached)
                {
                    HookFile(file);
                    MusicFiles.Add(file);
                    if (MusicFiles.Count == 1)
                        SelectedFile = file;
                }
            });
        }

        // 2. Authoritative refresh: diff the filesystem, re-parse only changed files.
        IReadOnlyList<MusicFile> fresh;
        try { fresh = await LibraryCacheService.Instance.RefreshFolderFilesAsync(path, token); }
        catch (OperationCanceledException) { return; }
        catch { return; }

        if (token.IsCancellationRequested) return;

        // 3. Reconcile the grid against the fresh, authoritative list.
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (token.IsCancellationRequested) return;
            ReconcileMusicFiles(fresh);
        });
    }

    private void ReconcileMusicFiles(IReadOnlyList<MusicFile> fresh)
    {
        var freshByPath = new Dictionary<string, MusicFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fresh)
            freshByPath[f.FullPath] = f;

        // Remove entries no longer present on disk.
        for (int i = MusicFiles.Count - 1; i >= 0; i--)
        {
            if (!freshByPath.ContainsKey(MusicFiles[i].FullPath))
                MusicFiles.RemoveAt(i);
        }

        // Index remaining entries by path.
        var indexByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < MusicFiles.Count; i++)
            indexByPath[MusicFiles[i].FullPath] = i;

        // Add new entries and replace changed ones (different instance = re-parsed metadata).
        foreach (var f in fresh)
        {
            if (indexByPath.TryGetValue(f.FullPath, out var idx))
            {
                if (!ReferenceEquals(MusicFiles[idx], f))
                {
                    HookFile(f);
                    MusicFiles[idx] = f;
                }
            }
            else
            {
                HookFile(f);
                indexByPath[f.FullPath] = MusicFiles.Count;
                MusicFiles.Add(f);
            }
        }

        if (SelectedFile == null && MusicFiles.Count > 0)
            SelectedFile = MusicFiles[0];
    }

    // Persist rating/tag edits made on a track back to the SQLite cache.
    private void HookFile(MusicFile file)
    {
        file.PropertyChanged -= File_PropertyChanged;
        file.PropertyChanged += File_PropertyChanged;
    }

    private void File_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MusicFile f) return;
        try
        {
            if (e.PropertyName == nameof(MusicFile.Rating))
                DatabaseService.Instance.SetFileRating(f.FullPath, f.Rating);
            else if (e.PropertyName == nameof(MusicFile.Tags))
                DatabaseService.Instance.SetFileTags(f.FullPath, f.Tags);
        }
        catch { }
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

            AddRecentPlayedFolder(Path.GetDirectoryName(file.FullPath));

            // Only load artist info from cache (don't fetch from API automatically)
            LoadArtistInfoFromCache(file);
        }
        catch
        {
            // Handle playback error
            NowPlaying = null;
            IsPlaying = false;
        }
    }

    private void LoadArtistInfoFromCache(MusicFile file)
    {
        var artistName = _artistInfoService.DetectArtistName(file.Artist, file.FullPath);
        var cached = _artistInfoService.TryGetFromCache(artistName);

        if (cached != null)
        {
            ArtistSummary = cached.Summary;
        }
        else
        {
            ArtistSummary = "Click ↻ to load artist info";
        }
    }

    private async Task FetchArtistInfoAsync(MusicFile file)
    {
        var artistName = _artistInfoService.DetectArtistName(file.Artist, file.FullPath);

        IsLoadingArtistInfo = true;
        ArtistSummary = !string.IsNullOrWhiteSpace(artistName)
            ? $"Loading info for {artistName}..."
            : "Identifying artist...";

        try
        {
            var result = await _artistInfoService.GetArtistSummaryAsync(
                artistName,
                file.Title,
                file.Album,
                file.FileName);

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
        LibraryCacheService.Instance.Stop();
    }
}

public class RecentPlayedFolderItem
{
    public string FullPath { get; }
    public string DisplayName { get; }

    public RecentPlayedFolderItem(string fullPath)
    {
        FullPath = fullPath;
        DisplayName = RecentPlayedFolders.GetTwoLevelDisplay(fullPath);
    }
}
