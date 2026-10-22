// Data models mirroring the desktop control API JSON.

int _int(dynamic v) => (v is num) ? v.toInt() : 0;
String _str(dynamic v) => (v is String) ? v : '';
bool _bool(dynamic v) => v == true;

class NowPlaying {
  final String path, title, artist, album;
  final int durationSec;
  final int artCount;
  final bool hasLyrics, synced;
  NowPlaying(
      {required this.path,
      required this.title,
      required this.artist,
      required this.album,
      required this.durationSec,
      this.artCount = 1,
      this.hasLyrics = false,
      this.synced = false});
  factory NowPlaying.fromJson(Map<String, dynamic> j) => NowPlaying(
        path: _str(j['path']),
        title: _str(j['title']),
        artist: _str(j['artist']),
        album: _str(j['album']),
        durationSec: _int(j['durationSec']),
        artCount: (j['artCount'] as num?)?.toInt() ?? 1,
        hasLyrics: _bool(j['hasLyrics']),
        synced: _bool(j['synced']),
      );
  String get name => title.isNotEmpty ? title : path.split(RegExp(r'[\\/]')).last;
}

class PlayerStatus {
  final NowPlaying? nowPlaying;
  final int positionSec, durationSec, volume, systemVolume;
  final bool isPlaying, isPaused, shuffle, stopAfterCurrent;
  final String repeat; // off | all | one
  final String? outputDeviceId;
  final String currentFolder; // desktop's open folder path ('' = root/playlist view)
  PlayerStatus({
    this.nowPlaying,
    required this.positionSec,
    required this.durationSec,
    required this.volume,
    required this.systemVolume,
    required this.isPlaying,
    required this.isPaused,
    required this.shuffle,
    this.stopAfterCurrent = false,
    required this.repeat,
    this.outputDeviceId,
    this.currentFolder = '',
  });
  factory PlayerStatus.fromJson(Map<String, dynamic> j) => PlayerStatus(
        nowPlaying: j['nowPlaying'] == null
            ? null
            : NowPlaying.fromJson(j['nowPlaying'] as Map<String, dynamic>),
        positionSec: _int(j['positionSec']),
        durationSec: _int(j['durationSec']),
        volume: _int(j['volume']),
        systemVolume: _int(j['systemVolume']),
        isPlaying: _bool(j['isPlaying']),
        isPaused: _bool(j['isPaused']),
        shuffle: _bool(j['shuffle']),
        stopAfterCurrent: _bool(j['stopAfterCurrent']),
        repeat: _str(j['repeat']).isEmpty ? 'off' : _str(j['repeat']),
        outputDeviceId: j['outputDeviceId'] == null ? null : _str(j['outputDeviceId']),
        currentFolder: _str(j['currentFolder']),
      );
  static PlayerStatus empty() => PlayerStatus(
      positionSec: 0,
      durationSec: 0,
      volume: 50,
      systemVolume: 50,
      isPlaying: false,
      isPaused: false,
      shuffle: false,
      repeat: 'off');
}

class AudioDevice {
  final String id, name;
  AudioDevice({required this.id, required this.name});
  factory AudioDevice.fromJson(Map<String, dynamic> j) =>
      AudioDevice(id: _str(j['id']), name: _str(j['name']));
}

class TrackFile {
  final String path, title, artist, album, tags;
  final int durationSec;
  final int? rating;
  final bool isPlaying;
  final int? playlistEntryId; // set only for tracks fetched from a playlist
  final int? createdSec, modifiedSec; // filesystem times (browse only); null elsewhere
  TrackFile({
    required this.path,
    required this.title,
    required this.artist,
    required this.album,
    required this.tags,
    required this.durationSec,
    required this.rating,
    required this.isPlaying,
    this.playlistEntryId,
    this.createdSec,
    this.modifiedSec,
  });
  factory TrackFile.fromJson(Map<String, dynamic> j) => TrackFile(
        path: _str(j['path']),
        title: _str(j['title']),
        artist: _str(j['artist']),
        album: _str(j['album']),
        tags: _str(j['tags']),
        durationSec: _int(j['durationSec']),
        rating: j['rating'] == null ? null : _int(j['rating']),
        isPlaying: _bool(j['isPlaying']),
        playlistEntryId: j['playlistEntryId'] == null ? null : _int(j['playlistEntryId']),
        createdSec: j['createdSec'] == null ? null : _int(j['createdSec']),
        modifiedSec: j['modifiedSec'] == null ? null : _int(j['modifiedSec']),
      );
  String get name => title.isNotEmpty ? title : path.split(RegExp(r'[\\/]')).last;
  String get subtitle => [artist, album].where((s) => s.isNotEmpty).join(' — ');
  List<String> get tagList =>
      tags.split(',').map((t) => t.trim()).where((t) => t.isNotEmpty).toList();
}

class FolderEntry {
  final String name, path, tags;
  final int? createdSec, modifiedSec; // filesystem times; null for drives
  FolderEntry({required this.name, required this.path, this.tags = '', this.createdSec, this.modifiedSec});
  factory FolderEntry.fromJson(Map<String, dynamic> j) => FolderEntry(
        name: _str(j['name']),
        path: _str(j['path']),
        tags: _str(j['tags']),
        createdSec: j['createdSec'] == null ? null : _int(j['createdSec']),
        modifiedSec: j['modifiedSec'] == null ? null : _int(j['modifiedSec']),
      );
  List<String> get tagList =>
      tags.split(',').map((t) => t.trim()).where((t) => t.isNotEmpty).toList();
}

class BrowseResult {
  final String path;
  final List<FolderEntry> folders;
  final List<TrackFile> files;
  BrowseResult({required this.path, required this.folders, required this.files});
  factory BrowseResult.fromJson(Map<String, dynamic> j) => BrowseResult(
        path: _str(j['path']),
        folders: ((j['folders'] ?? []) as List)
            .map((e) => FolderEntry.fromJson(e as Map<String, dynamic>))
            .toList(),
        files: ((j['files'] ?? []) as List)
            .map((e) => TrackFile.fromJson(e as Map<String, dynamic>))
            .toList(),
      );
}

class Playlist {
  final int id;
  final String name;
  final int trackCount;
  final bool isOpen;
  Playlist({required this.id, required this.name, required this.trackCount, required this.isOpen});
  factory Playlist.fromJson(Map<String, dynamic> j) => Playlist(
        id: _int(j['id']),
        name: _str(j['name']),
        trackCount: _int(j['trackCount']),
        isOpen: _bool(j['isOpen']),
      );
}

class HistoryItem {
  final String path, displayName;
  HistoryItem({required this.path, required this.displayName});
  factory HistoryItem.fromJson(Map<String, dynamic> j) =>
      HistoryItem(path: _str(j['path']), displayName: _str(j['displayName']));
}

/// One timed karaoke line, parsed from an LRC file.
class LyricLine {
  final Duration time;
  final String text;
  const LyricLine(this.time, this.text);
}

class LyricsData {
  final String? plain;
  final String? lrc;
  final String? source; // lrclib | ai | whisper | manual | embedded
  final bool synced;
  LyricsData({this.plain, this.lrc, this.source, this.synced = false});
  factory LyricsData.fromJson(Map<String, dynamic> j) {
    final plain = _str(j['plain']);
    final lrc = _str(j['lrc']);
    return LyricsData(
      plain: plain.isEmpty ? null : plain,
      lrc: lrc.isEmpty ? null : lrc,
      source: j['source'] == null ? null : _str(j['source']),
      synced: _bool(j['synced']),
    );
  }
  bool get hasAny => (plain?.isNotEmpty ?? false) || (lrc?.isNotEmpty ?? false);

  /// Parsed `[mm:ss.xx] text` lines from [lrc], sorted by time; empty when unsynced
  /// or unparsable. ponytail: only a single leading timestamp per line is handled —
  /// real-world LRC occasionally repeats a line under multiple tags
  /// (`[00:12.00][00:45.00] text`); those extra tags are ignored here.
  List<LyricLine> get lines {
    final raw = lrc;
    if (raw == null || raw.isEmpty) return const [];
    final re = RegExp(r'^\[(\d{1,2}):(\d{2})(?:[.:](\d{1,3}))?\](.*)$');
    final out = <LyricLine>[];
    for (final line in raw.split('\n')) {
      final m = re.firstMatch(line.trimRight());
      if (m == null) continue;
      final min = int.parse(m.group(1)!);
      final sec = int.parse(m.group(2)!);
      final frac = (m.group(3) ?? '0').padRight(3, '0').substring(0, 3);
      out.add(LyricLine(
          Duration(minutes: min, seconds: sec, milliseconds: int.parse(frac)), m.group(4)!.trim()));
    }
    out.sort((a, b) => a.time.compareTo(b.time));
    return out;
  }
}

/// One entry from GET /tags: a folder-tag vocabulary name and how many folders carry it.
class TagUsage {
  final String name;
  final int folderCount;
  TagUsage({required this.name, required this.folderCount});
  factory TagUsage.fromJson(Map<String, dynamic> j) =>
      TagUsage(name: _str(j['name']), folderCount: _int(j['folderCount']));
}

/// One existing destination folder that looks like the folder being moved (/folder/similar).
class SimilarFolder {
  final String path;
  final String name;
  final int fileCount;
  final int totalSize;
  final int newCount; // source files missing in this folder
  final int existingCount; // source files already in this folder
  SimilarFolder({
    required this.path,
    required this.name,
    required this.fileCount,
    required this.totalSize,
    required this.newCount,
    required this.existingCount,
  });
  factory SimilarFolder.fromJson(Map<String, dynamic> j) => SimilarFolder(
        path: _str(j['path']),
        name: _str(j['name']),
        fileCount: _int(j['fileCount']),
        totalSize: _int(j['totalSize']),
        newCount: _int(j['newCount']),
        existingCount: _int(j['existingCount']),
      );
}

class SimilarFolders {
  final String sourceName;
  final int sourceFileCount;
  final int sourceTotalSize;
  final String suggestedName;
  final List<SimilarFolder> folders;
  SimilarFolders({
    this.sourceName = '',
    this.sourceFileCount = 0,
    this.sourceTotalSize = 0,
    this.suggestedName = '',
    this.folders = const [],
  });
  factory SimilarFolders.fromJson(Map<String, dynamic> j) {
    final s = j['source'] is Map ? (j['source'] as Map).cast<String, dynamic>() : <String, dynamic>{};
    return SimilarFolders(
      sourceName: _str(s['name']),
      sourceFileCount: _int(s['fileCount']),
      sourceTotalSize: _int(s['totalSize']),
      suggestedName: _str(j['suggestedName']),
      folders: j['folders'] is List
          ? (j['folders'] as List).map((e) => SimilarFolder.fromJson(e as Map<String, dynamic>)).toList()
          : <SimilarFolder>[],
    );
  }
}

/// Seek-bar waveform: loudness [peaks] (0-255) and optional per-band energy used to colour the bars.
class WaveformData {
  final List<int> peaks;
  final List<int>? low, mid, high; // null unless all three bands are present and match peaks
  final List<int>? tempo, beat; // null unless present and matching peaks
  final List<List<int>>? spectrum; // 8 band rows (bass first), null unless complete
  WaveformData(this.peaks, {this.low, this.mid, this.high, this.tempo, this.beat, this.spectrum});
  bool get hasBands => low != null;
  bool get hasFingerprint => low != null && tempo != null && beat != null && spectrum != null;
}

class FolderTagSuggestion {
  final List<String>? suggested; // null = AI unavailable
  final String current; // csv of the folder's currently saved tags
  FolderTagSuggestion({this.suggested, required this.current});
  factory FolderTagSuggestion.fromJson(Map<String, dynamic> j) => FolderTagSuggestion(
        suggested: j['suggested'] == null ? null : (j['suggested'] as List).map((e) => e.toString()).toList(),
        current: _str(j['current']),
      );
}

/// One file returned by the desktop's recursive /files?path=&recursive=1 listing (used by the
/// bulk-tag band preview player). [relativePath] is relative to the requested folder.
class BandFile {
  final String path;
  final String relativePath;
  BandFile({required this.path, required this.relativePath});
  factory BandFile.fromJson(Map<String, dynamic> j) =>
      BandFile(path: _str(j['path']), relativePath: _str(j['relativePath']));
}
