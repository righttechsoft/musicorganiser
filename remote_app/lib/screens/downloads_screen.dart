import 'package:flutter/material.dart';

import '../app_state.dart';
import '../offline/download_service.dart';
import '../offline/offline_player.dart';
import '../offline/offline_store.dart';
import '../sort_utils.dart';
import '../theme.dart';
import '../widgets.dart';

/// Offline library: browse downloaded tracks (mirrored desktop folder structure),
/// watch/manage the download queue, and play fully offline via [OfflinePlayer].
/// [standalone] wraps in a Scaffold — used when pushed from the connect screen.
class DownloadsScreen extends StatefulWidget {
  final AppState app;
  final bool standalone;
  const DownloadsScreen({super.key, required this.app, this.standalone = false});

  @override
  State<DownloadsScreen> createState() => _DownloadsScreenState();
}

class _DownloadsScreenState extends State<DownloadsScreen> {
  String _dir = ''; // current rel dir inside the downloads mirror ('' = root)
  String _search = '';
  LibSort _sort = LibSort.nameAsc;
  final TextEditingController _searchCtrl = TextEditingController();

  AppState get app => widget.app;
  OfflineStore get store => app.offline;
  OfflinePlayer get player => app.offlinePlayer;

  @override
  void dispose() {
    _searchCtrl.dispose();
    super.dispose();
  }

  void _go(String relDir) {
    _searchCtrl.clear();
    setState(() {
      _dir = relDir;
      _search = '';
    });
  }

  void _up() {
    if (_dir.isEmpty) return;
    final cut = _dir.lastIndexOf('/');
    _go(cut < 0 ? '' : _dir.substring(0, cut));
  }

  String _fmtBytes(int b) {
    if (b >= 1 << 30) return '${(b / (1 << 30)).toStringAsFixed(1)} GB';
    if (b >= 1 << 20) return '${(b / (1 << 20)).toStringAsFixed(0)} MB';
    return '${(b / 1024).toStringAsFixed(0)} KB';
  }

  @override
  Widget build(BuildContext context) {
    final body = ListenableBuilder(
      listenable: app,
      builder: (context, _) {
        final listing = store.listDir(_dir);
        final q = _search.toLowerCase();
        final folders = sortedBy(
            listing.folders.where((f) => q.isEmpty || f.toLowerCase().contains(q)).toList(),
            _sort, (f) => f, (_) => null, (_) => null);
        final tracks = sortedBy(
            listing.tracks
                .where((t) =>
                    q.isEmpty ||
                    t.name.toLowerCase().contains(q) ||
                    t.title.toLowerCase().contains(q) ||
                    t.artist.toLowerCase().contains(q) ||
                    t.album.toLowerCase().contains(q))
                .toList(),
            _sort, (t) => t.name, (t) => t.downloadedAtMs ~/ 1000, (t) => t.downloadedAtMs ~/ 1000);
        final hasQueue = app.downloads.items.any((i) => i.state != DlState.done);

        return Column(
          children: [
            _header(),
            if (hasQueue) _QueuePanel(app: app),
            if (store.count > 0) _searchSortBar(),
            const Divider(height: 1, color: AppColors.line),
            Expanded(
              child: (folders.isEmpty && tracks.isEmpty)
                  ? EmptyState(
                      icon: _search.isNotEmpty ? Icons.search_off : Icons.download_for_offline_outlined,
                      title: _search.isNotEmpty ? 'No matches' : 'No downloads yet',
                      subtitle: _search.isNotEmpty
                          ? 'Nothing matches "$_search".'
                          : 'Use ⋮ on a track, folder or playlist while connected.',
                    )
                  : ListView.builder(
                      itemCount: folders.length + tracks.length,
                      itemBuilder: (_, i) {
                        if (i < folders.length) {
                          final name = folders[i];
                          final rel = _dir.isEmpty ? name : '$_dir/$name';
                          return FolderRow(
                            name: name,
                            onTap: () => _go(rel),
                            onMenu: () => _folderMenu(name, rel),
                          );
                        }
                        final t = tracks[i - folders.length];
                        return TrackRow(
                          track: t.toTrackFile(),
                          api: null,
                          artFile: store.artFile(t),
                          playing: player.current?.key == t.key,
                          onTap: () {
                            final idx = tracks.indexOf(t);
                            player.playFrom(tracks, idx);
                          },
                          onMenu: () => _trackMenu(t),
                        );
                      },
                    ),
            ),
            _OfflineMiniBar(player: player, store: store),
          ],
        );
      },
    );

    if (!widget.standalone) return body;
    return Scaffold(
      backgroundColor: AppColors.bg,
      appBar: AppBar(
        backgroundColor: AppColors.surface,
        surfaceTintColor: AppColors.surface,
        elevation: 0,
        title: Text('Downloads', style: AppText.screenTitle.copyWith(fontSize: 18)),
      ),
      body: SafeArea(child: body),
    );
  }

  Widget _header() {
    final name = _dir.isEmpty ? 'Downloads' : _dir.split('/').last;
    final sub = _dir.isEmpty
        ? '${_fmtBytes(store.totalBytes)} · ${store.count} ${store.count == 1 ? 'track' : 'tracks'}'
        : _dir;
    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 10, 8, 6),
      child: Row(
        children: [
          OutlinedButton.icon(
            onPressed: _dir.isEmpty ? null : _up,
            style: OutlinedButton.styleFrom(
                side: const BorderSide(color: AppColors.line), visualDensity: VisualDensity.compact),
            icon: const Icon(Icons.arrow_upward, size: 16),
            label: const Text('Up'),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(name, maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.screenTitle.copyWith(fontSize: 18)),
                Text(sub, maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.mono),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _searchSortBar() {
    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 0, 8, 6),
      child: Row(
        children: [
          Expanded(
            child: TextField(
              controller: _searchCtrl,
              onChanged: (v) => setState(() => _search = v),
              style: AppText.mono.copyWith(color: AppColors.text, fontSize: 14),
              decoration: InputDecoration(
                isDense: true,
                contentPadding: const EdgeInsets.symmetric(vertical: 10),
                prefixIcon: const Icon(Icons.search, color: AppColors.muted, size: 20),
                suffixIcon: _search.isEmpty
                    ? null
                    : IconButton(
                        icon: const Icon(Icons.clear, size: 18, color: AppColors.muted),
                        onPressed: () {
                          _searchCtrl.clear();
                          setState(() => _search = '');
                        },
                      ),
                hintText: 'Search downloads',
                filled: true,
                fillColor: AppColors.surface,
                enabledBorder: OutlineInputBorder(
                    borderRadius: BorderRadius.circular(12), borderSide: const BorderSide(color: AppColors.line)),
                focusedBorder: OutlineInputBorder(
                    borderRadius: BorderRadius.circular(12),
                    borderSide: const BorderSide(color: AppColors.accent, width: 1.5)),
              ),
            ),
          ),
          const SizedBox(width: 4),
          PopupMenuButton<LibSort>(
            icon: const Icon(Icons.sort, color: AppColors.muted),
            tooltip: 'Sort',
            initialValue: _sort,
            onSelected: (v) => setState(() => _sort = v),
            itemBuilder: (_) => LibSort.values
                .map((s) => PopupMenuItem<LibSort>(
                      value: s,
                      child: Row(
                        children: [
                          s == _sort
                              ? const Icon(Icons.check, size: 18, color: AppColors.accent)
                              : const SizedBox(width: 18),
                          const SizedBox(width: 8),
                          Text(libSortLabels[s]!),
                        ],
                      ),
                    ))
                .toList(),
          ),
        ],
      ),
    );
  }

  void _trackMenu(OfflineTrack t) {
    showModalBottomSheet(
      context: context,
      backgroundColor: AppColors.surface,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(20))),
      builder: (ctx) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const SizedBox(height: 12),
            ListTile(
              leading: AlbumArt(file: store.artFile(t), size: 40),
              title: Text(t.name, maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.trackTitle),
              subtitle: Text(
                  '${_fmtBytes(t.sizeBytes)} · ${t.bitrate ~/ 1000} kbps',
                  style: AppText.sub),
            ),
            const Divider(height: 1, color: AppColors.line),
            ListTile(
              leading: const Icon(Icons.play_arrow_rounded, color: AppColors.text),
              title: Text('Play', style: AppText.trackTitle.copyWith(fontWeight: FontWeight.w500)),
              onTap: () {
                Navigator.pop(ctx);
                player.playFrom([t], 0);
              },
            ),
            if (app.api != null)
              ListTile(
                leading: const Icon(Icons.refresh, color: AppColors.text),
                title: Text('Re-download', style: AppText.trackTitle.copyWith(fontWeight: FontWeight.w500)),
                onTap: () {
                  Navigator.pop(ctx);
                  app.downloads.enqueueTrack(t.toTrackFile());
                  ScaffoldMessenger.of(context)
                      .showSnackBar(const SnackBar(content: Text('Added to downloads')));
                },
              ),
            ListTile(
              leading: const Icon(Icons.delete_outline_rounded, color: AppColors.danger),
              title: Text('Delete download',
                  style: AppText.trackTitle.copyWith(color: AppColors.danger, fontWeight: FontWeight.w500)),
              onTap: () async {
                Navigator.pop(ctx);
                final yes = await _confirmDelete(t.name,
                    'Removes the downloaded copy from this phone — the desktop file is untouched.');
                if (yes == true) {
                  player.handleDeleted(t.key);
                  await store.removeTrack(t);
                }
              },
            ),
            const SizedBox(height: 8),
          ],
        ),
      ),
    );
  }

  void _folderMenu(String name, String relDir) {
    showModalBottomSheet(
      context: context,
      backgroundColor: AppColors.surface,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(20))),
      builder: (ctx) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const SizedBox(height: 12),
            ListTile(
                leading: const Icon(Icons.folder_open, color: AppColors.text),
                title: Text(name, style: AppText.trackTitle)),
            const Divider(height: 1, color: AppColors.line),
            ListTile(
              leading: const Icon(Icons.delete_outline_rounded, color: AppColors.danger),
              title: Text('Delete all downloads inside',
                  style: AppText.trackTitle.copyWith(color: AppColors.danger, fontWeight: FontWeight.w500)),
              onTap: () async {
                Navigator.pop(ctx);
                final yes = await _confirmDelete(name,
                    'Removes every downloaded track under this folder from the phone — desktop files are untouched.');
                if (yes == true) {
                  // Stop anything playing from inside this folder first.
                  final prefix = '$relDir/';
                  for (final t in store.all.where((t) => t.relPath.startsWith(prefix))) {
                    player.handleDeleted(t.key);
                  }
                  await store.removeDir(relDir);
                }
              },
            ),
            const SizedBox(height: 8),
          ],
        ),
      ),
    );
  }

  Future<bool?> _confirmDelete(String name, String message) {
    return showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        backgroundColor: AppColors.surface,
        title: Text('Delete download?', style: AppText.trackTitle),
        content: Text('"$name" — $message', style: AppText.sub),
        actions: [
          TextButton(
              onPressed: () => Navigator.pop(c, false),
              child: Text('Cancel', style: TextStyle(color: AppColors.text))),
          FilledButton(
            onPressed: () => Navigator.pop(c, true),
            style: FilledButton.styleFrom(backgroundColor: AppColors.danger),
            child: const Text('Delete'),
          ),
        ],
      ),
    );
  }
}

/// Active download queue: progress per item, cancel/retry, clear finished.
class _QueuePanel extends StatelessWidget {
  final AppState app;
  const _QueuePanel({required this.app});

  @override
  Widget build(BuildContext context) {
    final items = app.downloads.items;
    final finished = items.where((i) => i.state != DlState.queued && i.state != DlState.downloading);
    return Container(
      margin: const EdgeInsets.fromLTRB(12, 0, 12, 8),
      padding: const EdgeInsets.fromLTRB(12, 8, 8, 8),
      decoration: BoxDecoration(
        color: AppColors.surface,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: AppColors.line),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(child: Text('DOWNLOAD QUEUE', style: AppText.sectionLabel)),
              if (finished.isNotEmpty)
                TextButton(
                  onPressed: app.downloads.clearFinished,
                  child: Text('Clear finished', style: AppText.sub.copyWith(color: AppColors.accent)),
                ),
            ],
          ),
          // Cap the visible height so a huge queue doesn't swallow the screen.
          ConstrainedBox(
            constraints: const BoxConstraints(maxHeight: 180),
            child: ListView(
              shrinkWrap: true,
              children: [for (final i in items) _row(i)],
            ),
          ),
        ],
      ),
    );
  }

  Widget _row(DownloadItem i) {
    final (icon, color) = switch (i.state) {
      DlState.queued => (Icons.schedule, AppColors.muted),
      DlState.downloading => (Icons.downloading, AppColors.accent),
      DlState.done => (Icons.check_circle_outline, AppColors.success),
      DlState.failed => (Icons.error_outline, AppColors.danger),
      DlState.canceled => (Icons.cancel_outlined, AppColors.muted),
    };
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 3),
      child: Row(
        children: [
          Icon(icon, size: 16, color: color),
          const SizedBox(width: 8),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(i.track.name,
                    maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.sub.copyWith(fontSize: 12)),
                if (i.state == DlState.downloading)
                  Padding(
                    padding: const EdgeInsets.only(top: 3),
                    child: LinearProgressIndicator(
                      value: i.progress,
                      minHeight: 3,
                      backgroundColor: AppColors.line,
                      color: AppColors.accent,
                    ),
                  ),
                if (i.state == DlState.failed && i.error != null)
                  Text(i.error!, maxLines: 1, overflow: TextOverflow.ellipsis,
                      style: AppText.sub.copyWith(fontSize: 11, color: AppColors.danger)),
              ],
            ),
          ),
          if (i.state == DlState.queued || i.state == DlState.downloading)
            InkResponse(
                onTap: () => app.downloads.cancel(i),
                radius: 16,
                child: const Icon(Icons.close, size: 16, color: AppColors.muted))
          else if (i.state == DlState.failed || i.state == DlState.canceled)
            InkResponse(
                onTap: () => app.downloads.retry(i),
                radius: 16,
                child: const Icon(Icons.refresh, size: 16, color: AppColors.accent)),
          const SizedBox(width: 4),
        ],
      ),
    );
  }
}

/// Bottom mini player for offline playback; tap for the full transport sheet.
class _OfflineMiniBar extends StatelessWidget {
  final OfflinePlayer player;
  final OfflineStore store;
  const _OfflineMiniBar({required this.player, required this.store});

  @override
  Widget build(BuildContext context) {
    final t = player.current;
    if (t == null) return const SizedBox.shrink();
    return InkWell(
      onTap: () => _openTransport(context),
      child: Container(
        decoration: const BoxDecoration(
          color: AppColors.surface,
          border: Border(top: BorderSide(color: AppColors.line)),
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            StreamBuilder<Duration>(
              stream: player.positionStream,
              builder: (_, snap) {
                final pos = snap.data ?? Duration.zero;
                final dur = player.duration ?? Duration.zero;
                final v = dur.inMilliseconds > 0 ? pos.inMilliseconds / dur.inMilliseconds : 0.0;
                return LinearProgressIndicator(
                    value: v.clamp(0.0, 1.0), minHeight: 2, backgroundColor: AppColors.line, color: AppColors.accent);
              },
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(12, 6, 4, 6),
              child: Row(
                children: [
                  AlbumArt(file: store.artFile(t), size: 36),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(t.name, maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.trackTitle.copyWith(fontSize: 13)),
                        if (t.artist.isNotEmpty)
                          Text(t.artist, maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.sub.copyWith(fontSize: 11)),
                      ],
                    ),
                  ),
                  IconButton(
                      onPressed: player.prev,
                      icon: const Icon(Icons.skip_previous_rounded, color: AppColors.text)),
                  IconButton(
                      onPressed: player.toggle,
                      icon: Icon(player.playing ? Icons.pause_circle_filled : Icons.play_circle_filled,
                          size: 34, color: AppColors.accent)),
                  IconButton(
                      onPressed: player.next,
                      icon: const Icon(Icons.skip_next_rounded, color: AppColors.text)),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  void _openTransport(BuildContext context) {
    showModalBottomSheet(
      context: context,
      backgroundColor: AppColors.surface,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(20))),
      builder: (_) => _TransportSheet(player: player, store: store),
    );
  }
}

class _TransportSheet extends StatelessWidget {
  final OfflinePlayer player;
  final OfflineStore store;
  const _TransportSheet({required this.player, required this.store});

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: player,
      builder: (context, _) {
        final t = player.current;
        if (t == null) return const SizedBox(height: 80);
        return SafeArea(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(20, 16, 20, 16),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Row(
                  children: [
                    AlbumArt(file: store.artFile(t), size: 56),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(t.name, maxLines: 2, overflow: TextOverflow.ellipsis, style: AppText.trackTitle),
                          if (t.artist.isNotEmpty) Text(t.artist, style: AppText.sub),
                        ],
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 10),
                StreamBuilder<Duration>(
                  stream: player.positionStream,
                  builder: (_, snap) {
                    final pos = snap.data ?? Duration.zero;
                    final dur = player.duration ?? Duration.zero;
                    final max = dur.inSeconds > 0 ? dur.inSeconds.toDouble() : 1.0;
                    return Column(
                      children: [
                        Slider(
                          value: pos.inSeconds.clamp(0, max.toInt()).toDouble(),
                          max: max,
                          activeColor: AppColors.accent,
                          inactiveColor: AppColors.line,
                          onChanged: (v) => player.seek(Duration(seconds: v.round())),
                        ),
                        Row(
                          mainAxisAlignment: MainAxisAlignment.spaceBetween,
                          children: [
                            Text(fmtTime(pos.inSeconds), style: AppText.mono),
                            Text(fmtTime(dur.inSeconds), style: AppText.mono),
                          ],
                        ),
                      ],
                    );
                  },
                ),
                Row(
                  mainAxisAlignment: MainAxisAlignment.spaceEvenly,
                  children: [
                    IconButton(
                      onPressed: player.toggleShuffle,
                      icon: Icon(Icons.shuffle,
                          color: player.shuffle ? AppColors.accent : AppColors.muted),
                    ),
                    IconButton(
                        onPressed: player.prev,
                        icon: const Icon(Icons.skip_previous_rounded, size: 32, color: AppColors.text)),
                    IconButton(
                        onPressed: player.toggle,
                        icon: Icon(player.playing ? Icons.pause_circle_filled : Icons.play_circle_filled,
                            size: 56, color: AppColors.accent)),
                    IconButton(
                        onPressed: player.next,
                        icon: const Icon(Icons.skip_next_rounded, size: 32, color: AppColors.text)),
                    IconButton(
                      onPressed: player.cycleRepeat,
                      icon: Icon(
                        player.repeat == 'one' ? Icons.repeat_one : Icons.repeat,
                        color: player.repeat == 'off' ? AppColors.muted : AppColors.accent,
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        );
      },
    );
  }
}
