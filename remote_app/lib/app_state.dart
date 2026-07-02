import 'dart:async';
import 'dart:convert';
import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'api_client.dart';
import 'models.dart';
import 'watch_link.dart';

enum ConnState { disconnected, connecting, connected, lost }

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
    notifyListeners();
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
