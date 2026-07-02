import 'dart:io' show Platform;
import 'package:flutter/services.dart';

/// Pushes the current host:port to a paired Apple Watch (iOS only) via a native
/// WatchConnectivity bridge. No-op on Android / if the channel isn't wired.
class WatchLink {
  static const _channel = MethodChannel('music_organiser/watch');

  static Future<void> sendHost(String host, int port) async {
    if (!Platform.isIOS) return;
    try {
      await _channel.invokeMethod('sendHost', {'host': host, 'port': port});
    } catch (_) {
      // Watch not paired / channel absent — ignore.
    }
  }
}
