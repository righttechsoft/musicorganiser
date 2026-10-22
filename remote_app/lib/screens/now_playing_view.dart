import 'dart:async';
import 'dart:math' as math;
import 'dart:ui' as ui;
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
  bool _showLyrics = false; // karaoke mode: swaps the art region for the lyrics panel

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
                              child: _showLyrics ? _lyricsPanel(np) : _art(np),
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

  Widget _lyricsPanel(NowPlaying np) {
    return Container(
      width: double.infinity,
      decoration: BoxDecoration(
        color: AppColors.surface,
        borderRadius: BorderRadius.circular(16),
        border: Border.all(color: AppColors.line),
      ),
      child: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(14, 8, 4, 4),
            child: Row(
              children: [
                Expanded(child: Text('LYRICS', style: AppText.sectionLabel)),
                // Wipes the cached lyrics so the user can re-fetch or paste fresh — mirrors the
                // desktop's Clear button, which is only shown once there's something to clear.
                if (app.lyrics?.hasAny ?? false)
                  IconButton(
                    iconSize: 18,
                    tooltip: 'Clear lyrics',
                    icon: const Icon(Icons.delete_outline, color: AppColors.muted),
                    onPressed: () => app.clearLyrics(np.path),
                  ),
                IconButton(
                  iconSize: 18,
                  tooltip: 'Edit lyrics',
                  icon: const Icon(Icons.edit_outlined, color: AppColors.muted),
                  onPressed: () => _editLyrics(np.path),
                ),
              ],
            ),
          ),
          const Divider(height: 1, color: AppColors.line),
          Expanded(child: _KaraokeView(app: app, path: np.path)),
        ],
      ),
    );
  }

  // Plain-lyrics text editor, mirroring showTagEditor's bottom-sheet shape.
  // ponytail: edits plain lyrics only — raw LRC line-timing edit stays desktop-only.
  Future<void> _editLyrics(String path) async {
    final text = await showModalBottomSheet<String>(
      context: context,
      isScrollControlled: true,
      backgroundColor: AppColors.surface,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(20))),
      builder: (ctx) => Padding(
        padding: EdgeInsets.only(bottom: MediaQuery.of(ctx).viewInsets.bottom),
        child: _LyricsEditor(initial: app.lyrics?.plain ?? ''),
      ),
    );
    if (text != null) await app.saveLyrics(path, plain: text);
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
          IconButton(
            onPressed: () => setState(() => _showLyrics = !_showLyrics),
            tooltip: _showLyrics ? 'Show album art' : 'Show lyrics',
            visualDensity: VisualDensity.compact,
            padding: EdgeInsets.zero,
            constraints: const BoxConstraints(minWidth: 32, minHeight: 32),
            icon: Icon(Icons.mic_none_rounded,
                size: 22,
                color: _showLyrics
                    ? AppColors.accent
                    : (np.hasLyrics ? AppColors.text : AppColors.muted)),
          ),
          const SizedBox(width: 4),
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
      if (app.waveform != null && dur > 0)
        _WaveSeek(
          style: app.waveformStyle,
          wave: app.waveform!,
          position: pos.clamp(0, dur),
          duration: dur,
          onChanged: (v) => setState(() => _seekDrag = v),
          onChangeEnd: (v) async {
            await app.api!.seek(v.round());
            setState(() => _seekDrag = null);
          },
        )
      else
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
          if (app.waveform != null && dur > 0)
            DropdownButton<String>(
              value: _waveStyles.contains(app.waveformStyle) ? app.waveformStyle : 'Bars',
              isDense: true,
              underline: const SizedBox.shrink(),
              iconSize: 16,
              style: const TextStyle(fontSize: 11, color: AppColors.muted),
              items: [for (final n in _waveStyles) DropdownMenuItem(value: n, child: Text(n))],
              onChanged: (v) {
                if (v != null) app.setWaveformStyle(v);
              },
            ),
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
          child: shuffleMenu(
            enabled: s.shuffle,
            onLabel: 'Shuffle (least played first)',
            onSelected: (v) => app.api!.setShuffle(v),
            child: _pill(
              icon: Icons.shuffle,
              label: 'Shuffle',
              active: s.shuffle,
              dropdown: true,
            ),
          ),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: repeatMenu(
            mode: s.repeat,
            onSelected: (v) => app.api!.setRepeat(v),
            child: _pill(
              icon: s.repeat == 'one' ? Icons.repeat_one : Icons.repeat,
              label: s.repeat == 'one' ? 'Repeat one' : 'Repeat all',
              active: repeatOn,
              dropdown: true,
            ),
          ),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: _pill(
            icon: Icons.stop_circle_outlined,
            label: 'Stop after',
            active: s.stopAfterCurrent,
            onTap: () => app.api!.playback('stop-after-current'),
          ),
        ),
      ],
    );
  }

  Widget _pill({required IconData icon, required String label, required bool active, VoidCallback? onTap, bool dropdown = false}) {
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
            if (dropdown) Icon(Icons.arrow_drop_down, size: 18, color: active ? AppColors.accent : AppColors.muted),
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
        ? Timer.periodic(const Duration(seconds: 15), (_) => _advance())
        : null;
  }

  /// Preload the next candidate before swapping so the crossfade shows the image, not a placeholder.
  Future<void> _advance() async {
    final next = (_index + 1) % widget.artCount;
    try {
      await precacheImage(NetworkImage(widget.api.artUrl(widget.path, next)), context);
    } catch (_) {
      // fall through: the Image's errorBuilder handles a bad candidate
    }
    if (mounted) setState(() => _index = next);
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AnimatedSwitcher(
      duration: const Duration(milliseconds: 1200),
      child: AlbumArt(
        key: ValueKey(_index),
        url: widget.api.artUrl(widget.path, _index),
        big: true,
        radius: 16,
        expand: true,
      ),
    );
  }
}

/// Karaoke lyrics view: highlights + auto-scrolls the current line from
/// `app.status.positionSec`. Falls back to plain, unhighlighted text when only
/// `plain` lyrics are cached (no synced .lrc yet), and to a "Find lyrics" prompt
/// when nothing is cached.
class _KaraokeView extends StatefulWidget {
  final AppState app;
  final String path;
  const _KaraokeView({required this.app, required this.path});

  @override
  State<_KaraokeView> createState() => _KaraokeViewState();
}

class _KaraokeViewState extends State<_KaraokeView> {
  final _scroll = ScrollController();
  int _lastScrolledIndex = -1;

  @override
  void didUpdateWidget(covariant _KaraokeView oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.path != widget.path) _lastScrolledIndex = -1;
  }

  // Rough average row height — exact heights vary with wrapped lines, but this is
  // close enough to keep the active line comfortably in view.
  void _scrollToActive(int index) {
    if (index < 0 || index == _lastScrolledIndex || !_scroll.hasClients) return;
    _lastScrolledIndex = index;
    final target = (index * 40.0 - 120).clamp(0.0, _scroll.position.maxScrollExtent);
    _scroll.animateTo(target, duration: const Duration(milliseconds: 300), curve: Curves.easeOut);
  }

  @override
  void dispose() {
    _scroll.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final app = widget.app;
    if (app.loadingLyrics) {
      return const Center(child: CircularProgressIndicator(color: AppColors.accent));
    }
    final data = app.lyrics;
    if (data == null || !data.hasAny) return _findPrompt();

    final lines = data.lines;
    if (lines.isEmpty) {
      // Plain lyrics only — no sync yet, nothing to highlight/scroll.
      return SingleChildScrollView(
        padding: const EdgeInsets.all(16),
        child: Text(data.plain ?? '', style: AppText.trackTitle.copyWith(height: 1.6)),
      );
    }

    final active = app.activeLyricLineIndex;
    WidgetsBinding.instance.addPostFrameCallback((_) => _scrollToActive(active));
    return ListView.builder(
      controller: _scroll,
      padding: const EdgeInsets.symmetric(vertical: 60, horizontal: 20),
      itemCount: lines.length,
      itemBuilder: (context, i) {
        final isActive = i == active;
        return Padding(
          padding: const EdgeInsets.symmetric(vertical: 6),
          child: Text(
            lines[i].text,
            textAlign: TextAlign.center,
            style: isActive
                ? AppText.screenTitle.copyWith(fontSize: 18, color: AppColors.accent)
                : AppText.trackTitle.copyWith(color: AppColors.muted),
          ),
        );
      },
    );
  }

  Widget _findPrompt() {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const Icon(Icons.lyrics_outlined, size: 40, color: AppColors.muted),
          const SizedBox(height: 10),
          Text('No lyrics yet', style: AppText.sub),
          const SizedBox(height: 14),
          FilledButton.icon(
            onPressed: () => widget.app.findLyrics(widget.path),
            style: FilledButton.styleFrom(backgroundColor: AppColors.accent),
            icon: const Icon(Icons.search, size: 18),
            label: const Text('Find lyrics'),
          ),
        ],
      ),
    );
  }
}

/// Bottom-sheet plain-lyrics editor. Returns the edited text, or null on cancel.
class _LyricsEditor extends StatefulWidget {
  final String initial;
  const _LyricsEditor({required this.initial});
  @override
  State<_LyricsEditor> createState() => _LyricsEditorState();
}

class _LyricsEditorState extends State<_LyricsEditor> {
  late final _ctrl = TextEditingController(text: widget.initial);

  @override
  void dispose() {
    _ctrl.dispose();
    super.dispose();
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
            sectionLabel('Edit lyrics'),
            TextField(
              controller: _ctrl,
              maxLines: 10,
              minLines: 6,
              style: AppText.trackTitle.copyWith(fontWeight: FontWeight.w500),
              decoration: InputDecoration(
                hintText: 'Paste or type lyrics…',
                filled: true,
                fillColor: AppColors.bg,
                border: OutlineInputBorder(
                    borderRadius: BorderRadius.circular(10), borderSide: BorderSide.none),
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
                    onPressed: () => Navigator.pop(context, _ctrl.text),
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

/// Waveform seek bar: tap or drag to seek; [onChangeEnd] fires on release (same contract as the Slider).
class _WaveSeek extends StatefulWidget {
  final WaveformData wave;
  final double position;
  final double duration;
  final ValueChanged<double> onChanged;
  final ValueChanged<double> onChangeEnd;
  final String style;
  const _WaveSeek({
    required this.style,
    required this.wave,
    required this.position,
    required this.duration,
    required this.onChanged,
    required this.onChangeEnd,
  });

  @override
  State<_WaveSeek> createState() => _WaveSeekState();
}

class _WaveSeekState extends State<_WaveSeek> {
  double? _last; // last value computed from the pointer; committed on release regardless of rebuilds
  WaveformData? _cacheWave; // per-bar colours, rebuilt only when the data or bar count changes
  int _cacheBars = 0;
  List<Color>? _played, _unplayed;
  final _ChartCache _chart = _ChartCache();

  @override
  void dispose() {
    _chart.dispose();
    super.dispose();
  }

  static const _cLow = Color(0xFFFF2D95), _cMid = Color(0xFF7C4DFF), _cHigh = Color(0xFF00C8FF);

  void _colours(int bars) {
    final w = widget.wave;
    if (identical(w, _cacheWave) && bars == _cacheBars) return;
    _cacheWave = w;
    _cacheBars = bars;
    if (!w.hasBands || bars <= 0) {
      _played = _unplayed = null;
      return;
    }
    final n = w.peaks.length;
    final played = <Color>[], unplayed = <Color>[];
    for (var i = 0; i < bars; i++) {
      final a = i * n ~/ bars;
      var b = (i + 1) * n ~/ bars;
      if (b <= a) b = a + 1;
      var l = 0, m = 0, h = 0;
      for (var k = a; k < b && k < n; k++) {
        if (w.low![k] > l) l = w.low![k];
        if (w.mid![k] > m) m = w.mid![k];
        if (w.high![k] > h) h = w.high![k];
      }
      var wl = l / 255 * l / 255, wm = m / 255 * m / 255, wh = h / 255 * h / 255;
      var sum = wl + wm + wh;
      if (sum < 1e-6) {
        wl = wm = wh = 1;
        sum = 3;
      }
      double ch(int s) =>
          (wl * ((_cLow.toARGB32() >> s) & 255) + wm * ((_cMid.toARGB32() >> s) & 255) + wh * ((_cHigh.toARGB32() >> s) & 255)) / sum;
      final c = Color.fromARGB(255, ch(16).round(), ch(8).round(), ch(0).round());
      played.add(c);
      unplayed.add(Color.lerp(c, AppColors.bg, 0.65)!);
    }
    _played = played;
    _unplayed = unplayed;
  }

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(builder: (context, c) {
      double at(double dx) => _last = (dx / c.maxWidth).clamp(0.0, 1.0) * widget.duration;
      _colours((c.maxWidth / _WavePainter.step).floor());
      return GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTapDown: (d) => widget.onChanged(at(d.localPosition.dx)),
        onTapUp: (d) => widget.onChangeEnd(at(d.localPosition.dx)),
        onHorizontalDragStart: (d) => widget.onChanged(at(d.localPosition.dx)),
        onHorizontalDragUpdate: (d) => widget.onChanged(at(d.localPosition.dx)),
        onHorizontalDragEnd: (_) => widget.onChangeEnd(_last ?? widget.position),
        onHorizontalDragCancel: () => widget.onChangeEnd(_last ?? widget.position),
        child: CustomPaint(
          size: Size(c.maxWidth, 48),
          painter: _WavePainter(widget.wave, widget.position, widget.duration, _played, _unplayed,
              widget.style, MediaQuery.devicePixelRatioOf(context), _chart),
        ),
      );
    });
  }
}

const _waveStyles = ['Bars', 'Ribbons', 'Strands', 'BeatBars', 'Spectrum'];

class _ChartCache {
  WaveformData? wave;
  double w = 0, h = 0, dpr = 0;
  String style = '';
  ui.Picture? full, wash;
  bool failed = false;

  void dispose() {
    full?.dispose();
    wash?.dispose();
    full = wash = null;
  }

  bool ensure(WaveformData wf, Size size, String st, double dpr) {
    if (!identical(wf, wave) || size.width != w || size.height != h || st != style || dpr != this.dpr) {
      dispose();
      wave = wf;
      w = size.width;
      h = size.height;
      style = st;
      this.dpr = dpr;
      failed = false;
      try {
        final rec = ui.PictureRecorder();
        final c = Canvas(rec);
        final px = 1 / dpr;
        switch (st) {
          case 'Ribbons':
            _buildRibbons(c, wf, w, h, px);
          case 'Strands':
            _buildStrands(c, wf, w, h, px);
          case 'BeatBars':
            _buildBeatBars(c, wf, w, h, px);
          default:
            _buildSpectrum(c, wf, w, h, px);
        }
        full = rec.endRecording();
        final rec2 = ui.PictureRecorder();
        final c2 = Canvas(rec2);
        c2.saveLayer(Rect.fromLTWH(0, 0, w, h), Paint()..color = Color.fromRGBO(0, 0, 0, 1 - _unplayedBlend));
        c2.drawPicture(full!);
        c2.restore();
        wash = rec2.endRecording();
      } catch (_) {
        dispose();
        failed = true;
      }
    }
    return !failed && full != null;
  }
}

const _unplayedBlend = 0.65;
const _fLow = Color(0xFFFF2D95), _fMid = Color(0xFF7C4DFF), _fHigh = Color(0xFF00C8FF);
const _rippleK = 840.0, _rippleMinPx = 4.0, _rippleMaxPx = 16.0, _rippleDepth = 0.25;
const _strandAmp = 0.9, _strandMinPx = 5.0, _strandShareGain = 2.0;
const _strandBassPx = 2.2, _strandMidPx = 1.4, _strandHighPx = 0.9;
const _beatPitchSlow = 9.0, _beatPitchFast = 3.0, _beatSlowBpm = 60.0, _beatFastBpm = 170.0;
const _specRows = 8;
const _tickWidthPx = 1.0, _tickHeightPx = 4.0;
const _tiltMid = 1.25, _tiltHigh = 1.6, _sharePower = 1.5, _shareAbsent = 0.08, _shareMin = 0.06;
const _specFloor = 0.2, _specGamma = 0.6;
const _defaultBpm = 90.0;

double _fin(double v) => v.isFinite ? v : 0;

void _fill(Canvas c, Paint p, double x, double y, double w, double h) {
  if (!(_fin(w) > 0) || !(_fin(h) > 0)) return;
  c.drawRect(Rect.fromLTWH(_fin(x), _fin(y), w, h), p);
}

Paint _solid(Color col, [double a = 1]) => Paint()..color = col.withValues(alpha: a);

double _sample(List<int> a, double x, double w) {
  final pos = x / w * a.length - 0.5;
  final i0 = pos.floor().clamp(0, a.length - 1);
  final i1 = math.min(i0 + 1, a.length - 1);
  final f = (pos - i0).clamp(0.0, 1.0);
  return a[i0] + (a[i1] - a[i0]) * f;
}

double _wavelength(double bpm) => (_rippleK / (bpm > 0 ? bpm : _defaultBpm)).clamp(_rippleMinPx, _rippleMaxPx);

Path _mirror(List<double> half, double step, double w, double mid) {
  final p = Path()..moveTo(0, mid - half[0]);
  for (var i = 1; i < half.length; i++) {
    p.lineTo(math.min(i * step, w), mid - half[i]);
  }
  for (var i = half.length - 1; i >= 0; i--) {
    p.lineTo(math.min(i * step, w), mid + half[i]);
  }
  return p..close();
}

(double, double, double) _shares(WaveformData wf, double x0, double x1, double w) {
  final xm = (x0 + x1) / 2;
  final rl = _sample(wf.low!, xm, w) / 255, rm = _sample(wf.mid!, xm, w) / 255, rh = _sample(wf.high!, xm, w) / 255;
  final nb = wf.peaks.length;
  final spec = wf.spectrum!;
  final b0 = (x0 / w * nb).floor().clamp(0, nb - 1);
  final b1 = (x1 / w * nb).ceil().clamp(b0 + 1, nb);
  final mx = List<int>.filled(_specRows, 0);
  for (var r = 0; r < _specRows; r++) {
    for (var k = b0; k < b1; k++) {
      mx[r] = math.max(mx[r], spec[r][k]);
    }
  }
  final a = [
    math.max(mx[0], mx[1]) / 255.0,
    math.max(math.max(mx[2], mx[3]), math.max(mx[4], mx[5])) / 255.0,
    math.max(mx[6], mx[7]) / 255.0,
  ];
  final rel = [rl * rl, rm * rm, rh * rh];
  const tilt = [1.0, _tiltMid, _tiltHigh];
  final v = List<double>.filled(3, 0);
  var sum = 0.0;
  for (var k = 0; k < 3; k++) {
    v[k] = a[k] < _shareAbsent ? 0 : math.pow(a[k] * tilt[k], _sharePower) * rel[k];
    sum += v[k];
  }
  if (sum < 1e-9) {
    for (var k = 0; k < 3; k++) {
      v[k] = a[k] < _shareAbsent ? 0 : math.pow(a[k] * tilt[k], _sharePower).toDouble();
      sum += v[k];
    }
    if (sum < 1e-9) return (1 / 3, 1 / 3, 1 / 3);
  }
  var t = 0.0;
  for (var k = 0; k < 3; k++) {
    v[k] /= sum;
    if (v[k] > 0 || a[k] >= _shareAbsent) v[k] = math.max(v[k], _shareMin);
    t += v[k];
  }
  return (v[0] / t, v[1] / t, v[2] / t);
}

void _buildRibbons(Canvas g, WaveformData wf, double w, double h, double px) {
  final mid = h / 2, usable = h - 2 * px, step = math.max(px, 1.0);
  final n = (w / step).ceil() + 1;
  final outer = List<double>.filled(n, 0), upper = List<double>.filled(n, 0), inner = List<double>.filled(n, 0);
  var phase = 0.0;
  for (var i = 0; i < n; i++) {
    final x = math.min(i * step, w);
    final hp = math.max(px, _sample(wf.peaks, x, w) / 255 * usable / 2);
    phase += 2 * math.pi * step / _wavelength(_sample(wf.tempo!, x, w));
    final d = _rippleDepth * _sample(wf.beat!, x, w) / 255;
    final e = hp * (1 - d * 0.5 * (1 - math.sin(phase)));
    final (lo, mi, _) = _shares(wf, x, x + step, w);
    outer[i] = e;
    upper[i] = e * (lo + mi);
    inner[i] = e * lo;
  }
  g.drawPath(_mirror(outer, step, w, mid), _solid(_fHigh));
  g.drawPath(_mirror(upper, step, w, mid), _solid(_fMid));
  g.drawPath(_mirror(inner, step, w, mid), _solid(_fLow));
}

void _buildStrands(Canvas g, WaveformData wf, double w, double h, double px) {
  final mid = h / 2, usable = h - 2 * px, step = math.max(px, 1.0) / 2;
  final n = (w / step).ceil() + 1;
  final env = List<double>.filled(n, 0);
  final pts = List.generate(3, (_) => List<Offset>.filled(n, Offset.zero));
  final on = List.generate(3, (_) => List<bool>.filled(n, false));
  final phase = List<double>.filled(3, 0);
  for (var i = 0; i < n; i++) {
    final x = math.min(i * step, w);
    final hp = math.max(px, _sample(wf.peaks, x, w) / 255 * usable / 2);
    env[i] = hp;
    final wl = _wavelength(_sample(wf.tempo!, x, w));
    final (sl, sm, sh) = _shares(wf, x, x + step, w);
    final share = [sl, sm, sh];
    final weight = [for (final s in share) math.min(1.0, s * _strandShareGain)];
    for (var k = 0; k < 3; k++) {
      phase[k] += 2 * math.pi * step / math.max(_strandMinPx, wl / (1 << k));
      pts[k][i] = Offset(x, mid + math.sin(phase[k]) * hp * _strandAmp * weight[k] * (0.5 + 0.5 * weight[k]));
      on[k][i] = share[k] > 0;
    }
  }
  g.drawPath(_mirror(env, step, w, mid), _solid(const Color(0xFF9098A4), 0.22));
  const col = [_fLow, _fMid, _fHigh];
  const th = [_strandBassPx, _strandMidPx, _strandHighPx];
  for (var k = 2; k >= 0; k--) {
    final path = Path();
    var open = false;
    for (var i = 0; i < n; i++) {
      if (!on[k][i]) {
        open = false;
        continue;
      }
      if (!open) {
        path.moveTo(pts[k][i].dx, pts[k][i].dy);
        open = true;
      } else {
        path.lineTo(pts[k][i].dx, pts[k][i].dy);
      }
    }
    g.drawPath(
        path,
        Paint()
          ..color = col[k]
          ..style = PaintingStyle.stroke
          ..strokeWidth = th[k]
          ..strokeJoin = StrokeJoin.round
          ..strokeCap = StrokeCap.round);
  }
}

void _buildBeatBars(Canvas g, WaveformData wf, double w, double h, double px) {
  final mid = h / 2, usable = h - 2 * px, gap = px;
  final bHi = _solid(_fHigh), bMi = _solid(_fMid), bLo = _solid(_fLow);
  final np = wf.peaks.length;
  var x = 0.0;
  while (x < w) {
    final bpm = _sample(wf.tempo!, x, w);
    final f = ((_beatFastBpm - bpm) / (_beatFastBpm - _beatSlowBpm)).clamp(0.0, 1.0);
    final pitch = bpm <= 0 ? 3 * px : _beatPitchFast + (_beatPitchSlow - _beatPitchFast) * f;
    final x1 = math.min(x + pitch, w);
    final from = (x / w * np).floor().clamp(0, np - 1);
    final to = (x1 / w * np).ceil().clamp(from + 1, np);
    var v = 0;
    for (var k = from; k < to; k++) {
      v = math.max(v, wf.peaks[k]);
    }
    final bh = math.max(2 * px, v / 255.0 * usable);
    final (_, mi, hi) = _shares(wf, x, x1, w);
    final bw = math.max(px, x1 - x - gap), top = mid - bh / 2, hHi = bh * hi, hMi = bh * mi;
    _fill(g, bHi, x, top, bw, hHi);
    _fill(g, bMi, x, top + hHi, bw, hMi);
    _fill(g, bLo, x, top + hHi + hMi, bw, bh - hHi - hMi);
    x += pitch;
  }
}

void _buildSpectrum(Canvas g, WaveformData wf, double w, double h, double px) {
  final mid = h / 2, usable = h - 2 * px, pitch = 3 * px, cellW = 2 * px;
  final rowCol = <Color>[];
  for (var r = 0; r < _specRows; r++) {
    final t = r / (_specRows - 1);
    rowCol.add(t < 0.5 ? Color.lerp(_fLow, _fMid, t * 2)! : Color.lerp(_fMid, _fHigh, (t - 0.5) * 2)!);
  }
  final bars = math.max(1, (w / pitch).floor());
  final nb = wf.peaks.length;
  final p = Paint();
  for (var i = 0; i < bars; i++) {
    final x = i * pitch, cx = x + cellW / 2;
    final hh = math.max(px, _sample(wf.peaks, cx, w) / 255 * usable / 2);
    final cellH = 2 * hh / _specRows;
    final bucket = (cx / w * nb).floor().clamp(0, nb - 1);
    for (var r = 0; r < _specRows; r++) {
      final q = wf.spectrum![r][bucket];
      if (q < 1) continue;
      p.color = rowCol[r].withValues(alpha: _specFloor + (1 - _specFloor) * math.pow(q / 255.0, _specGamma));
      _fill(g, p, x, mid + hh - (r + 1) * cellH, cellW, cellH);
    }
  }
  var phase = 0.0;
  for (var x = 0.0; x < w; x += px) {
    phase += px / _wavelength(_sample(wf.tempo!, x, w));
    if (phase < 1) continue;
    phase -= 1;
    final a = 0.8 * _sample(wf.beat!, x, w) / 255;
    if (a >= 0.03) _fill(g, _solid(const Color(0xFF404858), a), x, h - _tickHeightPx, _tickWidthPx * math.max(px, 1.0), _tickHeightPx);
  }
}

class _WavePainter extends CustomPainter {
  final WaveformData wave;
  List<int> get peaks => wave.peaks;
  final double position;
  final double duration;
  final List<Color>? played, unplayed; // per-bar colours when the desktop sent bands
  final String style;
  final double dpr;
  final _ChartCache cache;
  _WavePainter(this.wave, this.position, this.duration, this.played, this.unplayed, this.style, this.dpr, this.cache);

  static const step = 3.0; // logical px per bar
  static const _rest = Color(0xFFC9CDD4); // unplayed bars (single-colour mode)

  @override
  void paint(Canvas canvas, Size size) {
    if (style != 'Bars' && wave.hasFingerprint && size.width > 0 && size.height > 0 &&
        cache.ensure(wave, size, style, dpr)) {
      final px = size.width * (duration > 0 ? (position / duration).clamp(0.0, 1.0) : 0.0);
      canvas.save();
      canvas.clipRect(Rect.fromLTWH(0, 0, px, size.height));
      canvas.drawPicture(cache.full!);
      canvas.restore();
      canvas.save();
      canvas.clipRect(Rect.fromLTWH(px, 0, size.width - px, size.height));
      canvas.drawPicture(cache.wash!);
      canvas.restore();
      if (px > 0) {
        canvas.drawRect(Rect.fromLTWH(math.min(px, size.width - 1.5), 0, 1.5, size.height), Paint()..color = AppColors.accent);
      }
      return;
    }
    final bars = (size.width / step).floor();
    if (bars <= 0 || peaks.isEmpty) return;
    final mid = size.height / 2;
    final playedX = duration > 0 ? size.width * (position / duration).clamp(0.0, 1.0) : 0.0;
    final paint = Paint()..strokeWidth = 2;
    for (var i = 0; i < bars; i++) {
      final a = i * peaks.length ~/ bars;
      var b = (i + 1) * peaks.length ~/ bars;
      if (b <= a) b = a + 1;
      var m = 0;
      for (var k = a; k < b && k < peaks.length; k++) {
        if (peaks[k] > m) m = peaks[k];
      }
      final h = (m / 255 * size.height).clamp(2.0, size.height);
      final x = i * step + 1;
      final on = x <= playedX;
      paint.color = played != null && i < played!.length
          ? (on ? played![i] : unplayed![i])
          : (on ? AppColors.accent : _rest);
      canvas.drawLine(Offset(x, mid - h / 2), Offset(x, mid + h / 2), paint);
    }
    canvas.drawLine(Offset(playedX, 0), Offset(playedX, size.height), paint..color = AppColors.text);
  }

  @override
  bool shouldRepaint(_WavePainter o) =>
      !identical(o.wave, wave) ||
      o.style != style ||
      o.dpr != dpr ||
      o.position != position ||
      o.duration != duration ||
      !identical(o.played, played);
}
