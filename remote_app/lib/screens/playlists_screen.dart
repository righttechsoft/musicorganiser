import 'package:flutter/material.dart';
import '../api_client.dart';
import '../app_state.dart';
import '../models.dart';
import '../theme.dart';
import '../widgets.dart';
import 'track_actions.dart';

class PlaylistsScreen extends StatefulWidget {
  final AppState app;
  const PlaylistsScreen({super.key, required this.app});

  @override
  State<PlaylistsScreen> createState() => _PlaylistsScreenState();
}

class _PlaylistsScreenState extends State<PlaylistsScreen> {
  List<Playlist>? _items;
  bool _loading = true;

  AppState get app => widget.app;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() => _loading = true);
    try {
      final items = await app.api!.playlists();
      if (mounted) setState(() { _items = items; _loading = false; });
    } catch (_) {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _create() async {
    final name = await promptName(context, title: 'New playlist', hint: 'Playlist name');
    if (name == null || name.trim().isEmpty) return;
    await app.api!.createPlaylist(name.trim());
    await _load();
  }

  void _open(Playlist p) async {
    await Navigator.of(context).push(MaterialPageRoute(
      builder: (_) => PlaylistDetailScreen(app: app, playlist: p),
    ));
    _load(); // counts may have changed
  }

  void _menu(Playlist p) {
    showModalBottomSheet(
      context: context,
      backgroundColor: AppColors.surface,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(20))),
      builder: (ctx) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const SizedBox(height: 12),
            ListTile(leading: const Icon(Icons.queue_music, color: AppColors.text), title: Text(p.name, style: AppText.trackTitle)),
            const Divider(height: 1, color: AppColors.line),
            ListTile(
              leading: const Icon(Icons.play_arrow_rounded, color: AppColors.text),
              title: Text('Play', style: AppText.trackTitle.copyWith(fontWeight: FontWeight.w500)),
              onTap: () { Navigator.pop(ctx); app.goNow(); app.api!.playPlaylist(p.id); },
            ),
            ListTile(
              leading: const Icon(Icons.drive_file_rename_outline, color: AppColors.text),
              title: Text('Rename', style: AppText.trackTitle.copyWith(fontWeight: FontWeight.w500)),
              onTap: () async {
                Navigator.pop(ctx);
                final name = await promptName(context, title: 'Rename playlist', hint: 'Name', initial: p.name);
                if (name != null && name.trim().isNotEmpty) {
                  await app.api!.renamePlaylist(p.id, name.trim());
                  _load();
                }
              },
            ),
            ListTile(
              leading: const Icon(Icons.delete_outline_rounded, color: AppColors.danger),
              title: Text('Delete', style: AppText.trackTitle.copyWith(color: AppColors.danger, fontWeight: FontWeight.w500)),
              onTap: () async {
                Navigator.pop(ctx);
                final yes = await showDialog<bool>(
                  context: context,
                  builder: (c) => AlertDialog(
                    backgroundColor: AppColors.surface,
                    title: Text('Delete playlist?', style: AppText.trackTitle),
                    content: Text('"${p.name}" — the tracks stay on disk, only the playlist is removed.', style: AppText.sub),
                    actions: [
                      TextButton(onPressed: () => Navigator.pop(c, false), child: Text('Cancel', style: TextStyle(color: AppColors.text))),
                      FilledButton(
                        onPressed: () => Navigator.pop(c, true),
                        style: FilledButton.styleFrom(backgroundColor: AppColors.danger),
                        child: const Text('Delete'),
                      ),
                    ],
                  ),
                );
                if (yes == true) { await app.api!.deletePlaylist(p.id); _load(); }
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
    final items = _items ?? const <Playlist>[];
    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 10, 8, 6),
          child: Row(
            children: [
              Expanded(child: Text('Playlists', style: AppText.screenTitle)),
              IconButton(onPressed: _create, icon: const Icon(Icons.add, color: AppColors.accent)),
              IconButton(onPressed: _load, icon: const Icon(Icons.refresh, color: AppColors.muted)),
            ],
          ),
        ),
        Expanded(
          child: _loading
              ? const SkeletonList()
              : items.isEmpty
                  ? EmptyState(
                      icon: Icons.queue_music,
                      title: 'No playlists',
                      subtitle: 'Create one, then add tracks or folders from the ⋮ menus.',
                      action: FilledButton.icon(
                        onPressed: _create,
                        style: FilledButton.styleFrom(backgroundColor: AppColors.accent),
                        icon: const Icon(Icons.add),
                        label: const Text('New playlist'),
                      ),
                    )
                  : RefreshIndicator(
                      onRefresh: _load,
                      color: AppColors.accent,
                      child: ListView.builder(
                        itemCount: items.length,
                        itemBuilder: (_, i) {
                          final p = items[i];
                          return InkWell(
                            onTap: () => _open(p),
                            child: Container(
                              decoration: const BoxDecoration(border: Border(bottom: BorderSide(color: AppColors.line))),
                              padding: const EdgeInsets.fromLTRB(16, 14, 8, 14),
                              child: Row(
                                children: [
                                  Container(
                                    padding: const EdgeInsets.all(9),
                                    decoration: BoxDecoration(color: AppColors.accentTint, borderRadius: BorderRadius.circular(10)),
                                    child: const Icon(Icons.queue_music, size: 22, color: AppColors.accent),
                                  ),
                                  const SizedBox(width: 12),
                                  Expanded(
                                    child: Column(
                                      crossAxisAlignment: CrossAxisAlignment.start,
                                      children: [
                                        Text(p.name, maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.trackTitle),
                                        Text('${p.trackCount} ${p.trackCount == 1 ? "track" : "tracks"}', style: AppText.sub),
                                      ],
                                    ),
                                  ),
                                  IconButton(
                                    onPressed: () { app.goNow(); app.api!.playPlaylist(p.id); },
                                    icon: const Icon(Icons.play_circle_outline, color: AppColors.accent),
                                  ),
                                  InkResponse(
                                      onTap: () => _menu(p),
                                      radius: 20,
                                      child: const Icon(Icons.more_vert, size: 20, color: AppColors.muted)),
                                  const SizedBox(width: 4),
                                ],
                              ),
                            ),
                          );
                        },
                      ),
                    ),
        ),
      ],
    );
  }
}

class PlaylistDetailScreen extends StatefulWidget {
  final AppState app;
  final Playlist playlist;
  const PlaylistDetailScreen({super.key, required this.app, required this.playlist});

  @override
  State<PlaylistDetailScreen> createState() => _PlaylistDetailScreenState();
}

class _PlaylistDetailScreenState extends State<PlaylistDetailScreen> {
  List<TrackFile>? _tracks;
  bool _loading = true;

  AppState get app => widget.app;
  int get id => widget.playlist.id;

  @override
  void initState() {
    super.initState();
    // Make this playlist the desktop's active queue so track taps play in-context.
    app.api!.openPlaylist(id);
    _load();
  }

  Future<void> _load() async {
    setState(() => _loading = true);
    try {
      final t = await app.api!.playlistFiles(id);
      if (mounted) setState(() { _tracks = t; _loading = false; });
    } catch (_) {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _play(TrackFile t) async {
    app.goNow();
    if (mounted) Navigator.pop(context); // leave the detail so the Now screen shows
    await app.api!.openPlaylist(id); // ensure the queue is this playlist
    await app.api!.play(t.path);
    await app.loadFiles();
  }

  bool _same(String? a, String b) => a != null && a.toLowerCase() == b.toLowerCase();

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: app,
      builder: (context, _) {
        final tracks = _tracks ?? const <TrackFile>[];
        final playingPath = app.status.nowPlaying?.path;
        return Scaffold(
          backgroundColor: AppColors.bg,
          appBar: AppBar(
            backgroundColor: AppColors.surface,
            surfaceTintColor: AppColors.surface,
            elevation: 0,
            title: Text(widget.playlist.name, style: AppText.screenTitle.copyWith(fontSize: 18)),
            actions: [IconButton(onPressed: _load, icon: const Icon(Icons.refresh, color: AppColors.muted))],
          ),
          body: Column(
            children: [
              if (tracks.isNotEmpty)
                Padding(
                  padding: const EdgeInsets.fromLTRB(12, 8, 12, 8),
                  child: SizedBox(
                    width: double.infinity,
                    child: FilledButton.icon(
                      onPressed: () {
                        app.goNow();
                        app.api!.playPlaylist(id);
                        Navigator.pop(context); // back to the shell, now on Now Playing
                      },
                      style: FilledButton.styleFrom(
                          backgroundColor: AppColors.accent, padding: const EdgeInsets.symmetric(vertical: 14)),
                      icon: const Icon(Icons.play_arrow_rounded),
                      label: const Text('Play'),
                    ),
                  ),
                ),
              const Divider(height: 1, color: AppColors.line),
              Expanded(
                child: _loading
                    ? const SkeletonList()
                    : tracks.isEmpty
                        ? const EmptyState(
                            icon: Icons.queue_music,
                            title: 'Empty playlist',
                            subtitle: 'Add tracks or folders from the ⋮ menus in Library.')
                        : RefreshIndicator(
                            onRefresh: _load,
                            color: AppColors.accent,
                            child: ListView.builder(
                              itemCount: tracks.length,
                              itemBuilder: (_, i) {
                                final t = tracks[i];
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
                                      await _load();
                                    },
                                    removeFromPlaylistId: t.playlistEntryId == null ? null : id,
                                  ),
                                );
                              },
                            ),
                          ),
              ),
            ],
          ),
        );
      },
    );
  }
}

/// Pick (or create) a playlist to add [path] or [folder] to.
Future<void> showAddToPlaylist(BuildContext context, ApiClient api, {String? path, String? folder}) async {
  List<Playlist> playlists;
  try {
    playlists = await api.playlists();
  } catch (_) {
    playlists = [];
  }
  if (!context.mounted) return;

  Future<void> addTo(int id) async {
    if (folder != null) {
      await api.addFolderToPlaylist(id, folder);
    } else if (path != null) {
      await api.addFileToPlaylist(id, path);
    }
  }

  await showModalBottomSheet(
    context: context,
    backgroundColor: AppColors.surface,
    shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(20))),
    builder: (ctx) => SafeArea(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const SizedBox(height: 14),
          sectionLabel('Add to playlist'),
          const Divider(height: 1, color: AppColors.line),
          ListTile(
            leading: const Icon(Icons.add, color: AppColors.accent),
            title: Text('New playlist…', style: AppText.trackTitle.copyWith(color: AppColors.accent, fontWeight: FontWeight.w600)),
            onTap: () async {
              final name = await promptName(ctx, title: 'New playlist', hint: 'Playlist name');
              if (name == null || name.trim().isEmpty) return;
              final id = await api.createPlaylist(name.trim());
              if (id != null) await addTo(id);
              if (ctx.mounted) Navigator.pop(ctx);
              if (context.mounted) {
                ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Added to playlist')));
              }
            },
          ),
          if (playlists.isNotEmpty) const Divider(height: 1, color: AppColors.line),
          Flexible(
            child: ListView(
              shrinkWrap: true,
              children: [
                for (final p in playlists)
                  ListTile(
                    leading: const Icon(Icons.queue_music, color: AppColors.text),
                    title: Text(p.name, style: AppText.trackTitle.copyWith(fontWeight: FontWeight.w500)),
                    subtitle: Text('${p.trackCount} ${p.trackCount == 1 ? "track" : "tracks"}', style: AppText.sub),
                    onTap: () async {
                      await addTo(p.id);
                      if (ctx.mounted) Navigator.pop(ctx);
                      if (context.mounted) {
                        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Added to ${p.name}')));
                      }
                    },
                  ),
              ],
            ),
          ),
          const SizedBox(height: 8),
        ],
      ),
    ),
  );
}

/// Simple single-field name prompt. Returns the text, or null on cancel.
Future<String?> promptName(BuildContext context, {required String title, required String hint, String? initial}) {
  final ctrl = TextEditingController(text: initial ?? '');
  return showDialog<String>(
    context: context,
    builder: (ctx) => AlertDialog(
      backgroundColor: AppColors.surface,
      title: Text(title, style: AppText.trackTitle),
      content: TextField(
        controller: ctrl,
        autofocus: true,
        onSubmitted: (v) => Navigator.pop(ctx, v),
        decoration: InputDecoration(
          hintText: hint,
          filled: true,
          fillColor: AppColors.bg,
          border: OutlineInputBorder(borderRadius: BorderRadius.circular(10), borderSide: BorderSide.none),
        ),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(ctx), child: Text('Cancel', style: TextStyle(color: AppColors.text))),
        FilledButton(
          onPressed: () => Navigator.pop(ctx, ctrl.text),
          style: FilledButton.styleFrom(backgroundColor: AppColors.accent),
          child: const Text('OK'),
        ),
      ],
    ),
  );
}
