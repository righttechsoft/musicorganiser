import 'package:flutter/material.dart';
import '../app_state.dart';
import '../models.dart';
import '../theme.dart';
import '../widgets.dart';

class HistoryScreen extends StatefulWidget {
  final AppState app;
  final VoidCallback onGoLibrary;
  const HistoryScreen({super.key, required this.app, required this.onGoLibrary});

  @override
  State<HistoryScreen> createState() => _HistoryScreenState();
}

class _HistoryScreenState extends State<HistoryScreen> {
  List<HistoryItem>? _items;
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
      final items = await app.api!.history();
      if (mounted) setState(() { _items = items; _loading = false; });
    } catch (_) {
      if (mounted) setState(() => _loading = false);
    }
  }

  void _open(HistoryItem it) {
    // Navigate the Library screen into this folder (shows its tracks inline).
    app.openInLibrary(it.path);
    widget.onGoLibrary();
  }

  @override
  Widget build(BuildContext context) {
    final items = _items ?? const <HistoryItem>[];
    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 10, 8, 6),
          child: Row(
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text('History', style: AppText.screenTitle),
                    Text('Recently played folders · tap to load', style: AppText.sub),
                  ],
                ),
              ),
              IconButton(onPressed: _load, icon: const Icon(Icons.refresh, color: AppColors.muted)),
            ],
          ),
        ),
        Expanded(
          child: _loading
              ? const SkeletonList()
              : items.isEmpty
                  ? const EmptyState(icon: Icons.history, title: 'No history yet')
                  : RefreshIndicator(
                      onRefresh: _load,
                      color: AppColors.accent,
                      child: ListView.builder(
                        itemCount: items.length,
                        itemBuilder: (_, i) {
                          final it = items[i];
                          return InkWell(
                            onTap: () => _open(it),
                            child: Container(
                              decoration: const BoxDecoration(
                                  border: Border(bottom: BorderSide(color: AppColors.line))),
                              padding: const EdgeInsets.fromLTRB(16, 14, 12, 14),
                              child: Row(
                                children: [
                                  Container(
                                    padding: const EdgeInsets.all(8),
                                    decoration: BoxDecoration(
                                        color: AppColors.bg, borderRadius: BorderRadius.circular(10)),
                                    child: const Icon(Icons.history, size: 20, color: AppColors.muted),
                                  ),
                                  const SizedBox(width: 12),
                                  Expanded(
                                    child: Column(
                                      crossAxisAlignment: CrossAxisAlignment.start,
                                      children: [
                                        Text(it.displayName.isEmpty ? it.path : it.displayName,
                                            maxLines: 1,
                                            overflow: TextOverflow.ellipsis,
                                            style: AppText.trackTitle),
                                        Text(it.path,
                                            maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.mono),
                                      ],
                                    ),
                                  ),
                                  IconButton(
                                    onPressed: () => _open(it),
                                    icon: const Icon(Icons.play_circle_outline, color: AppColors.accent),
                                  ),
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
