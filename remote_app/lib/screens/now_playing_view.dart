import 'dart:async';
import 'package:flutter/material.dart';
import '../api_client.dart';
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
  double? _sysVolDrag; // non-null while dragging the system-volume bar
  List<AudioDevice> _devices = [];
  bool _outputExpanded = false; // collapse device + system volume + quality by default

  AppState get app => widget.app;

  @override
  void initState() {
    super.initState();
    _loadDevices();
  }

  Future<void> _loadDevices() async {
    final api = app.api;
    if (api == null) return;
    try {
      final d = await api.devices();
      if (mounted) setState(() => _devices = d);
    } catch (_) {/* offline / not connected yet */}
  }

  String _deviceName(String? id) {
    if (app.localOutput) return 'This device';
    if (id == null || id.isEmpty) return 'System default';
    for (final d in _devices) {
      if (d.id == id) return d.name;
    }
    return 'Selected device';
  }

  Future<void> _pickDevice() async {
    final api = app.api;
    if (api == null) return;
    // Refresh the list so plugged/unplugged devices show up.
    try {
      final d = await api.devices();
      if (mounted) setState(() => _devices = d);
    } catch (_) {}
    if (!mounted) return;
    final current = app.localOutput ? kThisDevice : app.status.outputDeviceId;
    // "This device" (phone playback) sits at the top, above the desktop's devices.
    final options = [AudioDevice(id: kThisDevice, name: 'This device'), ..._devices];
    final chosen = await showModalBottomSheet<AudioDevice>(
      context: context,
      backgroundColor: AppColors.bg,
      builder: (c) => SafeArea(
        child: ListView(
          shrinkWrap: true,
          children: [
            for (final d in options)
              ListTile(
                leading: Icon(
                    d.id == current ? Icons.radio_button_checked : Icons.radio_button_unchecked,
                    color: d.id == current ? AppColors.accent : AppColors.muted),
                title: Text(d.name, style: AppText.trackTitle),
                onTap: () => Navigator.pop(c, d),
              ),
          ],
        ),
      ),
    );
    if (chosen == null) return;
    if (chosen.id == kThisDevice) {
      await app.enableLocalOutput();
    } else {
      await app.selectDesktopDevice(chosen.id);
    }
  }

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
              // Queue lookup gives rating/tags; fall back to a minimal TrackFile so the
              // menu (incl. Download) still works when the queue list is stale.
              final t = app.trackByPath(np.path) ??
                  TrackFile(
                      path: np.path,
                      title: np.title,
                      artist: np.artist,
                      album: np.album,
                      tags: '',
                      durationSec: np.durationSec,
                      rating: null,
                      isPlaying: true);
              await showTrackActions(context, track: t, api: app.api!, app: app, onChanged: app.loadFiles);
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
        child: _CyclingArt(api: app.api!, path: np.path, artCount: np.artCount),
      ),
    );
  }

  List<Widget> _meta(NowPlaying np, PlayerStatus s) {
    final track = app.trackByPath(np.path);
    final rating = track?.rating ?? 0;
    final pos = _seekDrag ?? s.positionSec.toDouble();
    final dur = s.durationSec.toDouble();
    final vol = _volDrag ?? (app.localOutput ? app.localVolume : s.volume.toDouble());
    final sysVol = _sysVolDrag ?? s.systemVolume.toDouble();
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
                  if (app.localOutput) {
                    await app.setLocalVolume(v);
                  } else {
                    await app.api!.setVolume(v.round());
                  }
                  setState(() => _volDrag = null);
                },
              ),
            ),
          ),
          SizedBox(width: 30, child: Text('${vol.round()}', textAlign: TextAlign.right, style: AppText.mono)),
        ],
      ),
      const SizedBox(height: 4),
      // Collapsible output section: device + system volume + stream quality.
      InkWell(
        onTap: () => setState(() => _outputExpanded = !_outputExpanded),
        borderRadius: BorderRadius.circular(10),
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Row(
            children: [
              const Icon(Icons.tune, size: 20, color: AppColors.muted),
              const SizedBox(width: 8),
              Text('Output', style: AppText.trackTitle.copyWith(fontSize: 14, color: AppColors.muted)),
              const SizedBox(width: 12),
              // Show the current device as a hint while collapsed.
              if (!_outputExpanded)
                Expanded(
                  child: Text(_deviceName(s.outputDeviceId),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      textAlign: TextAlign.right,
                      style: AppText.mono.copyWith(color: AppColors.muted)),
                )
              else
                const Spacer(),
              const SizedBox(width: 4),
              Icon(_outputExpanded ? Icons.expand_less : Icons.expand_more, color: AppColors.muted),
            ],
          ),
        ),
      ),
      if (_outputExpanded) ...[
        // System (Windows master) volume — desktop PC only; hidden while streaming to phone.
        if (!app.localOutput)
          Row(
            children: [
              const Icon(Icons.computer, size: 20, color: AppColors.muted),
              Expanded(
                child: SliderTheme(
                  data: _sliderTheme(),
                  child: Slider(
                    value: sysVol.clamp(0, 100),
                    max: 100,
                    onChanged: (v) => setState(() => _sysVolDrag = v),
                    onChangeEnd: (v) async {
                      await app.api!.setSystemVolume(v.round());
                      setState(() => _sysVolDrag = null);
                    },
                  ),
                ),
              ),
              SizedBox(width: 30, child: Text('${sysVol.round()}', textAlign: TextAlign.right, style: AppText.mono)),
            ],
          ),
        const SizedBox(height: 4),
        // Output device selector.
        InkWell(
          onTap: _pickDevice,
          borderRadius: BorderRadius.circular(10),
          child: Padding(
            padding: const EdgeInsets.symmetric(vertical: 6),
            child: Row(
              children: [
                const Icon(Icons.speaker_group_outlined, size: 20, color: AppColors.muted),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(_deviceName(s.outputDeviceId),
                      maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.mono),
                ),
                const Icon(Icons.arrow_drop_down, color: AppColors.muted),
              ],
            ),
          ),
        ),
        // Stream quality (only relevant when the phone is the sink).
        if (app.localOutput) ...[
          const SizedBox(height: 8),
          Row(
            children: [
              const Icon(Icons.high_quality_outlined, size: 20, color: AppColors.muted),
              const SizedBox(width: 8),
              for (final entry in kQualities.entries)
                Padding(
                  padding: const EdgeInsets.only(right: 8),
                  child: _qualityChip(entry.key, entry.value),
                ),
            ],
          ),
        ],
      ],
    ];
  }

  Widget _qualityChip(String label, int bitrate) {
    final active = app.streamBitrate == bitrate;
    return InkWell(
      onTap: () => app.setQuality(bitrate),
      borderRadius: BorderRadius.circular(8),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
        decoration: BoxDecoration(
          color: active ? AppColors.accentTint : AppColors.bg,
          borderRadius: BorderRadius.circular(8),
          border: Border.all(color: active ? AppColors.accent : AppColors.line),
        ),
        child: Text(label,
            style: AppText.trackTitle.copyWith(
                fontSize: 13,
                color: active ? AppColors.accent : AppColors.muted,
                fontWeight: FontWeight.w600)),
      ),
    );
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

/// Big now-playing art. Cycles through every candidate image (one every 5s) when the
/// track's folder has more than one; static otherwise.
class _CyclingArt extends StatefulWidget {
  final ApiClient api;
  final String path;
  final int artCount;
  const _CyclingArt({required this.api, required this.path, required this.artCount});

  @override
  State<_CyclingArt> createState() => _CyclingArtState();
}

class _CyclingArtState extends State<_CyclingArt> {
  int _index = 0;
  Timer? _timer;

  @override
  void initState() {
    super.initState();
    _arm();
  }

  @override
  void didUpdateWidget(covariant _CyclingArt oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.path != widget.path) {
      _index = 0;
      _arm();
    }
  }

  void _arm() {
    _timer?.cancel();
    _timer = widget.artCount > 1
        ? Timer.periodic(const Duration(seconds: 5),
            (_) => setState(() => _index = (_index + 1) % widget.artCount))
        : null;
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AlbumArt(url: widget.api.artUrl(widget.path, _index), big: true, radius: 16, expand: true);
  }
}
