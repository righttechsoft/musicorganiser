import 'package:flutter/material.dart';
import '../app_state.dart';
import '../theme.dart';
import '../widgets.dart';
import 'downloads_screen.dart';
import 'history_screen.dart';
import 'library_screen.dart';
import 'now_playing_view.dart';
import 'playlists_screen.dart';

/// Responsive home. Phone = bottom nav + mini-player; tablet (wide) = nav rail +
/// list pane + persistent Now-Playing panel.
class HomeShell extends StatefulWidget {
  final AppState app;
  const HomeShell({super.key, required this.app});

  @override
  State<HomeShell> createState() => _HomeShellState();
}

class _HomeShellState extends State<HomeShell> {
  int _tab = 1; // 0 Now, 1 Library, 2 Playlists, 3 History
  AppState get app => widget.app;

  @override
  void initState() {
    super.initState();
    app.addListener(_onApp);
  }

  @override
  void dispose() {
    app.removeListener(_onApp);
    super.dispose();
  }

  // Jump to Now Playing when a play action fires it.
  void _onApp() {
    if (app.consumeWantNow() && mounted && _tab != 0) setState(() => _tab = 0);
  }

  void _go(int i) => setState(() => _tab = i);

  Widget _library() => LibraryScreen(app: app);
  Widget _playlists() => PlaylistsScreen(app: app);
  Widget _history() => HistoryScreen(app: app, onGoLibrary: () => _go(1));
  Widget _downloads() => DownloadsScreen(app: app);

  Widget _listPane() {
    switch (_tab) {
      case 2:
        return _playlists();
      case 3:
        return _history();
      case 4:
        return _downloads();
      default:
        return _library();
    }
  }

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: app,
      builder: (context, _) {
        return LayoutBuilder(builder: (context, constraints) {
          final wide = constraints.maxWidth >= 900;
          return Scaffold(
            backgroundColor: AppColors.bg,
            body: SafeArea(
              child: Column(
                children: [
                  ConnectionBanner(app: app),
                  Expanded(child: wide ? _tablet() : _phone()),
                ],
              ),
            ),
            bottomNavigationBar: wide ? null : _bottomNav(),
          );
        });
      },
    );
  }

  // ---------- phone ----------
  Widget _phone() {
    final showMini = app.status.nowPlaying != null && _tab != 0;
    return Column(
      children: [
        Expanded(
          child: IndexedStack(
            index: _tab,
            children: [
              NowPlayingView(app: app, onCollapse: () => _go(1)),
              _library(),
              _playlists(),
              _history(),
              _downloads(),
            ],
          ),
        ),
        if (showMini)
          MiniPlayer(
            status: app.status,
            api: app.api!,
            onTap: () => _go(0),
            onToggle: () => app.api!.playback('playpause'),
          ),
      ],
    );
  }

  Widget _bottomNav() {
    return NavigationBarTheme(
      data: NavigationBarThemeData(
        backgroundColor: AppColors.surface,
        indicatorColor: AppColors.accentTint,
        labelTextStyle: WidgetStateProperty.all(AppText.sub.copyWith(fontSize: 11)),
        iconTheme: WidgetStateProperty.resolveWith((states) => IconThemeData(
            color: states.contains(WidgetState.selected) ? AppColors.accent : AppColors.muted)),
      ),
      child: NavigationBar(
        height: 64,
        selectedIndex: _tab,
        onDestinationSelected: _go,
        destinations: const [
          NavigationDestination(icon: Icon(Icons.play_circle_outline), label: 'Now'),
          NavigationDestination(icon: Icon(Icons.folder_outlined), label: 'Library'),
          NavigationDestination(icon: Icon(Icons.queue_music), label: 'Playlists'),
          NavigationDestination(icon: Icon(Icons.history), label: 'History'),
          NavigationDestination(icon: Icon(Icons.download_for_offline_outlined), label: 'Offline'),
        ],
      ),
    );
  }

  // ---------- tablet ----------
  Widget _tablet() {
    return Row(
      children: [
        _rail(),
        const VerticalDivider(width: 1, color: AppColors.line),
        Expanded(flex: 3, child: Container(color: AppColors.surface, child: _listPane())),
        const VerticalDivider(width: 1, color: AppColors.line),
        SizedBox(
          width: 400,
          child: Container(color: AppColors.surface, child: NowPlayingView(app: app)),
        ),
      ],
    );
  }

  Widget _rail() {
    final railIndex = (_tab <= 1) ? 0 : _tab - 1; // Library / Playlists / History
    return NavigationRail(
      backgroundColor: AppColors.surface,
      selectedIndex: railIndex,
      onDestinationSelected: (i) => _go(i + 1),
      labelType: NavigationRailLabelType.all,
      selectedIconTheme: const IconThemeData(color: AppColors.accent),
      unselectedIconTheme: const IconThemeData(color: AppColors.muted),
      selectedLabelTextStyle: AppText.sub.copyWith(color: AppColors.accent, fontSize: 11),
      unselectedLabelTextStyle: AppText.sub.copyWith(fontSize: 11),
      leading: Padding(
        padding: const EdgeInsets.symmetric(vertical: 16),
        child: Container(
          padding: const EdgeInsets.all(8),
          decoration: BoxDecoration(color: AppColors.accentTint, borderRadius: BorderRadius.circular(12)),
          child: const Icon(Icons.library_music_rounded, color: AppColors.accent),
        ),
      ),
      trailing: Expanded(
        child: Align(
          alignment: Alignment.bottomCenter,
          child: Padding(
            padding: const EdgeInsets.only(bottom: 16),
            child: IconButton(
              tooltip: 'Disconnect',
              onPressed: app.disconnect,
              icon: const Icon(Icons.settings_outlined, color: AppColors.muted),
            ),
          ),
        ),
      ),
      destinations: const [
        NavigationRailDestination(icon: Icon(Icons.folder_outlined), label: Text('Library')),
        NavigationRailDestination(icon: Icon(Icons.queue_music), label: Text('Playlists')),
        NavigationRailDestination(icon: Icon(Icons.history), label: Text('History')),
        NavigationRailDestination(icon: Icon(Icons.download_for_offline_outlined), label: Text('Offline')),
      ],
    );
  }
}
