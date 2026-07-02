import 'package:flutter/material.dart';
import '../app_state.dart';
import '../models.dart';
import '../theme.dart';
import '../widgets.dart';
import 'track_actions.dart';

/// Now-Playing UI. Used as a phone tab and as the tablet right-hand panel.
class NowPlayingView extends StatefulWidget {
  final AppState app;
  final VoidCallback? onCollapse; // shows a down-chevron on phone
  const NowPlayingView({super.key, required this.app, this.onCollapse});

  @override
  State<NowPlayingView> createState() => _NowPlayingViewState();
}

class _NowPlayingViewState extends State<NowPlayingView> {
  double? _seekDrag; // non-null while dragging the seek bar
  double? _volDrag;

  AppState get app => widget.app;

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: app,
      builder: (context, _) {
        final s = app.status;
        final np = s.nowPlaying;
        return Column(
          children: [
            _header(context),
            Expanded(
              child: np == null
                  ? const EmptyState(
                      icon: Icons.music_note,
                      title: 'Nothing playing',
                      subtitle: 'Open a folder in Library and tap a track to play.')
                  : Padding(
                      padding: const EdgeInsets.fromLTRB(20, 4, 20, 12),
                      child: Column(
                        children: [
                          // Art flexes to fill leftover space; the controls below stay fixed
                          // and always on-screen (no scrolling), matching the design.
                          Flexible(
                            child: Padding(
                              padding: const EdgeInsets.symmetric(vertical: 10),
                              child: _art(np),
                            ),
                          ),
                          ..._meta(np, s),
                        ],
                      ),
                    ),
            ),
          ],
        );
      },
    );
  }

  Widget _header(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(8, 8, 8, 0),
      child: Row(
        children: [
          if (widget.onCollapse != null)
            IconButton(onPressed: widget.onCollapse, icon: const Icon(Icons.keyboard_arrow_down))
          else
            const SizedBox(width: 8),
          Expanded(child: Center(child: Text('NOW PLAYING', style: AppText.sectionLabel))),
          IconButton(
            onPressed: () async {
              final np = app.status.nowPlaying;
              if (np == null || app.api == null) return;
              final t = app.trackByPath(np.path);
              if (t != null) {
                await showTrackActions(context, track: t, api: app.api!, onChanged: app.loadFiles);
              }
            },
            icon: const Icon(Icons.more_vert),
          ),
        ],
      ),
    );
  }

  Widget _art(NowPlaying np) {
    return Center(
      child: AspectRatio(
        aspectRatio: 1,
        child: AlbumArt(url: app.api!.artUrl(np.path), big: true, radius: 16, expand: true),
      ),
    );
  }

  List<Widget> _meta(NowPlaying np, PlayerStatus s) {
    final track = app.trackByPath(np.path);
    final rating = track?.rating ?? 0;
    final pos = _seekDrag ?? s.positionSec.toDouble();
    final dur = s.durationSec.toDouble();
    final vol = _volDrag ?? s.volume.toDouble();
    return [
      Text(np.name, maxLines: 2, overflow: TextOverflow.ellipsis, style: AppText.screenTitle),
      const SizedBox(height: 4),
      Text([np.artist, np.album].where((x) => x.isNotEmpty).join(' — '),
          maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.sub.copyWith(fontSize: 14)),
      const SizedBox(height: 14),
      Row(
        children: [
          StarRating(
            rating: rating,
            size: 20,
            onSet: (v) async {
              await app.api!.setRating(np.path, v);
              await app.loadFiles();
            },
          ),
          const Spacer(),
          for (final tg in (track?.tagList ?? const <String>[]))
            Padding(padding: const EdgeInsets.only(left: 6), child: TagChip(label: tg)),
          const SizedBox(width: 6),
          GestureDetector(
            onTap: () async {
              final csv = await showTagEditor(context, initial: track?.tags ?? '');
              if (csv != null) {
                await app.api!.setTags(np.path, csv);
                await app.loadFiles();
              }
            },
            child: Container(
              padding: const EdgeInsets.all(4),
              decoration: BoxDecoration(shape: BoxShape.circle, border: Border.all(color: AppColors.line)),
              child: const Icon(Icons.add, size: 16, color: AppColors.muted),
            ),
          ),
        ],
      ),
      const SizedBox(height: 10),
      SliderTheme(
        data: _sliderTheme(),
        child: Slider(
          value: dur > 0 ? pos.clamp(0, dur) : 0,
          max: dur > 0 ? dur : 1,
          onChanged: dur > 0 ? (v) => setState(() => _seekDrag = v) : null,
          onChangeEnd: (v) async {
            await app.api!.seek(v.round());
            setState(() => _seekDrag = null);
          },
        ),
      ),
      Row(
        mainAxisAlignment: MainAxisAlignment.spaceBetween,
        children: [
          Text(fmtTime(pos.round()), style: AppText.mono),
          Text(fmtTime(s.durationSec), style: AppText.mono),
        ],
      ),
      const SizedBox(height: 8),
      _transport(s),
      const SizedBox(height: 16),
      _toggles(s),
      const SizedBox(height: 18),
      Row(
        children: [
          const Icon(Icons.volume_up_rounded, size: 20, color: AppColors.muted),
          Expanded(
            child: SliderTheme(
              data: _sliderTheme(),
              child: Slider(
                value: vol.clamp(0, 100),
                max: 100,
                onChanged: (v) => setState(() => _volDrag = v),
                onChangeEnd: (v) async {
                  await app.api!.setVolume(v.round());
                  setState(() => _volDrag = null);
                },
              ),
            ),
          ),
          SizedBox(width: 30, child: Text('${vol.round()}', textAlign: TextAlign.right, style: AppText.mono)),
        ],
      ),
    ];
  }

  Widget _transport(PlayerStatus s) {
    Widget btn(IconData i, VoidCallback onTap, {double size = 30}) =>
        IconButton(onPressed: onTap, icon: Icon(i, size: size, color: AppColors.text));
    return Row(
      mainAxisAlignment: MainAxisAlignment.spaceEvenly,
      children: [
        btn(Icons.skip_previous_rounded, () => app.api!.playback('previous')),
        Container(
          width: 68,
          height: 68,
          decoration: const BoxDecoration(shape: BoxShape.circle, color: AppColors.accent),
          child: IconButton(
            onPressed: () => app.api!.playback('playpause'),
            icon: Icon(s.isPlaying ? Icons.pause : Icons.play_arrow, color: Colors.white, size: 34),
          ),
        ),
        btn(Icons.stop_rounded, () => app.api!.playback('stop')),
        btn(Icons.skip_next_rounded, () => app.api!.playback('next')),
      ],
    );
  }

  Widget _toggles(PlayerStatus s) {
    final repeatOn = s.repeat != 'off';
    return Row(
      children: [
        Expanded(
          child: _pill(
            icon: Icons.shuffle,
            label: 'Shuffle',
            active: s.shuffle,
            onTap: () => app.api!.playback('shuffle'),
          ),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: _pill(
            icon: s.repeat == 'one' ? Icons.repeat_one : Icons.repeat,
            label: s.repeat == 'one' ? 'Repeat one' : 'Repeat all',
            active: repeatOn,
            onTap: () => app.api!.playback('repeat'),
          ),
        ),
      ],
    );
  }

  Widget _pill({required IconData icon, required String label, required bool active, required VoidCallback onTap}) {
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(10),
      child: Container(
        padding: const EdgeInsets.symmetric(vertical: 12),
        decoration: BoxDecoration(
          color: active ? AppColors.accentTint : AppColors.bg,
          borderRadius: BorderRadius.circular(10),
          border: Border.all(color: active ? AppColors.accent.withValues(alpha: 0.4) : AppColors.line),
        ),
        child: Row(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(icon, size: 18, color: active ? AppColors.accent : AppColors.muted),
            const SizedBox(width: 8),
            Text(label,
                style: AppText.trackTitle.copyWith(
                    fontSize: 13, color: active ? AppColors.accent : AppColors.muted, fontWeight: FontWeight.w600)),
          ],
        ),
      ),
    );
  }

  SliderThemeData _sliderTheme() => SliderThemeData(
        activeTrackColor: AppColors.accent,
        inactiveTrackColor: AppColors.line,
        thumbColor: AppColors.accent,
        trackHeight: 4,
        overlayShape: const RoundSliderOverlayShape(overlayRadius: 16),
        thumbShape: const RoundSliderThumbShape(enabledThumbRadius: 7),
      );
}
