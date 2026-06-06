using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MusicOrganiser.Models;

namespace MusicOrganiser.Services;

/// <summary>
/// Orchestrates cache-first serving plus an in-app background refresh. Wraps the filesystem/
/// TagLib reader (<see cref="MusicMetadataService"/>) and the SQLite cache
/// (<see cref="DatabaseService"/>).
///
/// Read path: callers show <see cref="GetCachedFiles"/> instantly, then await
/// <see cref="RefreshFolderFilesAsync"/> which re-scans the filesystem, re-parses only
/// new/changed files, updates the DB, and returns the authoritative current list.
///
/// A background worker (unbounded channel + single consumer) services
/// <see cref="Enqueue"/> requests to keep neighbouring folders warm off the UI thread.
/// </summary>
public sealed class LibraryCacheService
{
    private static readonly Lazy<LibraryCacheService> _instance = new(() => new LibraryCacheService());
    public static LibraryCacheService Instance => _instance.Value;

    private readonly MusicMetadataService _metadata = new();
    private readonly DatabaseService _db = DatabaseService.Instance;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    private Task? _worker;
    private CancellationTokenSource? _workerCts;

    private LibraryCacheService() { }

    public void Start()
    {
        if (_worker != null) return;
        _workerCts = new CancellationTokenSource();
        _worker = Task.Run(() => WorkerLoopAsync(_workerCts.Token));
    }

    public void Stop()
    {
        try
        {
            _queue.Writer.TryComplete();
            _workerCts?.Cancel();
        }
        catch { }
    }

    /// <summary>Queues a folder for a background cache refresh (fire-and-forget).</summary>
    public void Enqueue(string folderPath)
    {
        if (!string.IsNullOrEmpty(folderPath))
            _queue.Writer.TryWrite(folderPath);
    }

    private async Task WorkerLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var path in _queue.Reader.ReadAllAsync(token))
            {
                try { await RefreshFolderFilesAsync(path, CancellationToken.None); }
                catch { /* one bad folder must not kill the worker */ }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    /// <summary>Instant cache read for a folder's music files (may be stale; refresh follows).</summary>
    public IReadOnlyList<MusicFile> GetCachedFiles(string folderPath)
        => _db.GetFiles(folderPath);

    /// <summary>
    /// Authoritative scan: enumerates the folder, re-parses only files whose size/mtime changed
    /// (unchanged files are served straight from cache — no TagLib), upserts the DB, marks
    /// vanished files deleted, and returns the current file list in enumeration order.
    /// </summary>
    public Task<IReadOnlyList<MusicFile>> RefreshFolderFilesAsync(string folderPath, CancellationToken token)
    {
        return Task.Run<IReadOnlyList<MusicFile>>(() =>
        {
            var result = new List<MusicFile>();

            if (!Directory.Exists(folderPath))
            {
                // Folder gone — mark everything we had cached as deleted.
                var cachedGone = _db.GetFiles(folderPath);
                if (cachedGone.Count > 0)
                    _db.MarkFilesDeleted(cachedGone.Select(f => f.FullPath));
                return result;
            }

            var cached = _db.GetFiles(folderPath)
                .ToDictionary(f => f.FullPath, StringComparer.OrdinalIgnoreCase);

            var toUpsert = new List<MusicFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var filePath in MusicMetadataService.EnumerateSupportedFiles(folderPath))
            {
                token.ThrowIfCancellationRequested();
                seen.Add(filePath);

                long size = 0;
                var mod = DateTime.MinValue;
                try
                {
                    var fi = new FileInfo(filePath);
                    size = fi.Length;
                    mod = fi.LastWriteTimeUtc;
                }
                catch { }

                if (cached.TryGetValue(filePath, out var c) && c.FileSize == size && SameTime(c.FileModifiedUtc, mod))
                {
                    result.Add(c); // unchanged → reuse cache, skip TagLib
                    continue;
                }

                var mf = _metadata.ReadMetadata(filePath);
                if (mf == null) continue;
                mf.FileSize = size;
                mf.FileModifiedUtc = mod;
                if (cached.TryGetValue(filePath, out var prev))
                    mf.Rating = prev.Rating; // preserve rating across re-parse
                toUpsert.Add(mf);
                result.Add(mf);
            }

            if (toUpsert.Count > 0)
                _db.UpsertFiles(folderPath, toUpsert);

            var removed = cached.Keys.Where(k => !seen.Contains(k)).ToList();
            if (removed.Count > 0)
                _db.MarkFilesDeleted(removed);

            return result;
        }, token);
    }

    private static bool SameTime(DateTime a, DateTime b)
        => Math.Abs((a - b).TotalSeconds) < 2; // tolerate fs/db roundtrip granularity
}
