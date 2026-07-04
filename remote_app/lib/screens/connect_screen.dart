import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import '../app_state.dart';
import '../theme.dart';
import '../widgets.dart';
import 'downloads_screen.dart';

class ConnectScreen extends StatefulWidget {
  final AppState app;
  const ConnectScreen({super.key, required this.app});

  @override
  State<ConnectScreen> createState() => _ConnectScreenState();
}

class _ConnectScreenState extends State<ConnectScreen> {
  late final TextEditingController _host;
  late final TextEditingController _port;

  AppState get app => widget.app;

  @override
  void initState() {
    super.initState();
    final r = app.mostRecent;
    _host = TextEditingController(text: r?.host ?? '');
    _port = TextEditingController(text: (r?.port ?? 8787).toString());
  }

  @override
  void dispose() {
    _host.dispose();
    _port.dispose();
    super.dispose();
  }

  Future<void> _connect(String host, int port) async {
    _host.text = host;
    _port.text = port.toString();
    final ok = await app.connect(host.trim(), port);
    if (!ok && mounted) {
      ScaffoldMessenger.of(context)
          .showSnackBar(const SnackBar(content: Text('Could not reach the desktop.')));
    }
  }

  String _ago(int ms) {
    if (ms == 0) return '';
    final d = DateTime.now().difference(DateTime.fromMillisecondsSinceEpoch(ms));
    if (d.inMinutes < 1) return 'just now';
    if (d.inMinutes < 60) return '${d.inMinutes}m ago';
    if (d.inHours < 24) return '${d.inHours}h ago';
    return '${d.inDays}d ago';
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: AppColors.bg,
      body: SafeArea(
        child: ListenableBuilder(
          listenable: app,
          builder: (context, _) {
            final connecting = app.conn == ConnState.connecting;
            return Center(
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 420),
                child: ListView(
                  padding: const EdgeInsets.all(24),
                  shrinkWrap: true,
                  children: [
                    const SizedBox(height: 24),
                    Center(
                      child: Container(
                        padding: const EdgeInsets.all(16),
                        decoration: BoxDecoration(
                            color: AppColors.accentTint, borderRadius: BorderRadius.circular(20)),
                        child: const Icon(Icons.library_music_rounded, size: 40, color: AppColors.accent),
                      ),
                    ),
                    const SizedBox(height: 16),
                    Text('Music Organiser\nRemote',
                        textAlign: TextAlign.center,
                        style: AppText.screenTitle.copyWith(fontSize: 26, height: 1.15)),
                    const SizedBox(height: 6),
                    Text('Control your desktop player over Wi-Fi',
                        textAlign: TextAlign.center, style: AppText.sub),
                    const SizedBox(height: 28),
                    sectionLabel('Host'),
                    _field(_host, Icons.dns_outlined, '192.168.0.10', TextInputType.text),
                    const SizedBox(height: 16),
                    sectionLabel('Port'),
                    _field(_port, Icons.tag, '8787', TextInputType.number,
                        formatters: [FilteringTextInputFormatter.digitsOnly]),
                    if (app.recents.isNotEmpty) ...[
                      const SizedBox(height: 20),
                      sectionLabel('Recent'),
                      for (final r in app.recents)
                        Padding(
                          padding: const EdgeInsets.only(bottom: 8),
                          child: Material(
                            // ListTile paints ink on the nearest Material; give it one so the
                            // Panel's coloured DecoratedBox doesn't swallow the splash (asserts otherwise).
                            color: AppColors.surface,
                            borderRadius: BorderRadius.circular(14),
                            child: ListTile(
                              shape: RoundedRectangleBorder(
                                borderRadius: BorderRadius.circular(14),
                                side: const BorderSide(color: AppColors.line),
                              ),
                              leading: const Icon(Icons.history, color: AppColors.muted),
                              title: Text(r.key, style: AppText.trackTitle.copyWith(fontSize: 14)),
                              subtitle: Text('Last connected · ${_ago(r.lastSeenMs)}', style: AppText.sub),
                              trailing: const Icon(Icons.north_east, size: 18, color: AppColors.muted),
                              onTap: connecting ? null : () => _connect(r.host, r.port),
                            ),
                          ),
                        ),
                    ],
                    const SizedBox(height: 28),
                    FilledButton(
                      onPressed: connecting
                          ? null
                          : () {
                              final port = int.tryParse(_port.text.trim()) ?? 8787;
                              if (_host.text.trim().isEmpty) return;
                              _connect(_host.text.trim(), port);
                            },
                      style: FilledButton.styleFrom(
                          backgroundColor: AppColors.accent,
                          padding: const EdgeInsets.symmetric(vertical: 16)),
                      child: connecting
                          ? const SizedBox(
                              height: 20,
                              width: 20,
                              child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
                          : const Row(
                              mainAxisAlignment: MainAxisAlignment.center,
                              children: [Text('Connect'), SizedBox(width: 8), Icon(Icons.arrow_forward, size: 18)],
                            ),
                    ),
                    const SizedBox(height: 12),
                    OutlinedButton.icon(
                      onPressed: () => Navigator.of(context).push(MaterialPageRoute(
                          builder: (_) => DownloadsScreen(app: app, standalone: true))),
                      style: OutlinedButton.styleFrom(
                          side: const BorderSide(color: AppColors.line),
                          padding: const EdgeInsets.symmetric(vertical: 14)),
                      icon: const Icon(Icons.download_for_offline_outlined, size: 18),
                      label: const Text('Browse offline'),
                    ),
                    const SizedBox(height: 12),
                    if (connecting)
                      Center(child: Text('Connecting to ${app.api?.label ?? ''}…', style: AppText.sub)),
                  ],
                ),
              ),
            );
          },
        ),
      ),
    );
  }

  Widget _field(TextEditingController c, IconData icon, String hint, TextInputType type,
      {List<TextInputFormatter>? formatters}) {
    return TextField(
      controller: c,
      keyboardType: type,
      inputFormatters: formatters,
      style: AppText.mono.copyWith(color: AppColors.text, fontSize: 15),
      decoration: InputDecoration(
        prefixIcon: Icon(icon, color: AppColors.muted, size: 20),
        hintText: hint,
        filled: true,
        fillColor: AppColors.surface,
        enabledBorder: OutlineInputBorder(
            borderRadius: BorderRadius.circular(12), borderSide: const BorderSide(color: AppColors.line)),
        focusedBorder: OutlineInputBorder(
            borderRadius: BorderRadius.circular(12), borderSide: const BorderSide(color: AppColors.accent, width: 1.5)),
      ),
    );
  }
}
