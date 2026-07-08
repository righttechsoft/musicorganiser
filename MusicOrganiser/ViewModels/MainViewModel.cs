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
    private readonly ControlApiService _controlApi;

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
    // Folder being played as a recursive queue; swallows the one selection-echo LoadFolder
    // that fires when clicking ▶ moves tree selection (which would wipe the queue).
    private string? _playTreeFolder;
    private string _artistSummary = string.Empty;
    private bool _isLoadingArtistInfo;
    private ImageSource? _albumCover;
    private OutputDeviceItem? _selectedOutputDevice;
    private bool _remoteSink;
    // Last bitrate the phone requested; used to prewarm the transcode cache.
    private int _lastStreamBitrate = AudioStreamService.DefaultBitrate;

    public FolderTreeViewModel FolderTree { get; }
    public RecentFolders RecentFolders { get; }
    public RecentPlayedFolders RecentPlayedStore { get; }
    public ObservableCollection<RecentPlayedFolderItem> RecentPlayedFolders { get; } = new();
    public ObservableCollection<MusicFile> MusicFiles { get; } = new();
    public ObservableCollection<Playlist> Playlists { get; } = new();

    // Playlist view state. MusicFiles doubles as the playback queue; when a playlist is
    // loaded it holds the playlist's tracks, otherwise the selected folder's files.
    private Playlist? _selectedPlaylist;
    private int? _currentPlaylistId;

    // Playback modifiers.
    private bool _shuffleEnabled;
    private RepeatMode _repeat = RepeatMode.Off;
    private List<int>? _shuffleOrder;   // permutation of MusicFiles indices; rebuilt lazily
    private readonly Random _rng = new();
    private bool _suppressPlaylistPersist;   // true while restoring a playlist's saved state

    public Playlist? SelectedPlaylist
    {
        get => _selectedPlaylist;
        set => SetProperty(ref _selectedPlaylist, value);
    }

    public bool IsPlaylistView => _currentPlaylistId != null;
    public int? CurrentPlaylistId => _currentPlaylistId;

    public bool ShuffleEnabled
    {
        get => _shuffleEnabled;
        private set
        {
            if (SetProperty(ref _shuffleEnabled, value))
            {
                _shuffleOrder = null;
                PersistPlaylistState();
            }
        }
    }

    public RepeatMode Repeat
    {
        get => _repeat;
        private set
        {
            if (SetProperty(ref _repeat, value))
            {
                OnPropertyChanged(nameof(RepeatGlyph));
                OnPropertyChanged(nameof(RepeatActive));
                PersistPlaylistState();
            }
        }
    }

    public bool RepeatActive => _repeat != RepeatMode.Off;
    public string RepeatGlyph => _repeat == RepeatMode.One ? "\U0001F502" : "\U0001F501"; // 🔂 / 🔁

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

    // Windows master volume of the active output device (0..100). Not persisted — live OS value.
    public double SystemVolume
    {
        get => _audioPlayer.SystemVolume * 100;
        set
        {
            _audioPlayer.SystemVolume = (float)(value / 100);
            OnPropertyChanged(nameof(SystemVolume));
        }
    }

    public List<OutputDeviceItem> OutputDevices { get; private set; } = new();

    public OutputDeviceItem? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set
        {
            if (SetProperty(ref _selectedOutputDevice, value))
            {
                _audioPlayer.OutputDeviceId = value?.Id;
                _appSettings.OutputDeviceId = value?.Id;
                _appSettings.Save();
                OnPropertyChanged(nameof(SystemVolume)); // master volume is per-device
            }
        }
    }

    // When true, the phone is the audio sink: desktop output is muted and tracks are
    // transcoded/streamed to the phone. Session-only (not persisted).
    public bool RemoteSink
    {
        get => _remoteSink;
        set
        {
            if (SetProperty(ref _remoteSink, value))
            {
                _audioPlayer.LocalMuted = value;
                if (value && NowPlaying != null)
                    AudioStreamService.Prewarm(NowPlaying.FullPath, _lastStreamBitrate);
            }
        }
    }

    // Set by the control API when the phone requests a stream, so PlayFile can prewarm
    // the next track at the same bitrate.
    public int LastStreamBitrate
    {
        get => _lastStreamBitrate;
        set => _lastStreamBitrate = AudioStreamService.ClampBitrate(value);
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

    private bool _isLoadingFiles;
    public bool IsLoadingFiles
    {
        get => _isLoadingFiles;
        set => SetProperty(ref _isLoadingFiles, value);
    }

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
    public ICommand ToggleShuffleCommand { get; }
    public ICommand CycleRepeatCommand { get; }
    public ICommand PlayFolderCommand { get; }
    public ICommand ExitRemoteSinkCommand { get; }

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

        // Restore output device selection; populate the device list for the picker.
        _audioPlayer.OutputDeviceId = _appSettings.OutputDeviceId;
        OutputDevices = _audioPlayer.GetOutputDevices()
            .Select(d => new OutputDeviceItem(d.Id, d.Name)).ToList();
        // Show the actual current device by default (saved one, else the system default,
        // else just the first active device so the box is never blank).
        var currentDeviceId = _audioPlayer.OutputDeviceId ?? _audioPlayer.GetDefaultDeviceId();
        _selectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Id == currentDeviceId)
            ?? OutputDevices.FirstOrDefault();

        _audioPlayer.PositionChanged += OnPositionChanged;
        _audioPlayer.PlaybackStopped += OnPlaybackStopped;
        _audioPlayer.SystemVolumeChanged += OnSystemVolumeChanged;
        _audioPlayer.DevicesChanged += OnDevicesChanged;

        PlayPauseCommand = new RelayCommand(PlayPause);
        StopCommand = new RelayCommand(Stop);
        PreviousCommand = new RelayCommand(Previous);
        NextCommand = new RelayCommand(Next);
        PlayFileCommand = new RelayCommand(PlayFile);
        RefreshArtistInfoCommand = new RelayCommand(RefreshArtistInfo);
        ToggleShuffleCommand = new RelayCommand(() => ShuffleEnabled = !ShuffleEnabled);
        CycleRepeatCommand = new RelayCommand(CycleRepeat);
        PlayFolderCommand = new RelayCommand(p => _ = PlayFolderTreeAsync((p as FolderNode)?.FullPath));
        // Desktop-side kill switch for remote-sink mode (phone is the audio sink).
        ExitRemoteSinkCommand = new RelayCommand(() => RemoteSink = false);

        RefreshPlaylists();

        // Local HTTP control API (for the mobile remote). Fails soft if it can't bind.
        _controlApi = new ControlApiService(this);
        _controlApi.Start();
    }

    private void CycleRepeat()
    {
        Repeat = _repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off
        };
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

    public void RemoveRecentPlayedFolderTree(string folderPath)
    {
        RecentPlayedStore.RemoveTree(folderPath);
        RefreshRecentPlayedFolders();
    }

    public void ClearRecentPlayedFolders()
    {
        RecentPlayedStore.Clear();
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
        // Ignore the selection echo from clicking ▶ on an unselected folder; the recursive
        // play queue already owns this folder and a reload would wipe it (consume once).
        if (_playTreeFolder != null && string.Equals(path, _playTreeFolder, StringComparison.OrdinalIgnoreCase))
        {
            _playTreeFolder = null;
            return;
        }
        _playTreeFolder = null;

        // Cancel any previous loading operation
        _loadingCts?.Cancel();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;

        // Leaving playlist view: clear the playlist context and deselect it in the list.
        _currentPlaylistId = null;
        _shuffleOrder = null;
        OnPropertyChanged(nameof(IsPlaylistView));
        SelectedPlaylist = null;

        CurrentFolderPath = path;

        // Remember the last real folder so it can be restored on next launch.
        // ponytail: writes settings.json per folder click (same as the Volume setter); fine at this rate.
        if (Directory.Exists(path))
        {
            _appSettings.LastFolderPath = path;
            _appSettings.Save();
        }

        MusicFiles.Clear();
        AlbumCover = null;

        _ = LoadFolderAsync(path, token);
        _ = LoadAlbumCoverAsync(path, token);
    }


    /// <summary>Queues every supported track under <paramref name="folderPath"/> (recursively,
    /// including subfolders) and starts playback. Shuffle/repeat apply to the queue as usual.</summary>
    public async Task PlayFolderTreeAsync(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;

        _loadingCts?.Cancel();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;
        IsLoadingFiles = true;
        // Claim this folder so the selection echo (clicking ▶ moves tree selection) is ignored.
        _playTreeFolder = folderPath;
        try
        {

        // Leaving playlist view: this is a folder-style queue.
        _currentPlaylistId = null;
        _shuffleOrder = null;
        OnPropertyChanged(nameof(IsPlaylistView));
        SelectedPlaylist = null;

        var files = await Task.Run(() =>
        {
            List<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
                    .Where(MusicMetadataService.IsSupportedFile)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch { paths = new List<string>(); }

            // Enrich from the cache; parse tags for not-yet-scanned files so duration/title show.
            var meta = DatabaseService.Instance.GetCachedFilesUnder(folderPath)
                .GroupBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var result = new List<MusicFile>(paths.Count);
            var parsed = new List<MusicFile>();
            foreach (var p in paths)
            {
                if (meta.TryGetValue(p, out var cached)) { result.Add(cached); continue; }
                var mf = _metadataService.ReadMetadata(p)
                         ?? new MusicFile { FullPath = p, FileName = Path.GetFileName(p) };
                result.Add(mf);
                parsed.Add(mf);
            }

            // Warm the cache (grouped by directory) so a re-play is instant.
            // ponytail: parses every uncached file up front; fine for a folder tree,
            // make it progressive if someone "plays" their whole library and waits.
            foreach (var grp in parsed.GroupBy(f => Path.GetDirectoryName(f.FullPath) ?? string.Empty))
                if (grp.Key.Length > 0) DatabaseService.Instance.UpsertFiles(grp.Key, grp);

            return result;
        }, token);

        if (token.IsCancellationRequested) return;

        CurrentFolderPath = folderPath;
        MusicFiles.Clear();
        AlbumCover = null;
        foreach (var f in files)
        {
            HookFile(f);
            MusicFiles.Add(f);
        }

        if (MusicFiles.Count > 0)
        {
            SelectedFile = MusicFiles[0];
            PlayFile(MusicFiles[0]);
            // Cover comes from the playing track's folder (the clicked folder may hold only subfolders).
            _ = LoadAlbumCoverAsync(Path.GetDirectoryName(MusicFiles[0].FullPath) ?? folderPath, token);
        }
        }
        finally
        {
            // Only clear if a newer load hasn't superseded this one.
            if (token == _loadingCts?.Token) IsLoadingFiles = false;
        }
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

        // Spinner only for cold folders (cache miss) — a cache hit is instant, no flicker.
        if (cached.Count == 0) IsLoadingFiles = true;
        try
        {
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
        finally
        {
            if (token == _loadingCts?.Token) IsLoadingFiles = false;
        }
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

            // Phone is the sink: warm the transcode cache for THIS track and the NEXT one,
            // so the phone's stream request (and the upcoming track change) serve instantly.
            if (_remoteSink)
            {
                AudioStreamService.Prewarm(file.FullPath, _lastStreamBitrate);
                var next = GetAdjacent(file, +1, _repeat == RepeatMode.All);
                if (next != null)
                    AudioStreamService.Prewarm(next.FullPath, _lastStreamBitrate);
            }

            AddRecentPlayedFolder(Path.GetDirectoryName(file.FullPath));
            PersistPlaylistState();   // remember this as the playlist's last track

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
        var prev = GetAdjacent(NowPlaying, -1, _repeat == RepeatMode.All);
        if (prev != null)
        {
            SelectedFile = prev;
            PlayFile(prev);
        }
    }

    private void Next()
    {
        if (NowPlaying == null || MusicFiles.Count == 0) return;
        var next = GetAdjacent(NowPlaying, +1, _repeat == RepeatMode.All);
        if (next != null)
        {
            SelectedFile = next;
            PlayFile(next);
        }
    }

    /// <summary>Returns the track <paramref name="direction"/> steps from <paramref name="current"/> in
    /// the active queue, following shuffle order when enabled. Wraps around when <paramref name="wrap"/>
    /// is true, otherwise returns null at the ends.</summary>
    private MusicFile? GetAdjacent(MusicFile current, int direction, bool wrap)
    {
        int n = MusicFiles.Count;
        if (n == 0) return null;
        int curIdx = MusicFiles.IndexOf(current);
        if (curIdx < 0) return null;

        if (_shuffleEnabled)
        {
            EnsureShuffleOrder();
            int pos = _shuffleOrder!.IndexOf(curIdx);
            if (pos < 0) return null;
            int npos = pos + direction;
            if (npos < 0 || npos >= n)
            {
                if (!wrap) return null;
                npos = (npos % n + n) % n;
            }
            return MusicFiles[_shuffleOrder[npos]];
        }

        int nidx = curIdx + direction;
        if (nidx < 0 || nidx >= n)
        {
            if (!wrap) return null;
            nidx = (nidx % n + n) % n;
        }
        return MusicFiles[nidx];
    }

    private void EnsureShuffleOrder()
    {
        if (_shuffleOrder != null && _shuffleOrder.Count == MusicFiles.Count) return;
        var order = Enumerable.Range(0, MusicFiles.Count).ToList();
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        _shuffleOrder = order;
    }

    // OS master volume changed (possibly from outside the app) — refresh the bound bar.
    // Fires on a COM thread; marshal to the UI thread.
    private void OnSystemVolumeChanged()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(SystemVolume)));
    }

    // Render devices added/removed (e.g. Bluetooth) — rebuild the picker list on the UI thread.
    private void OnDevicesChanged()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshOutputDevices);
    }

    // Re-enumerate active output devices and re-resolve the shown selection. Safe to call on the
    // UI thread. Also invoked when the picker dropdown opens, because the CoreAudio device-change
    // callback is unreliable for Bluetooth connects (device wouldn't appear until an app restart).
    public void RefreshOutputDevices()
    {
        OutputDevices = _audioPlayer.GetOutputDevices()
            .Select(d => new OutputDeviceItem(d.Id, d.Name)).ToList();
        OnPropertyChanged(nameof(OutputDevices));

        // Re-resolve the shown selection (record equality keeps it if still present; else
        // the service already fell back to default). Set the field directly — no re-persist/switch.
        var currentId = _audioPlayer.OutputDeviceId ?? _audioPlayer.GetDefaultDeviceId();
        _selectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Id == currentId)
            ?? OutputDevices.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedOutputDevice));
        OnPropertyChanged(nameof(SystemVolume)); // active endpoint may have changed
    }

    // Restore the folder open when the app last closed. Called once after the window loads.
    public async Task RestoreLastFolderAsync()
    {
        var path = _appSettings.LastFolderPath;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        LoadFolder(path);
        await FolderTree.NavigateToPathAsync(path);
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

        if (NowPlaying == null) return;

        // Repeat-one: replay the same track.
        if (_repeat == RepeatMode.One)
        {
            var same = NowPlaying;
            System.Windows.Application.Current?.Dispatcher.Invoke(() => PlayFile(same));
            return;
        }

        // Auto-play next track when current track ends naturally (wrap when Repeat-all).
        var nextFile = GetAdjacent(NowPlaying, +1, _repeat == RepeatMode.All);
        if (nextFile != null)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                SelectedFile = nextFile;
                PlayFile(nextFile);
            });
        }
    }

    private static string FormatTime(TimeSpan time)
    {
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
    }

    // ----------------------------------------------------------- Playlists

    /// <summary>Reloads the playlist list from the cache, preserving the current selection by id.</summary>
    public void RefreshPlaylists()
    {
        var selId = _selectedPlaylist?.Id;
        Playlists.Clear();
        foreach (var p in DatabaseService.Instance.GetPlaylists())
            Playlists.Add(p);
        if (selId != null)
        {
            var match = Playlists.FirstOrDefault(p => p.Id == selId.Value);
            if (match != null) SelectedPlaylist = match;
        }
    }

    public Playlist? CreatePlaylist(string name)
    {
        var id = DatabaseService.Instance.CreatePlaylist(name);
        if (id <= 0) return null;
        RefreshPlaylists();
        return Playlists.FirstOrDefault(p => p.Id == id);
    }

    public void RenamePlaylist(Playlist? playlist, string newName)
    {
        if (playlist == null || string.IsNullOrWhiteSpace(newName)) return;
        DatabaseService.Instance.RenamePlaylist(playlist.Id, newName);
        playlist.Name = newName.Trim();
    }

    public void DeletePlaylist(Playlist? playlist)
    {
        if (playlist == null) return;
        DatabaseService.Instance.DeletePlaylist(playlist.Id);
        if (_currentPlaylistId == playlist.Id)
        {
            _currentPlaylistId = null;
            _shuffleOrder = null;
            OnPropertyChanged(nameof(IsPlaylistView));
            MusicFiles.Clear();
        }
        Playlists.Remove(playlist);
    }

    /// <summary>Loads a playlist's tracks into the grid/queue (cache read; no filesystem scan).</summary>
    public void LoadPlaylist(Playlist? playlist)
    {
        if (playlist == null) return;

        _loadingCts?.Cancel();
        _loadingCts = new CancellationTokenSource();

        // Suppress write-back while we apply the saved state, otherwise restoring the
        // mode would overwrite last_full_path with whatever was playing before.
        _suppressPlaylistPersist = true;

        _currentPlaylistId = playlist.Id;
        _shuffleOrder = null;
        OnPropertyChanged(nameof(IsPlaylistView));

        CurrentFolderPath = playlist.Name;
        MusicFiles.Clear();
        AlbumCover = null;

        foreach (var f in DatabaseService.Instance.GetPlaylistFiles(playlist.Id))
        {
            HookFile(f);
            MusicFiles.Add(f);
        }

        // Restore saved shuffle/repeat mode and the last-played track.
        ShuffleEnabled = playlist.Shuffle;
        Repeat = (RepeatMode)playlist.Repeat;

        SelectedFile = (string.IsNullOrEmpty(playlist.LastFullPath)
            ? null
            : MusicFiles.FirstOrDefault(f =>
                string.Equals(f.FullPath, playlist.LastFullPath, StringComparison.OrdinalIgnoreCase)))
            ?? MusicFiles.FirstOrDefault();

        _suppressPlaylistPersist = false;
    }

    // Writes the current playback state (shuffle/repeat + last track) to the open playlist.
    // No-op outside playlist view or while a playlist is being restored.
    private void PersistPlaylistState()
    {
        if (_suppressPlaylistPersist || _currentPlaylistId == null) return;
        DatabaseService.Instance.SavePlaylistState(
            _currentPlaylistId.Value, _shuffleEnabled, (int)_repeat, NowPlaying?.FullPath);

        // Keep the in-memory Playlist in sync so reopening it this session restores correctly.
        var p = Playlists.FirstOrDefault(x => x.Id == _currentPlaylistId.Value);
        if (p != null)
        {
            p.Shuffle = _shuffleEnabled;
            p.Repeat = (int)_repeat;
            p.LastFullPath = NowPlaying?.FullPath;
        }
    }

    private void ReloadCurrentPlaylist()
    {
        if (_currentPlaylistId == null) return;
        MusicFiles.Clear();
        foreach (var f in DatabaseService.Instance.GetPlaylistFiles(_currentPlaylistId.Value))
        {
            HookFile(f);
            MusicFiles.Add(f);
        }
        _shuffleOrder = null;
    }

    public void AddFilesToPlaylist(int playlistId, IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (list.Count == 0) return;
        DatabaseService.Instance.AddToPlaylist(playlistId, list);
        UpdatePlaylistCount(playlistId);
        if (playlistId == _currentPlaylistId) ReloadCurrentPlaylist();
    }

    /// <summary>Adds every supported music file under <paramref name="folderPath"/> (recursively,
    /// including subfolders) to the playlist. Enumeration runs off the UI thread.</summary>
    public async Task AddFolderToPlaylistAsync(int playlistId, string folderPath)
    {
        var paths = await Task.Run(() =>
        {
            try
            {
                return Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
                    .Where(MusicMetadataService.IsSupportedFile)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch { return new List<string>(); }
        });
        AddFilesToPlaylist(playlistId, paths);
    }

    public void RemoveFromCurrentPlaylist(IEnumerable<MusicFile> files)
    {
        if (_currentPlaylistId == null) return;
        var list = files.Where(f => f.PlaylistEntryId != null).ToList();
        if (list.Count == 0) return;

        DatabaseService.Instance.RemoveFromPlaylist(
            _currentPlaylistId.Value, list.Select(f => f.PlaylistEntryId!.Value));
        foreach (var f in list) MusicFiles.Remove(f);
        _shuffleOrder = null;
        UpdatePlaylistCount(_currentPlaylistId.Value);
    }

    public void MoveEntry(MusicFile? file, int delta)
    {
        if (_currentPlaylistId == null || file == null) return;
        int idx = MusicFiles.IndexOf(file);
        int dest = idx + delta;
        if (idx < 0 || dest < 0 || dest >= MusicFiles.Count) return;

        MusicFiles.Move(idx, dest);
        _shuffleOrder = null;
        DatabaseService.Instance.ReorderPlaylist(
            _currentPlaylistId.Value,
            MusicFiles.Where(f => f.PlaylistEntryId != null)
                      .Select(f => f.PlaylistEntryId!.Value).ToList());
    }

    private void UpdatePlaylistCount(int id)
    {
        try
        {
            var existing = Playlists.FirstOrDefault(p => p.Id == id);
            var fresh = DatabaseService.Instance.GetPlaylists().FirstOrDefault(p => p.Id == id);
            if (existing != null && fresh != null) existing.TrackCount = fresh.TrackCount;
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _controlApi.Dispose();
        _audioPlayer.PositionChanged -= OnPositionChanged;
        _audioPlayer.PlaybackStopped -= OnPlaybackStopped;
        _audioPlayer.Dispose();
        LibraryCacheService.Instance.Stop();
    }
}

public enum RepeatMode
{
    Off,
    All,
    One
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
