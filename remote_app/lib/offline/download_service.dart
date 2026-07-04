import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:http/http.dart' as http;

import '../api_client.dart';
import '../models.dart';
import 'offline_store.dart';

enum DlState { queued, downloading, done, failed, canceled }

class DownloadItem {
  final TrackFile track;
  final int bitrate; // snapshot at enqueue time
  DlState state = DlState.queued;
  int received = 0;
  int? total;
  String? error;

  DownloadItem(this.track, this.bitrate);

  String get key => track.path.toLowerCase();
  double? get progress =>
      (total == null || total == 0) ? null : (received / total!).clamp(0.0, 1.0);
}

/// Serial download queue: one track at a time (the desktop has no transcode
/// concurrency cap, so the phone throttles). Queue is in-memory only — lost on
/// app kill; OfflineStore.init sweeps any orphaned .part files.
class DownloadService extends ChangeNotifier {
  final OfflineStore store;
  final ApiClient? Function() api;
  final int Function() bitrate;

  final List<DownloadItem> _items = [];
  http.Client? _client; // active transfer's client, closed on cancel
  DownloadItem? _active;
  DateTime _lastNotify = DateTime.fromMillisecondsSinceEpoch(0);

  DownloadService(this.store, this.api, this.bitrate);

  List<DownloadItem> get items => List.unmodifiable(_items);
  bool get busy => _active != null;
  bool get hasPending => _items.any(
      (i) => i.state == DlState.queued || i.state == DlState.downloading);

  /// Single-track enqueue always downloads (acts as re-download when it exists).
  void enqueueTrack(TrackFile t) {
    if (_items.any((i) =>
        i.key == t.path.toLowerCase() &&
        (i.state == DlState.queued || i.state == DlState.downloading))) {
      return; // already pending
    }
    _items.add(DownloadItem(t, bitrate()));
    notifyListeners();
    _pump();
  }

  /// Bulk enqueue: skips tracks already downloaded at the same bitrate.
  int enqueueAll(List<TrackFile> tracks) {
    var queued = 0;
    final rate = bitrate();
    for (final t in tracks) {
      final existing = store.byRemote(t.path);
      if (existing != null && existing.bitrate == rate) continue;
      if (_items.any((i) =>
          i.key == t.path.toLowerCase() &&
          (i.state == DlState.queued || i.state == DlState.downloading))) {
        continue;
      }
      _items.add(DownloadItem(t, rate));
      queued++;
    }
    if (queued > 0) {
      notifyListeners();
      _pump();
    }
    return queued;
  }

  void cancel(DownloadItem item) {
    if (item.state == DlState.queued) {
      _items.remove(item);
    } else if (item.state == DlState.downloading) {
      item.state = DlState.canceled; // _run sees this and aborts
      _client?.close(); // kills the byte stream
    }
    notifyListeners();
  }

  void retry(DownloadItem item) {
    if (item.state != DlState.failed && item.state != DlState.canceled) return;
    item.state = DlState.queued;
    item.received = 0;
    item.total = null;
    item.error = null;
    notifyListeners();
    _pump();
  }

  void clearFinished() {
    _items.removeWhere((i) =>
        i.state == DlState.done ||
        i.state == DlState.failed ||
        i.state == DlState.canceled);
    notifyListeners();
  }

  void _pump() {
    if (_active != null) return;
    DownloadItem? next;
    for (final i in _items) {
      if (i.state == DlState.queued) { next = i; break; }
    }
    if (next == null) return;
    _active = next;
    next.state = DlState.downloading;
    notifyListeners();
    _run(next);
  }

  Future<void> _run(DownloadItem item) async {
    final a = api();
    if (a == null) {
      _finish(item, DlState.failed, 'Not connected');
      return;
    }

    final relPath = store.relPathFor(item.track.path);
    final finalPath = store.absPath(relPath);
    final partFile = File('$finalPath.part');
    IOSink? sink;
    try {
      // Client exists before any await so cancel()'s _client.close() always aborts.
      _client = http.Client();
      await partFile.parent.create(recursive: true);
      if (item.state == DlState.canceled) throw const _CanceledException();
      sink = partFile.openWrite();

      final req = http.Request('GET', Uri.parse(a.streamUrl(item.track.path, item.bitrate)));
      // No overall timeout: the server blocks until its transcode finishes, so the
      // first byte can take minutes. Guard with per-chunk inactivity instead.
      final resp = await _client!.send(req);
      if (item.state == DlState.canceled) throw const _CanceledException();
      if (resp.statusCode != 200) {
        throw HttpException('HTTP ${resp.statusCode}');
      }
      item.total = resp.contentLength;

      await for (final chunk in resp.stream.timeout(const Duration(minutes: 2))) {
        if (item.state == DlState.canceled) throw const _CanceledException();
        sink.add(chunk);
        item.received += chunk.length;
        _throttledNotify();
      }
      await sink.flush();
      await sink.close();
      sink = null;
      if (item.state == DlState.canceled) throw const _CanceledException();

      // Desktop died mid-GET -> truncated body. Refuse partial files.
      if (item.total != null && item.received != item.total) {
        throw const HttpException('Incomplete');
      }

      // Best-effort art sidecar.
      var hasArt = false;
      try {
        final art = await http
            .get(Uri.parse(a.artUrl(item.track.path)))
            .timeout(const Duration(seconds: 15));
        if (art.statusCode == 200 && art.bodyBytes.isNotEmpty) {
          await File('$finalPath.jpg').writeAsBytes(art.bodyBytes, flush: true);
          hasArt = true;
        }
      } catch (_) {}
      if (item.state == DlState.canceled) throw const _CanceledException();

      try { await File(finalPath).delete(); } catch (_) {}
      await partFile.rename(finalPath);
      try {
        await store.upsert(OfflineTrack.fromTrackFile(item.track,
            relPath: relPath,
            bitrate: item.bitrate,
            sizeBytes: item.received,
            hasArt: hasArt));
      } catch (e) {
        // Manifest didn't persist: remove the just-renamed file rather than leaving
        // an orphan invisible to (and undeletable from) the UI.
        try { await File(finalPath).delete(); } catch (_) {}
        try { await File('$finalPath.jpg').delete(); } catch (_) {}
        rethrow;
      }
      _finish(item, DlState.done, null);
    } on _CanceledException {
      await _cleanup(sink, partFile);
      _finish(item, DlState.canceled, null);
    } on FileSystemException catch (e) {
      await _cleanup(sink, partFile);
      _finish(item, DlState.failed, 'Write error — storage full? (${e.osError?.message ?? e.message})');
    } catch (e) {
      await _cleanup(sink, partFile);
      // A closed client (cancel race) surfaces as a generic error — honor the cancel.
      _finish(item, item.state == DlState.canceled ? DlState.canceled : DlState.failed,
          item.state == DlState.canceled ? null : '$e');
    }
  }

  Future<void> _cleanup(IOSink? sink, File partFile) async {
    try { await sink?.close(); } catch (_) {}
    try { if (await partFile.exists()) await partFile.delete(); } catch (_) {}
  }

  void _finish(DownloadItem item, DlState state, String? error) {
    // Never overwrite an externally-set cancel with done/failed — the user's
    // cancel during the finalize window must stick.
    if (item.state == DlState.canceled && state != DlState.canceled) {
      state = DlState.canceled;
      error = null;
    }
    item.state = state;
    item.error = error;
    _client?.close();
    _client = null;
    _active = null;
    notifyListeners();
    _pump();
  }

  void _throttledNotify() {
    final now = DateTime.now();
    if (now.difference(_lastNotify).inMilliseconds >= 250) {
      _lastNotify = now;
      notifyListeners();
    }
  }
}

class _CanceledException implements Exception {
  const _CanceledException();
}
