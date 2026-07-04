import 'dart:convert';
import 'package:http/http.dart' as http;
import 'models.dart';

/// Thin wrapper over the desktop control API. All paths are relative to base.
class ApiClient {
  final String host;
  final int port;
  ApiClient(this.host, this.port);

  String get base => 'http://$host:$port';
  String get label => '$host:$port';

  Uri _u(String path, [Map<String, String>? q]) =>
      Uri.parse('$base$path').replace(queryParameters: q);

  // ---- reads ----
  Future<PlayerStatus> status() async {
    final r = await http.get(_u('/status')).timeout(const Duration(seconds: 4));
    return PlayerStatus.fromJson(jsonDecode(r.body) as Map<String, dynamic>);
  }

  Future<List<TrackFile>> files() async {
    final r = await http.get(_u('/files')).timeout(const Duration(seconds: 8));
    return (jsonDecode(r.body) as List)
        .map((e) => TrackFile.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  // Global library search: matches title/artist/album/filename across every folder.
  Future<List<TrackFile>> search(String query, {int limit = 300}) async {
    final r = await http
        .get(_u('/search', {'q': query, 'limit': '$limit'}))
        .timeout(const Duration(seconds: 8));
    return (jsonDecode(r.body) as List)
        .map((e) => TrackFile.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  // open=true also navigates the desktop app's folder tree to [path].
  Future<BrowseResult> browse(String path, {bool open = false}) async {
    final q = {'path': path, if (open) 'open': '1'};
    final r = await http.get(_u('/browse', q)).timeout(const Duration(seconds: 8));
    return BrowseResult.fromJson(jsonDecode(r.body) as Map<String, dynamic>);
  }

  Future<List<HistoryItem>> history() async {
    final r = await http.get(_u('/history')).timeout(const Duration(seconds: 6));
    return (jsonDecode(r.body) as List)
        .map((e) => HistoryItem.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  String artUrl(String path) => _u('/file/art', {'path': path}).toString();

  // Transcoded AAC stream for local phone playback ("This device").
  String streamUrl(String path, int bitrate) =>
      _u('/file/audio', {'path': path, 'bitrate': '$bitrate'}).toString();

  Future<bool> ping() async {
    // Generous: the first request on a cold app/network can be slow; a short timeout
    // makes auto-connect on launch flaky.
    try {
      final r = await http.get(_u('/status')).timeout(const Duration(seconds: 8));
      return r.statusCode == 200;
    } catch (_) {
      return false;
    }
  }

  // ---- playback ----
  Future<void> playback(String action) => http.post(_u('/playback/$action'));
  Future<void> seek(int sec) => _post('/playback/seek', {'positionSec': sec});
  Future<void> setVolume(int level) => _post('/volume', {'level': level});
  Future<void> setSystemVolume(int level) => _post('/system-volume', {'level': level});

  // ---- output device ----
  Future<List<AudioDevice>> devices() async {
    final r = await http.get(_u('/devices')).timeout(const Duration(seconds: 6));
    return (jsonDecode(r.body) as List)
        .map((e) => AudioDevice.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<void> setDevice(String id) => _post('/device', {'id': id});

  // ---- files / folders ----
  Future<void> loadFolder(String path) => _post('/folder', {'path': path});
  Future<void> play(String path) => _post('/play', {'path': path});
  Future<void> setRating(String path, int rating) =>
      _post('/file/rating', {'path': path, 'rating': rating});
  Future<void> setTags(String path, String tags) =>
      _post('/file/tags', {'path': path, 'tags': tags});

  Future<bool> moveFile(String path, String dest) =>
      _postOk('/file/move', {'path': path, 'destFolder': dest});
  Future<bool> deleteFile(String path) => _postOk('/file/delete', {'path': path});
  Future<bool> moveFolder(String path, String dest) =>
      _postOk('/folder/move', {'path': path, 'destFolder': dest});
  Future<bool> deleteFolder(String path) => _postOk('/folder/delete', {'path': path});

  // ---- playlists ----
  Future<List<Playlist>> playlists() async {
    final r = await http.get(_u('/playlists')).timeout(const Duration(seconds: 6));
    return (jsonDecode(r.body) as List)
        .map((e) => Playlist.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<List<TrackFile>> playlistFiles(int id) async {
    final r = await http.get(_u('/playlist/files', {'id': '$id'})).timeout(const Duration(seconds: 8));
    return (jsonDecode(r.body) as List)
        .map((e) => TrackFile.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<int?> createPlaylist(String name) async {
    final r = await _post('/playlist/create', {'name': name});
    try {
      final j = jsonDecode(r.body);
      return (j is Map && j['id'] is num) ? (j['id'] as num).toInt() : null;
    } catch (_) {
      return null;
    }
  }

  Future<void> renamePlaylist(int id, String name) => _post('/playlist/rename', {'id': id, 'name': name});
  Future<void> deletePlaylist(int id) => _post('/playlist/delete', {'id': id});
  Future<void> openPlaylist(int id) => _post('/playlist/open', {'id': id});
  Future<bool> playPlaylist(int id) => _postOk('/playlist/play', {'id': id});
  Future<void> addFileToPlaylist(int id, String path) => _post('/playlist/add', {'id': id, 'path': path});
  Future<void> addFolderToPlaylist(int id, String folder) => _post('/playlist/add', {'id': id, 'folder': folder});
  Future<void> removeFromPlaylist(int id, int entryId) =>
      _post('/playlist/remove', {'id': id, 'entryId': entryId});

  Future<http.Response> _post(String path, Map<String, dynamic> body) => http.post(_u(path),
      headers: {'Content-Type': 'application/json'}, body: jsonEncode(body));

  Future<bool> _postOk(String path, Map<String, dynamic> body) async {
    final r = await _post(path, body);
    try {
      final j = jsonDecode(r.body);
      return j is Map && j['success'] == true;
    } catch (_) {
      return r.statusCode == 200;
    }
  }
}
