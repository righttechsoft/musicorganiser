import 'package:flutter/material.dart';

/// Light palette locked to the delivered design spec.
class AppColors {
  static const bg = Color(0xFFF5F6F8);
  static const surface = Color(0xFFFFFFFF);
  static const text = Color(0xFF191C20);
  static const muted = Color(0xFF61666F);
  static const line = Color(0xFFE9EBEF);
  static const accent = Color(0xFF2F8FE0);
  static const accentTint = Color(0xFFE9F2FB);
  static const success = Color(0xFF1C9257);
  static const danger = Color(0xFFD64541);
  static const dangerTint = Color(0xFFFDECEA);
}

ThemeData buildTheme() {
  final base = ThemeData(
    useMaterial3: true,
    brightness: Brightness.light,
    scaffoldBackgroundColor: AppColors.bg,
    fontFamily: 'Manrope', // bundled; see pubspec fonts
    colorScheme: ColorScheme.fromSeed(
      seedColor: AppColors.accent,
      brightness: Brightness.light,
    ).copyWith(
      primary: AppColors.accent,
      surface: AppColors.surface,
      error: AppColors.danger,
    ),
    dividerColor: AppColors.line,
    splashFactory: InkRipple.splashFactory,
  );
  return base.copyWith(
    textTheme: base.textTheme.apply(bodyColor: AppColors.text, displayColor: AppColors.text),
  );
}

/// Named text styles from the spec.
class AppText {
  static const TextStyle screenTitle =
      TextStyle(fontFamily: 'Manrope', fontSize: 22, fontWeight: FontWeight.w800, color: AppColors.text);
  static const TextStyle trackTitle =
      TextStyle(fontFamily: 'Manrope', fontSize: 15, fontWeight: FontWeight.w700, color: AppColors.text);
  static const TextStyle sub =
      TextStyle(fontFamily: 'Manrope', fontSize: 13, fontWeight: FontWeight.w500, color: AppColors.muted);
  static const TextStyle sectionLabel = TextStyle(
      fontFamily: 'Manrope', fontSize: 11, fontWeight: FontWeight.w800, letterSpacing: 0.66, color: AppColors.muted);
  static const TextStyle mono = TextStyle(
      fontFamily: 'RobotoMono',
      fontSize: 12,
      color: AppColors.muted,
      fontFeatures: [FontFeature.tabularFigures()]);
}

String fmtTime(int seconds) {
  if (seconds < 0) seconds = 0;
  final m = seconds ~/ 60;
  final s = seconds % 60;
  return '$m:${s.toString().padLeft(2, '0')}';
}
