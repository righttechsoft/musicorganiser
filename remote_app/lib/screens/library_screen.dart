import 'dart:async';
import 'package:flutter/material.dart';
import 'package:just_audio/just_audio.dart';
import '../api_client.dart';
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

  // Folder tag filter (browse mode only): ANY selected tag matches a folder.
  // Options are scoped to the tags visible in the current listing, not the whole library.
  final Set<String> _selectedFolderTags = {};

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
          if (!_searchMode) ...[
            const SizedBox(width: 4),
            IconButton(
              icon: Icon(Icons.sell_outlined,
                  color: _selectedFolderTags.isEmpty ? AppColors.muted : AppColors.accent),
              tooltip: 'Filter by tag',
              onPressed: _showTagFilterSheet,
            ),
          ],
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

  // ANY selected tag matches (case-insensitive), same rule as the desktop tree filter.
  List<FolderEntry> _filterByTags(List<FolderEntry> src) {
    if (_selectedFolderTags.isEmpty) return src;
    return src
        .where((f) => _selectedFolderTags
            .any((t) => f.tagList.any((ft) => ft.toLowerCase() == t.toLowerCase())))
        .toList();
  }

  Future<void> _showTagFilterSheet() async {
    final available = <String>{};
    for (final f in _data?.folders ?? const <FolderEntry>[]) {
      available.addAll(f.tagList);
    }
    final sortedTags = available.toList()..sort();
    if (sortedTags.isEmpty) {
      ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('No tags in this folder')));
      return;
    }
    await showModalBottomSheet<void>(
      context: context,
      backgroundColor: AppColors.surface,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(20))),
      builder: (ctx) => StatefulBuilder(
        builder: (ctx, setSheetState) => SafeArea(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  mainAxisAlignment: MainAxisAlignment.spaceBetween,
                  children: [
                    sectionLabel('Filter by tag'),
                    TextButton.icon(
                      onPressed: () {
                        Navigator.pop(ctx);
                        _manageTags();
                      },
                      style: TextButton.styleFrom(
                          padding: EdgeInsets.zero,
                          minimumSize: Size.zero,
                          tapTargetSize: MaterialTapTargetSize.shrinkWrap),
                      icon: const Icon(Icons.settings_outlined, size: 15, color: AppColors.muted),
                      label: Text('Manage tags', style: AppText.sub.copyWith(color: AppColors.muted)),
                    ),
                  ],
                ),
                const SizedBox(height: 4),
                Wrap(
                  spacing: 8,
                  runSpacing: 8,
                  children: [
                    for (final t in sortedTags)
                      TagChip(
                        label: t,
                        ghost: !_selectedFolderTags.contains(t),
                        onTap: () => setSheetState(() => setState(() {
                          if (!_selectedFolderTags.remove(t)) _selectedFolderTags.add(t);
                        })),
                      ),
                  ],
                ),
                const SizedBox(height: 16),
                Row(
                  children: [
                    Expanded(
                      child: OutlinedButton(
                        onPressed: _selectedFolderTags.isEmpty
                            ? null
                            : () => setSheetState(() => setState(() => _selectedFolderTags.clear())),
                        style: OutlinedButton.styleFrom(side: const BorderSide(color: AppColors.line)),
                        child: Text('Clear', style: TextStyle(color: AppColors.text)),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: FilledButton(
                        onPressed: () => Navigator.pop(ctx),
                        style: FilledButton.styleFrom(backgroundColor: AppColors.accent),
                        child: const Text('Done'),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
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
              leading: const Icon(Icons.sell_outlined, color: AppColors.text),
              title: Text('Tag folder', style: AppText.trackTitle.copyWith(fontWeight: FontWeight.w500)),
              onTap: () {
                Navigator.pop(ctx);
                _tagFolder(f);
              },
            ),
            ListTile(
              leading: const Icon(Icons.checklist, color: AppColors.text),
              title: Text('Bulk tag', style: AppText.trackTitle.copyWith(fontWeight: FontWeight.w500)),
              onTap: () {
                Navigator.pop(ctx);
                _bulkTagFolder(f);
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

  Future<void> _tagFolder(FolderEntry f) async {
    final saved = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      backgroundColor: AppColors.surface,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(20))),
      builder: (ctx) => Padding(
        padding: EdgeInsets.only(bottom: MediaQuery.of(ctx).viewInsets.bottom),
        child: _FolderTagSheet(api: app.api!, folder: f),
      ),
    );
    if (saved == true && mounted) {
      ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Tags saved')));
    }
  }

  Future<void> _bulkTagFolder(FolderEntry f) async {
    await Navigator.of(context).push(
      MaterialPageRoute(
          builder: (_) => _BulkTagPage(api: app.api!, parent: f, bitrate: app.streamBitrate), fullscreenDialog: true),
    );
  }

  Future<void> _manageTags() async {
    await Navigator.of(context).push(
      MaterialPageRoute(builder: (_) => _ManageTagsPage(api: app.api!), fullscreenDialog: true),
    );
    // A delete/merge on the desktop-shared vocabulary can change what this folder shows.
    if (mounted) await _go(_path);
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
            : _filterByTags(_sorted((_data?.folders ?? const <FolderEntry>[]).toList(),
                (f) => f.name, (f) => f.createdSec, (f) => f.modifiedSec));
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

/// Tag-folder bottom sheet: current tags as deletable chips, an auto-run AI
/// suggestion pass (ghost chips, tap to add), a custom-tag field, and Save.
class _FolderTagSheet extends StatefulWidget {
  final ApiClient api;
  final FolderEntry folder;
  const _FolderTagSheet({required this.api, required this.folder});

  @override
  State<_FolderTagSheet> createState() => _FolderTagSheetState();
}

class _FolderTagSheetState extends State<_FolderTagSheet> {
  List<String> _tags = [];
  List<String> _suggestions = [];
  final _ctrl = TextEditingController();
  bool _loading = true;
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _ctrl.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final csv = await widget.api.folderTags(widget.folder.path);
      _tags = csv.split(',').map((t) => t.trim()).where((t) => t.isNotEmpty).toList();
    } catch (_) {}
    if (mounted) setState(() {});

    try {
      final res = await widget.api.suggestFolderTags(widget.folder.path);
      if (!mounted) return;
      setState(() {
        _loading = false;
        if (res.suggested == null) {
          _error = 'AI unavailable';
        } else {
          _suggestions = res.suggested!.where((t) => !_tags.contains(t)).toList();
        }
      });
    } catch (_) {
      if (mounted) {
        setState(() {
          _loading = false;
          _error = 'AI unavailable';
        });
      }
    }
  }

  void _add(String raw) {
    final t = raw.trim().replaceAll('#', '');
    if (t.isEmpty || _tags.contains(t)) return;
    setState(() {
      _tags.add(t);
      _suggestions.remove(t);
    });
    _ctrl.clear();
  }

  Future<void> _save() async {
    await widget.api.setFolderTags(widget.folder.path, _tags.join(','));
    if (mounted) Navigator.pop(context, true);
  }

  @override
  Widget build(BuildContext context) {
    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            sectionLabel('Tag folder — ${widget.folder.name}'),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                for (final t in _tags)
                  TagChip(label: t, onDeleted: () => setState(() => _tags.remove(t))),
              ],
            ),
            const SizedBox(height: 10),
            if (_loading)
              Row(
                children: [
                  const SizedBox(
                      width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2, color: AppColors.accent)),
                  const SizedBox(width: 8),
                  Text('Suggesting tags (AI)…', style: AppText.sub),
                ],
              )
            else if (_error != null)
              Text(_error!, style: AppText.sub.copyWith(color: AppColors.danger))
            else if (_suggestions.isNotEmpty) ...[
              Text('Suggested', style: AppText.sub),
              const SizedBox(height: 6),
              Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  for (final s in _suggestions)
                    TagChip(label: '+ #$s', ghost: true, onTap: () => _add(s)),
                ],
              ),
            ],
            const SizedBox(height: 12),
            TextField(
              controller: _ctrl,
              onSubmitted: _add,
              decoration: InputDecoration(
                hintText: 'add tag',
                filled: true,
                fillColor: AppColors.bg,
                border: OutlineInputBorder(borderRadius: BorderRadius.circular(10), borderSide: BorderSide.none),
                suffixIcon: IconButton(icon: const Icon(Icons.add), onPressed: () => _add(_ctrl.text)),
              ),
            ),
            const SizedBox(height: 16),
            Row(
              children: [
                Expanded(
                  child: OutlinedButton(
                      onPressed: () => Navigator.pop(context),
                      style: OutlinedButton.styleFrom(side: const BorderSide(color: AppColors.line)),
                      child: Text('Cancel', style: TextStyle(color: AppColors.text))),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: FilledButton(
                    onPressed: _save,
                    style: FilledButton.styleFrom(backgroundColor: AppColors.accent),
                    child: const Text('Save'),
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

/// One row in the bulk-tag list: a subfolder (band) plus its AI-suggested tags
/// (editable in [controller] before Apply) and a status string.
class _BulkRow {
  final String name;
  final String path;
  bool include = true;
  bool saved = false;
  String status = 'pending';
  final TextEditingController controller = TextEditingController();
  _BulkRow(this.name, this.path);
}

/// Lists the whole (desktop-shared) folder-tag vocabulary with its per-tag folder count, and
/// lets the user delete a tag or merge it into another — both irreversible, both confirmed.
/// Thin client mirroring the desktop's TagManagerDialog over GET/POST /tags*.
class _ManageTagsPage extends StatefulWidget {
  final ApiClient api;
  const _ManageTagsPage({required this.api});

  @override
  State<_ManageTagsPage> createState() => _ManageTagsPageState();
}

class _ManageTagsPageState extends State<_ManageTagsPage> {
  List<TagUsage> _tags = [];
  bool _loading = true;
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() => _loading = true);
    List<TagUsage> tags;
    try {
      tags = await widget.api.tags();
    } catch (_) {
      tags = const [];
    }
    if (!mounted) return;
    setState(() {
      _tags = tags;
      _loading = false;
      _busy = false;
    });
  }

  Future<bool?> _confirm(String title, String message) => showDialog<bool>(
        context: context,
        builder: (ctx) => AlertDialog(
          backgroundColor: AppColors.surface,
          title: Text(title, style: AppText.trackTitle),
          content: Text(message, style: AppText.sub.copyWith(color: AppColors.text)),
          actions: [
            TextButton(
                onPressed: () => Navigator.pop(ctx, false),
                child: Text('Cancel', style: TextStyle(color: AppColors.text))),
            FilledButton(
              onPressed: () => Navigator.pop(ctx, true),
              style: FilledButton.styleFrom(backgroundColor: AppColors.danger),
              child: Text(title),
            ),
          ],
        ),
      );

  Future<void> _delete(TagUsage row) async {
    final message = row.folderCount == 0
        ? 'This tag is unused. This cannot be undone.'
        : 'This will remove "${row.name}" from ${row.folderCount} '
            '${row.folderCount == 1 ? "folder" : "folders"}. This cannot be undone.';
    final yes = await _confirm('Delete "${row.name}"?', message);
    if (yes != true || !mounted) return;
    setState(() => _busy = true);
    try {
      await widget.api.deleteTag(row.name);
    } catch (_) {}
    await _load();
  }

  Future<void> _merge(TagUsage row) async {
    final others = _tags.where((t) => t.name.toLowerCase() != row.name.toLowerCase()).map((t) => t.name).toList();
    if (others.isEmpty) {
      ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('No other tags to merge into')));
      return;
    }
    final target = await showModalBottomSheet<String>(
      context: context,
      backgroundColor: AppColors.surface,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(20))),
      builder: (ctx) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const SizedBox(height: 12),
            sectionLabel('Merge "${row.name}" into'),
            ConstrainedBox(
              constraints: const BoxConstraints(maxHeight: 320),
              child: ListView(
                shrinkWrap: true,
                children: [
                  for (final t in others)
                    ListTile(
                      title: Text(t, style: AppText.trackTitle),
                      onTap: () => Navigator.pop(ctx, t),
                    ),
                ],
              ),
            ),
            const SizedBox(height: 8),
          ],
        ),
      ),
    );
    if (target == null || !mounted) return;

    final yes = await _confirm('Merge "${row.name}" into "$target"?',
        'Every folder tagged "${row.name}" will be tagged "$target" instead, and '
            '"${row.name}" will be removed. This cannot be undone.');
    if (yes != true || !mounted) return;
    setState(() => _busy = true);
    try {
      await widget.api.mergeTag(row.name, target);
    } catch (_) {}
    await _load();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: AppColors.surface,
      appBar: AppBar(
        backgroundColor: AppColors.surface,
        surfaceTintColor: AppColors.surface,
        elevation: 0,
        title: Text('Manage tags', style: AppText.trackTitle.copyWith(fontSize: 16)),
      ),
      body: _loading
          ? const SkeletonList()
          : _tags.isEmpty
              ? const EmptyState(icon: Icons.sell_outlined, title: 'No tags yet')
              : AbsorbPointer(
                  absorbing: _busy,
                  child: Opacity(
                    opacity: _busy ? 0.5 : 1,
                    child: ListView.separated(
                      padding: const EdgeInsets.symmetric(vertical: 8),
                      itemCount: _tags.length,
                      separatorBuilder: (_, _) => const Divider(height: 1, color: AppColors.line),
                      itemBuilder: (_, i) {
                        final row = _tags[i];
                        return ListTile(
                          title: Text(row.name, style: AppText.trackTitle.copyWith(fontWeight: FontWeight.w500)),
                          subtitle: Text('${row.folderCount} ${row.folderCount == 1 ? "folder" : "folders"}',
                              style: AppText.sub),
                          trailing: Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              IconButton(
                                icon: const Icon(Icons.merge_type, color: AppColors.muted),
                                tooltip: 'Merge into…',
                                onPressed: () => _merge(row),
                              ),
                              IconButton(
                                icon: const Icon(Icons.delete_outline, color: AppColors.danger),
                                tooltip: 'Delete',
                                onPressed: () => _delete(row),
                              ),
                            ],
                          ),
                        );
                      },
                    ),
                  ),
                ),
    );
  }
}

/// Bulk-tags every untagged immediate subfolder of [parent], treating each as a band.
/// Thin client — the desktop does the AI work via the same /folder/tags/suggest endpoint
/// the single-folder _FolderTagSheet uses. Processes in batches of [_batchSize] with
/// bounded AI concurrency; Apply saves the checked rows, Next batch continues.
class _BulkTagPage extends StatefulWidget {
  final ApiClient api;
  final FolderEntry parent;
  final int bitrate;
  const _BulkTagPage({required this.api, required this.parent, required this.bitrate});

  @override
  State<_BulkTagPage> createState() => _BulkTagPageState();
}

class _BulkTagPageState extends State<_BulkTagPage> {
  static const _batchSize = 20;
  static const _concurrency = 3;

  List<FolderEntry> _remaining = [];
  int _totalSubfolders = 0;
  final List<_BulkRow> _rows = [];
  bool _loadingList = true;
  bool _busy = false;
  bool _closed = false;

  @override
  void initState() {
    super.initState();
    _init();
  }

  @override
  void dispose() {
    _closed = true;
    for (final r in _rows) {
      r.controller.dispose();
    }
    super.dispose();
  }

  Future<void> _init() async {
    try {
      final r = await widget.api.browse(widget.parent.path);
      _totalSubfolders = r.folders.length;
      final untagged = <FolderEntry>[];
      for (final sub in r.folders) {
        if (_closed) return;
        try {
          final tags = await widget.api.folderTags(sub.path);
          if (tags.trim().isEmpty) untagged.add(sub);
        } catch (_) {
          untagged.add(sub); // lookup failed — safest to still offer it for tagging
        }
      }
      _remaining = untagged;
    } catch (_) {}
    if (!mounted) return;
    setState(() => _loadingList = false);
    await _loadNextBatch();
  }

  Future<void> _loadNextBatch() async {
    if (_closed || _remaining.isEmpty) return;

    final batch = _remaining.take(_batchSize).toList();
    _remaining = _remaining.skip(batch.length).toList();

    for (final r in _rows) {
      r.controller.dispose();
    }
    setState(() {
      _rows
        ..clear()
        ..addAll(batch.map((f) => _BulkRow(f.name.isEmpty ? f.path : f.name, f.path)));
      _busy = true;
    });

    final queue = List<_BulkRow>.from(_rows);
    Future<void> worker() async {
      while (queue.isNotEmpty) {
        if (_closed) return;
        final row = queue.removeAt(0);
        setState(() => row.status = 'searching');
        try {
          final res = await widget.api.suggestFolderTags(row.path);
          if (_closed) return;
          if (res.suggested == null) {
            row.status = 'failed';
            row.include = false;
          } else if (res.suggested!.isEmpty) {
            row.status = 'not identified';
            row.include = false;
          } else {
            row.controller.text = res.suggested!.join(', ');
            row.status = 'done';
          }
        } catch (_) {
          if (_closed) return;
          row.status = 'failed';
          row.include = false;
        }
        if (mounted) setState(() {});
      }
    }

    await Future.wait(List.generate(_concurrency, (_) => worker()));
    if (mounted) setState(() => _busy = false);
  }

  Future<void> _apply() async {
    setState(() => _busy = true);
    for (final row in _rows) {
      if (!row.include || row.saved) continue;
      final csv = row.controller.text.trim();
      if (csv.isEmpty) continue;
      try {
        await widget.api.setFolderTags(row.path, csv);
        // Read back the authoritative persisted value (the desktop trims/dedupes) so the row
        // shows exactly what was saved, not just what was typed.
        var saved = csv;
        try {
          saved = await widget.api.folderTags(row.path);
        } catch (_) {}
        if (mounted) {
          setState(() {
            row.controller.text = saved;
            row.status = 'saved';
            row.saved = true;
          });
        }
      } catch (_) {}
    }
    if (mounted) {
      setState(() => _busy = false);
      ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Tags saved')));
    }
  }

  void _previewBand(_BulkRow row) {
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: AppColors.surface,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(20))),
      builder: (_) => _BandPreviewSheet(
        api: widget.api,
        bandName: row.name,
        folderPath: row.path,
        bitrate: widget.bitrate,
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: AppColors.surface,
      appBar: AppBar(
        backgroundColor: AppColors.surface,
        surfaceTintColor: AppColors.surface,
        elevation: 0,
        titleSpacing: 0,
        title: Text('Bulk tag — ${widget.parent.name}',
            style: AppText.trackTitle.copyWith(fontSize: 16), overflow: TextOverflow.ellipsis),
        actions: [IconButton(onPressed: () => Navigator.pop(context), icon: const Icon(Icons.close))],
      ),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 8, 16, 4),
            child: Align(
              alignment: Alignment.centerLeft,
              child: Text(
                _loadingList ? 'Scanning subfolders…' : '${_remaining.length + _rows.length} untagged of $_totalSubfolders',
                style: AppText.sub,
              ),
            ),
          ),
          const Divider(height: 1, color: AppColors.line),
          Expanded(
            child: _loadingList
                ? const SkeletonList()
                : _rows.isEmpty
                    ? const EmptyState(icon: Icons.check_circle_outline, title: 'Nothing to tag')
                    : ListView.builder(
                        itemCount: _rows.length,
                        itemBuilder: (_, i) {
                          final row = _rows[i];
                          return Padding(
                            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
                            child: Row(
                              crossAxisAlignment: CrossAxisAlignment.center,
                              children: [
                                Checkbox(
                                  value: row.include,
                                  activeColor: AppColors.accent,
                                  onChanged:
                                      row.saved ? null : (v) => setState(() => row.include = v ?? false),
                                ),
                                IconButton(
                                  icon: const Icon(Icons.play_circle_outline, color: AppColors.accent),
                                  visualDensity: VisualDensity.compact,
                                  padding: EdgeInsets.zero,
                                  constraints: const BoxConstraints(),
                                  onPressed: () => _previewBand(row),
                                ),
                                const SizedBox(width: 4),
                                SizedBox(
                                  width: 100,
                                  child: Text(row.name,
                                      maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.trackTitle.copyWith(fontSize: 13)),
                                ),
                                const SizedBox(width: 8),
                                Expanded(
                                  child: TextField(
                                    controller: row.controller,
                                    style: AppText.sub.copyWith(color: AppColors.text),
                                    decoration: const InputDecoration(isDense: true, hintText: 'tags'),
                                  ),
                                ),
                                const SizedBox(width: 8),
                                SizedBox(
                                  width: 84,
                                  child: Text(row.status, style: AppText.sub, textAlign: TextAlign.right, maxLines: 1, overflow: TextOverflow.ellipsis),
                                ),
                              ],
                            ),
                          );
                        },
                      ),
          ),
          SafeArea(
            child: Padding(
              padding: const EdgeInsets.all(12),
              child: Row(
                children: [
                  Expanded(
                    child: FilledButton(
                      onPressed: (_busy || _rows.isEmpty) ? null : _apply,
                      style: FilledButton.styleFrom(backgroundColor: AppColors.accent),
                      child: const Text('Apply'),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: OutlinedButton(
                      onPressed: (_busy || _remaining.isEmpty) ? null : _loadNextBatch,
                      style: OutlinedButton.styleFrom(side: const BorderSide(color: AppColors.line)),
                      child: Text('Next batch', style: TextStyle(color: AppColors.text)),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// Per-band audio preview sheet, opened from a _BulkTagPage row's play icon. Owns its OWN
/// just_audio player instance — deliberately not AppState's player or OfflinePlayer, so
/// auditioning a track never disturbs "now playing" or offline playback elsewhere in the app.
class _BandPreviewSheet extends StatefulWidget {
  final ApiClient api;
  final String bandName;
  final String folderPath;
  final int bitrate;
  const _BandPreviewSheet(
      {required this.api, required this.bandName, required this.folderPath, required this.bitrate});

  @override
  State<_BandPreviewSheet> createState() => _BandPreviewSheetState();
}

class _BandPreviewSheetState extends State<_BandPreviewSheet> {
  final AudioPlayer _player = AudioPlayer();
  List<BandFile> _files = [];
  bool _loading = true;
  int _current = -1;
  StreamSubscription<PlayerState>? _stateSub;

  @override
  void initState() {
    super.initState();
    _stateSub = _player.playerStateStream.listen((state) {
      if (state.processingState == ProcessingState.completed) _playNext();
    });
    _load();
  }

  @override
  void dispose() {
    _stateSub?.cancel();
    _player.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final files = await widget.api.filesRecursive(widget.folderPath);
      if (mounted) {
        setState(() {
          _files = files;
          _loading = false;
        });
      }
    } catch (_) {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _play(int index) async {
    if (index < 0 || index >= _files.length) return;
    setState(() => _current = index);
    try {
      await _player.setUrl(widget.api.streamUrl(_files[index].path, widget.bitrate));
      await _player.play();
    } catch (_) {}
  }

  void _playNext() {
    if (_current + 1 < _files.length) _play(_current + 1);
  }

  @override
  Widget build(BuildContext context) {
    return SafeArea(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const SizedBox(height: 10),
          Container(
              width: 40,
              height: 4,
              decoration: BoxDecoration(color: AppColors.line, borderRadius: BorderRadius.circular(2))),
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
            child: Text(widget.bandName, style: AppText.trackTitle, maxLines: 1, overflow: TextOverflow.ellipsis),
          ),
          StreamBuilder<Duration>(
            stream: _player.positionStream,
            builder: (context, snap) {
              final pos = snap.data ?? Duration.zero;
              final dur = _player.duration ?? Duration.zero;
              final maxMs = dur.inMilliseconds > 0 ? dur.inMilliseconds.toDouble() : 1.0;
              return Slider(
                value: pos.inMilliseconds.toDouble().clamp(0, maxMs),
                max: maxMs,
                activeColor: AppColors.accent,
                onChanged: (v) => _player.seek(Duration(milliseconds: v.toInt())),
              );
            },
          ),
          StreamBuilder<PlayerState>(
            stream: _player.playerStateStream,
            builder: (context, snap) {
              final playing = snap.data?.playing ?? false;
              return IconButton(
                icon: Icon(playing ? Icons.pause_circle : Icons.play_circle, size: 44, color: AppColors.accent),
                onPressed: _current < 0 ? null : () => playing ? _player.pause() : _player.play(),
              );
            },
          ),
          const Divider(height: 1, color: AppColors.line),
          SizedBox(
            height: 360,
            child: _loading
                ? const SkeletonList()
                : ListView.builder(
                    itemCount: _files.length,
                    itemBuilder: (_, i) => ListTile(
                      selected: i == _current,
                      selectedTileColor: AppColors.accentTint,
                      title: Text(_files[i].relativePath,
                          maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.trackTitle.copyWith(fontSize: 13)),
                      onTap: () => _play(i),
                    ),
                  ),
          ),
          const SizedBox(height: 8),
        ],
      ),
    );
  }
}
