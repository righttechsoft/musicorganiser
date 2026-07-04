import 'dart:async';
import 'dart:math';

import 'package:flutter/foundation.dart';
import 'package:just_audio/just_audio.dart';

import 'offline_store.dart';

/// Local playback of downloaded tracks with its OWN transport (queue/next/prev/
/// shuffle/repeat) — the desktop drives none of this. Owns a dedicated
/// AudioPlayer; the AppState `_player` belongs to the remote-sink stream loop.
class OfflinePlayer extends ChangeNotifier {
  final OfflineStore store;

  /// Called right before offline playback starts — AppState wires this to stop
  /// the remote-sink ("This device") stream so both never play at once.
  void Function()? onWillPlay;

  final AudioPlayer _p = AudioPlayer();
  final Random _rng = Random();

  List<OfflineTrack> _queue = []; // snapshot of the displayed (sorted) tracks
  List<int> _order = [];          // play order: indices into _queue
  int _pos = -1;                  // position within _order
  bool shuffle = false;
  String repeat = 'off';          // off | all | one
  bool _loading = false;          // suppresses auto-advance while a load is in flight
  // Generation token: every new load/stop invalidates in-flight _load()s so a
  // superseded setFilePath can't skip-advance or play() after stop.
  int _gen = 0;

  OfflinePlayer(this.store) {
    _p.processingStateStream.listen((s) {
      if (s == ProcessingState.completed && !_loading) _onCompleted();
    });
    _p.playingStream.listen((_) => notifyListeners());
  }

  OfflineTrack? get current =>
      (_pos >= 0 && _pos < _order.length) ? _queue[_order[_pos]] : null;
  bool get playing => _p.playing && _p.processingState != ProcessingState.completed;
  Stream<Duration> get positionStream => _p.positionStream;
  Duration? get duration => _p.duration;
  Duration get position => _p.position;

  Future<void> playFrom(List<OfflineTrack> tracks, int index) async {
    if (tracks.isEmpty || index < 0 || index >= tracks.length) return;
    onWillPlay?.call();
    _queue = List.of(tracks);
    _rebuildOrder(startAt: index); // sets _pos (0 when shuffled, index otherwise)
    await _load();
  }

  void _rebuildOrder({required int startAt}) {
    _order = List.generate(_queue.length, (i) => i);
    if (shuffle) {
      _order.removeAt(startAt);
      _order.shuffle(_rng);
      _order.insert(0, startAt);
    } else {
      // natural order, but _pos points at startAt
      _pos = startAt;
      return;
    }
    _pos = 0;
  }

  Future<void> _load({bool autoplay = true}) async {
    final t = current;
    if (t == null) return;
    final gen = ++_gen; // supersede any in-flight load
    _loading = true;
    notifyListeners();
    var skips = 0;
    var track = t;
    while (true) {
      try {
        await _p.setFilePath(store.absPath(track.relPath));
        if (gen != _gen) return; // superseded while loading — do nothing
        if (autoplay) _p.play();
        break;
      } on PlayerInterruptedException {
        return; // a newer load/stop preempted this one — never treat as missing file
      } catch (_) {
        if (gen != _gen) return;
        // File missing (deleted out-of-band): skip forward, capped to one lap.
        skips++;
        if (skips >= _queue.length || !_advance(wrapOk: repeat == 'all')) {
          await _p.stop();
          if (gen == _gen) _pos = -1;
          break;
        }
        track = current!;
      }
    }
    if (gen == _gen) {
      _loading = false;
      notifyListeners();
    }
  }

  void _onCompleted() {
    if (repeat == 'one') {
      _p.seek(Duration.zero);
      _p.play();
      return;
    }
    if (_advance(wrapOk: repeat == 'all')) {
      _load();
    } else {
      _p.stop(); // end of queue; keep the queue for the UI
      notifyListeners();
    }
  }

  // Moves _pos forward; returns false at the end when wrapping isn't allowed.
  bool _advance({required bool wrapOk}) {
    if (_pos + 1 < _order.length) {
      _pos++;
      return true;
    }
    if (wrapOk && _order.isNotEmpty) {
      _pos = 0;
      return true;
    }
    return false;
  }

  Future<void> toggle() async {
    if (current == null) return;
    if (_p.processingState == ProcessingState.completed) {
      await _p.seek(Duration.zero);
      _p.play();
    } else if (_p.playing) {
      await _p.pause();
    } else {
      _p.play();
    }
    notifyListeners();
  }

  Future<void> next() async {
    if (current == null) return;
    if (_advance(wrapOk: true)) await _load();
  }

  Future<void> prev() async {
    if (current == null) return;
    if (_p.position.inSeconds > 3) {
      await _p.seek(Duration.zero);
      return;
    }
    if (_pos > 0) {
      _pos--;
    } else if (repeat == 'all' && _order.isNotEmpty) {
      _pos = _order.length - 1;
    } else {
      await _p.seek(Duration.zero);
      return;
    }
    await _load();
  }

  Future<void> seek(Duration d) => _p.seek(d);

  Future<void> stop() async {
    _gen++; // invalidate any in-flight _load so it can't play() after this stop
    _loading = false;
    await _p.stop();
    _pos = -1;
    _queue = [];
    _order = [];
    notifyListeners();
  }

  void toggleShuffle() {
    shuffle = !shuffle;
    if (_queue.isEmpty) { notifyListeners(); return; }
    final cur = _order.isNotEmpty && _pos >= 0 ? _order[_pos] : 0;
    if (shuffle) {
      _order = List.generate(_queue.length, (i) => i)..remove(cur);
      _order.shuffle(_rng);
      _order.insert(0, cur);
      _pos = 0;
    } else {
      _order = List.generate(_queue.length, (i) => i);
      _pos = cur;
    }
    notifyListeners();
  }

  void cycleRepeat() {
    repeat = switch (repeat) { 'off' => 'all', 'all' => 'one', _ => 'off' };
    notifyListeners();
  }

  /// A downloaded track was deleted from the store.
  void handleDeleted(String key) {
    if (current?.key == key) {
      // Drop it, then continue with whatever is next (or stop). Preserve the
      // paused state; only wrap past the end when repeat-all is on.
      final wasPlaying = _p.playing;
      final idx = _order[_pos];
      _queue.removeAt(idx);
      _order = [
        for (final i in _order)
          if (i != idx) i > idx ? i - 1 : i
      ];
      if (_pos >= _order.length) {
        _pos = (repeat == 'all' && _order.isNotEmpty) ? 0 : -1;
      }
      if (_pos >= 0) {
        _load(autoplay: wasPlaying);
      } else {
        stop();
      }
      return;
    }
    final idx = _queue.indexWhere((t) => t.key == key);
    if (idx < 0) return;
    final curQueueIdx = _pos >= 0 && _pos < _order.length ? _order[_pos] : -1;
    _queue.removeAt(idx);
    _order = [
      for (final i in _order)
        if (i != idx) i > idx ? i - 1 : i
    ];
    if (curQueueIdx >= 0) {
      final newCur = curQueueIdx > idx ? curQueueIdx - 1 : curQueueIdx;
      _pos = _order.indexOf(newCur);
    }
    notifyListeners();
  }

  @override
  void dispose() {
    _p.dispose();
    super.dispose();
  }
}
