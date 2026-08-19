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
    // Coalesces overlapping position-timer ticks into at most one queued UI-thread update — see
    // OnPositionChanged. _positionUpdatePending is set/read from multiple timer threads, hence
    // volatile; _latestPosition just needs the freshest value when the queued update runs.
    private volatile bool _positionUpdatePending;
    private TimeSpan _latestPosition;

    private readonly MusicMetadataService _metadataService;
    private readonly AudioPlayerService _audioPlayer;
    private readonly FileOperationsService _fileOperations;
    private readonly ArtistInfoService _artistInfoService;
    private readonly LyricsService _lyricsService;
    private readonly WhisperAlignService _whisperAlignService;
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
    private readonly List<ImageSource> _albumCovers = new();
    private int _albumCoverIndex;
    private System.Windows.Threading.DispatcherTimer? _albumCoverTimer;
    private CancellationTokenSource? _albumCoverCts;
    private OutputDeviceItem? _selectedOutputDevice;
    private bool _remoteSink;
    // Last bitrate the phone requested; used to prewarm the transcode cache.
    private int _lastStreamBitrate = AudioStreamService.DefaultBitrate;
    // ponytail: debounce settings.json writes while the volume slider is dragged — the setter
    // fires per tick and Save() is a synchronous File.WriteAllText.
    private System.Windows.Threading.DispatcherTimer? _volumeSaveTimer;

    // Karaoke lyrics state.
    private CancellationTokenSource? _lyricsCts;
    private bool _hasSyncedLyrics;
    private bool _showLyrics;
    private bool _isLyricsEditMode;
    private string _lyricsEditText = string.Empty;
    private bool _isLoadingLyrics;
    private string _lyricsStatus = string.Empty;
    private int _currentLyricIndex = -1;
    private bool _canSyncLyrics;
    private bool _karaokeMode;
    private bool _lyricsFollowing = true;
    // Debounced auto-save for lyrics edits (now/nudge timestamps, raw-text typing) — mirrors
    // _volumeSaveTimer's idiom. Suppressed while lyrics are being populated programmatically
    // (load/fetch), not user-edited, mirroring FolderNode's ApplyCache/EnablePersist gate.
    private System.Windows.Threading.DispatcherTimer? _lyricsSaveTimer;
    private bool _suppressLyricsAutoSave;

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
    private readonly List<MusicFile> _shuffleHistory = new();   // shuffle play order, for Previous
    private readonly HashSet<string> _shufflePlayed = new(StringComparer.OrdinalIgnoreCase);  // paths played this shuffle cycle
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
                ResetShuffleState();
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
            if (SetProperty(ref _currentPosition, value))
            {
                if (!_isSliderDragging && TotalDuration.TotalSeconds > 0)
                {
                    _sliderValue = value.TotalSeconds / TotalDuration.TotalSeconds * 100;
                    OnPropertyChanged(nameof(SliderValue));
                }
            }
        }
    }

    public TimeSpan TotalDuration
    {
        get => _totalDuration;
        set
        {
            if (SetProperty(ref _totalDuration, value))
            {
                // Pre-existing bug, found while diagnosing the timeline: TotalDurationFormatted
                // was never re-notified, so the transport bar's total-duration readout only ever
                // showed whatever it evaluated to at initial binding (0:00, before any track had
                // played) and never updated again for the rest of the session.
                OnPropertyChanged(nameof(TotalDurationFormatted));
                RecomputeLyricRowGaps(); // px/second scale is derived from TotalDuration
            }
        }
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
            SaveSettingsDebounced();
            OnPropertyChanged(nameof(Volume));
        }
    }

    private void SaveSettingsDebounced()
    {
        if (_volumeSaveTimer == null)
        {
            _volumeSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _volumeSaveTimer.Tick += (_, _) =>
            {
                _volumeSaveTimer!.Stop();
                _appSettings.Save();
            };
        }
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
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

    public List<int> BufferOptions { get; } = new() { 100, 200, 300, 500, 1000 };

    public int OutputBufferMs
    {
        get => _appSettings.OutputBufferMs;
        set
        {
            if (_appSettings.OutputBufferMs == value) return;
            _appSettings.OutputBufferMs = value;
            _appSettings.Save();
            _audioPlayer.OutputBufferMs = value;
            OnPropertyChanged(nameof(OutputBufferMs));
        }
    }

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

    // Karaoke lyrics: lines for the ListBox (view mode), highlighted by OnPositionChanged
    // when HasSyncedLyrics. Populated by LoadLyricsFromCacheAsync (on play) or a Find/Sync fetch.
    public ObservableCollection<LyricLine> LyricsLines { get; } = new();

    public bool HasSyncedLyrics
    {
        get => _hasSyncedLyrics;
        private set => SetProperty(ref _hasSyncedLyrics, value);
    }

    // Whether the lyrics row list is currently auto-scrolling to follow the playhead, vs. frozen
    // by a row selection/hover/recent manual scroll (see MainWindow.xaml.cs
    // IsLyricsAutoScrollSuspended). Pushed from code-behind — the freeze reasons live in the View
    // (ListBox selection/mouse state) — purely so the "Follow" header button can bind to it with
    // the same DataTrigger idiom as the Karaoke toggle.
    public bool LyricsFollowing
    {
        get => _lyricsFollowing;
        set => SetProperty(ref _lyricsFollowing, value);
    }

    // Karaoke panel visibility toggle (transport bar button).
    public bool ShowLyrics
    {
        get => _showLyrics;
        set => SetProperty(ref _showLyrics, value);
    }

    public bool IsLyricsEditMode
    {
        get => _isLyricsEditMode;
        private set
        {
            // Mutually exclusive with KaraokeMode — both compete for the same Row=1 slot, and
            // with both on every view's visibility trigger fails, leaving a blank panel. Only
            // clear the other when turning THIS one on (never on turning off), so the two
            // setters can't ping-pong into each other.
            if (SetProperty(ref _isLyricsEditMode, value) && value)
                KaraokeMode = false;
        }
    }

    public string LyricsEditText
    {
        get => _lyricsEditText;
        set
        {
            if (SetProperty(ref _lyricsEditText, value) && !_suppressLyricsAutoSave)
                ScheduleLyricsAutoSave();
        }
    }

    public bool IsLoadingLyrics
    {
        get => _isLoadingLyrics;
        private set => SetProperty(ref _isLoadingLyrics, value);
    }

    public string LyricsStatus
    {
        get => _lyricsStatus;
        private set => SetProperty(ref _lyricsStatus, value);
    }

    public int CurrentLyricIndex
    {
        get => _currentLyricIndex;
        private set
        {
            if (SetProperty(ref _currentLyricIndex, value))
                UpdateKaraokeOffsets();
        }
    }

    // True whenever there are lyric lines loaded — offers the (re-runnable) Sync button and
    // drives the Edit/Done toggle for hand-timed edits made via the "now"/nudge row controls.
    public bool CanSyncLyrics
    {
        get => _canSyncLyrics;
        private set => SetProperty(ref _canSyncLyrics, value);
    }

    // Karaoke big-view toggle: swaps the line list for a big centered scrolling window
    // (KaraokeWindow), driven by the same CurrentLyricIndex as the list highlight.
    public bool KaraokeMode
    {
        get => _karaokeMode;
        set
        {
            // Mutually exclusive with IsLyricsEditMode — see its setter's comment. Only clear
            // the other when turning THIS one on, so the two setters can't ping-pong.
            if (SetProperty(ref _karaokeMode, value) && value)
            {
                IsLyricsEditMode = false;

                // Compute the active line from the live position instead of waiting for the
                // next ~100ms tick — otherwise turning karaoke on before a tick has landed (or
                // during the intro, before the first line's timestamp) shows a blank panel.
                if (HasSyncedLyrics) UpdateActiveLyricLine(_audioPlayer.CurrentPosition);
                UpdateKaraokeOffsets();
            }
        }
    }

    // Every LyricsLines entry, one KaraokeWindowLine each — populated once per track by
    // RebuildKaraokeWindow, then updated IN PLACE (Offset/IsCurrent only, no Clear/Add) by
    // UpdateKaraokeOffsets as CurrentLyricIndex advances. Keeping the collection structurally
    // stable (not rebuilt every tick) is what lets MainWindow's code-behind pan the karaoke
    // big-view smoothly via a TranslateTransform — regenerating item containers on every line
    // change would kill that animation. All lines render; the view clips/fades to a window via
    // XAML (ClipToBounds + OpacityMask), not by excluding lines here.
    public ObservableCollection<KaraokeWindowLine> KaraokeWindow { get; } = new();

    // Offsets beyond this are visually indistinguishable (smallest/dimmest style) — the XAML
    // DataTriggers only define -3..3, so clamp here rather than leaving huge raw offsets unstyled.
    private const int KaraokeMaxOffset = 3;

    private void RebuildKaraokeWindow()
    {
        KaraokeWindow.Clear();
        foreach (var line in LyricsLines)
            KaraokeWindow.Add(new KaraokeWindowLine { Text = line.Text });
        UpdateKaraokeOffsets();
    }

    private void UpdateKaraokeOffsets()
    {
        // No active line yet (e.g. still in the intro, before the first timestamp) — anchor at
        // the top of the song and show the opening lines dimmed, rather than nothing marked.
        var hasCurrent = CurrentLyricIndex >= 0 && CurrentLyricIndex < LyricsLines.Count;
        var anchor = hasCurrent ? CurrentLyricIndex : 0;

        for (int i = 0; i < KaraokeWindow.Count; i++)
        {
            KaraokeWindow[i].Offset = Math.Clamp(i - anchor, -KaraokeMaxOffset, KaraokeMaxOffset);
            KaraokeWindow[i].IsCurrent = hasCurrent && i == CurrentLyricIndex;
        }
    }

    // ponytail: LyricRowPxPerSecond is the row-spacing density knob — tune to taste. Public so
    // MainWindow's code-behind can derive the same content-Y-per-second for auto-scroll.
    public const double LyricRowPxPerSecond = 6.0;
    // ponytail: approximate fixed row content height; if the row template is restyled this
    // needs updating to match.
    private const double LyricRowHeightPx = 24.0;
    private const double MinGapPx = 2.0;

    /// <summary>Sets each line's <see cref="LyricLine.GapPx"/> — a top spacer proportional to
    /// the time since the previous line, so the row list reads as a time-proportional layout
    /// (sparse where lines are far apart, tight where they come fast) instead of an evenly
    /// stacked one. Order-dependent (each line's gap needs its predecessor's Time), so this is
    /// always a whole-list pass, not something a single LyricLine can compute for itself.
    /// Plain/unsynced lyrics (no real timestamps) stack normally — all gaps are 0.
    /// Each row also has its own fixed height, so for TRUE proportionality (line-to-line
    /// distance, not just the gap, matching elapsed time) that row height is subtracted back
    /// out of the gap before it's applied — distance between consecutive line tops then works
    /// out to exactly gapSeconds * LyricRowPxPerSecond. Floored at MinGapPx so rows never
    /// overlap when a gap is shorter than a row's own height (that case can't be made
    /// proportional — rows have physical height). The first line has no preceding row to
    /// subtract, so it's positioned straight from the top of the list.</summary>
    private void RecomputeLyricRowGaps()
    {
        for (int i = 0; i < LyricsLines.Count; i++)
        {
            if (!HasSyncedLyrics)
            {
                LyricsLines[i].GapPx = 0;
                continue;
            }
            if (i == 0)
            {
                LyricsLines[i].GapPx = Math.Max(0, LyricsLines[i].Time.TotalSeconds * LyricRowPxPerSecond);
                continue;
            }
            var gapSeconds = (LyricsLines[i].Time - LyricsLines[i - 1].Time).TotalSeconds;
            LyricsLines[i].GapPx = Math.Max(MinGapPx, gapSeconds * LyricRowPxPerSecond - LyricRowHeightPx);
        }
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
    public ICommand FindLyricsCommand { get; }
    public ICommand SyncLyricsWhisperCommand { get; }
    public ICommand ClearLyricsCommand { get; }
    public ICommand EditLyricsCommand { get; }
    public ICommand ToggleLyricsCommand { get; }
    public ICommand ToggleKaraokeModeCommand { get; }
    public ICommand StartKaraokeCommand { get; }
    public ICommand SetLyricLineNowCommand { get; }
    public ICommand NudgeLyricLineBackCommand { get; }
    public ICommand NudgeLyricLineForwardCommand { get; }

    public AudioPlayerService AudioPlayer => _audioPlayer;
    public FileOperationsService FileOperations => _fileOperations;
    public MusicMetadataService MetadataService => _metadataService;
    public LyricsService Lyrics => _lyricsService;

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
        _lyricsService = new LyricsService();
        _whisperAlignService = new WhisperAlignService();
        _appSettings = AppSettings.Load();

        FolderTree = new FolderTreeViewModel();
        RecentFolders = RecentFolders.Load();
        RecentPlayedStore = Models.RecentPlayedFolders.Load();
        RefreshRecentPlayedFolders();

        // Restore volume from settings
        _audioPlayer.Volume = _appSettings.Volume / 100f;

        // Restore output device selection; populate the device list for the picker.
        _audioPlayer.OutputBufferMs = _appSettings.OutputBufferMs;
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
        FindLyricsCommand = new RelayCommand(FindLyrics);
        SyncLyricsWhisperCommand = new RelayCommand(SyncLyricsWhisper);
        ClearLyricsCommand = new RelayCommand(ClearLyrics);
        EditLyricsCommand = new RelayCommand(EditLyrics);
        ToggleLyricsCommand = new RelayCommand(() => ShowLyrics = !ShowLyrics);
        ToggleKaraokeModeCommand = new RelayCommand(() => KaraokeMode = !KaraokeMode);
        StartKaraokeCommand = new RelayCommand(() => _ = StartKaraokeAsync());
        SetLyricLineNowCommand = new RelayCommand(p => SetLyricLineNow(p as LyricLine));
        NudgeLyricLineBackCommand = new RelayCommand(p => NudgeLyricLine(p as LyricLine, -0.2));
        NudgeLyricLineForwardCommand = new RelayCommand(p => NudgeLyricLine(p as LyricLine, 0.2));

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

        // Browsing away from the restored track's folder retires the pending re-selection
        // (loading that same folder again — the tree-selection echo, Refresh — keeps it).
        if (_restoreSelectPath != null &&
            !string.Equals(Path.GetDirectoryName(_restoreSelectPath), path, StringComparison.OrdinalIgnoreCase))
            _restoreSelectPath = null;

        // Cancel any previous loading operation
        _loadingCts?.Cancel();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;

        // Leaving playlist view: clear the playlist context and deselect it in the list.
        _currentPlaylistId = null;
        ResetShuffleState();
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
        ResetAlbumCover();

        _ = LoadFolderAsync(path, token);
        _ = LoadAlbumCoverAsync(path, null);
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
        ResetShuffleState();
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
        ResetAlbumCover();
        var withLyrics = DatabaseService.Instance.GetPathsWithLyrics(folderPath);
        foreach (var f in files)
        {
            f.HasLyrics = withLyrics.Contains(f.FullPath);
            HookFile(f);
            MusicFiles.Add(f);
        }

        if (MusicFiles.Count > 0)
        {
            SelectedFile = MusicFiles[0];
            PlayFile(MusicFiles[0]);
            // Cover comes from the playing track's folder (the clicked folder may hold only subfolders).
            _ = LoadAlbumCoverAsync(Path.GetDirectoryName(MusicFiles[0].FullPath) ?? folderPath, MusicFiles[0].FullPath);
        }
        }
        finally
        {
            // Only clear if a newer load hasn't superseded this one.
            if (token == _loadingCts?.Token) IsLoadingFiles = false;
        }
    }

    /// <summary>Stops the cycling timer and clears the album cover state. Call before loading
    /// a new folder/track's art (or when there is none).</summary>
    private void ResetAlbumCover()
    {
        _albumCoverCts?.Cancel();
        _albumCoverTimer?.Stop();
        _albumCovers.Clear();
        _albumCoverIndex = 0;
        AlbumCover = null;
    }

    private void AlbumCoverTimer_Tick(object? sender, EventArgs e)
    {
        if (_albumCovers.Count <= 1)
            return;
        _albumCoverIndex = (_albumCoverIndex + 1) % _albumCovers.Count;
        AlbumCover = _albumCovers[_albumCoverIndex];
    }

    private async Task LoadAlbumCoverAsync(string folderPath, string? filePath)
    {
        _albumCoverCts?.Cancel();
        _albumCoverCts = new CancellationTokenSource();
        var token = _albumCoverCts.Token;

        List<byte[]> arts;
        try
        {
            arts = await Task.Run(() => _metadataService.GetAlbumArts(folderPath, filePath), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (token.IsCancellationRequested)
            return;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (token.IsCancellationRequested)
                return;

            _albumCoverTimer?.Stop();
            _albumCovers.Clear();

            foreach (var bytes in arts)
            {
                try
                {
                    var img = new BitmapImage();
                    using var ms = new MemoryStream(bytes);
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                    img.Freeze();
                    _albumCovers.Add(img);
                }
                catch
                {
                    // skip images that fail to decode
                }
            }

            _albumCoverIndex = 0;
            AlbumCover = _albumCovers.Count > 0 ? _albumCovers[0] : null;

            if (_albumCovers.Count > 1)
            {
                if (_albumCoverTimer == null)
                {
                    _albumCoverTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(15)
                    };
                    _albumCoverTimer.Tick += AlbumCoverTimer_Tick;
                }
                _albumCoverTimer.Start();
            }
            else
            {
                _albumCoverTimer?.Stop();
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
            var withLyrics = DatabaseService.Instance.GetPathsWithLyrics(path);
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (token.IsCancellationRequested) return;
                foreach (var file in cached)
                {
                    file.HasLyrics = withLyrics.Contains(file.FullPath);
                    HookFile(file);
                    MusicFiles.Add(file);
                    if (MusicFiles.Count == 1 && _restoreSelectPath == null)
                        SelectedFile = file;
                }
                ApplyRestoreSelection();
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
                ReconcileMusicFiles(fresh, path);
                ApplyRestoreSelection();
            });
        }
        finally
        {
            if (token == _loadingCts?.Token) IsLoadingFiles = false;
        }
    }

    private void ReconcileMusicFiles(IReadOnlyList<MusicFile> fresh, string folderPath)
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

        // Add new entries and replace changed ones. RefreshFolderFilesAsync always returns fresh
        // MusicFile instances (even for unchanged files, since it re-reads the DB) — but NowPlaying,
        // GetAdjacent's shuffle history, and the DataGrid's SelectedFile all hold references into
        // MusicFiles. Swapping in a new instance on every harmless background refresh silently
        // orphaned those references (IndexOf/ReferenceEquals lookups missed the new object), which
        // broke auto-advance/Next/Previous whenever a refresh landed while a track was playing. So:
        // keep the existing grid instance unless the file actually changed on disk.
        var withLyrics = DatabaseService.Instance.GetPathsWithLyrics(folderPath);
        foreach (var f in fresh)
        {
            if (indexByPath.TryGetValue(f.FullPath, out var idx))
            {
                var cur = MusicFiles[idx];
                bool changed = cur.FileSize != f.FileSize ||
                    Math.Abs((cur.FileModifiedUtc - f.FileModifiedUtc).TotalSeconds) > 2;
                if (changed)
                {
                    f.HasLyrics = withLyrics.Contains(f.FullPath);
                    HookFile(f);
                    MusicFiles[idx] = f;
                    if (ReferenceEquals(NowPlaying, cur))
                        NowPlaying = f;
                    // Shuffle history holds instances too — retarget any so Previous doesn't
                    // silently die if the playing track changed on disk mid-shuffle.
                    for (int i = 0; i < _shuffleHistory.Count; i++)
                        if (ReferenceEquals(_shuffleHistory[i], cur))
                            _shuffleHistory[i] = f;
                }
                else
                {
                    cur.HasLyrics = withLyrics.Contains(f.FullPath);
                }
            }
            else
            {
                f.HasLyrics = withLyrics.Contains(f.FullPath);
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

            RecordPlay(file);

            // Remember it for next launch (re-selected on restart) — and once something plays,
            // the startup re-selection has done its job.
            _restoreSelectPath = null;
            _appSettings.LastPlayedPath = file.FullPath;
            _appSettings.Save();

            // Cover follows the track: its embedded art wins over the folder's images.
            _ = LoadAlbumCoverAsync(Path.GetDirectoryName(file.FullPath) ?? "", file.FullPath);

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

            // Lyrics: cache/embedded only on play — Find/Sync are on-request (network + AI cost).
            _ = LoadLyricsFromCacheAsync(file);
        }
        catch
        {
            // Handle playback error
            NowPlaying = null;
            IsPlaying = false;
        }
    }

    // Bumps the play counter (in memory + cache DB) and remembers the track in the shuffle
    // cycle/history. A track already in history was reached by Previous — don't re-append it,
    // that would break stepping forward again.
    private void RecordPlay(MusicFile file)
    {
        file.PlayCount++;
        _shufflePlayed.Add(file.FullPath);
        if (!_shuffleHistory.Contains(file)) _shuffleHistory.Add(file);
        Task.Run(() => DatabaseService.Instance.IncrementPlayCount(file.FullPath));
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

    public Task<IReadOnlyList<string>?> SuggestFolderTagsAsync(string bandName, IReadOnlyList<string> albums) =>
        _artistInfoService.SuggestFolderTagsAsync(bandName, albums, DatabaseService.Instance.GetAllTagNames());

    // ------------------------------------------------------------- Lyrics

    /// <summary>Cache/embedded-tag only — no network. Called on every track change.</summary>
    private async Task LoadLyricsFromCacheAsync(MusicFile file)
    {
        _lyricsCts?.Cancel();
        _lyricsCts = new CancellationTokenSource();
        var token = _lyricsCts.Token;

        _suppressLyricsAutoSave = true;
        LyricsLines.Clear();
        HasSyncedLyrics = false;
        IsLyricsEditMode = false;
        LyricsEditText = string.Empty;
        CurrentLyricIndex = -1;
        RebuildKaraokeWindow(); // CurrentLyricIndex may already have been -1 (no change notify)
        RecomputeLyricRowGaps();
        CanSyncLyrics = false;
        _suppressLyricsAutoSave = false;
        LyricsStatus = "Loading lyrics...";

        LyricsRecord? cached;
        try { cached = await Task.Run(() => _lyricsService.GetCached(file.FullPath), token); }
        catch (OperationCanceledException) { return; }
        if (token.IsCancellationRequested || token != _lyricsCts?.Token) return;

        if (cached == null || (string.IsNullOrWhiteSpace(cached.Lrc) && string.IsNullOrWhiteSpace(cached.Plain)))
        {
            LyricsStatus = "No lyrics cached. Click Find to search.";
            return;
        }

        ApplyLyricsRecord(cached, enterEditMode: false);
        LyricsStatus = HasSyncedLyrics
            ? "Synced lyrics."
            : cached.Source == "embedded" ? "Embedded lyrics (unsynced)." : "Plain lyrics (unsynced).";
    }

    private void FindLyrics()
    {
        if (NowPlaying != null) _ = FetchLyricsFlowAsync(NowPlaying);
    }

    /// <summary>Wipes a track's cached lyrics (DB row + in-memory state) and drops into the raw
    /// paste box — the only way to replace a whole synced/bad result, since Edit on a synced
    /// track shows inline rows (not a paste box) and Find is hidden while editing. Same reset
    /// idiom as LoadLyricsFromCacheAsync's reset / the AI-unavailable branch, plus
    /// IsLyricsEditMode = true so the empty state is immediately pasteable.</summary>
    private void ClearLyrics()
    {
        if (NowPlaying == null) return;
        _lyricsCts?.Cancel();
        _lyricsService.Delete(NowPlaying.FullPath);

        _suppressLyricsAutoSave = true;
        try
        {
            LyricsLines.Clear();
            HasSyncedLyrics = false;
            CurrentLyricIndex = -1;
            RebuildKaraokeWindow();
            RecomputeLyricRowGaps();
            CanSyncLyrics = false;
            IsLyricsEditMode = true;
            LyricsEditText = string.Empty;
        }
        finally
        {
            _suppressLyricsAutoSave = false;
        }

        MarkNowPlayingHasLyrics(false);
        LyricsStatus = "Cleared — paste lyrics here, then Save (and Sync or hand-time).";
    }

    /// <summary>The free sources first (LRCLIB → LRCMux → SyncLRC, see
    /// <see cref="LyricsService.FetchOnlineAsync"/>), AI web-search plain lyrics as a fallback.
    /// All cost a network call, so this only ever runs on request (the Find button). Any
    /// plain-only hit is immediately chained into a whisper auto-sync so Find ends synced
    /// whenever possible — the manual Sync button (via <see cref="CanSyncLyrics"/>) stays
    /// available afterwards as a re-run. An instrumental track stops the flow outright.</summary>
    private async Task FetchLyricsFlowAsync(MusicFile file)
    {
        _lyricsCts?.Cancel();
        _lyricsCts = new CancellationTokenSource();
        var token = _lyricsCts.Token;

        IsLoadingLyrics = true;
        LyricsStatus = "Searching lyrics sources...";
        try
        {
            var artist = _artistInfoService.DetectArtistName(file.Artist, file.FullPath);
            var online = await _lyricsService.FetchOnlineAsync(artist, file.Title, file.Album, (int)file.Duration.TotalSeconds);
            if (token.IsCancellationRequested || token != _lyricsCts?.Token) return;

            if (online is { Instrumental: true })
            {
                // The source knows this track has no vocals — an AI call and a whisper run would
                // both be wasted, and whisper would hallucinate lines out of the instrumentation.
                LyricsStatus = "Instrumental track — no lyrics.";
                return;
            }

            if (online != null && !string.IsNullOrWhiteSpace(online.Lrc))
            {
                _lyricsService.Save(file.FullPath, online.Plain, online.Lrc, online.Source);
                MarkNowPlayingHasLyrics();
                ApplyLyricsRecord(online, enterEditMode: false);
                LyricsStatus = $"Synced lyrics found ({online.Source}).";
                return;
            }

            var plain = online?.Plain;
            var plainSource = online?.Source ?? "lrclib";
            if (string.IsNullOrWhiteSpace(plain))
            {
                // Miss on every free source: fall back to AI web-search plain lyrics.
                LyricsStatus = "No online match — asking AI...";
                var (text, confident, available) = await _lyricsService.FetchAiPlainAsync(artist, file.Title, file.Album, file.FileName);
                if (token.IsCancellationRequested || token != _lyricsCts?.Token) return;

                if (!available)
                {
                    // A refusal (copyright etc.) — or an API-key/error message — never gets
                    // saved, synced, or karaoke-displayed as if it were real lyrics. Drop straight
                    // into the manual paste box instead (gated on IsLyricsEditMode && !HasSyncedLyrics).
                    _suppressLyricsAutoSave = true;
                    LyricsLines.Clear();
                    HasSyncedLyrics = false;
                    IsLyricsEditMode = true;
                    LyricsEditText = string.Empty;
                    CurrentLyricIndex = -1;
                    RebuildKaraokeWindow();
                    RecomputeLyricRowGaps();
                    CanSyncLyrics = false;
                    _suppressLyricsAutoSave = false;
                    LyricsStatus = !string.IsNullOrWhiteSpace(text)
                        ? text
                        : "No lyrics found online — paste them here, then Save and Sync (or hand-time).";
                    return;
                }
                plain = text;
                plainSource = "ai";
                LyricsStatus = confident ? "AI lyrics found — syncing..." : "Uncertain AI match — syncing...";
            }
            else
            {
                LyricsStatus = $"Plain lyrics found ({plainSource}) — syncing...";
            }

            // Save the plain text right away so it isn't lost if the whisper sync below fails.
            _lyricsService.Save(file.FullPath, plain, null, plainSource);
            MarkNowPlayingHasLyrics();
            ApplyLyricsRecord(new LyricsRecord { Plain = plain, Source = plainSource }, enterEditMode: false);

            await RunWhisperSyncAsync(file, plain, token);
        }
        finally
        {
            if (token == _lyricsCts?.Token) IsLoadingLyrics = false;
        }
    }

    private void SyncLyricsWhisper()
    {
        if (NowPlaying != null) _ = SyncLyricsWhisperAsync(NowPlaying);
    }

    /// <summary>Smart-start for the transport-bar 🎤 button: already synced → straight into the
    /// karaoke big view; otherwise runs the full Find flow (LRCLIB → AI → auto-sync) and only
    /// switches to karaoke mode if that ends up synced. A plain-only result (whisper unavailable
    /// or failed) leaves the panel showing the list/edit view for hand-timing instead.</summary>
    private async Task StartKaraokeAsync()
    {
        // Already in the big view: a second tap just closes the panel (toggle feel). The
        // in-panel Karaoke button remains the manual override for switching modes otherwise.
        if (ShowLyrics && KaraokeMode)
        {
            ShowLyrics = false;
            return;
        }

        ShowLyrics = true;
        if (HasSyncedLyrics)
        {
            KaraokeMode = true;
            return;
        }

        if (NowPlaying == null) return;
        await FetchLyricsFlowAsync(NowPlaying);
        if (HasSyncedLyrics) KaraokeMode = true;
    }

    private async Task SyncLyricsWhisperAsync(MusicFile file)
    {
        var plain = LyricsEditText;
        if (string.IsNullOrWhiteSpace(plain)) return;

        _lyricsCts?.Cancel();
        _lyricsCts = new CancellationTokenSource();
        var token = _lyricsCts.Token;

        IsLoadingLyrics = true;
        try { await RunWhisperSyncAsync(file, plain, token); }
        finally { if (token == _lyricsCts?.Token) IsLoadingLyrics = false; }
    }

    /// <summary>Aligns <paramref name="plain"/> to <paramref name="file"/>'s audio via
    /// Whisper.net and saves the result. Shared by the manual Sync button and Find's
    /// auto-sync chain — both already own <paramref name="token"/> and toggle
    /// <see cref="IsLoadingLyrics"/> themselves.</summary>
    private async Task RunWhisperSyncAsync(MusicFile file, string plain, CancellationToken token)
    {
        var progress = new Progress<string>(msg => LyricsStatus = msg);
        try
        {
            var lrc = await _whisperAlignService.AlignAsync(file.FullPath, plain, progress, token);
            if (token.IsCancellationRequested || token != _lyricsCts?.Token) return;

            _lyricsService.Save(file.FullPath, plain, lrc, "whisper");
            MarkNowPlayingHasLyrics();
            ApplyLyricsRecord(new LyricsRecord { Plain = plain, Lrc = lrc, Source = "whisper" }, enterEditMode: false);
            KaraokeMode = true; // "find + map, then switch to karaoke" — auto-engage on a successful sync
            LyricsStatus = "Synced via speech alignment.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // ponytail: keep the plain lyrics on a failed/unavailable sync — they're already
            // showing as a per-line list (from the plain-text apply above), so the user can
            // hand-time them with the row "now"/nudge controls instead of a hard failure.
            LyricsStatus = $"Auto-sync failed ({ex.Message}) — tap Sync to retry, or time lines manually.";
        }
    }

    // Edit is a toggle: no more Save/Cancel buttons now that lyrics auto-save (see
    // ScheduleLyricsAutoSave). Leaving raw-text mode flushes any pending debounce immediately
    // (so a quick type-then-toggle can't lose the last edit) and re-splits the text into fresh
    // display lines — old per-line timestamps don't survive a wording/line-count change anyway.
    // Edit is a toggle. Entering edit mode never touches LyricsEditText/LyricsLines — the row
    // list (for a synced track) or the raw textbox (for a plain one) picks up whatever's
    // already loaded, per the XAML visibility triggers on HasSyncedLyrics.
    private void EditLyrics()
    {
        if (IsLyricsEditMode)
        {
            _lyricsSaveTimer?.Stop();
            if (HasSyncedLyrics)
            {
                // Synced inline edit: the row TextBoxes already write straight into
                // LyricsLines (words + timestamps) — just flush the pending debounce.
                // Rebuilding from LyricsEditText here is exactly what would zero every
                // timestamp, which is the bug this branch exists to avoid.
                PersistTimedLines();
                IsLyricsEditMode = false;
            }
            else
            {
                PersistRawText();
                ApplyLyricsRecord(new LyricsRecord { Plain = LyricsEditText, Source = "manual" }, enterEditMode: false);
            }
            return;
        }
        if (LyricsLines.Count == 0 && string.IsNullOrWhiteSpace(LyricsEditText)) return;
        IsLyricsEditMode = true;
    }

    // ponytail: the WASAPI output buffer is the dominant latency term between the decoder
    // position (CurrentPosition) and what the user actually hears, so it's what we compensate
    // for here — device/driver latency beyond it isn't accounted for, and human reaction time is
    // still on the user (that's what the −/+ 0.2s nudges are for).
    private void SetLyricLineNow(LyricLine? line)
    {
        if (line == null) return;
        var audible = CurrentPosition - TimeSpan.FromMilliseconds(_audioPlayer.OutputBufferMs);
        RetimeLine(line, audible < TimeSpan.Zero ? TimeSpan.Zero : audible);
    }

    private void NudgeLyricLine(LyricLine? line, double deltaSeconds)
    {
        if (line == null) return;
        var t = line.Time + TimeSpan.FromSeconds(deltaSeconds);
        RetimeLine(line, t < TimeSpan.Zero ? TimeSpan.Zero : t);
    }

    /// <summary>Sets <paramref name="line"/>'s timestamp and ripple-shifts every LATER line in
    /// <see cref="LyricsLines"/> by the same delta, so their relative spacing is preserved.
    /// Earlier lines are untouched. Each shifted line's own Time setter fires
    /// <see cref="LyricLine_PropertyChanged"/>, which schedules the auto-save.</summary>
    private void RetimeLine(LyricLine line, TimeSpan newTime)
    {
        var idx = LyricsLines.IndexOf(line);
        if (idx < 0) { line.Time = newTime; return; }

        var delta = newTime - line.Time;
        line.Time = newTime;
        if (delta == TimeSpan.Zero) return;

        for (int i = idx + 1; i < LyricsLines.Count; i++)
        {
            var t = LyricsLines[i].Time + delta;
            LyricsLines[i].Time = t < TimeSpan.Zero ? TimeSpan.Zero : t;
        }
    }

    // ---- Auto-save (debounced ~500ms, mirrors _volumeSaveTimer) ----

    private void ScheduleLyricsAutoSave()
    {
        if (_lyricsSaveTimer == null)
        {
            _lyricsSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _lyricsSaveTimer.Tick += (_, _) =>
            {
                _lyricsSaveTimer!.Stop();
                PersistLyricsNow();
            };
        }
        _lyricsSaveTimer.Stop();
        _lyricsSaveTimer.Start();
    }

    private void PersistLyricsNow()
    {
        // Raw-text editor only applies to plain/unsynced lyrics (see the XAML visibility
        // triggers) — a synced track being edited inline (words + timestamps, same row list)
        // must persist via the timed-lines path so BuildLrc keeps its timestamps.
        if (IsLyricsEditMode && !HasSyncedLyrics) PersistRawText();
        else if (LyricsLines.Count > 0) PersistTimedLines();
    }

    private void PersistRawText()
    {
        if (NowPlaying == null) return;
        var text = LyricsEditText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;
        _lyricsService.Save(NowPlaying.FullPath, text, null, "manual");
        MarkNowPlayingHasLyrics();
        LyricsStatus = "Saved.";
    }

    private void PersistTimedLines()
    {
        if (NowPlaying == null) return;
        var lrc = LyricsService.BuildLrc(LyricsLines);
        var plain = string.Join('\n', LyricsLines.Select(l => l.Text));
        _lyricsService.Save(NowPlaying.FullPath, plain, lrc, "manual");
        MarkNowPlayingHasLyrics();
        LyricsStatus = "Saved.";
    }

    // Flips the mic icon on immediately after a save (or off after Clear — see ClearLyrics).
    // NowPlaying is normally the same MusicFile instance as its MusicFiles row (PlayFile is
    // invoked with a grid item), but fall back to a path lookup so a stale reference still
    // updates the visible row.
    private void MarkNowPlayingHasLyrics(bool hasLyrics = true)
    {
        if (NowPlaying == null) return;
        NowPlaying.HasLyrics = hasLyrics;
        var row = MusicFiles.FirstOrDefault(f => string.Equals(f.FullPath, NowPlaying.FullPath, StringComparison.OrdinalIgnoreCase));
        if (row != null) row.HasLyrics = hasLyrics;
    }

    // Persist Time/Text edits made on a loaded line back to the SQLite cache (debounced),
    // mirroring HookFile/File_PropertyChanged for MusicFile rating/tags.
    private void HookLyricLine(LyricLine line)
    {
        line.PropertyChanged -= LyricLine_PropertyChanged;
        line.PropertyChanged += LyricLine_PropertyChanged;
    }

    private void LyricLine_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressLyricsAutoSave) return;
        var isTime = e.PropertyName == nameof(LyricLine.Time);
        var isText = e.PropertyName == nameof(LyricLine.Text);
        if (!isTime && !isText) return; // ignore TimeFormatted/IsActive — those aren't edits

        if (isTime)
        {
            HasSyncedLyrics = LyricsLines.Any(l => l.Time > TimeSpan.Zero);
            RecomputeLyricRowGaps(); // live feedback while hand-timing a line
        }
        ScheduleLyricsAutoSave();
    }

    /// <summary>Populates <see cref="LyricsLines"/>/<see cref="LyricsEditText"/> from a fetched or
    /// cached record. Synced (has Lrc) wins; otherwise the plain text is split into static lines.
    /// Runs under <see cref="_suppressLyricsAutoSave"/> — this is a load, not a user edit.</summary>
    private void ApplyLyricsRecord(LyricsRecord rec, bool enterEditMode)
    {
        _suppressLyricsAutoSave = true;
        try
        {
            LyricsLines.Clear();
            if (!string.IsNullOrWhiteSpace(rec.Lrc))
            {
                foreach (var line in LyricsService.ParseLrc(rec.Lrc))
                {
                    HookLyricLine(line);
                    LyricsLines.Add(line);
                }
                HasSyncedLyrics = LyricsLines.Count > 0;
                LyricsEditText = rec.Plain ?? string.Join('\n', LyricsLines.Select(l => l.Text));
            }
            else
            {
                foreach (var lineText in (rec.Plain ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(lineText)) continue;
                    var line = new LyricLine { Text = lineText };
                    HookLyricLine(line);
                    LyricsLines.Add(line);
                }
                HasSyncedLyrics = false;
                LyricsEditText = rec.Plain ?? string.Empty;
            }
            CurrentLyricIndex = -1;
            RebuildKaraokeWindow(); // CurrentLyricIndex may already have been -1 (no change notify)
            RecomputeLyricRowGaps();
            CanSyncLyrics = LyricsLines.Count > 0;
            IsLyricsEditMode = enterEditMode;
        }
        finally
        {
            _suppressLyricsAutoSave = false;
        }
    }

    /// <summary>Binary-searches <see cref="LyricsLines"/> for the current line at <paramref name="position"/>
    /// and updates its <see cref="LyricLine.IsActive"/> flag (and <see cref="CurrentLyricIndex"/>).</summary>
    private void UpdateActiveLyricLine(TimeSpan position)
    {
        int lo = 0, hi = LyricsLines.Count - 1, idx = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (LyricsLines[mid].Time <= position) { idx = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (idx == CurrentLyricIndex) return;

        if (CurrentLyricIndex >= 0 && CurrentLyricIndex < LyricsLines.Count)
            LyricsLines[CurrentLyricIndex].IsActive = false;
        if (idx >= 0)
            LyricsLines[idx].IsActive = true;
        CurrentLyricIndex = idx;
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

    /// <summary>Deletes a file and keeps the music going: if it is the *playing* track, the queue's
    /// next track takes over instead of playback dying with the file. Every delete entry point
    /// (grid context menu, now-playing button, mobile API) routes through here, so the behaviour is
    /// uniform — <see cref="AudioPlayerService.StopAndReleaseFile"/> detaches PlaybackStopped before
    /// stopping, so the normal auto-advance never fires for a deleted track.</summary>
    public async Task<bool> DeleteFileAsync(string path)
    {
        var wasPlaying = NowPlaying != null &&
                         string.Equals(NowPlaying.FullPath, path, StringComparison.OrdinalIgnoreCase);

        // Pick the successor BEFORE the delete: afterwards the track is out of the queue and
        // GetAdjacent has nothing to step from. Same wrap rule as Next()/auto-advance.
        var next = wasPlaying ? GetAdjacent(NowPlaying!, +1, _repeat == RepeatMode.All) : null;
        if (next != null && string.Equals(next.FullPath, path, StringComparison.OrdinalIgnoreCase))
            next = null;    // single-track queue wrapping onto the file we just deleted

        if (!await FileOperations.DeleteFileAsync(path)) return false;

        // Drop the row in place rather than reloading the folder: a reload rebuilds the queue
        // (and wipes a playlist / recursive-tree queue) out from under the track about to play.
        var row = MusicFiles.FirstOrDefault(f =>
            string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (row != null)
        {
            MusicFiles.Remove(row);
            _shuffleHistory.Remove(row);      // Previous must not walk back into a deleted file
            _shufflePlayed.Remove(path);
        }

        // Let the cache notice it is gone (the folder rescan marks vanished files deleted).
        LibraryCacheService.Instance.Enqueue(Path.GetDirectoryName(path) ?? string.Empty);

        if (wasPlaying)
        {
            if (next != null)
            {
                SelectedFile = next;
                PlayFile(next);
            }
            else
            {
                // End of queue (or the queue is now empty) — the handle is already released.
                NowPlaying = null;
                IsPlaying = false;
                CurrentPosition = TimeSpan.Zero;
            }
        }
        return true;
    }

    /// <summary>Returns the track <paramref name="direction"/> steps from <paramref name="current"/> in
    /// the active queue, following shuffle order when enabled. Wraps around when <paramref name="wrap"/>
    /// is true, otherwise returns null at the ends.</summary>
    private MusicFile? GetAdjacent(MusicFile current, int direction, bool wrap)
    {
        int n = MusicFiles.Count;
        if (n == 0) return null;
        int curIdx = MusicFiles.IndexOf(current);
        if (curIdx < 0)
        {
            // Belt-and-braces: current's instance was swapped out from under us (e.g. the file
            // was retagged/rescanned on disk while playing). Fall back to a path match so
            // Next/Previous/auto-advance don't just die.
            for (int i = 0; i < n; i++)
            {
                if (MusicFiles[i].FullPath.Equals(current.FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    curIdx = i;
                    break;
                }
            }
        }
        if (curIdx < 0) return null;

        if (_shuffleEnabled)
        {
            // No fixed permutation: forward picks the least-played track, so nothing gets
            // played three times while something else never plays. Back retraces history.
            int hi = _shuffleHistory.LastIndexOf(current);
            if (direction < 0)
                return hi > 0 ? _shuffleHistory[hi - 1] : null;
            if (hi >= 0 && hi < _shuffleHistory.Count - 1)
                return _shuffleHistory[hi + 1];   // stepping forward again after a Previous
            return PickLeastPlayed(current, wrap);
        }

        int nidx = curIdx + direction;
        if (nidx < 0 || nidx >= n)
        {
            if (!wrap) return null;
            nidx = (nidx % n + n) % n;
        }
        return MusicFiles[nidx];
    }

    /// <summary>Random pick among the queue's least-played tracks (persisted <see cref="MusicFile.PlayCount"/>),
    /// skipping anything already played this cycle so one pass still covers the whole queue.
    /// Returns null at the end of a cycle when not wrapping (repeat off).</summary>
    private MusicFile? PickLeastPlayed(MusicFile current, bool wrap)
    {
        var pool = MusicFiles.Where(f => !_shufflePlayed.Contains(f.FullPath)).ToList();
        if (pool.Count == 0)
        {
            if (!wrap) return null;
            _shufflePlayed.Clear();
            pool = MusicFiles.Where(f => MusicFiles.Count == 1 || f != current).ToList();
            if (pool.Count == 0) return null;
        }
        int min = pool.Min(f => f.PlayCount);
        var tied = pool.Where(f => f.PlayCount == min).ToList();
        return tied[_rng.Next(tied.Count)];
    }

    // Queue changed (or shuffle toggled): forget the cycle and the back-history.
    private void ResetShuffleState()
    {
        _shuffleHistory.Clear();
        _shufflePlayed.Clear();
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

    // Restore the folder open when the app last closed, and re-select the track that was playing
    // then, so Play picks up where you left off. Called once after the window loads.
    public async Task RestoreLastFolderAsync()
    {
        var path = _appSettings.LastFolderPath;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

        // Arm before LoadFolder: the grid fills asynchronously (cache pass, then refresh), and the
        // tree selection echoes a second LoadFolder for the same path, so the request has to
        // outlive both instead of being a one-shot select here.
        var last = _appSettings.LastPlayedPath;
        if (!string.IsNullOrEmpty(last) && File.Exists(last) &&
            string.Equals(Path.GetDirectoryName(last), path, StringComparison.OrdinalIgnoreCase))
            _restoreSelectPath = last;

        LoadFolder(path);
        await FolderTree.NavigateToPathAsync(path);
    }

    /// <summary>Track to re-select once the grid holding it is populated (last played before the
    /// app closed). Retired by browsing to another folder or by playing anything.</summary>
    private string? _restoreSelectPath;

    /// <summary>Raised when the startup re-selection lands, so the view can scroll that row in
    /// (a Selector selects without scrolling). May fire twice — cache pass, then refresh pass.</summary>
    public event Action<MusicFile>? SelectionRestored;

    private void ApplyRestoreSelection()
    {
        if (_restoreSelectPath == null) return;
        var file = MusicFiles.FirstOrDefault(f =>
            string.Equals(f.FullPath, _restoreSelectPath, StringComparison.OrdinalIgnoreCase));
        if (file == null) return;
        SelectedFile = file;
        SelectionRestored?.Invoke(file);
    }

    // ROOT CAUSE of the karaoke "line stopped advancing" regression: AudioPlayerService's
    // _positionTimer is a System.Timers.Timer, so PositionChanged fires on a ThreadPool thread,
    // not the UI thread. Simple property sets (CurrentPosition, LyricLine.IsActive) tolerate
    // that fine — WPF auto-marshals plain property-binding notifications — but
    // UpdateActiveLyricLine → CurrentLyricIndex's setter → the karaoke window update mutates an
    // ObservableCollection, and WPF only allows structural collection changes from the
    // Dispatcher thread. That throws (silently, off the UI thread) the first time the song
    // crosses into line 2, so the index never visibly advances again. Marshal the whole handler,
    // matching the Dispatcher.Invoke idiom already used elsewhere (LoadAlbumCoverAsync,
    // LoadFolderAsync's cache-first block).
    private void OnPositionChanged(object? sender, TimeSpan position)
    {
        _latestPosition = position;          // any timer thread: just record the latest
        if (_positionUpdatePending) return;  // an update is already queued — coalesce
        _positionUpdatePending = true;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _positionUpdatePending = false;
            var pos = _latestPosition;
            if (!_isSliderDragging)
            {
                CurrentPosition = pos;
                OnPropertyChanged(nameof(CurrentPositionFormatted));
            }

            if (HasSyncedLyrics)
                UpdateActiveLyricLine(pos);
        });
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
        // ponytail: BeginInvoke, not Invoke — PlayFile → AudioPlayerService.Play disposes the old
        // WasapiOut, and WasapiOut raises PlaybackStopped back on this same captured UI-thread
        // SynchronizationContext. A synchronous Invoke here runs PlayFile inline/reentrantly from
        // inside that very PlaybackStopped callback, disposing _output from within its own
        // callback and deadlocking the whole app (same hazard AudioPlayerService.OnPlaybackStopped
        // already defers its push-mode retry to a ThreadPool thread to avoid). BeginInvoke queues
        // the switch to run only after this callback unwinds and the render thread has exited.
        if (_repeat == RepeatMode.One)
        {
            var same = NowPlaying;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => PlayFile(same));
            return;
        }

        // Auto-play next track when current track ends naturally (wrap when Repeat-all).
        var nextFile = GetAdjacent(NowPlaying, +1, _repeat == RepeatMode.All);
        if (nextFile != null)
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
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
            ResetShuffleState();
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
        ResetShuffleState();
        OnPropertyChanged(nameof(IsPlaylistView));

        CurrentFolderPath = playlist.Name;
        MusicFiles.Clear();
        ResetAlbumCover();

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
        ResetShuffleState();
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
        ResetShuffleState();
        UpdatePlaylistCount(_currentPlaylistId.Value);
    }

    public void MoveEntry(MusicFile? file, int delta)
    {
        if (_currentPlaylistId == null || file == null) return;
        int idx = MusicFiles.IndexOf(file);
        int dest = idx + delta;
        if (idx < 0 || dest < 0 || dest >= MusicFiles.Count) return;

        MusicFiles.Move(idx, dest);
        ResetShuffleState();
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

        if (_volumeSaveTimer?.IsEnabled == true)
        {
            _volumeSaveTimer.Stop();
            _appSettings.Save();
        }

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
