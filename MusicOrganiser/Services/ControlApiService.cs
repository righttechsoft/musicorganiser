using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MusicOrganiser.Models;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Services;

/// <summary>
/// Local HTTP control API for the desktop app (step one of the mobile-remote work).
/// Built on <see cref="HttpListener"/> — no extra packages. No auth: binds to all
/// interfaces so a phone on the same LAN can reach it. All app state lives on
/// <see cref="MainViewModel"/>, so every touch is marshalled onto the UI thread.
/// A start failure (e.g. missing URL ACL) is logged and swallowed so it never
/// crashes the app.
/// </summary>
public class ControlApiService : IDisposable
{
    // ponytail: hardcoded port; read from AppSettings if it ever needs to be configurable.
    private const int DefaultPort = 8787;

    // Sentinel output-device id meaning "play on the phone" (the remote streams audio).
    private const string ThisDeviceId = "__this_device__";

    private readonly MainViewModel _vm;
    private readonly int _port;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    // /status polls at 1Hz; only recompute the art count when the now-playing path changes.
    private (string? Path, int Count) _artCountCache;

    public ControlApiService(MainViewModel vm, int port = DefaultPort)
    {
        _vm = vm;
        _port = port;
    }

    public void Start()
    {
        try
        {
            AudioStreamService.ClearCache(); // fresh transcode cache each run
            _listener.Prefixes.Add($"http://+:{_port}/");
            _listener.Start();
            _ = AcceptLoopAsync();
            Debug.WriteLine($"[ControlApi] listening on http://+:{_port}/");
        }
        catch (HttpListenerException ex)
        {
            Debug.WriteLine($"[ControlApi] could not start on port {_port}: {ex.Message}. " +
                $"Run once (elevated): netsh http add urlacl url=http://+:{_port}/ user=Everyone");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ControlApi] could not start: {ex}");
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception) when (_cts.IsCancellationRequested) { break; }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }

            _ = HandleAsync(ctx);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            await RouteAsync(ctx);
        }
        catch (Exception ex)
        {
            try { await WriteError(ctx, ex.Message, 500); } catch { }
        }
    }

    private async Task RouteAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var method = req.HttpMethod;
        var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
        if (path.Length == 0) path = "/";

        switch (method, path)
        {
            // ---- Playback ----
            case ("GET", "/status"):
                await WriteJson(ctx, BuildStatus());
                break;
            case ("POST", "/playback/playpause"):
                OnUi(() => _vm.PlayPauseCommand.Execute(null));
                await WriteJson(ctx, new { ok = true });
                break;
            case ("POST", "/playback/stop"):
                OnUi(() => _vm.StopCommand.Execute(null));
                await WriteJson(ctx, new { ok = true });
                break;
            case ("POST", "/playback/next"):
                OnUi(() => _vm.NextCommand.Execute(null));
                await WriteJson(ctx, new { ok = true });
                break;
            case ("POST", "/playback/previous"):
                OnUi(() => _vm.PreviousCommand.Execute(null));
                await WriteJson(ctx, new { ok = true });
                break;
            case ("POST", "/playback/shuffle"):
                OnUi(() => _vm.ToggleShuffleCommand.Execute(null));
                await WriteJson(ctx, new { ok = true });
                break;
            case ("POST", "/playback/repeat"):
                OnUi(() => _vm.CycleRepeatCommand.Execute(null));
                await WriteJson(ctx, new { ok = true });
                break;
            case ("POST", "/playback/seek"):
            {
                var body = await ReadBody(req);
                var sec = GetNum(body, "positionSec");
                if (sec == null) { await WriteError(ctx, "positionSec required", 400); break; }
                OnUi(() => _vm.AudioPlayer.Seek(TimeSpan.FromSeconds(sec.Value)));
                await WriteJson(ctx, new { ok = true });
                break;
            }

            // ---- Volume ----
            case ("POST", "/volume"):
            {
                var body = await ReadBody(req);
                var level = GetNum(body, "level");
                if (level == null) { await WriteError(ctx, "level required", 400); break; }
                OnUi(() => _vm.Volume = level.Value);
                await WriteJson(ctx, new { ok = true });
                break;
            }

            // ---- System (Windows master) volume ----
            case ("POST", "/system-volume"):
            {
                var body = await ReadBody(req);
                var level = GetNum(body, "level");
                if (level == null) { await WriteError(ctx, "level required", 400); break; }
                OnUi(() => _vm.SystemVolume = level.Value);
                await WriteJson(ctx, new { ok = true });
                break;
            }

            // ---- Output device selection ----
            case ("GET", "/devices"):
                await WriteJson(ctx, OnUi(() => (object)_vm.OutputDevices
                    .Select(d => new { id = d.Id, name = d.Name }).ToList()));
                break;
            case ("POST", "/device"):
            {
                var body = await ReadBody(req);
                var id = GetStr(body, "id"); // null/empty = revert to system default
                if (id == ThisDeviceId)
                {
                    OnUi(() => _vm.RemoteSink = true); // phone becomes the audio sink
                }
                else
                {
                    OnUi(() =>
                    {
                        _vm.RemoteSink = false;
                        _vm.SelectedOutputDevice = _vm.OutputDevices.FirstOrDefault(d => d.Id == id);
                    });
                }
                await WriteJson(ctx, new { ok = true });
                break;
            }

            // ---- Files / browse ----
            case ("GET", "/browse"):
                await WriteJson(ctx, await BrowseAsync(req.QueryString["path"], req.QueryString["open"] == "1"));
                break;
            case ("POST", "/folder"):
            {
                var body = await ReadBody(req);
                var folder = GetStr(body, "path");
                if (folder == null) { await WriteError(ctx, "path required", 400); break; }
                OnUi(() => _vm.LoadFolder(folder));
                await WriteJson(ctx, new { ok = true });
                break;
            }
            case ("GET", "/files"):
                await WriteJson(ctx, OnUi(BuildFiles));
                break;
            case ("GET", "/search"):
            {
                var q = req.QueryString["q"] ?? "";
                var limit = int.TryParse(req.QueryString["limit"], out var l) ? Math.Clamp(l, 1, 1000) : 300;
                await WriteJson(ctx, BuildSearch(q, limit));
                break;
            }
            case ("POST", "/select"):
            {
                var body = await ReadBody(req);
                var p = GetStr(body, "path");
                if (p == null) { await WriteError(ctx, "path required", 400); break; }
                var found = OnUi(() =>
                {
                    var f = _vm.MusicFiles.FirstOrDefault(x =>
                        string.Equals(x.FullPath, p, StringComparison.OrdinalIgnoreCase));
                    if (f == null) return false;
                    _vm.SelectedFile = f;
                    return true;
                });
                if (found) await WriteJson(ctx, new { success = true });
                else await WriteError(ctx, "file not in current folder", 404);
                break;
            }
            case ("POST", "/play"):
            {
                var body = await ReadBody(req);
                var p = GetStr(body, "path");
                if (p == null) { await WriteError(ctx, "path required", 400); break; }
                // Plays a track from any folder: if it isn't in the loaded queue, load its
                // folder as the queue first (so next/prev work), then play.
                if (await PlayPathAsync(p)) await WriteJson(ctx, new { success = true });
                else await WriteError(ctx, "could not play file", 404);
                break;
            }
            case ("GET", "/file/art"):
                await ServeArtAsync(ctx, req.QueryString["path"], req.QueryString["i"]);
                break;
            case ("GET", "/file/audio"):
                await ServeAudioAsync(ctx, req.QueryString["path"], req.QueryString["bitrate"]);
                break;
            case ("POST", "/file/rating"):
            {
                var body = await ReadBody(req);
                var p = GetStr(body, "path");
                var r = GetNum(body, "rating");
                if (p == null || r == null) { await WriteError(ctx, "path and rating required", 400); break; }
                var rating = (int)r.Value;
                int? stored = rating <= 0 ? null : rating; // 0 clears the rating
                OnUi(() =>
                {
                    var f = _vm.MusicFiles.FirstOrDefault(x =>
                        string.Equals(x.FullPath, p, StringComparison.OrdinalIgnoreCase));
                    // Loaded track: setter write-throughs to the DB + updates the desktop grid.
                    if (f != null) f.Rating = stored;
                    else DatabaseService.Instance.SetFileRating(p, stored);
                });
                await WriteJson(ctx, new { success = true });
                break;
            }
            case ("POST", "/file/tags"):
            {
                var body = await ReadBody(req);
                var p = GetStr(body, "path");
                var tags = GetStr(body, "tags");
                if (p == null || tags == null) { await WriteError(ctx, "path and tags required", 400); break; }
                OnUi(() =>
                {
                    var f = _vm.MusicFiles.FirstOrDefault(x =>
                        string.Equals(x.FullPath, p, StringComparison.OrdinalIgnoreCase));
                    if (f != null) f.Tags = tags;
                    else DatabaseService.Instance.SetFileTags(p, tags);
                });
                await WriteJson(ctx, new { success = true });
                break;
            }

            // ---- Playlists ----
            case ("GET", "/playlists"):
                await WriteJson(ctx, OnUi(() => _vm.Playlists.Select(p => (object)new
                {
                    id = p.Id,
                    name = p.Name,
                    trackCount = p.TrackCount,
                    isOpen = _vm.CurrentPlaylistId == p.Id,
                }).ToList()));
                break;
            case ("POST", "/playlist/create"):
            {
                var body = await ReadBody(req);
                var name = GetStr(body, "name");
                if (string.IsNullOrWhiteSpace(name)) { await WriteError(ctx, "name required", 400); break; }
                var id = OnUi(() => _vm.CreatePlaylist(name)?.Id ?? 0);
                if (id == 0) await WriteError(ctx, "create failed", 500);
                else await WriteJson(ctx, new { id });
                break;
            }
            case ("POST", "/playlist/rename"):
            {
                var body = await ReadBody(req);
                var id = GetInt(body, "id");
                var name = GetStr(body, "name");
                if (id == null || string.IsNullOrWhiteSpace(name)) { await WriteError(ctx, "id and name required", 400); break; }
                OnUi(() => { var pl = FindPlaylist(id.Value); if (pl != null) _vm.RenamePlaylist(pl, name); });
                await WriteJson(ctx, new { success = true });
                break;
            }
            case ("POST", "/playlist/delete"):
            {
                var body = await ReadBody(req);
                var id = GetInt(body, "id");
                if (id == null) { await WriteError(ctx, "id required", 400); break; }
                OnUi(() => { var pl = FindPlaylist(id.Value); if (pl != null) _vm.DeletePlaylist(pl); });
                await WriteJson(ctx, new { success = true });
                break;
            }
            case ("GET", "/playlist/files"):
            {
                if (!int.TryParse(req.QueryString["id"], out var id)) { await WriteError(ctx, "id required", 400); break; }
                await WriteJson(ctx, BuildPlaylistFiles(id));
                break;
            }
            case ("POST", "/playlist/open"):
            {
                var body = await ReadBody(req);
                var id = GetInt(body, "id");
                if (id == null) { await WriteError(ctx, "id required", 400); break; }
                OnUi(() =>
                {
                    var pl = FindPlaylist(id.Value);
                    if (pl == null) return;
                    // Selecting the playlist mirrors a real click: highlights it in the desktop's
                    // Playlists list and drives the same open path. LoadPlaylist guarantees the grid.
                    _vm.SelectedPlaylist = pl;
                    _vm.LoadPlaylist(pl);
                });
                await WriteJson(ctx, new { success = true });
                break;
            }
            case ("POST", "/playlist/play"):
            {
                var body = await ReadBody(req);
                var id = GetInt(body, "id");
                if (id == null) { await WriteError(ctx, "id required", 400); break; }
                var ok = OnUi(() =>
                {
                    var pl = FindPlaylist(id.Value);
                    if (pl == null) return false;
                    _vm.SelectedPlaylist = pl;
                    _vm.LoadPlaylist(pl);
                    if (_vm.MusicFiles.Count == 0) return false;
                    _vm.PlayFileCommand.Execute(_vm.MusicFiles[0]);
                    return true;
                });
                await WriteJson(ctx, new { success = ok });
                break;
            }
            case ("POST", "/playlist/add"):
            {
                var body = await ReadBody(req);
                var id = GetInt(body, "id");
                var p = GetStr(body, "path");
                var folder = GetStr(body, "folder");
                if (id == null || (p == null && folder == null)) { await WriteError(ctx, "id and path/folder required", 400); break; }
                if (folder != null)
                    await OnUiAsync(async () => { await _vm.AddFolderToPlaylistAsync(id.Value, folder); return true; });
                else
                    OnUi(() => _vm.AddFilesToPlaylist(id.Value, new[] { p! }));
                await WriteJson(ctx, new { success = true });
                break;
            }
            case ("POST", "/playlist/remove"):
            {
                var body = await ReadBody(req);
                var id = GetInt(body, "id");
                var entryId = GetInt(body, "entryId");
                if (id == null || entryId == null) { await WriteError(ctx, "id and entryId required", 400); break; }
                OnUi(() =>
                {
                    // Removing from the open playlist keeps the live queue in sync; otherwise
                    // hit the DB directly and refresh the list's track counts.
                    if (_vm.CurrentPlaylistId == id)
                    {
                        var mf = _vm.MusicFiles.FirstOrDefault(f => f.PlaylistEntryId == entryId);
                        if (mf != null) _vm.RemoveFromCurrentPlaylist(new[] { mf });
                    }
                    else
                    {
                        DatabaseService.Instance.RemoveFromPlaylist(id.Value, new[] { entryId.Value });
                        _vm.RefreshPlaylists();
                    }
                });
                await WriteJson(ctx, new { success = true });
                break;
            }

            // ---- History (existing recently-played folders) ----
            case ("GET", "/history"):
                await WriteJson(ctx, OnUi(() => _vm.RecentPlayedFolders
                    .Select(r => (object)new { path = r.FullPath, displayName = r.DisplayName })
                    .ToList()));
                break;
            case ("POST", "/history/clear"):
                OnUi(() => _vm.ClearRecentPlayedFolders());
                await WriteJson(ctx, new { ok = true });
                break;

            // ---- Move / Delete (reuse FileOperationsService) ----
            case ("POST", "/file/move"):
                await FileOp(ctx, req, (src, dst) => _vm.FileOperations.MoveFileAsync(src, dst), needsDest: true);
                break;
            case ("POST", "/file/delete"):
                await FileOp(ctx, req, (src, _) => _vm.FileOperations.DeleteFileAsync(src), needsDest: false);
                break;
            case ("POST", "/folder/move"):
                await FileOp(ctx, req, (src, dst) => _vm.FileOperations.MoveFolderAsync(src, dst), needsDest: true);
                break;
            case ("POST", "/folder/delete"):
                await FileOp(ctx, req, async (src, _) =>
                {
                    var ok = await _vm.FileOperations.DeleteFolderAsync(src);
                    if (ok) _vm.RemoveRecentPlayedFolderTree(src);
                    return ok;
                }, needsDest: false);
                break;

            // ---- Static PWA web client (served from /) ----
            default:
                if (method == "GET" && await ServeStaticAsync(ctx, path)) break;
                await WriteError(ctx, $"no route for {method} {path}", 404);
                break;
        }
    }

    // Shared handler for the four move/delete routes. The op runs on the UI thread
    // because it may stop the audio player (a VM-owned service).
    private async Task FileOp(HttpListenerContext ctx, HttpListenerRequest req,
        Func<string, string, Task<bool>> op, bool needsDest)
    {
        var body = await ReadBody(req);
        var path = GetStr(body, "path");
        var dest = GetStr(body, "destFolder");
        if (path == null || (needsDest && dest == null))
        {
            await WriteError(ctx, needsDest ? "path and destFolder required" : "path required", 400);
            return;
        }
        var ok = await OnUiAsync(() => op(path, dest ?? ""));
        await WriteJson(ctx, new { success = ok });
    }

    private object BuildStatus()
    {
        return OnUi(() =>
        {
            var np = _vm.NowPlaying;
            return (object)new
            {
                nowPlaying = np == null ? null : new
                {
                    path = np.FullPath,
                    title = np.Title,
                    artist = np.Artist,
                    album = np.Album,
                    durationSec = (int)_vm.TotalDuration.TotalSeconds,
                    artCount = GetArtCount(np.FullPath),
                },
                positionSec = (int)_vm.CurrentPosition.TotalSeconds,
                durationSec = (int)_vm.TotalDuration.TotalSeconds,
                isPlaying = _vm.IsPlaying,
                isPaused = _vm.AudioPlayer.IsPaused,
                volume = (int)_vm.Volume,
                systemVolume = (int)_vm.SystemVolume,
                outputDeviceId = _vm.RemoteSink ? ThisDeviceId : _vm.SelectedOutputDevice?.Id,
                currentFolder = _vm.IsPlaylistView ? "" : _vm.CurrentFolderPath,
                shuffle = _vm.ShuffleEnabled,
                repeat = _vm.Repeat.ToString().ToLowerInvariant(),
            };
        });
    }

    // Number of art candidates for the now-playing track: 1 if it has an embedded picture
    // (folder images are no longer candidates), otherwise the folder's image file count.
    // Cached per-path so the 1Hz /status poll only re-reads tags/enumerates the folder
    // when the track actually changes.
    private int GetArtCount(string path)
    {
        if (_artCountCache.Path == path)
            return _artCountCache.Count;

        var count = 1;
        try
        {
            var embedded = 0;
            using (var tag = TagLib.File.Create(path))
            {
                if (tag.Tag.Pictures.Length > 0 && tag.Tag.Pictures[0].Data.Count > 0)
                    embedded = 1;
            }
            count = embedded > 0 ? 1 : MusicMetadataService.GetFolderImagePaths(Path.GetDirectoryName(path) ?? "").Count;
        }
        catch
        {
            count = 1;
        }

        _artCountCache = (path, count);
        return count;
    }

    // Must run inside OnUi — enumerates the VM's ObservableCollection.
    private object BuildFiles()
    {
        var np = _vm.NowPlaying;
        return _vm.MusicFiles.Select(f => (object)new
        {
            path = f.FullPath,
            title = f.Title,
            artist = f.Artist,
            album = f.Album,
            durationSec = (int)f.Duration.TotalSeconds,
            rating = f.Rating,
            tags = f.Tags,
            isPlaying = ReferenceEquals(f, np),
        }).ToList();
    }

    // Global library search (DB read across every folder). Same track shape as /files.
    private object BuildSearch(string query, int limit)
    {
        var np = OnUi(() => _vm.NowPlaying);
        var files = DatabaseService.Instance.SearchFiles(query, limit);
        return files.Select(f => (object)new
        {
            path = f.FullPath,
            title = f.Title,
            artist = f.Artist,
            album = f.Album,
            durationSec = (int)f.Duration.TotalSeconds,
            rating = f.Rating,
            tags = f.Tags,
            isPlaying = np != null && string.Equals(f.FullPath, np.FullPath, StringComparison.OrdinalIgnoreCase),
        }).ToList();
    }

    private static int? GetInt(JsonElement e, string name)
    {
        var n = GetNum(e, name);
        return n == null ? null : (int)n.Value;
    }

    // Must run inside OnUi.
    private Playlist? FindPlaylist(int id) => _vm.Playlists.FirstOrDefault(p => p.Id == id);

    // Serialize a playlist's tracks (DB read; works whether or not the playlist is open).
    private object BuildPlaylistFiles(int id)
    {
        var np = OnUi(() => _vm.NowPlaying);
        var files = DatabaseService.Instance.GetPlaylistFiles(id);
        return files.Select(f => (object)new
        {
            path = f.FullPath,
            title = f.Title,
            artist = f.Artist,
            album = f.Album,
            durationSec = (int)f.Duration.TotalSeconds,
            rating = f.Rating,
            tags = f.Tags,
            playlistEntryId = f.PlaylistEntryId,
            isPlaying = np != null && string.Equals(f.FullPath, np.FullPath, StringComparison.OrdinalIgnoreCase),
        }).ToList();
    }

    // Browse a folder: returns its subfolders AND its music files (with cached
    // rating/tags), so the client can list tracks inline without loading the folder
    // as the desktop's play queue. Empty path => drive roots.
    private async Task<object> BrowseAsync(string? path, bool open)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => (object)new { name = d.Name, path = d.RootDirectory.FullName })
                .ToList();
            return new { path = "", folders = drives, files = new List<object>() };
        }

        // Mirror the navigation on the desktop: expand + select the folder in its tree
        // (which loads that folder's files into the desktop grid). Fire-and-forget so a
        // slow tree expansion never delays the mobile response.
        if (open)
        {
            try { _ = OnUiAsync(() => _vm.FolderTree.NavigateToPathAsync(path)); } catch { }
        }

        var folders = Directory.GetDirectories(path)
            .Select(d => (object)new
            {
                name = Path.GetFileName(d),
                path = d,
                createdSec = UnixOrNull(() => Directory.GetCreationTimeUtc(d)),
                modifiedSec = UnixOrNull(() => Directory.GetLastWriteTimeUtc(d)),
            })
            .ToList();

        IReadOnlyList<MusicFile> files;
        try { files = await LibraryCacheService.Instance.RefreshFolderFilesAsync(path, CancellationToken.None); }
        catch { files = Array.Empty<MusicFile>(); }

        var np = OnUi(() => _vm.NowPlaying);
        var fileObjs = files.Select(f => (object)new
        {
            path = f.FullPath,
            title = f.Title,
            artist = f.Artist,
            album = f.Album,
            durationSec = (int)f.Duration.TotalSeconds,
            rating = f.Rating,
            tags = f.Tags,
            createdSec = UnixOrNull(() => File.GetCreationTimeUtc(f.FullPath)),
            modifiedSec = UnixOrNull(() => File.GetLastWriteTimeUtc(f.FullPath)),
            isPlaying = np != null && string.Equals(f.FullPath, np.FullPath, StringComparison.OrdinalIgnoreCase),
        }).ToList();

        return new { path, folders, files = fileObjs };
    }

    // Unix seconds for a filesystem timestamp, or null on IO failure (sorts to the end on the client).
    private static long? UnixOrNull(Func<DateTime> get)
    {
        try { return new DateTimeOffset(get()).ToUnixTimeSeconds(); }
        catch { return null; }
    }

    // Play a track by full path. If it's already in the loaded queue, play it directly;
    // otherwise load its containing folder (cache-first, so usually instant) and play it
    // once it appears in the queue.
    private async Task<bool> PlayPathAsync(string path)
    {
        if (OnUi(() => TryPlayFromLoaded(path))) return true;

        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return false;
        OnUi(() => _vm.LoadFolder(dir));

        // ponytail: poll until the folder finishes loading (cache-first hit is near-instant);
        // give up after ~3s so a bad path can't hang the request.
        for (var i = 0; i < 60; i++)
        {
            if (OnUi(() => TryPlayFromLoaded(path))) return true;
            await Task.Delay(50);
        }
        return false;
    }

    // Must run inside OnUi.
    private bool TryPlayFromLoaded(string path)
    {
        var f = _vm.MusicFiles.FirstOrDefault(x =>
            string.Equals(x.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (f == null) return false;
        _vm.PlayFileCommand.Execute(f);
        return true;
    }

    // ---- Static web client (embedded resources, LogicalName "web/<file>") ----
    // Returns false when no asset matches so the caller can fall through to 404.
    private static async Task<bool> ServeStaticAsync(HttpListenerContext ctx, string path)
    {
        var rel = path is "/" or "" ? "index.html" : path.TrimStart('/');
        if (rel.Contains("..")) return false; // resource names are flat; reject traversal anyway

        using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("web/" + rel);
        if (src == null) return false;

        using var ms = new MemoryStream();
        await src.CopyToAsync(ms);
        var bytes = ms.ToArray();

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = ContentType(rel);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
        return true;
    }

    private static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".webmanifest" => "application/manifest+json",
        ".json" => "application/json",
        ".css" => "text/css; charset=utf-8",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".svg" => "image/svg+xml",
        // Folder cover-art formats (MusicMetadataService.ImageExtensions).
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    // ---- Album art (embedded cover, or a folder image file, via TagLib) ----
    // Candidate list for the track: [embedded picture] if present, else [folder image files].
    // Streams candidate `i` (default 0); 404 when out of range so the client can show its
    // "no album art" placeholder. When the track has an embedded picture it is served for
    // every index (it is the only candidate); folder images are only used as a fallback.
    private static async Task ServeArtAsync(HttpListenerContext ctx, string? path, string? indexStr)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            await WriteError(ctx, "not found", 404);
            return;
        }
        var index = int.TryParse(indexStr, out var i) ? i : 0;
        try
        {
            byte[]? embedded = null;
            var embeddedMime = "image/jpeg";
            // ponytail: full embedded image; client caches by URL. Resize server-side only if LAN bandwidth bites.
            using (var tag = TagLib.File.Create(path))
            {
                var pics = tag.Tag.Pictures;
                if (pics.Length > 0 && pics[0].Data.Count > 0)
                {
                    embedded = pics[0].Data.Data;
                    if (!string.IsNullOrEmpty(pics[0].MimeType)) embeddedMime = pics[0].MimeType;
                }
            }

            byte[] data;
            string mime;
            if (embedded != null)
            {
                data = embedded;
                mime = embeddedMime;
            }
            else
            {
                var images = MusicMetadataService.GetFolderImagePaths(Path.GetDirectoryName(path) ?? "");
                if (index < 0 || index >= images.Count) { await WriteError(ctx, "no art", 404); return; }
                data = File.ReadAllBytes(images[index]);
                mime = ContentType(images[index]);
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = mime;
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers["Cache-Control"] = "max-age=86400";
            ctx.Response.ContentLength64 = data.Length;
            await ctx.Response.OutputStream.WriteAsync(data);
            ctx.Response.Close();
        }
        catch
        {
            try { await WriteError(ctx, "no art", 404); } catch { }
        }
    }

    // ---- Audio stream (transcoded AAC for the phone; supports HTTP Range) ----
    private async Task ServeAudioAsync(HttpListenerContext ctx, string? path, string? bitrateStr)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            await WriteError(ctx, "not found", 404);
            return;
        }

        var bitrate = AudioStreamService.DefaultBitrate;
        if (int.TryParse(bitrateStr, out var b)) bitrate = b;
        bitrate = AudioStreamService.ClampBitrate(bitrate);
        _vm.LastStreamBitrate = bitrate; // so PlayFile prewarms the next track at this rate

        string file;
        try
        {
            file = await Task.Run(() => AudioStreamService.GetOrCreateAac(path, bitrate));
        }
        catch
        {
            await WriteError(ctx, "transcode failed", 500);
            return;
        }

        try
        {
            var len = new FileInfo(file).Length;
            long start = 0, end = len - 1;
            var rangeHeader = ctx.Request.Headers["Range"];
            var isRange = !string.IsNullOrEmpty(rangeHeader)
                          && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)
                          && TryParseRange(rangeHeader, len, out start, out end);

            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers["Accept-Ranges"] = "bytes";
            ctx.Response.ContentType = "audio/mp4";
            if (isRange)
            {
                ctx.Response.StatusCode = 206;
                ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{len}";
            }
            else
            {
                ctx.Response.StatusCode = 200;
                start = 0;
                end = len - 1;
            }

            var count = end - start + 1;
            ctx.Response.ContentLength64 = count;

            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[81920];
            var remaining = count;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await fs.ReadAsync(buffer.AsMemory(0, toRead));
                if (read <= 0) break;
                await ctx.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read));
                remaining -= read;
            }
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { } // client disconnected mid-stream
        }
    }

    // Parses a single-range "bytes=start-end" header. Handles open start/end and suffix ranges.
    private static bool TryParseRange(string header, long len, out long start, out long end)
    {
        start = 0;
        end = len - 1;
        var spec = header.Substring("bytes=".Length);
        var dash = spec.IndexOf('-');
        if (dash < 0) return false;
        var startStr = spec.Substring(0, dash).Trim();
        var endStr = spec.Substring(dash + 1).Trim();

        if (startStr.Length == 0)
        {
            // suffix range: last N bytes
            if (!long.TryParse(endStr, out var suffix) || suffix <= 0) return false;
            if (suffix > len) suffix = len;
            start = len - suffix;
            end = len - 1;
            return true;
        }

        if (!long.TryParse(startStr, out start)) return false;
        if (endStr.Length == 0) end = len - 1;
        else if (!long.TryParse(endStr, out end)) return false;

        if (start < 0 || start >= len) return false;
        if (end >= len) end = len - 1;
        return end >= start;
    }

    // ---- UI-thread marshalling ----
    private static T OnUi<T>(Func<T> f) => Application.Current.Dispatcher.Invoke(f);
    private static void OnUi(Action a) => Application.Current.Dispatcher.Invoke(a);
    private static Task<T> OnUiAsync<T>(Func<Task<T>> f) => Application.Current.Dispatcher.Invoke(f);

    // ---- JSON helpers ----
    private static async Task<JsonElement> ReadBody(HttpListenerRequest req)
    {
        using var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
        var s = await sr.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(s)) return default;
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.Clone();
    }

    private static string? GetStr(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? GetNum(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static async Task WriteJson(HttpListenerContext ctx, object obj, int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static Task WriteError(HttpListenerContext ctx, string message, int status)
        => WriteJson(ctx, new { error = message }, status);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { if (_listener.IsListening) _listener.Stop(); _listener.Close(); } catch { }
        _cts.Dispose();
    }
}
