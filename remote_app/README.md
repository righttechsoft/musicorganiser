# Music Organiser Remote

Flutter companion app for the **Music Organiser** Windows desktop player. It controls the
desktop over the local network (and streams / downloads music to the phone) via the
desktop's built-in HTTP control API on port **8787**.

## Features

- **Remote control** — Now Playing, transport (play/pause/stop/next/prev/seek), shuffle &
  repeat, in-app + Windows system volume, output-device picker.
- **Library** — browse the desktop's folders/drives with search and 6-way sort
  (Name / Created / Modified, asc & desc). Playlists and History too. Edit ratings and tags.
- **Stream to phone** — pick **"This device"** as the output to play the desktop's audio on
  the phone. The desktop transcodes to AAC on the fly (Low / Med / High = 96 / 160 / 192 kbps)
  and goes silent; the phone follows the desktop's position.
- **Offline mode** — download a track, folder or playlist (from its ⋮ menu) to the phone.
  Downloads mirror the desktop folder structure and are transcoded to AAC. Browse and play
  them with **no connection** to the desktop (also reachable via "Browse offline" on the
  connect screen).

## Running

```bash
flutter pub get
flutter run
```

On first launch, enter the desktop's LAN IP and port `8787`. From an **Android emulator**,
the host machine is `10.0.2.2`, so connect to `10.0.2.2:8787`. The desktop app must be
running (its control API starts automatically).

## Architecture

Plain `ChangeNotifier` + `ListenableBuilder` (no Provider/Bloc). Single app-wide `AppState`.

| Area | Files |
|---|---|
| App state, `/status` poll, streaming player | `lib/app_state.dart` |
| HTTP API wrapper | `lib/api_client.dart` |
| JSON models | `lib/models.dart` |
| Screens (Now/Library/Playlists/History/Downloads, connect) | `lib/screens/` |
| Shared widgets, theme, sort helpers | `lib/widgets.dart`, `lib/theme.dart`, `lib/sort_utils.dart` |
| Offline: store / download queue / local player | `lib/offline/` |

- **`OfflineStore`** — JSON manifest at `<app-documents>/downloads/manifest.json` + the
  mirrored file tree; path sanitization and deletion.
- **`DownloadService`** — serial download queue (`.part` → rename), progress / cancel / retry.
- **`OfflinePlayer`** — its own `just_audio` player with local transport (next/prev/shuffle/
  repeat/auto-advance); mutually exclusive with "This device" streaming.

## Release (iOS TestFlight)

CI in `.github/workflows/ios-testflight.yml` builds and uploads on a `v*` tag
(`git tag v1.6.0 && git push --tags`) or manual dispatch. See
`../docs/flutter-ios-testflight-github-actions.md` for the full setup + gotchas.
