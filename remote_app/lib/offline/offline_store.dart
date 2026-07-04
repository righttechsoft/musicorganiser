import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:path_provider/path_provider.dart';

import '../models.dart';

/// One downloaded track. `remotePath` (the desktop's path) is the identity key;
/// `relPath` is where the transcoded .m4a lives under the downloads root.
class OfflineTrack {
  final String remotePath;
  final String relPath;
  final String title, artist, album, tags;
  final int? rating;
  final int durationSec;
  final int bitrate;
  final int sizeBytes;
  final bool hasArt;
  final int downloadedAtMs;

  OfflineTrack({
    required this.remotePath,
    required this.relPath,
    required this.title,
    required this.artist,
    required this.album,
    required this.tags,
    required this.rating,
    required this.durationSec,
    required this.bitrate,
    required this.sizeBytes,
    required this.hasArt,
    required this.downloadedAtMs,
  });

  String get key => remotePath.toLowerCase();
  String get name => title.isNotEmpty ? title : relPath.split('/').last;

  factory OfflineTrack.fromTrackFile(TrackFile t,
          {required String relPath,
          required int bitrate,
          required int sizeBytes,
          required bool hasArt}) =>
      OfflineTrack(
        remotePath: t.path,
        relPath: relPath,
        title: t.title,
        artist: t.artist,
        album: t.album,
        tags: t.tags,
        rating: t.rating,
        durationSec: t.durationSec,
        bitrate: bitrate,
        sizeBytes: sizeBytes,
        hasArt: hasArt,
        downloadedAtMs: DateTime.now().millisecondsSinceEpoch,
      );

  /// Renders through the existing TrackRow unchanged.
  TrackFile toTrackFile() => TrackFile(
        path: remotePath,
        title: title,
        artist: artist,
        album: album,
        tags: tags,
        durationSec: durationSec,
        rating: rating,
        isPlaying: false,
        createdSec: downloadedAtMs ~/ 1000,
        modifiedSec: downloadedAtMs ~/ 1000,
      );

  Map<String, dynamic> toJson() => {
        'remotePath': remotePath,
        'relPath': relPath,
        'title': title,
        'artist': artist,
        'album': album,
        'tags': tags,
        'rating': rating,
        'durationSec': durationSec,
        'bitrate': bitrate,
        'sizeBytes': sizeBytes,
        'hasArt': hasArt,
        'downloadedAtMs': downloadedAtMs,
      };

  factory OfflineTrack.fromJson(Map<String, dynamic> j) => OfflineTrack(
        remotePath: j['remotePath'] as String,
        relPath: j['relPath'] as String,
        title: (j['title'] as String?) ?? '',
        artist: (j['artist'] as String?) ?? '',
        album: (j['album'] as String?) ?? '',
        tags: (j['tags'] as String?) ?? '',
        rating: (j['rating'] as num?)?.toInt(),
        durationSec: (j['durationSec'] as num?)?.toInt() ?? 0,
        bitrate: (j['bitrate'] as num?)?.toInt() ?? 0,
        sizeBytes: (j['sizeBytes'] as num?)?.toInt() ?? 0,
        hasArt: j['hasArt'] == true,
        downloadedAtMs: (j['downloadedAtMs'] as num?)?.toInt() ?? 0,
      );
}

/// Source of truth for what is downloaded: a JSON manifest plus the mirrored
/// file tree under `<documents>/downloads/`.
/// ponytail: full-rewrite JSON manifest — fine to a few thousand tracks (~1 MB);
/// swap the persistence inside this file if that ceiling is ever hit.
class OfflineStore extends ChangeNotifier {
  late final Directory _root; // <documents>/downloads
  final Map<String, OfflineTrack> _byKey = {};
  bool _ready = false;

  bool get isReady => _ready;
  List<OfflineTrack> get all => _byKey.values.toList();
  int get count => _byKey.length;
  int get totalBytes => _byKey.values.fold(0, (s, t) => s + t.sizeBytes);

  File get _manifest => File('${_root.path}/manifest.json');

  Future<void> init() async {
    final docs = await getApplicationDocumentsDirectory();
    _root = Directory('${docs.path}/downloads');
    await _root.create(recursive: true);
    await _load();
    await _sweepPartials();
    _ready = true;
    notifyListeners();
  }

  Future<void> _load() async {
    if (!await _manifest.exists()) return;
    try {
      final j = jsonDecode(await _manifest.readAsString()) as Map<String, dynamic>;
      for (final t in (j['tracks'] as List? ?? [])) {
        final track = OfflineTrack.fromJson(t as Map<String, dynamic>);
        _byKey[track.key] = track;
      }
    } catch (_) {
      // Corrupt manifest: keep the audio files, park the bad manifest, start empty.
      try { await _manifest.rename('${_root.path}/manifest.bad.json'); } catch (_) {}
      _byKey.clear();
    }
  }

  // Saves are serialized through a chained future: concurrent upsert/remove calls
  // would otherwise interleave writes to the same tmp file and could install a
  // torn manifest (losing the whole download index on next launch).
  Future<void> _saveQueue = Future.value();

  Future<void> _save() {
    final done = _saveQueue.then((_) => _writeManifest());
    // Keep the chain alive even if a write fails.
    _saveQueue = done.catchError((_) {});
    return done;
  }

  Future<void> _writeManifest() async {
    final tmp = File('${_root.path}/manifest.json.tmp');
    final json = jsonEncode({
      'version': 1,
      'tracks': _byKey.values.map((t) => t.toJson()).toList(),
    });
    await tmp.writeAsString(json, flush: true);
    await tmp.rename(_manifest.path);
  }

  // Orphan .part files from a killed app.
  Future<void> _sweepPartials() async {
    try {
      await for (final e in _root.list(recursive: true, followLinks: false)) {
        if (e is File && e.path.endsWith('.part')) {
          try { await e.delete(); } catch (_) {}
        }
      }
    } catch (_) {}
  }

  bool has(String remotePath) => _byKey.containsKey(remotePath.toLowerCase());
  OfflineTrack? byRemote(String remotePath) => _byKey[remotePath.toLowerCase()];

  String absPath(String relPath) => '${_root.path}/$relPath';

  File? artFile(OfflineTrack t) =>
      t.hasArt ? File('${absPath(t.relPath)}.jpg') : null;

  /// Maps a remote (Windows) path to a safe local relative path ending in .m4a.
  /// Existing entry for the same track reuses its relPath; a fresh path gets a
  /// " (2)" style suffix if another track already claimed it.
  String relPathFor(String remotePath) {
    final existing = byRemote(remotePath);
    if (existing != null) return existing.relPath;

    final segments = remotePath
        .split(RegExp(r'[\\/]+'))
        .where((s) => s.isNotEmpty)
        .map(_sanitize)
        .toList();
    if (segments.isEmpty) return '_unknown.m4a';

    // Filename: strip original extension, force .m4a (server always sends AAC).
    var file = segments.removeLast();
    final dot = file.lastIndexOf('.');
    if (dot > 0) file = file.substring(0, dot);

    final dir = segments.join('/');
    var candidate = dir.isEmpty ? '$file.m4a' : '$dir/$file.m4a';
    var n = 2;
    while (_relPathClaimed(candidate, remotePath)) {
      candidate = dir.isEmpty ? '$file ($n).m4a' : '$dir/$file ($n).m4a';
      n++;
    }
    return candidate;
  }

  bool _relPathClaimed(String relPath, String forRemotePath) {
    if (_byKey.values.any((t) =>
        t.relPath.toLowerCase() == relPath.toLowerCase() &&
        t.remotePath.toLowerCase() != forRemotePath.toLowerCase())) {
      return true;
    }
    // Also claimed if a file already sits there (manifest/save raced, or an
    // unswept leftover) — never silently overwrite someone else's audio.
    return File(absPath(relPath)).existsSync();
  }

  String _sanitize(String segment) {
    var s = segment.replaceAll(RegExp(r'[<>:"/\\|?*\x00-\x1f]'), '_');
    // Drive letter "N:" already became "N_" above; strip back to "N".
    s = s.replaceAll(RegExp(r'[. _]+$'), '');
    return s.isEmpty ? '_' : s;
  }

  Future<void> upsert(OfflineTrack t) async {
    _byKey[t.key] = t;
    await _save();
    notifyListeners();
  }

  Future<void> removeTrack(OfflineTrack t) async {
    _byKey.remove(t.key);
    try { await File(absPath(t.relPath)).delete(); } catch (_) {}
    try { await File('${absPath(t.relPath)}.jpg').delete(); } catch (_) {}
    await _pruneEmptyDirs(File(absPath(t.relPath)).parent);
    await _save();
    notifyListeners();
  }

  /// Removes every downloaded track under [relDir] (''=everything) + the dir itself.
  Future<void> removeDir(String relDir) async {
    final prefix = relDir.isEmpty ? '' : '$relDir/';
    _byKey.removeWhere((_, t) => relDir.isEmpty || t.relPath.startsWith(prefix));
    try {
      final d = Directory(relDir.isEmpty ? _root.path : absPath(relDir));
      if (await d.exists()) await d.delete(recursive: true);
      if (relDir.isEmpty) await _root.create(recursive: true);
    } catch (_) {}
    await _save();
    notifyListeners();
  }

  // Walk up deleting now-empty directories, stopping at the downloads root.
  Future<void> _pruneEmptyDirs(Directory dir) async {
    var d = dir;
    while (d.path.length > _root.path.length && d.path.startsWith(_root.path)) {
      try {
        if ((await d.list().isEmpty)) {
          await d.delete();
          d = d.parent;
        } else {
          break;
        }
      } catch (_) {
        break;
      }
    }
  }

  /// Single level of the mirrored tree at [relDir] ('' = root), derived from
  /// manifest relPaths: immediate subfolder names + tracks directly inside.
  ({List<String> folders, List<OfflineTrack> tracks}) listDir(String relDir) {
    final prefix = relDir.isEmpty ? '' : '$relDir/';
    final folders = <String>{};
    final tracks = <OfflineTrack>[];
    for (final t in _byKey.values) {
      if (!t.relPath.startsWith(prefix)) continue;
      final rest = t.relPath.substring(prefix.length);
      final slash = rest.indexOf('/');
      if (slash < 0) {
        tracks.add(t);
      } else {
        folders.add(rest.substring(0, slash));
      }
    }
    return (folders: folders.toList(), tracks: tracks);
  }
}
