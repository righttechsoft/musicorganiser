import 'package:flutter/material.dart';
import 'app_state.dart';
import 'screens/connect_screen.dart';
import 'screens/home_shell.dart';
import 'theme.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final app = AppState();
  await app.loadPrefs();
  await app.initOffline();
  // Try to reconnect to the most recent desktop silently on launch.
  final r = app.mostRecent;
  if (r != null) {
    // ignore: unawaited_futures
    app.autoConnect(r.host, r.port);
  }
  runApp(RemoteApp(app: app));
}

class RemoteApp extends StatelessWidget {
  final AppState app;
  const RemoteApp({super.key, required this.app});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Music Organiser Remote',
      debugShowCheckedModeBanner: false,
      theme: buildTheme(),
      home: ListenableBuilder(
        listenable: app,
        builder: (context, _) =>
            app.inHome ? HomeShell(app: app) : ConnectScreen(app: app),
      ),
    );
  }
}
