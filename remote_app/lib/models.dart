// Data models mirroring the desktop control API JSON.

int _int(dynamic v) => (v is num) ? v.toInt() : 0;
String _str(dynamic v) => (v is String) ? v : '';
bool _bool(dynamic v) => v == true;

class NowPlaying {
  final String path, title, artist, album;
  final int durationSec;
  NowPlaying(
      {required this.path,
      required this.title,
      required this.artist,
      required this.album,
      required this.durationSec});
  factory NowPlaying.fromJson(Map<String, dynamic> j) => NowPlaying(
        path: _str(j['path']),
        title: _str(j['title']),
        artist: _str(j['artist']),
        album: _str(j['album']),
        durationSec: _int(j['durationSec']),
      );
  String get name => title.isNotEmpty ? title : path.split(RegExp(r'[\\/]')).last;
}

class PlayerStatus {
  final NowPlaying? nowPlaying;
  final int positionSec, durationSec, volume, systemVolume;
  final bool isPlaying, isPaused, shuffle;
  final String repeat; // off | all | one
  final String? outputDeviceId;
  PlayerStatus({
    this.nowPlaying,
    required this.positionSec,
    required this.durationSec,
    required this.volume,
    required this.systemVolume,
    required this.isPlaying,
    required this.isPaused,
    required this.shuffle,
    required this.repeat,
    this.outputDeviceId,
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
        repeat: _str(j['repeat']).isEmpty ? 'off' : _str(j['repeat']),
        outputDeviceId: j['outputDeviceId'] == null ? null : _str(j['outputDeviceId']),
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
      );
  String get name => title.isNotEmpty ? title : path.split(RegExp(r'[\\/]')).last;
  String get subtitle => [artist, album].where((s) => s.isNotEmpty).join(' — ');
  List<String> get tagList =>
      tags.split(',').map((t) => t.trim()).where((t) => t.isNotEmpty).toList();
}

class FolderEntry {
  final String name, path;
  FolderEntry({required this.name, required this.path});
  factory FolderEntry.fromJson(Map<String, dynamic> j) =>
      FolderEntry(name: _str(j['name']), path: _str(j['path']));
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
