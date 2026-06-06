# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Music Organiser is a Windows desktop application for managing music files, built with C# WPF (.NET 8). It provides a file browser with filtering and sorting, music metadata display, audio playback with seeking and volume control, and file operations (copy/move/delete).

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

- **AudioPlayerService** (`Services/AudioPlayerService.cs`): NAudio-based audio playback with position tracking and volume control. Critical method `StopAndReleaseFile()` must be called before move/delete operations to release file handles.

- **FileOperationsService** (`Services/FileOperationsService.cs`): Handles copy/move/delete for files and folders. Automatically stops the audio player when operating on currently playing files.

- **MusicMetadataService** (`Services/MusicMetadataService.cs`): TagLib#-based metadata reader. Supported formats: MP3, FLAC, WAV, WMA, AAC, OGG, M4A. `EnumerateSupportedFiles()` returns music file paths (no tag reading) for the cache layer to diff.

- **DatabaseService** (`Services/DatabaseService.cs`): Singleton (`DatabaseService.Instance`) SQLite cache using `Microsoft.Data.Sqlite` (raw ADO.NET, no ORM). DB at `%APPDATA%\MusicOrganiser\cache.db`, WAL mode, pooled connection per operation. Tables: `folders`, `files`, `tags`, `file_tags`, `folder_tags`. Stores file/folder attributes plus `rating` (1–5) and tags. All operations wrapped in try/catch so a cache failure never crashes the app. User ratings are preserved across rescans (rating excluded from the upsert UPDATE set).

- **LibraryCacheService** (`Services/LibraryCacheService.cs`): Singleton orchestrating cache-first serving + in-app background refresh. `GetCachedFiles()` (instant DB read), `RefreshFolderFilesAsync()` (diff filesystem by size/mtime, re-parse only changed files, upsert, return authoritative list). Background worker (`Channel` + single consumer) services `Enqueue()` to warm neighbouring folder caches off the UI thread.

- **ArtistInfoService** (`Services/ArtistInfoService.cs`): Uses Claude Haiku 4.5 API with web search to generate artist summaries. Features:
  - Web search integration (`web_search_20250305` tool) for real-time artist lookup
  - Agentic approach: uses artist name, song title, album, and filename as context
  - Persistent caching of confident results to `%APPDATA%\MusicOrganiser\artist_info_cache.json`
  - Confidence detection via `[CONFIDENT]`/`[UNCERTAIN]` markers in responses
  - Requires `ANTHROPIC_API_KEY` in `.env` file

### Key ViewModels

- **MainViewModel** (`ViewModels/MainViewModel.cs`): Central state management for the application, owns all services and coordinates between folder tree, music grid, and audio player.

- **FolderTreeViewModel** (`ViewModels/FolderTreeViewModel.cs`): Manages folder tree with lazy-loading expansion, filtering, and sorting via `FolderNode` class.

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

## Dependencies

- **NAudio** (2.2.1): Audio playback and volume control
- **TagLibSharp** (2.3.0): Audio metadata reading
- **Microsoft.Data.Sqlite** (8.0.10): Local SQLite cache for files and folders
- **Anthropic.SDK** (5.9.0): Claude AI API client for artist summaries
- **DotNetEnv** (3.1.1): Environment variable loading from .env files

## Configuration

The app uses a `.env` file in the project root for configuration:
```
ANTHROPIC_API_KEY=your-api-key-here
```

The `.env` file is excluded from git via `.gitignore`.
