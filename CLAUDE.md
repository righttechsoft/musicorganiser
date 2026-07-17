# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Music Organiser is a Windows desktop application for managing music files, built with C# WPF (.NET 8). It provides a file browser with filtering and sorting, music metadata display, audio playback (WASAPI) with seeking, output-device selection and volume control, and file operations (copy/move/delete).

The desktop also hosts a local HTTP control API (`ControlApiService`, port 8787) driven by a companion **Flutter mobile remote app** in `remote_app/` (see "Mobile remote app" below). The mobile app can control playback, stream audio to the phone ("This device" output), and download tracks for a **pure offline mode**.

## Build Commands

```bash
# Build the application
dotnet build

# Run the application
dotnet run --project MusicOrganiser

# Build release version
dotnet build -c Release

# Publish single-file executable
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Architecture

### Project Structure
```
MusicOrganiser/
├── Models/           # Data models (MusicFile, RecentFolders, ArtistInfoCache)
├── Services/         # Business logic services
├── ViewModels/       # MVVM view models
├── Converters/       # WPF value converters
├── MainWindow.xaml   # Main application window
└── App.xaml          # Application entry point
```

### Key Services

- **AudioPlayerService** (`Services/AudioPlayerService.cs`): NAudio-based audio playback. Pipeline `AudioFileReader → MediaFoundationResampler → VolumeSampleProvider → WasapiOut` on a selected **CoreAudio** render device. Exposes: in-app `Volume` / `LocalMuted` (both write the tail `VolumeSampleProvider._gain` — **not** `AudioFileReader.Volume`, which sits upstream of the resampler's ~1s source buffer and delays volume changes by 1–2s), Windows master **`SystemVolume`** (`MMDevice.AudioEndpointVolume`), output-device selection (`OutputDeviceId`, `GetOutputDevices()`), and `LocalMuted` (silences local output while the phone is the audio sink — see remote-sink streaming). Live-tracks device add/remove/default-change (`IMMNotificationClient` → `DevicesChanged`) and external master-volume changes (`OnVolumeNotification` → `SystemVolumeChanged`); a removed selected device falls back to default. Critical method `StopAndReleaseFile()` must be called before move/delete operations to release file handles.

- **AudioStreamService** (`Services/AudioStreamService.cs`): Transcodes tracks to **AAC (`.m4a`)** via `MediaFoundationEncoder` for the mobile app to stream/download. Cache keyed by `path + mtime + bitrate` under `%TEMP%\MusicOrganiser\stream` (cleared on `ControlApiService.Start`), per-key lock, `Prewarm()` fire-and-forget. Bitrates snapped to `{96, 128, 160, 192}k`. Reuses `AudioFileReader` so anything that plays transcodes.

- **ControlApiService** (`Services/ControlApiService.cs`): Local `HttpListener` HTTP API on port **8787** (binds all interfaces, no auth) for the Flutter remote. Fire-and-forget per request; all state access marshalled onto the UI thread (`OnUi`). Key routes: `GET /status`, `POST /playback/{playpause,stop,next,previous,shuffle,repeat,seek}`, `POST /volume` · `/system-volume`, `GET /devices` · `POST /device` (id `"__this_device__"` = remote-sink mode), `GET /browse` · `/files` · `/search?q=&limit=` (global library search over the SQLite cache) · `/playlist/files`, `GET /file/art?path=&i=` (a track with an embedded cover serves it for **any** `i`; otherwise candidate `i` indexes the folder's image files), `GET /file/audio?path=&bitrate=` (transcoded AAC, **Range/206**, Content-Length always set), `/playlist/*`, `/history`, file/folder rating/tags/move/delete. `/status`'s `nowPlaying` includes `artCount` (`1` when the track has embedded art, else the folder image count; cached per-path). Serves an embedded PWA on the fallback route. Started in the `MainViewModel` constructor.

- **FileOperationsService** (`Services/FileOperationsService.cs`): Handles copy/move/delete for files and folders. Automatically stops the audio player when operating on currently playing files.

- **MusicMetadataService** (`Services/MusicMetadataService.cs`): TagLib#-based metadata reader. Supported formats: MP3, FLAC, WAV, WMA, AAC, OGG, M4A. `EnumerateSupportedFiles()` returns music file paths (no tag reading) for the cache layer to diff. `GetAlbumArts(folderPath, currentFilePath?)` returns all album-art candidates in the winning priority tier (capped at 12) so the UI can cycle the cover: **the current track's own embedded pictures win outright**, else folder images / a folder track's embedded picture / subfolder images. `GetFolderImagePaths()` lists a folder's image files, preferred cover names first.

- **DatabaseService** (`Services/DatabaseService.cs`): Singleton (`DatabaseService.Instance`) SQLite cache using `Microsoft.Data.Sqlite` (raw ADO.NET, no ORM). DB at `%APPDATA%\MusicOrganiser\cache.db`, WAL mode, pooled connection per operation. Tables: `folders`, `files`, `tags`, `file_tags`, `folder_tags`. Stores file/folder attributes plus `rating` (1–5) and tags. All operations wrapped in try/catch so a cache failure never crashes the app. User ratings are preserved across rescans (rating excluded from the upsert UPDATE set).

- **LibraryCacheService** (`Services/LibraryCacheService.cs`): Singleton orchestrating cache-first serving + in-app background refresh. `GetCachedFiles()` (instant DB read), `RefreshFolderFilesAsync()` (diff filesystem by size/mtime, re-parse only changed files, upsert, return authoritative list). Background worker (`Channel` + single consumer) services `Enqueue()` to warm neighbouring folder caches off the UI thread.

- **ArtistInfoService** (`Services/ArtistInfoService.cs`): Uses Claude Haiku 4.5 API with web search to generate artist summaries. Features:
  - Web search integration (`web_search_20250305` tool) for real-time artist lookup
  - Agentic approach: uses artist name, song title, album, and filename as context
  - Persistent caching of confident results to `%APPDATA%\MusicOrganiser\artist_info_cache.json`
  - Confidence detection via `[CONFIDENT]`/`[UNCERTAIN]` markers in responses
  - Requires `ANTHROPIC_API_KEY` in `.env` file

### Key ViewModels

- **MainViewModel** (`ViewModels/MainViewModel.cs`): Central state management for the application, owns all services and coordinates between folder tree, music grid, and audio player. Exposes `Volume`, `SystemVolume`, `OutputDevices`/`SelectedOutputDevice` (record `Models/OutputDeviceItem`), and `RemoteSink` (phone is the audio sink). Subscribes to `AudioPlayerService` events and marshals device/volume changes to the UI thread.

- **FolderTreeViewModel** (`ViewModels/FolderTreeViewModel.cs`): Manages folder tree with lazy-loading expansion, filtering, and sorting via `FolderNode` class. `LoadChildrenAsync` batches cache-node adds into a single UI dispatch (cache pass + disk-diff pass) for near-instant rendering; `NavigateToPathAsync` expands the ancestor chain and selects the target.

### Important Patterns

1. **File Handle Management**: Before any move/delete operation, always call `AudioPlayerService.StopAndReleaseFile()` to prevent file-in-use errors. The `FileOperationsService` handles this automatically.

2. **Recent Folders**: Stored in `%APPDATA%\MusicOrganiser\recent_folders.json`. Separate lists for Copy and Move operations (10 items each).

3. **Artist Info Cache**: Stored in `%APPDATA%\MusicOrganiser\artist_info_cache.json`. Caches AI-generated artist summaries when confidence is high. Can be refreshed via the refresh button in the UI.

4. **Async Loading**: Folder tree and music file list use async loading to keep UI responsive. Folders load children on expansion, music files load when a folder is selected.

5. **Folder Filtering**: Filter text propagates down the tree via `FilterText` property. Visibility is determined by `IsVisible` which checks if name matches or any child is visible.

6. **Folder Sorting**: Sort option propagates down the tree via `SortOption` property. Supports Name (A-Z/Z-A), Creation Date, and Modified Date in both ascending and descending order.

7. **Right-Click Context Menus**: Use `MouseRightButtonDown` (bubbling event) with `e.Handled = true` to correctly identify the clicked item. Do not overwrite the clicked item reference in menu opened handlers.

8. **Cache-First Loading**: Folder tree and music grid read from the SQLite cache first (instant), then run a background refresh (`LibraryCacheService.RefreshFolderFilesAsync` / `FolderNode.LoadChildrenAsync`) that diffs the filesystem, upserts the DB, and reconciles the UI (add/remove/replace). Initialized in `MainViewModel` constructor; worker stopped in `Dispose`.

9. **Ratings & Tags**: 1–5 star rating and comma-separated tags for tracks (`MusicFile`, `INotifyPropertyChanged`) and folders (`FolderNode`). UI via the reusable `Controls/StarRating` control + an inline tags box; both reveal on row hover (`StarRating.EditMode` / visibility triggers bound to row `IsMouseOver`). Edits write through to `DatabaseService` (`SetFileRating`/`SetFileTags`, `SetFolderRating`/`SetFolderTags`). Folder edits self-create the DB row via `EnsureFolderRow`; `FolderNode` guards write-back during cache load with `ApplyCache`/`EnablePersist`.

10. **Output device & system volume**: The transport bar has a device `ComboBox` (bound to `OutputDevices`/`SelectedOutputDevice`), an in-app volume bar, and a Windows-master (`SystemVolume`) bar. The master bar reflects external volume changes live (CoreAudio `OnVolumeNotification`); the device list updates live on Bluetooth/USB connect/disconnect (`IMMNotificationClient`). The selected device id is persisted in `settings.json` (`OutputDeviceId`); the master volume is a live OS value and is not persisted.

11. **Remote-sink streaming**: When the mobile app selects "This device", `POST /device {id:"__this_device__"}` sets `MainViewModel.RemoteSink` → `AudioPlayerService.LocalMuted = true` (desktop plays silently but keeps the queue/position clock and auto-advance). The phone streams the current track's transcoded AAC (`/file/audio`, prewarmed for the current + next track) and follows `/status`. `/status.outputDeviceId` reports `"__this_device__"` while active.

12. **Player buttons**: Transport buttons use a flat circular `PlayerButtonStyle` (`MainWindow.xaml` resources); play/pause is a larger accent-filled `PlayerPlayButtonStyle`. Shuffle/repeat grey when off, black-on-blue-circle when on (via `DataTrigger` on `Background`; the template `TemplateBinding`s `Background` so the triggers apply).

13. **Album cover**: The cover follows the *playing track*, not the folder — `PlayFile` calls `MainViewModel.LoadAlbumCoverAsync(folder, filePath)`, which owns its own `CancellationTokenSource` so fast track switches cancel stale loads. Multi-candidate folders cycle every 15s and crossfade via `AlbumCoverGhost` (`AlbumCoverImage_TargetUpdated` in `MainWindow.xaml.cs`). `UpdateAlbumCoverPosition` shifts the cover below the file rows using a **`TranslateTransform`** — never a `Margin`: margin participates in layout, shrinking the `Stretch="Uniform"` image's available height, which grows the offset, which shrinks it further (a self-collapsing feedback loop).

## Mobile remote app (`remote_app/`)

A Flutter app (package `music_organiser_remote`) that controls the desktop over the LAN HTTP API. Ships to iOS TestFlight via `.github/workflows/ios-testflight.yml` (tag `v*`). Plain state via `ChangeNotifier` + `ListenableBuilder` — no Provider/Bloc.

- **`lib/app_state.dart`** — `AppState`: connection lifecycle, 1s `/status` poll, `ApiClient`, and the local `just_audio` player used for "This device" streaming (`_syncLocal` slaves the phone to `/status`: load on track change, mirror play/pause, resync on drift, retry failed loads). Owns the offline sub-services.
- **`lib/api_client.dart`** — thin `http` wrapper over the desktop API; `streamUrl(path,bitrate)` / `artUrl(path,[i])` (art candidate index, omitted from the URL when `0`).
- **Screens** (`lib/screens/`): Now Playing, Library (browse + search + 6-option sort mirroring the desktop, `lib/sort_utils.dart`), Playlists, History, and **Downloads** (offline). Connect screen has a "Browse offline" entry. Now Playing's big art cycles through `NowPlaying.artCount` candidates (one every 15s, `_CyclingArt` in `now_playing_view.dart`) when the folder has more than one image and the track has no embedded cover (`artCount` is 1 if it does), crossfading via `AnimatedSwitcher` after precaching the next candidate; small tiles elsewhere stay static.
- **Streaming**: the output picker lists desktop devices plus **"This device"** — selecting it streams transcoded AAC to the phone (Low/Med/High = 96/160/192k, `streamBitrate` pref) and mutes the desktop.
- **Offline mode** (`lib/offline/`): `OfflineStore` (JSON manifest at `<app-docs>/downloads/manifest.json`, mirrored desktop folder tree, path sanitization), `DownloadService` (serial download queue → `.part` → rename, progress/cancel/retry), `OfflinePlayer` (own `AudioPlayer`, local transport: next/prev/shuffle/repeat/auto-advance; mutually exclusive with remote-sink streaming). Download a track/folder/playlist from the ⋮ menus; browse + play with no desktop connection.

Build/run: `cd remote_app && flutter run` (connect to `<desktop-LAN-ip>:8787`; use `10.0.2.2:8787` from an Android emulator).

## Dependencies

### Desktop
- **NAudio** (2.2.1): Audio playback and volume control
- **TagLibSharp** (2.3.0): Audio metadata reading
- **Microsoft.Data.Sqlite** (8.0.10): Local SQLite cache for files and folders
- **Anthropic.SDK** (5.9.0): Claude AI API client for artist summaries
- **DotNetEnv** (3.1.1): Environment variable loading from .env files
- NAudio 2.2.1 (meta-package) also provides CoreAudio (`WasapiOut`, `MMDeviceEnumerator`, `AudioEndpointVolume`) and MediaFoundation (`MediaFoundationEncoder`/`Resampler`) — no extra packages needed for device selection, system volume, or AAC transcoding.

### Mobile (`remote_app/pubspec.yaml`)
- **http**, **shared_preferences**, **just_audio** (local playback / streaming), **path_provider** (offline downloads dir)

## Configuration

The app uses a `.env` file in the project root for configuration:
```
ANTHROPIC_API_KEY=your-api-key-here
```

The `.env` file is excluded from git via `.gitignore`.
