import 'dart:async';
import 'dart:convert';
import 'package:flutter/foundation.dart';
import 'package:just_audio/just_audio.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'api_client.dart';
import 'models.dart';
import 'offline/download_service.dart';
import 'offline/offline_player.dart';
import 'offline/offline_store.dart';
import 'watch_link.dart';

enum ConnState { disconnected, connecting, connected, lost }

// Sentinel device id meaning "play on this phone" (stream from the desktop).
const String kThisDevice = '__this_device__';
// Manual stream-quality tiers (AAC bitrate). MediaFoundation AAC caps at 192k.
const Map<String, int> kQualities = {'Low': 96000, 'Med': 160000, 'High': 192000};

class RecentConn {
  final String host;
  final int port;
  final int lastSeenMs;
  RecentConn(this.host, this.port, this.lastSeenMs);
  String get key => '$host:$port';
  Map<String, dynamic> toJson() => {'host': host, 'port': port, 'ts': lastSeenMs};
  factory RecentConn.fromJson(Map<String, dynamic> j) =>
      RecentConn(j['host'] as String, (j['port'] as num).toInt(), (j['ts'] as num?)?.toInt() ?? 0);
}

/// Single app-wide store. Owns the API client, connection lifecycle and the
/// 1-second status poll. Screens listen to it; list data (files/browse/history)
/// is fetched by screens on demand, but the current files list is cached here so
/// Now-Playing can look up the playing track's rating/tags.
class AppState extends ChangeNotifier {
  ApiClient? api;
  ConnState conn = ConnState.disconnected;
  PlayerStatus status = PlayerStatus.empty();
  List<RecentConn> recents = [];

  List<TrackFile> files = [];
  final Map<String, TrackFile> _byPath = {};

  // Local playback ("This device"): the phone streams + plays audio itself and follows
  // the desktop's /status. Session-only; bitrate is a persisted preference.
  final AudioPlayer _player = AudioPlayer();
  bool localOutput = false;
  int streamBitrate = 160000;
  double localVolume = 100; // phone player volume (0..100) while in local output
  bool _loadingTrack = false; // true while a stream load attempt is in flight
  String? _loadedPath;        // track path currently loaded & playing on the phone
  String? _pendingPath;       // path we're trying to load (to capture its start target once)
  int _pendingTarget = 0;     // position to start the pending track at

  // Offline mode: downloads store + serial download queue + local-file player.
  late final OfflineStore offline;
  late final DownloadService downloads;
  late final OfflinePlayer offlinePlayer;

  AppState() {
    offline = OfflineStore();
    downloads = DownloadService(offline, () => api, () => streamBitrate);
    offlinePlayer = OfflinePlayer(offline);
    offlinePlayer.onWillPlay = stopLocalPlayback;
    // Proxy their changes so every existing ListenableBuilder(listenable: app)
    // repaints without new wiring.
    offline.addListener(notifyListeners);
    downloads.addListener(notifyListeners);
    offlinePlayer.addListener(notifyListeners);
  }

  Future<void> initOffline() => offline.init();

  /// Stops the remote-sink ("This device") stream. localOutput=false is essential:
  /// merely stopping _player would let the next 1s _syncLocal tick resume it.
  void stopLocalPlayback() {
    unawaited(_player.stop());
    localOutput = false;
    _loadedPath = null;
    _pendingPath = null;
    notifyListeners();
  }

  // Set by History to ask the Library screen to navigate to a folder; consumed there.
  String? pendingLibraryPath;
  void openInLibrary(String path) {
    pendingLibraryPath = path;
    notifyListeners();
  }

  // One-shot signal: a play action was started, so the shell should jump to Now Playing.
  bool _wantNow = false;
  void goNow() {
    _wantNow = true;
    notifyListeners();
  }

  bool consumeWantNow() {
    if (!_wantNow) return false;
    _wantNow = false;
    return true;
  }

  Timer? _poll;

  bool get isConnected => conn == ConnState.connected;
  bool get inHome => conn == ConnState.connected || conn == ConnState.lost;

  TrackFile? trackByPath(String path) => _byPath[path.toLowerCase()];

  Future<void> loadPrefs() async {
    final sp = await SharedPreferences.getInstance();
    final raw = sp.getStringList('recents') ?? [];
    recents = raw
        .map((s) => RecentConn.fromJson(jsonDecode(s) as Map<String, dynamic>))
        .toList()
      ..sort((a, b) => b.lastSeenMs.compareTo(a.lastSeenMs));
    streamBitrate = sp.getInt('streamBitrate') ?? 160000;
    notifyListeners();
  }

  RecentConn? get mostRecent => recents.isNotEmpty ? recents.first : null;

  Future<void> _saveRecent(String host, int port) async {
    final now = DateTime.now().millisecondsSinceEpoch;
    recents.removeWhere((r) => r.host == host && r.port == port);
    recents.insert(0, RecentConn(host, port, now));
    if (recents.length > 6) recents = recents.sublist(0, 6);
    final sp = await SharedPreferences.getInstance();
    await sp.setStringList('recents', recents.map((r) => jsonEncode(r.toJson())).toList());
    // Share the live host with a paired Apple Watch so it auto-configures.
    unawaited(WatchLink.sendHost(host, port));
  }

  Future<bool> connect(String host, int port) async {
    _poll?.cancel();
    api = ApiClient(host, port);
    conn = ConnState.connecting;
    notifyListeners();

    final ok = await api!.ping();
    if (!ok) {
      conn = ConnState.disconnected;
      notifyListeners();
      return false;
    }
    conn = ConnState.connected;
    await _saveRecent(host, port);
    notifyListeners();
    _startPolling();
    unawaited(loadFiles());
    return true;
  }

  Future<void> retry() async {
    if (api != null) await connect(api!.host, api!.port);
  }

  /// Launch-time connect: the first request on a cold app/network can miss, so retry
  /// a few times while staying in the "connecting" state (no disconnected flicker).
  Future<void> autoConnect(String host, int port) async {
    _poll?.cancel();
    api = ApiClient(host, port);
    conn = ConnState.connecting;
    notifyListeners();
    for (var i = 0; i < 5; i++) {
      if (await api!.ping()) {
        conn = ConnState.connected;
        await _saveRecent(host, port);
        notifyListeners();
        _startPolling();
        unawaited(loadFiles());
        return;
      }
      await Future.delayed(const Duration(milliseconds: 1200));
    }
    conn = ConnState.disconnected;
    notifyListeners();
  }

  void disconnect() {
    _poll?.cancel();
    _poll = null;
    conn = ConnState.disconnected;
    api = null;
    files = [];
    _byPath.clear();
    unawaited(_player.stop());
    localOutput = false; // session-only: next connect defaults to desktop output
    _loadedPath = null;
    _pendingPath = null;
    notifyListeners();
  }

  // ---- Local playback ("This device") ----

  Future<void> enableLocalOutput() async {
    if (api == null) return;
    await offlinePlayer.stop(); // mutual exclusion: offline playback yields to streaming
    localOutput = true;
    _loadedPath = null; // force a fresh load of the current track
    _pendingPath = null;
    notifyListeners();
    await api!.setDevice(kThisDevice); // desktop mutes + reports the sentinel
    await _syncLocal(); // trackChanged:false -> resume the current track at its position
  }

  Future<void> selectDesktopDevice(String id) async {
    localOutput = false;
    await _player.stop();
    _loadedPath = null;
    _pendingPath = null;
    notifyListeners();
    if (api != null) await api!.setDevice(id);
  }

  Future<void> setQuality(int bitrate) async {
    streamBitrate = bitrate;
    final sp = await SharedPreferences.getInstance();
    await sp.setInt('streamBitrate', bitrate);
    notifyListeners();
    if (!localOutput || api == null) return;
    final np = status.nowPlaying;
    if (np == null) return;
    final pos = _player.position;
    try {
      await _player.setUrl(api!.streamUrl(np.path, bitrate));
      if (!localOutput) return; // output switched away while reloading
      await _player.seek(pos);
      if (!localOutput) return;
      if (status.isPlaying && !status.isPaused) _player.play();
    } catch (_) {/* buffering; next tick retries */}
  }

  // Slaves the local player to the desktop's /status: keeps the phone playing the current
  // track, mirrors play/pause, and resyncs the playhead on drift. Loading is driven by
  // "loaded path != current path" (NOT a one-shot flag), so a failed stream load — e.g. the
  // transcode isn't ready yet, or an ExoPlayer timeout over VPN — is retried on the next
  // tick instead of leaving playback stuck after one track.
  // [trackChanged] only affects where a freshly-seen track starts: 0 vs the desktop position.
  Future<void> _syncLocal({bool trackChanged = false}) async {
    if (!localOutput || api == null) return;
    final np = status.nowPlaying;
    if (np == null) {
      if (_player.playing) await _player.stop();
      _loadedPath = null;
      _pendingPath = null;
      return;
    }

    // Need to (re)load? Either a new track, or a previous load that failed.
    if (np.path != _loadedPath) {
      // Capture the intended start position the first time we see this path, so retries
      // don't drift forward: a real track change starts at 0, an output switch resumes.
      if (np.path != _pendingPath) {
        _pendingPath = np.path;
        _pendingTarget = trackChanged ? 0 : status.positionSec;
      }
      if (_loadingTrack) return; // a load attempt is already in flight
      _loadingTrack = true;
      try {
        await _player.setUrl(api!.streamUrl(np.path, streamBitrate));
        // Re-check after every await: stopLocalPlayback/selectDesktopDevice/disconnect
        // may have run while we were suspended — playing now would resurrect the
        // stream on top of offline playback (or a desktop output).
        if (!localOutput || api == null) return;
        _loadedPath = np.path; // mark loaded ONLY on success
        await api!.seek(_pendingTarget); // rewind the muted desktop clock to the start
        if (!localOutput) return;
        await _player.seek(Duration(seconds: _pendingTarget));
        if (!localOutput) return;
        if (status.isPlaying && !status.isPaused) _player.play();
      } catch (_) {
        // Transient failure — leave _loadedPath stale so the next tick retries this track.
      } finally {
        _loadingTrack = false;
      }
      return;
    }

    // Loaded & current: mirror play/pause + drift resync.
    try {
      final shouldPlay = status.isPlaying && !status.isPaused;
      if (shouldPlay && !_player.playing) {
        _player.play();
      } else if (!shouldPlay && _player.playing) {
        _player.pause();
      }
      final drift = (_player.position.inSeconds - status.positionSec).abs();
      if (drift > 2) await _player.seek(Duration(seconds: status.positionSec));
    } catch (_) {/* transient; next tick retries */}
  }

  Future<void> setLocalVolume(double v) async {
    localVolume = v.clamp(0, 100);
    await _player.setVolume(localVolume / 100);
    notifyListeners();
  }

  @override
  void dispose() {
    _poll?.cancel();
    _player.dispose();
    offline.removeListener(notifyListeners);
    downloads.removeListener(notifyListeners);
    offlinePlayer.removeListener(notifyListeners);
    offlinePlayer.dispose();
    downloads.dispose();
    offline.dispose();
    super.dispose();
  }

  void _startPolling() {
    _poll?.cancel();
    _poll = Timer.periodic(const Duration(seconds: 1), (_) => _tick());
    _tick();
  }

  Future<void> _tick() async {
    if (api == null) return;
    try {
      final s = await api!.status();
      final trackChanged = s.nowPlaying?.path != status.nowPlaying?.path;
      status = s;
      if (conn == ConnState.lost) conn = ConnState.connected;
      notifyListeners();
      if (trackChanged) unawaited(loadFiles());
      unawaited(_syncLocal(trackChanged: trackChanged));
    } catch (_) {
      if (conn == ConnState.connected) {
        conn = ConnState.lost;
        notifyListeners();
      }
    }
  }

  Future<void> loadFiles() async {
    if (api == null) return;
    try {
      files = await api!.files();
      _byPath
        ..clear()
        ..addEntries(files.map((f) => MapEntry(f.path.toLowerCase(), f)));
      notifyListeners();
    } catch (_) {/* leave stale list; banner covers the error */}
  }
}
