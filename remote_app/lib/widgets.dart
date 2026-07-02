import 'package:flutter/material.dart';
import 'api_client.dart';
import 'app_state.dart';
import 'models.dart';
import 'theme.dart';

Widget sectionLabel(String text) => Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Text(text.toUpperCase(), style: AppText.sectionLabel),
    );

/// Album cover from the API, with a graceful "no album art" placeholder.
class AlbumArt extends StatelessWidget {
  final String? url;
  final double size;
  final bool big;
  final double radius;
  final bool expand; // fill the parent box instead of using [size]
  const AlbumArt(
      {super.key, required this.url, this.size = 44, this.big = false, this.radius = 10, this.expand = false});

  @override
  Widget build(BuildContext context) {
    final ph = _placeholder();
    if (url == null || url!.isEmpty) return ph;
    final w = expand ? double.infinity : size;
    return ClipRRect(
      borderRadius: BorderRadius.circular(radius),
      child: Image.network(
        url!,
        width: w,
        height: w,
        fit: BoxFit.cover,
        gaplessPlayback: true,
        errorBuilder: (_, __, ___) => ph,
        loadingBuilder: (_, child, progress) => progress == null ? child : ph,
      ),
    );
  }

  Widget _placeholder() {
    final iconSize = big ? 64.0 : size * 0.5;
    return Container(
      width: expand ? double.infinity : size,
      height: expand ? double.infinity : size,
      decoration: BoxDecoration(
        color: const Color(0xFFECEFF3),
        borderRadius: BorderRadius.circular(radius),
      ),
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Icon(Icons.music_note, size: iconSize, color: AppColors.muted.withValues(alpha: 0.55)),
          if (big) ...[
            const SizedBox(height: 6),
            Text('no album art', style: AppText.sub.copyWith(fontSize: 12)),
          ],
        ],
      ),
    );
  }
}

/// Tappable 1–5 star control. Tap the current highest star again to clear.
class StarRating extends StatelessWidget {
  final int rating; // 0..5
  final double size;
  final ValueChanged<int>? onSet; // null => read only
  const StarRating({super.key, required this.rating, this.size = 16, this.onSet});

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: List.generate(5, (i) {
        final filled = i < rating;
        final star = Icon(
          filled ? Icons.star_rounded : Icons.star_outline_rounded,
          size: size,
          color: filled ? AppColors.accent : AppColors.line,
        );
        if (onSet == null) return Padding(padding: const EdgeInsets.only(right: 1), child: star);
        return GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTap: () => onSet!(rating == i + 1 ? 0 : i + 1),
          child: Padding(padding: const EdgeInsets.symmetric(horizontal: 1), child: star),
        );
      }),
    );
  }
}

class TagChip extends StatelessWidget {
  final String label;
  final VoidCallback? onDeleted;
  final VoidCallback? onTap;
  final bool ghost;
  const TagChip({super.key, required this.label, this.onDeleted, this.onTap, this.ghost = false});

  @override
  Widget build(BuildContext context) {
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(20),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
        decoration: BoxDecoration(
          color: ghost ? Colors.transparent : AppColors.accentTint,
          border: ghost ? Border.all(color: AppColors.line) : null,
          borderRadius: BorderRadius.circular(20),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(label.startsWith('#') ? label : '#$label',
                style: AppText.sub.copyWith(
                    color: ghost ? AppColors.muted : AppColors.accent, fontWeight: FontWeight.w600, fontSize: 12)),
            if (onDeleted != null) ...[
              const SizedBox(width: 4),
              GestureDetector(
                  onTap: onDeleted,
                  child: Icon(Icons.close, size: 13, color: AppColors.accent.withValues(alpha: 0.8))),
            ],
          ],
        ),
      ),
    );
  }
}

/// A track row for the Files list. Highlights the now-playing track.
class TrackRow extends StatelessWidget {
  final TrackFile track;
  final ApiClient api;
  final VoidCallback onTap;
  final VoidCallback onMenu;
  final bool? playing; // live override; falls back to track.isPlaying
  const TrackRow(
      {super.key,
      required this.track,
      required this.api,
      required this.onTap,
      required this.onMenu,
      this.playing});

  @override
  Widget build(BuildContext context) {
    final playing = this.playing ?? track.isPlaying;
    final firstTag = track.tagList.isNotEmpty ? track.tagList.first : null;
    return InkWell(
      onTap: onTap,
      child: Container(
        decoration: BoxDecoration(
          color: playing ? AppColors.accentTint.withValues(alpha: 0.5) : null,
          border: Border(
            left: BorderSide(color: playing ? AppColors.accent : Colors.transparent, width: 3),
            bottom: const BorderSide(color: AppColors.line),
          ),
        ),
        padding: const EdgeInsets.fromLTRB(12, 10, 8, 10),
        child: Row(
          children: [
            playing
                ? Container(
                    width: 44,
                    height: 44,
                    decoration: BoxDecoration(
                        color: AppColors.accentTint, borderRadius: BorderRadius.circular(10)),
                    child: const Icon(Icons.graphic_eq, color: AppColors.accent, size: 22),
                  )
                : AlbumArt(url: api.artUrl(track.path)),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(track.name,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: AppText.trackTitle.copyWith(color: playing ? AppColors.accent : AppColors.text)),
                  if (track.subtitle.isNotEmpty)
                    Text(track.subtitle,
                        maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.sub),
                  const SizedBox(height: 4),
                  Row(children: [
                    StarRating(rating: track.rating ?? 0, size: 14),
                    if (firstTag != null) ...[
                      const SizedBox(width: 8),
                      TagChip(label: firstTag),
                    ],
                  ]),
                ],
              ),
            ),
            const SizedBox(width: 8),
            Column(
              crossAxisAlignment: CrossAxisAlignment.end,
              children: [
                Text(fmtTime(track.durationSec), style: AppText.mono),
                const SizedBox(height: 4),
                InkResponse(
                  onTap: onMenu,
                  radius: 20,
                  child: const Icon(Icons.more_vert, size: 20, color: AppColors.muted),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class FolderRow extends StatelessWidget {
  final String name;
  final VoidCallback onTap;
  final VoidCallback? onMenu;
  const FolderRow({super.key, required this.name, required this.onTap, this.onMenu});

  @override
  Widget build(BuildContext context) {
    return InkWell(
      onTap: onTap,
      child: Container(
        decoration: const BoxDecoration(border: Border(bottom: BorderSide(color: AppColors.line))),
        padding: const EdgeInsets.fromLTRB(12, 14, 8, 14),
        child: Row(
          children: [
            const Icon(Icons.folder_rounded, color: AppColors.accent, size: 26),
            const SizedBox(width: 12),
            Expanded(
              child: Text(name,
                  maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.trackTitle),
            ),
            if (onMenu != null)
              InkResponse(
                  onTap: onMenu,
                  radius: 20,
                  child: const Icon(Icons.more_vert, size: 20, color: AppColors.muted)),
            const SizedBox(width: 4),
            const Icon(Icons.chevron_right, color: AppColors.muted),
          ],
        ),
      ),
    );
  }
}

/// Compact player bar shown above the bottom nav on phone.
class MiniPlayer extends StatelessWidget {
  final PlayerStatus status;
  final ApiClient api;
  final VoidCallback onTap;
  final VoidCallback onToggle;
  const MiniPlayer(
      {super.key, required this.status, required this.api, required this.onTap, required this.onToggle});

  @override
  Widget build(BuildContext context) {
    final np = status.nowPlaying;
    if (np == null) return const SizedBox.shrink();
    final progress = status.durationSec > 0 ? status.positionSec / status.durationSec : 0.0;
    return Material(
      color: AppColors.surface,
      child: InkWell(
        onTap: onTap,
        child: Container(
          decoration: const BoxDecoration(border: Border(top: BorderSide(color: AppColors.line))),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              LinearProgressIndicator(
                value: progress.clamp(0.0, 1.0),
                minHeight: 2,
                backgroundColor: AppColors.line,
                color: AppColors.accent,
              ),
              Padding(
                padding: const EdgeInsets.fromLTRB(10, 8, 10, 8),
                child: Row(
                  children: [
                    AlbumArt(url: api.artUrl(np.path), size: 38, radius: 8),
                    const SizedBox(width: 10),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Text(np.name,
                              maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.trackTitle),
                          if (np.artist.isNotEmpty)
                            Text(np.artist,
                                maxLines: 1, overflow: TextOverflow.ellipsis, style: AppText.sub),
                        ],
                      ),
                    ),
                    IconButton(
                      onPressed: onToggle,
                      icon: Icon(status.isPlaying ? Icons.pause_circle_filled : Icons.play_circle_fill,
                          color: AppColors.accent, size: 34),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Inline banner reflecting the connection state (connecting / lost).
class ConnectionBanner extends StatelessWidget {
  final AppState app;
  const ConnectionBanner({super.key, required this.app});

  @override
  Widget build(BuildContext context) {
    if (app.conn == ConnState.connecting) {
      return _bar(AppColors.accentTint, AppColors.accent, 'Connecting…', app.api?.label ?? '', null);
    }
    if (app.conn == ConnState.lost) {
      return _bar(AppColors.dangerTint, AppColors.danger, 'Connection lost',
          "Can't reach ${app.api?.host ?? ''} · retrying…", app.retry);
    }
    return const SizedBox.shrink();
  }

  Widget _bar(Color bg, Color fg, String title, String sub, VoidCallback? onRetry) {
    return Container(
      width: double.infinity,
      color: bg,
      padding: const EdgeInsets.fromLTRB(14, 10, 10, 10),
      child: Row(
        children: [
          Icon(onRetry == null ? Icons.wifi_tethering : Icons.cloud_off, size: 18, color: fg),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(title, style: AppText.trackTitle.copyWith(color: fg, fontSize: 13)),
                if (sub.isNotEmpty) Text(sub, style: AppText.sub.copyWith(color: fg, fontSize: 11)),
              ],
            ),
          ),
          if (onRetry != null)
            FilledButton.icon(
              onPressed: onRetry,
              style: FilledButton.styleFrom(
                  backgroundColor: AppColors.danger,
                  padding: const EdgeInsets.symmetric(horizontal: 12),
                  visualDensity: VisualDensity.compact),
              icon: const Icon(Icons.refresh, size: 16),
              label: const Text('Retry'),
            ),
        ],
      ),
    );
  }
}

class EmptyState extends StatelessWidget {
  final IconData icon;
  final String title;
  final String? subtitle;
  final Widget? action;
  const EmptyState({super.key, required this.icon, required this.title, this.subtitle, this.action});

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              padding: const EdgeInsets.all(18),
              decoration: BoxDecoration(
                  color: const Color(0xFFECEFF3), borderRadius: BorderRadius.circular(20)),
              child: Icon(icon, size: 40, color: AppColors.muted),
            ),
            const SizedBox(height: 18),
            Text(title, textAlign: TextAlign.center, style: AppText.screenTitle.copyWith(fontSize: 18)),
            if (subtitle != null) ...[
              const SizedBox(height: 8),
              Text(subtitle!, textAlign: TextAlign.center, style: AppText.sub),
            ],
            if (action != null) ...[const SizedBox(height: 18), action!],
          ],
        ),
      ),
    );
  }
}

/// Static (non-animated) skeleton placeholder for list loads.
class SkeletonList extends StatelessWidget {
  const SkeletonList({super.key});

  @override
  Widget build(BuildContext context) {
    Widget bar(double w) => Container(
        height: 12,
        width: w,
        decoration: BoxDecoration(color: AppColors.line, borderRadius: BorderRadius.circular(6)));
    return ListView.builder(
      itemCount: 6,
      itemBuilder: (_, __) => Padding(
        padding: const EdgeInsets.fromLTRB(12, 12, 12, 0),
        child: Row(
          children: [
            Container(
                width: 44,
                height: 44,
                decoration: BoxDecoration(color: AppColors.line, borderRadius: BorderRadius.circular(10))),
            const SizedBox(width: 12),
            Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [bar(160), const SizedBox(height: 8), bar(100)]),
          ],
        ),
      ),
    );
  }
}

/// Card container matching the mockups (white, rounded, subtle border).
class Panel extends StatelessWidget {
  final Widget child;
  final EdgeInsetsGeometry? padding;
  const Panel({super.key, required this.child, this.padding});
  @override
  Widget build(BuildContext context) => Container(
        padding: padding,
        decoration: BoxDecoration(
          color: AppColors.surface,
          borderRadius: BorderRadius.circular(14),
          border: Border.all(color: AppColors.line),
        ),
        child: child,
      );
}
