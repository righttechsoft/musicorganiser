import 'dart:async';
import 'package:flutter/material.dart';
import '../app_state.dart';
import '../models.dart';
import '../sort_utils.dart';
import '../theme.dart';
import '../widgets.dart';
import 'playlists_screen.dart';
import 'track_actions.dart';

/// Merged browse + files: navigating into a folder shows its subfolders AND its
/// tracks inline. Tap a track to play it (its folder becomes the play queue).
class LibraryScreen extends StatefulWidget {
  final AppState app;
  const LibraryScreen({super.key, required this.app});

  @override
  State<LibraryScreen> createState() => _LibraryScreenState();
}

class _LibraryScreenState extends State<LibraryScreen> {
  String _path = '';
  BrowseResult? _data;
  bool _loading = true;
  String _search = '';
  LibSort _sort = LibSort.nameAsc;
  final TextEditingController _searchCtrl = TextEditingController();

  // Global search: when the box is non-empty we show flat results from the whole
  // library (desktop /search) instead of the current folder's contents.
  List<TrackFile>? _results; // null until a search runs; [] = ran, no matches
  bool _searching = false;
  Timer? _debounce;

  AppState get app => widget.app;

  // Last desktop folder we synced to. We follow the desktop only when THIS value
  // changes — so the 1s poll doesn't yank the phone off wherever the user browsed,
  // and our own mirror:true navigation (which moves the desktop) doesn't loop back.
  String? _lastDesktopFolder;

  @override
  void initState() {
    super.initState();
    final pending = app.pendingLibraryPath;
    app.pendingLibraryPath = null;
    _lastDesktopFolder = app.status.currentFolder;
    final desk = _lastDesktopFolder ?? '';
    _go(pending ?? (desk.isNotEmpty ? desk : ''));
    app.addListener(_onApp);
  }

  @override
  void dispose() {
    app.removeListener(_onApp);
    _debounce?.cancel();
    _searchCtrl.dispose();
    super.dispose();
  }

  // Consume an external "open this folder" request from History, and mirror the
  // desktop's open folder onto the phone when the desktop navigates.
  void _onApp() {
    final p = app.pendingLibraryPath;
    if (p != null) {
      app.pendingLibraryPath = null;
      _lastDesktopFolder = app.status.currentFolder;
      _go(p);
      return;
    }
    final df = app.status.currentFolder;
    if (df.isNotEmpty && df != _lastDesktopFolder) {
      _lastDesktopFolder = df; // record even if already there, so we don't re-follow
      if (!_same(_path, df)) _go(df);
    }
  }

  // mirror:true also moves the desktop's folder view/queue to [path]. Pass it ONLY for
  // explicit user navigation (folder tap / Up) — so merely opening, resuming, or refreshing
  // the app never hijacks the desktop's grid/queue while it's playing another folder.
  Future<void> _go(String path, {bool mirror = false}) async {
    _searchCtrl.clear();
    _debounce?.cancel();
    setState(() {
      _loading = true;
      _search = ''; // leaving search mode: entering a folder shows its contents
      _results = null;
      _searching = false;
    });
    try {
      final data = await app.api!.browse(path, open: mirror && path.isNotEmpty);
      if (!mounted) return;
      setState(() {
        _data = data;
        _path = data.path;
        _loading = false;
      });
    } catch (_) {
      if (mounted) setState(() => _loading = false);
    }
  }

  void _up() {
    if (_path.isEmpty) return;
    final p = _path.replaceAll(RegExp(r'[\\/]+$'), '');
    final cut = p.lastIndexOf(RegExp(r'[\\/]'));
    _go(cut > 1 ? p.substring(0, cut) : '', mirror: true);
  }

  Future<void> _play(TrackFile t) async {
    app.goNow();
    await app.api!.play(t.path);
    await app.loadFiles();
  }

  bool _same(String? a, String b) => a != null && a.toLowerCase() == b.toLowerCase();

  // ---- search + sort ----

  bool get _searchMode => _search.trim().isNotEmpty;

  void _onSearchChanged(String v) {
    setState(() => _search = v);
    _debounce?.cancel();
    final q = v.trim();
    if (q.isEmpty) {
      setState(() {
        _results = null;
        _searching = false;
      });
      return;
    }
    setState(() => _searching = true);
    _debounce = Timer(const Duration(milliseconds: 300), () => _runSearch(q));
  }

  Future<void> _runSearch(String q) async {
    try {
      final res = await app.api!.search(q);
      if (!mounted || _search.trim() != q) return; // typed on since; drop stale result
      setState(() {
        _results = res;
        _searching = false;
      });
    } catch (_) {
      if (mounted && _search.trim() == q) {
        setState(() {
          _results = const [];
          _searching = false;
        });
      }
    }
  }

  void _clearSearch() {
    _debounce?.cancel();
    _searchCtrl.clear();
    setState(() {
      _search = '';
      _results = null;
      _searching = false;
    });
  }

  List<T> _sorted<T>(List<T> src, String Function(T) name, int? Function(T) created, int? Function(T) modified) =>
      sortedBy(src, _sort, name, created, modified);

  Widget _searchSortBar() {
    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 0, 8, 6),
      child: Row(
        children: [
          Expanded(
            child: TextField(
              controller: _searchCtrl,
              onChanged: _onSearchChanged,
              textInputAction: TextInputAction.search,
              style: AppText.mono.copyWith(color: AppColors.text, fontSize: 14),
              decoration: InputDecoration(
                isDense: true,
                contentPadding: const EdgeInsets.symmetric(vertical: 10),
                prefixIcon: const Icon(Icons.search, color: AppColors.muted, size: 20),
                suffixIcon: _search.isEmpty
                    ? null
                    : IconButton(
                        icon: const Icon(Icons.clear, size: 18, color: AppColors.muted),
                        onPressed: _clearSearch,
                      ),
                hintText: 'Search whole library',
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

  void _folderMenu(FolderEntry f) {
    showModalBottomSheet(
      context: context,
      backgroundColor: AppColors.surface,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(20))),
      builder: (ctx) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const SizedBox(height: 12),
            ListTile(leading: const Icon(Icons.folder_open, color: AppColors.text), title: Text(f.name, style: AppText.trackTitle)),
            const Divider(height: 1, color: AppColors.line),
            ListTile(
              leading: const Icon(Icons.open_in_new, color: AppColors.text),
              title: Text('Open', style: AppText.trackTitle.copyWith(fontWeight: FontWeight.w500)),
              onTap: () {
                Navigator.pop(ctx);
                _go(f.path, mirror: true);
              },
            ),
            ListTile(
              leading: const Icon(Icons.playlist_add, color: AppColors.text),
              title: Text('Add to playlist', style: AppText.trackTitle.copyWith(fontWeight: FontWeight.w500)),
              onTap: () async {
                Navigator.pop(ctx);
                await showAddToPlaylist(context, app.api!, folder: f.path);
              },
            ),
            ListTile(
              leading: const Icon(Icons.download_for_offline_outlined, color: AppColors.text),
              title: Text('Download folder', style: AppText.trackTitle.copyWith(fontWeight: FontWeight.w500)),
              onTap: () async {
                Navigator.pop(ctx);
                try {
                  final r = await app.api!.browse(f.path);
                  final n = app.downloads.enqueueAll(r.files);
                  if (mounted) {
                    ScaffoldMessenger.of(context).showSnackBar(SnackBar(
                        content: Text(n > 0
                            ? '$n track${n == 1 ? '' : 's'} queued'
                            : (r.files.isEmpty ? 'No tracks in this folder' : 'Already downloaded'))));
                  }
                } catch (_) {
                  if (mounted) {
                    ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Could not load folder')));
                  }
                }
              },
            ),
            ListTile(
              leading: const Icon(Icons.drive_file_move_outline, color: AppColors.text),
              title: Text('Move to…', style: AppText.trackTitle.copyWith(fontWeight: FontWeight.w500)),
              onTap: () async {
                Navigator.pop(ctx);
                final dest = await showMovePicker(context, api: app.api!, movingLabel: f.name);
                if (dest != null) {
                  final ok = await app.api!.moveFolder(f.path, dest);
                  if (mounted) {
                    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(ok ? 'Moved' : 'Move failed')));
                  }
                  _go(_path);
                }
              },
            ),
            ListTile(
              leading: const Icon(Icons.delete_outline_rounded, color: AppColors.danger),
              title: Text('Delete', style: AppText.trackTitle.copyWith(color: AppColors.danger, fontWeight: FontWeight.w500)),
              onTap: () async {
                Navigator.pop(ctx);
                final yes = await showDeleteConfirm(context, name: f.name, isFolder: true);
                if (yes == true) {
                  final ok = await app.api!.deleteFolder(f.path);
                  if (mounted) {
                    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(ok ? 'Deleted' : 'Delete failed')));
                  }
                  _go(_path);
                }
              },
            ),
            const SizedBox(height: 8),
          ],
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: app,
      builder: (context, _) {
        final playingPath = app.status.nowPlaying?.path;
        final searchMode = _searchMode;

        // Search mode: flat global results (no folders). Browse mode: current folder.
        final folders = searchMode
            ? const <FolderEntry>[]
            : _sorted((_data?.folders ?? const <FolderEntry>[]).toList(),
                (f) => f.name, (f) => f.createdSec, (f) => f.modifiedSec);
        final files = _sorted(
            (searchMode ? (_results ?? const <TrackFile>[]) : (_data?.files ?? const <TrackFile>[])).toList(),
            (t) => t.name, (t) => t.createdSec, (t) => t.modifiedSec);

        return Column(
          children: [
            _header(searchMode: searchMode, resultCount: files.length),
            if (!_loading) _searchSortBar(),
            if (!searchMode && files.isNotEmpty)
              Padding(
                padding: const EdgeInsets.fromLTRB(12, 4, 12, 8),
                child: SizedBox(
                  width: double.infinity,
                  child: FilledButton.icon(
                    onPressed: () => _play(files.first),
                    style: FilledButton.styleFrom(
                        backgroundColor: AppColors.accent, padding: const EdgeInsets.symmetric(vertical: 14)),
                    icon: const Icon(Icons.play_arrow_rounded),
                    label: const Text('Play this folder'),
                  ),
                ),
              ),
            const Divider(height: 1, color: AppColors.line),
            Expanded(child: _body(searchMode, folders, files, playingPath)),
          ],
        );
      },
    );
  }

  Widget _body(bool searchMode, List<FolderEntry> folders, List<TrackFile> files, String? playingPath) {
    if (searchMode) {
      if (_searching && _results == null) return const SkeletonList();
      if (files.isEmpty) {
        return EmptyState(
          icon: Icons.search_off,
          title: 'No matches',
          subtitle: 'Nothing in your library matches "${_search.trim()}".',
        );
      }
      return ListView.builder(
        itemCount: files.length,
        itemBuilder: (_, i) {
          final t = files[i];
          return TrackRow(
            track: t,
            api: app.api!,
            playing: _same(playingPath, t.path),
            downloaded: app.offline.has(t.path),
            onTap: () => _play(t),
            onMenu: () => showTrackActions(
              context,
              track: t,
              api: app.api!,
              app: app,
              onChanged: () async {
                await app.loadFiles();
                await _runSearch(_search.trim()); // re-run after rating/tag/move/delete
              },
            ),
          );
        },
      );
    }

    if (_loading) return const SkeletonList();
    if (folders.isEmpty && files.isEmpty) {
      return EmptyState(
        icon: _path.isEmpty ? Icons.storage : Icons.folder_open,
        title: _path.isEmpty ? 'No drives' : 'Empty folder',
        subtitle: _path.isEmpty ? null : 'No subfolders or tracks here.',
      );
    }
    return RefreshIndicator(
      onRefresh: () => _go(_path),
      color: AppColors.accent,
      child: ListView.builder(
        itemCount: folders.length + files.length,
        itemBuilder: (_, i) {
          if (i < folders.length) {
            final f = folders[i];
            return FolderRow(
              name: f.name.isEmpty ? f.path : f.name,
              onTap: () => _go(f.path, mirror: true),
              onMenu: () => _folderMenu(f),
            );
          }
          final t = files[i - folders.length];
          return TrackRow(
            track: t,
            api: app.api!,
            playing: _same(playingPath, t.path),
            downloaded: app.offline.has(t.path),
            onTap: () => _play(t),
            onMenu: () => showTrackActions(
              context,
              track: t,
              api: app.api!,
              app: app,
              onChanged: () async {
                await app.loadFiles();
                await _go(_path);
              },
            ),
          );
        },
      ),
    );
  }

  Widget _header({bool searchMode = false, int resultCount = 0}) {
    final name = searchMode
        ? 'Search'
        : (_path.isEmpty
            ? 'Library'
            : (() {
                final p = _path.replaceAll(RegExp(r'[\\/]+$'), '');
                final cut = p.lastIndexOf(RegExp(r'[\\/]'));
                return cut >= 0 && cut < p.length - 1 ? p.substring(cut + 1) : p;
              })());
    final subtitle = searchMode
        ? (_searching && _results == null
            ? 'Searching…'
            : '$resultCount result${resultCount == 1 ? '' : 's'} · whole library')
        : (_path.isEmpty ? 'This PC (drives)' : _path);
    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 10, 8, 6),
      child: Row(
        children: [
          OutlinedButton.icon(
            onPressed: (searchMode || _path.isEmpty) ? null : _up,
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
                Text(subtitle, maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.mono),
              ],
            ),
          ),
          IconButton(
            onPressed: () => searchMode ? _runSearch(_search.trim()) : _go(_path),
            icon: const Icon(Icons.refresh, color: AppColors.muted),
          ),
        ],
      ),
    );
  }
}
