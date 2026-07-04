import 'package:flutter/material.dart';
import '../api_client.dart';
import '../app_state.dart';
import '../models.dart';
import '../theme.dart';
import '../widgets.dart';
import 'playlists_screen.dart';

/// Bottom sheet of actions for a track (⋮ / long-press). [onChanged] is called
/// after any mutation so the caller can refresh its list. When [removeFromPlaylistId]
/// is set (the track is shown inside a playlist), a "Remove from playlist" action appears.
/// When [app] is provided, a Download / Re-download action appears.
Future<void> showTrackActions(
  BuildContext context, {
  required TrackFile track,
  required ApiClient api,
  required Future<void> Function() onChanged,
  int? removeFromPlaylistId,
  AppState? app,
}) {
  return showModalBottomSheet(
    context: context,
    backgroundColor: AppColors.surface,
    shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20))),
    builder: (sheetCtx) => _TrackActionsSheet(
        track: track, api: api, onChanged: onChanged, removeFromPlaylistId: removeFromPlaylistId, app: app),
  );
}

class _TrackActionsSheet extends StatefulWidget {
  final TrackFile track;
  final ApiClient api;
  final Future<void> Function() onChanged;
  final int? removeFromPlaylistId;
  final AppState? app;
  const _TrackActionsSheet(
      {required this.track, required this.api, required this.onChanged, this.removeFromPlaylistId, this.app});

  @override
  State<_TrackActionsSheet> createState() => _TrackActionsSheetState();
}

class _TrackActionsSheetState extends State<_TrackActionsSheet> {
  late int _rating = widget.track.rating ?? 0;

  @override
  Widget build(BuildContext context) {
    final t = widget.track;
    return SafeArea(
      // Scrollable: with the Download action the sheet can exceed a small screen.
      child: SingleChildScrollView(
        child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const SizedBox(height: 10),
          Container(
              width: 40,
              height: 4,
              decoration: BoxDecoration(color: AppColors.line, borderRadius: BorderRadius.circular(2))),
          Padding(
            padding: const EdgeInsets.all(16),
            child: Row(
              children: [
                AlbumArt(url: widget.api.artUrl(t.path), size: 48),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(t.name, maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.trackTitle),
                      Text([t.subtitle, fmtTime(t.durationSec)].where((s) => s.isNotEmpty).join(' · '),
                          maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.sub),
                    ],
                  ),
                ),
              ],
            ),
          ),
          const Divider(height: 1, color: AppColors.line),
          _tile(Icons.play_arrow_rounded, 'Play now', AppColors.text, () async {
            await widget.api.play(t.path);
            if (mounted) Navigator.pop(context);
            await widget.onChanged();
          }),
          ListTile(
            leading: const Icon(Icons.star_outline_rounded, color: AppColors.text),
            title: Text('Rate', style: AppText.trackTitle.copyWith(fontWeight: FontWeight.w500)),
            trailing: StarRating(
              rating: _rating,
              size: 20,
              onSet: (v) async {
                setState(() => _rating = v);
                await widget.api.setRating(t.path, v);
                await widget.onChanged();
              },
            ),
          ),
          _tile(Icons.sell_outlined, 'Edit tags', AppColors.text, () async {
            final csv = await showTagEditor(context, initial: t.tags);
            if (csv != null) {
              await widget.api.setTags(t.path, csv);
              await widget.onChanged();
            }
            if (mounted) Navigator.pop(context);
          }),
          _tile(Icons.playlist_add, 'Add to playlist', AppColors.text, () async {
            await showAddToPlaylist(context, widget.api, path: t.path);
            if (mounted) Navigator.pop(context);
          }),
          if (widget.app != null)
            _tile(
                Icons.download_for_offline_outlined,
                widget.app!.offline.has(t.path) ? 'Re-download' : 'Download',
                AppColors.text, () {
              widget.app!.downloads.enqueueTrack(t);
              _toast(context, 'Added to downloads');
              Navigator.pop(context);
            }),
          if (widget.removeFromPlaylistId != null)
            _tile(Icons.playlist_remove, 'Remove from playlist', AppColors.text, () async {
              if (t.playlistEntryId != null) {
                await widget.api.removeFromPlaylist(widget.removeFromPlaylistId!, t.playlistEntryId!);
                await widget.onChanged();
              }
              if (mounted) Navigator.pop(context);
            }),
          _tile(Icons.drive_file_move_outline, 'Move to…', AppColors.text, () async {
            final dest = await showMovePicker(context, api: widget.api, movingLabel: t.name);
            if (dest != null) {
              final ok = await widget.api.moveFile(t.path, dest);
              await widget.onChanged();
              if (mounted) _toast(context, ok ? 'Moved' : 'Move failed');
            }
            if (mounted) Navigator.pop(context);
          }),
          _tile(Icons.delete_outline_rounded, 'Delete', AppColors.danger, () async {
            final yes = await showDeleteConfirm(context, name: t.name, isFolder: false);
            if (yes == true) {
              final ok = await widget.api.deleteFile(t.path);
              await widget.onChanged();
              if (mounted) _toast(context, ok ? 'Deleted' : 'Delete failed');
            }
            if (mounted) Navigator.pop(context);
          }),
          const SizedBox(height: 8),
        ],
        ),
      ),
    );
  }

  Widget _tile(IconData icon, String label, Color color, VoidCallback onTap) => ListTile(
        leading: Icon(icon, color: color),
        title: Text(label,
            style: AppText.trackTitle.copyWith(color: color, fontWeight: FontWeight.w500)),
        onTap: onTap,
      );
}

void _toast(BuildContext context, String msg) {
  ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(msg), duration: const Duration(seconds: 2)));
}

/// Permanent-delete confirmation. Returns true when the user confirms.
Future<bool?> showDeleteConfirm(BuildContext context, {required String name, required bool isFolder}) {
  final kind = isFolder ? 'folder' : 'track';
  return showDialog<bool>(
    context: context,
    builder: (ctx) => AlertDialog(
      backgroundColor: AppColors.surface,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(18)),
      title: Column(
        children: [
          Container(
            padding: const EdgeInsets.all(10),
            decoration: BoxDecoration(color: AppColors.dangerTint, borderRadius: BorderRadius.circular(12)),
            child: const Icon(Icons.delete_outline_rounded, color: AppColors.danger),
          ),
          const SizedBox(height: 14),
          Text('Delete $kind?', style: AppText.screenTitle.copyWith(fontSize: 18)),
        ],
      ),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(name, textAlign: TextAlign.center, style: AppText.sub.copyWith(color: AppColors.text)),
          const SizedBox(height: 14),
          Container(
            padding: const EdgeInsets.all(12),
            decoration: BoxDecoration(color: AppColors.dangerTint, borderRadius: BorderRadius.circular(10)),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const Icon(Icons.warning_amber_rounded, size: 18, color: AppColors.danger),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    'This is permanent — the $kind is deleted from disk. There is no recycle bin.',
                    style: AppText.sub.copyWith(color: AppColors.danger, fontSize: 12),
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
      actionsAlignment: MainAxisAlignment.spaceBetween,
      actions: [
        OutlinedButton(
            onPressed: () => Navigator.pop(ctx, false),
            style: OutlinedButton.styleFrom(side: const BorderSide(color: AppColors.line)),
            child: Text('Cancel', style: TextStyle(color: AppColors.text))),
        FilledButton.icon(
          onPressed: () => Navigator.pop(ctx, true),
          style: FilledButton.styleFrom(backgroundColor: AppColors.danger),
          icon: const Icon(Icons.delete_outline_rounded, size: 18),
          label: const Text('Delete'),
        ),
      ],
    ),
  );
}

/// Tag editor bottom sheet. Returns the new comma-separated tags, or null on cancel.
Future<String?> showTagEditor(BuildContext context, {required String initial}) {
  return showModalBottomSheet<String>(
    context: context,
    isScrollControlled: true,
    backgroundColor: AppColors.surface,
    shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(20))),
    builder: (ctx) => Padding(
      padding: EdgeInsets.only(bottom: MediaQuery.of(ctx).viewInsets.bottom),
      child: _TagEditor(initial: initial),
    ),
  );
}

class _TagEditor extends StatefulWidget {
  final String initial;
  const _TagEditor({required this.initial});
  @override
  State<_TagEditor> createState() => _TagEditorState();
}

class _TagEditorState extends State<_TagEditor> {
  late final List<String> _tags = widget.initial
      .split(',')
      .map((t) => t.trim())
      .where((t) => t.isNotEmpty)
      .toList();
  final _ctrl = TextEditingController();
  static const _suggestions = ['rock', 'live', 'fav', 'chill', 'disco'];

  void _add(String raw) {
    final t = raw.trim().replaceAll('#', '');
    if (t.isEmpty || _tags.contains(t)) return;
    setState(() => _tags.add(t));
    _ctrl.clear();
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
            sectionLabel('Edit tags'),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                for (final t in _tags)
                  TagChip(label: t, onDeleted: () => setState(() => _tags.remove(t))),
              ],
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _ctrl,
              onSubmitted: _add,
              decoration: InputDecoration(
                hintText: 'add tag',
                filled: true,
                fillColor: AppColors.bg,
                border: OutlineInputBorder(
                    borderRadius: BorderRadius.circular(10), borderSide: BorderSide.none),
                suffixIcon: IconButton(icon: const Icon(Icons.add), onPressed: () => _add(_ctrl.text)),
              ),
            ),
            const SizedBox(height: 10),
            Wrap(
              spacing: 8,
              children: [
                for (final s in _suggestions.where((s) => !_tags.contains(s)))
                  TagChip(label: '+ #$s', ghost: true, onTap: () => _add(s)),
              ],
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
                    onPressed: () => Navigator.pop(context, _tags.join(',')),
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

/// Folder picker for move operations. Returns the chosen destination path, or null.
Future<String?> showMovePicker(BuildContext context, {required ApiClient api, required String movingLabel}) {
  return Navigator.of(context).push<String>(
    MaterialPageRoute(builder: (_) => _MovePicker(api: api, movingLabel: movingLabel), fullscreenDialog: true),
  );
}

class _MovePicker extends StatefulWidget {
  final ApiClient api;
  final String movingLabel;
  const _MovePicker({required this.api, required this.movingLabel});
  @override
  State<_MovePicker> createState() => _MovePickerState();
}

class _MovePickerState extends State<_MovePicker> {
  String _path = '';
  BrowseResult? _data;
  bool _loading = true;

  @override
  void initState() {
    super.initState();
    _go('');
  }

  Future<void> _go(String path) async {
    setState(() => _loading = true);
    try {
      final data = await widget.api.browse(path);
      setState(() {
        _data = data;
        _path = data.path;
        _loading = false;
      });
    } catch (_) {
      setState(() => _loading = false);
    }
  }

  void _up() {
    if (_path.isEmpty) return;
    final p = _path.replaceAll(RegExp(r'[\\/]+$'), '');
    final cut = p.lastIndexOf(RegExp(r'[\\/]'));
    _go(cut > 1 ? p.substring(0, cut) : '');
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
        title: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Row(children: [
              const Icon(Icons.drive_file_move_outline, size: 16, color: AppColors.muted),
              const SizedBox(width: 6),
              Text('MOVE TO…', style: AppText.sectionLabel),
            ]),
            Text(widget.movingLabel,
                maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.trackTitle.copyWith(fontSize: 14)),
          ],
        ),
        actions: [IconButton(onPressed: () => Navigator.pop(context), icon: const Icon(Icons.close))],
      ),
      body: Column(
        children: [
          Container(
            padding: const EdgeInsets.fromLTRB(12, 4, 12, 10),
            child: Row(
              children: [
                OutlinedButton.icon(
                  onPressed: _path.isEmpty ? null : _up,
                  style: OutlinedButton.styleFrom(
                      side: const BorderSide(color: AppColors.line), visualDensity: VisualDensity.compact),
                  icon: const Icon(Icons.arrow_upward, size: 16),
                  label: const Text('Up'),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(_path.isEmpty ? 'This PC (drives)' : _path,
                      maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.mono),
                ),
              ],
            ),
          ),
          const Divider(height: 1, color: AppColors.line),
          Expanded(
            child: _loading
                ? const SkeletonList()
                : (_data == null || _data!.folders.isEmpty)
                    ? const EmptyState(icon: Icons.folder_open, title: 'No subfolders')
                    : ListView(
                        children: [
                          for (final f in _data!.folders)
                            FolderRow(name: f.name.isEmpty ? f.path : f.name, onTap: () => _go(f.path)),
                        ],
                      ),
          ),
          SafeArea(
            child: Padding(
              padding: const EdgeInsets.all(12),
              child: Row(
                children: [
                  Expanded(
                    child: OutlinedButton(
                        onPressed: () => Navigator.pop(context),
                        style: OutlinedButton.styleFrom(
                            side: const BorderSide(color: AppColors.line),
                            padding: const EdgeInsets.symmetric(vertical: 14)),
                        child: Text('Cancel', style: TextStyle(color: AppColors.text))),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: FilledButton.icon(
                      onPressed: _path.isEmpty ? null : () => Navigator.pop(context, _path),
                      style: FilledButton.styleFrom(
                          backgroundColor: AppColors.accent, padding: const EdgeInsets.symmetric(vertical: 14)),
                      icon: const Icon(Icons.check, size: 18),
                      label: const Text('Move here'),
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
