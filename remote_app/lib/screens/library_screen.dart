import 'package:flutter/material.dart';
import '../app_state.dart';
import '../models.dart';
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

  AppState get app => widget.app;

  @override
  void initState() {
    super.initState();
    final pending = app.pendingLibraryPath;
    app.pendingLibraryPath = null;
    _go(pending ?? '');
    app.addListener(_onApp);
  }

  @override
  void dispose() {
    app.removeListener(_onApp);
    super.dispose();
  }

  // Consume an external "open this folder" request from History.
  void _onApp() {
    final p = app.pendingLibraryPath;
    if (p != null) {
      app.pendingLibraryPath = null;
      _go(p);
    }
  }

  Future<void> _go(String path) async {
    setState(() => _loading = true);
    try {
      // open:true mirrors the navigation on the desktop (expands + selects the folder).
      final data = await app.api!.browse(path, open: path.isNotEmpty);
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
    _go(cut > 1 ? p.substring(0, cut) : '');
  }

  Future<void> _play(TrackFile t) async {
    app.goNow();
    await app.api!.play(t.path);
    await app.loadFiles();
  }

  bool _same(String? a, String b) => a != null && a.toLowerCase() == b.toLowerCase();

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
                _go(f.path);
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
        final folders = _data?.folders ?? const <FolderEntry>[];
        final files = _data?.files ?? const <TrackFile>[];
        final playingPath = app.status.nowPlaying?.path;
        return Column(
          children: [
            _header(),
            if (files.isNotEmpty)
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
            Expanded(
              child: _loading
                  ? const SkeletonList()
                  : (folders.isEmpty && files.isEmpty)
                      ? EmptyState(
                          icon: _path.isEmpty ? Icons.storage : Icons.folder_open,
                          title: _path.isEmpty ? 'No drives' : 'Empty folder',
                          subtitle: _path.isEmpty ? null : 'No subfolders or tracks here.',
                        )
                      : RefreshIndicator(
                          onRefresh: () => _go(_path),
                          color: AppColors.accent,
                          child: ListView.builder(
                            itemCount: folders.length + files.length,
                            itemBuilder: (_, i) {
                              if (i < folders.length) {
                                final f = folders[i];
                                return FolderRow(
                                  name: f.name.isEmpty ? f.path : f.name,
                                  onTap: () => _go(f.path),
                                  onMenu: () => _folderMenu(f),
                                );
                              }
                              final t = files[i - folders.length];
                              return TrackRow(
                                track: t,
                                api: app.api!,
                                playing: _same(playingPath, t.path),
                                onTap: () => _play(t),
                                onMenu: () => showTrackActions(
                                  context,
                                  track: t,
                                  api: app.api!,
                                  onChanged: () async {
                                    await app.loadFiles();
                                    await _go(_path);
                                  },
                                ),
                              );
                            },
                          ),
                        ),
            ),
          ],
        );
      },
    );
  }

  Widget _header() {
    final name = _path.isEmpty
        ? 'Library'
        : (() {
            final p = _path.replaceAll(RegExp(r'[\\/]+$'), '');
            final cut = p.lastIndexOf(RegExp(r'[\\/]'));
            return cut >= 0 && cut < p.length - 1 ? p.substring(cut + 1) : p;
          })();
    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 10, 8, 6),
      child: Row(
        children: [
          OutlinedButton.icon(
            onPressed: _path.isEmpty ? null : _up,
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
                Text(_path.isEmpty ? 'This PC (drives)' : _path,
                    maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.mono),
              ],
            ),
          ),
          IconButton(onPressed: () => _go(_path), icon: const Icon(Icons.refresh, color: AppColors.muted)),
        ],
      ),
    );
  }
}
