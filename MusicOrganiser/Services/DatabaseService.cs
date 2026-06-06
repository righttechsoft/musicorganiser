using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using MusicOrganiser.Models;

namespace MusicOrganiser.Services;

/// <summary>
/// SQLite cache for files and folders. Pure data access — no filesystem scanning logic
/// beyond cheap stat() needed to populate rows. All operations are wrapped so a cache
/// failure never crashes the app (mirrors the JSON loaders' try/catch style).
///
/// Concurrency: a fresh pooled connection is opened per operation (Microsoft.Data.Sqlite
/// pools by connection string). WAL mode (set once at init, persisted in the DB file)
/// allows the background refresh to write while the UI reads.
/// </summary>
public sealed class DatabaseService
{
    private static readonly Lazy<DatabaseService> _instance = new(() => new DatabaseService());
    public static DatabaseService Instance => _instance.Value;

    private static readonly string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MusicOrganiser");
    private static readonly string DbPath = Path.Combine(AppDataPath, "cache.db");

    private string _connectionString = string.Empty;

    public bool IsReady { get; private set; }

    private DatabaseService() { }

    public void Initialize()
    {
        try
        {
            Directory.CreateDirectory(AppDataPath);
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            ExecRaw(conn, "PRAGMA journal_mode=WAL;");
            ExecRaw(conn, "PRAGMA foreign_keys=ON;");
            ExecRaw(conn, SchemaSql);
            IsReady = true;
        }
        catch
        {
            IsReady = false;
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        ExecRaw(conn, "PRAGMA foreign_keys=ON;");
        return conn;
    }

    private static void ExecRaw(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- Files

    public IReadOnlyList<MusicFile> GetFiles(string folderPath)
    {
        var list = new List<MusicFile>();
        if (!IsReady) return list;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT f.full_path, f.file_name, f.title, f.artist, f.album, f.genre, f.year,
                       f.duration_ticks, f.bitrate, f.sample_rate, f.file_size, f.file_modified_utc, f.rating,
                       (SELECT group_concat(t.name, ', ') FROM file_tags l JOIN tags t ON t.id = l.tag_id WHERE l.file_id = f.id) AS tags
                FROM files f
                WHERE f.folder_path = $folder AND f.is_deleted = 0;";
            cmd.Parameters.AddWithValue("$folder", folderPath);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(ReadFileRow(r));
        }
        catch { /* serve what we have */ }
        return list;
    }

    public void UpsertFiles(string folderPath, IEnumerable<MusicFile> files)
    {
        if (!IsReady) return;
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            // Note: rating is intentionally NOT in the UPDATE set — user ratings survive rescans.
            cmd.CommandText = @"
                INSERT INTO files (full_path, folder_path, file_name, title, artist, album, genre,
                                   year, duration_ticks, bitrate, sample_rate, file_size,
                                   file_modified_utc, is_deleted, last_scanned_utc)
                VALUES ($full, $folder, $name, $title, $artist, $album, $genre,
                        $year, $dur, $bitrate, $sample, $size, $mod, 0, $scanned)
                ON CONFLICT(full_path) DO UPDATE SET
                    folder_path=excluded.folder_path, file_name=excluded.file_name,
                    title=excluded.title, artist=excluded.artist, album=excluded.album,
                    genre=excluded.genre, year=excluded.year, duration_ticks=excluded.duration_ticks,
                    bitrate=excluded.bitrate, sample_rate=excluded.sample_rate,
                    file_size=excluded.file_size, file_modified_utc=excluded.file_modified_utc,
                    is_deleted=0, last_scanned_utc=excluded.last_scanned_utc;";

            var pFull = cmd.Parameters.Add("$full", SqliteType.Text);
            var pFolder = cmd.Parameters.Add("$folder", SqliteType.Text);
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
            var pArtist = cmd.Parameters.Add("$artist", SqliteType.Text);
            var pAlbum = cmd.Parameters.Add("$album", SqliteType.Text);
            var pGenre = cmd.Parameters.Add("$genre", SqliteType.Text);
            var pYear = cmd.Parameters.Add("$year", SqliteType.Integer);
            var pDur = cmd.Parameters.Add("$dur", SqliteType.Integer);
            var pBitrate = cmd.Parameters.Add("$bitrate", SqliteType.Integer);
            var pSample = cmd.Parameters.Add("$sample", SqliteType.Integer);
            var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
            var pMod = cmd.Parameters.Add("$mod", SqliteType.Text);
            var pScanned = cmd.Parameters.Add("$scanned", SqliteType.Text);
            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            foreach (var f in files)
            {
                pFull.Value = f.FullPath;
                pFolder.Value = folderPath;
                pName.Value = f.FileName;
                pTitle.Value = f.Title ?? string.Empty;
                pArtist.Value = f.Artist ?? string.Empty;
                pAlbum.Value = f.Album ?? string.Empty;
                pGenre.Value = f.Genre ?? string.Empty;
                pYear.Value = f.Year;
                pDur.Value = f.Duration.Ticks;
                pBitrate.Value = f.Bitrate;
                pSample.Value = f.SampleRate;
                pSize.Value = f.FileSize;
                pMod.Value = f.FileModifiedUtc.ToString("o", CultureInfo.InvariantCulture);
                pScanned.Value = now;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch { /* ignore cache write failures */ }
    }

    public void MarkFilesDeleted(IEnumerable<string> paths)
    {
        if (!IsReady) return;
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE files SET is_deleted = 1 WHERE full_path = $p;";
            var p = cmd.Parameters.Add("$p", SqliteType.Text);
            foreach (var path in paths)
            {
                p.Value = path;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch { }
    }

    // -------------------------------------------------------------- Folders

    public IReadOnlyList<FolderRecord> GetChildFolders(string parentPath)
    {
        var list = new List<FolderRecord>();
        if (!IsReady) return list;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT fo.full_path, fo.name, fo.parent_path, fo.created_utc, fo.modified_utc, fo.rating, fo.is_deleted,
                       (SELECT group_concat(t.name, ', ') FROM folder_tags l JOIN tags t ON t.id = l.tag_id WHERE l.folder_id = fo.id) AS tags
                FROM folders fo
                WHERE fo.parent_path = $parent AND fo.is_deleted = 0;";
            cmd.Parameters.AddWithValue("$parent", parentPath);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(ReadFolderRow(r));
        }
        catch { }
        return list;
    }

    /// <summary>Upserts folder rows for the given child directory paths under <paramref name="parentPath"/>.
    /// Cheap stat() reads name/created/modified. User ratings are preserved across rescans.</summary>
    public void UpsertFolders(string parentPath, IEnumerable<string> dirPaths)
    {
        if (!IsReady) return;
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO folders (full_path, name, parent_path, created_utc, modified_utc,
                                     is_deleted, children_scanned, files_scanned, last_scanned_utc)
                VALUES ($full, $name, $parent, $created, $modified, 0, 0, 0, $scanned)
                ON CONFLICT(full_path) DO UPDATE SET
                    name=excluded.name, parent_path=excluded.parent_path,
                    created_utc=excluded.created_utc, modified_utc=excluded.modified_utc,
                    is_deleted=0, last_scanned_utc=excluded.last_scanned_utc;";

            var pFull = cmd.Parameters.Add("$full", SqliteType.Text);
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pParent = cmd.Parameters.Add("$parent", SqliteType.Text);
            var pCreated = cmd.Parameters.Add("$created", SqliteType.Text);
            var pModified = cmd.Parameters.Add("$modified", SqliteType.Text);
            var pScanned = cmd.Parameters.Add("$scanned", SqliteType.Text);
            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            foreach (var dir in dirPaths)
            {
                pFull.Value = dir;
                var name = Path.GetFileName(dir);
                pName.Value = string.IsNullOrEmpty(name) ? dir : name;
                pParent.Value = parentPath;
                pCreated.Value = SafeTime(() => Directory.GetCreationTimeUtc(dir));
                pModified.Value = SafeTime(() => Directory.GetLastWriteTimeUtc(dir));
                pScanned.Value = now;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch { }
    }

    public void MarkFolderDeleted(string path)
    {
        if (!IsReady) return;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE folders SET is_deleted = 1 WHERE full_path = $p;";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    // --------------------------------------------------------------- Rating

    public void SetFileRating(string path, int? rating)
        => SetRating("files", path, rating);

    public void SetFolderRating(string path, int? rating)
    {
        // Ensure a folder row exists so a rating can attach even before a scan caches it.
        EnsureFolderRow(path);
        SetRating("folders", path, rating);
    }

    private void SetRating(string table, string path, int? rating)
    {
        if (!IsReady) return;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE {table} SET rating = $r WHERE full_path = $p;";
            cmd.Parameters.AddWithValue("$r", (object?)rating ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    private void EnsureFolderRow(string path)
    {
        if (!IsReady) return;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO folders (full_path, name, parent_path, is_deleted)
                VALUES ($full, $name, $parent, 0)
                ON CONFLICT(full_path) DO NOTHING;";
            cmd.Parameters.AddWithValue("$full", path);
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            cmd.Parameters.AddWithValue("$name", string.IsNullOrEmpty(name) ? path : name);
            cmd.Parameters.AddWithValue("$parent", (object?)Path.GetDirectoryName(path) ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    // ----------------------------------------------------------------- Tags

    public bool AddFileTag(string filePath, string tag) => AddTag("file", filePath, tag);
    public bool RemoveFileTag(string filePath, string tag) => RemoveTag("file", filePath, tag);
    public bool AddFolderTag(string folderPath, string tag)
    {
        EnsureFolderRow(folderPath);
        return AddTag("folder", folderPath, tag);
    }
    public bool RemoveFolderTag(string folderPath, string tag) => RemoveTag("folder", folderPath, tag);

    public IReadOnlyList<string> GetFileTags(string filePath) => GetTags("file", filePath);
    public IReadOnlyList<string> GetFolderTags(string folderPath) => GetTags("folder", folderPath);

    /// <summary>Replaces all of an entity's tags with the parsed comma-separated list.</summary>
    public void SetFileTags(string filePath, string csv) => ReplaceTags("file", filePath, csv);
    public void SetFolderTags(string folderPath, string csv)
    {
        EnsureFolderRow(folderPath);
        ReplaceTags("folder", folderPath, csv);
    }

    private void ReplaceTags(string kind, string path, string csv)
    {
        if (!IsReady) return;
        var (table, linkTable, idCol) = TagTables(kind);
        var tags = (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            long? entityId = ScalarId(conn, tx, $"SELECT id FROM {table} WHERE full_path = $p;", path);
            if (entityId == null) { tx.Rollback(); return; }

            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM {linkTable} WHERE {idCol} = $e;";
                del.Parameters.AddWithValue("$e", entityId.Value);
                del.ExecuteNonQuery();
            }

            foreach (var tag in tags)
            {
                ExecParam(conn, tx, "INSERT OR IGNORE INTO tags (name) VALUES ($n);", ("$n", tag));
                long? tagId = ScalarId(conn, tx, "SELECT id FROM tags WHERE name = $n;", tag);
                if (tagId == null) continue;
                using var link = conn.CreateCommand();
                link.Transaction = tx;
                link.CommandText = $"INSERT OR IGNORE INTO {linkTable} ({idCol}, tag_id) VALUES ($e, $t);";
                link.Parameters.AddWithValue("$e", entityId.Value);
                link.Parameters.AddWithValue("$t", tagId.Value);
                link.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch { }
    }

    private bool AddTag(string kind, string path, string tag)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(tag)) return false;
        var (table, linkTable, idCol) = TagTables(kind);
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            long? entityId = ScalarId(conn, tx, $"SELECT id FROM {table} WHERE full_path = $p;", path);
            if (entityId == null) { tx.Rollback(); return false; }

            ExecParam(conn, tx, "INSERT OR IGNORE INTO tags (name) VALUES ($n);", ("$n", tag.Trim()));
            long? tagId = ScalarId(conn, tx, "SELECT id FROM tags WHERE name = $n;", tag.Trim());
            if (tagId == null) { tx.Rollback(); return false; }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"INSERT OR IGNORE INTO {linkTable} ({idCol}, tag_id) VALUES ($e, $t);";
                cmd.Parameters.AddWithValue("$e", entityId.Value);
                cmd.Parameters.AddWithValue("$t", tagId.Value);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return true;
        }
        catch { return false; }
    }

    private bool RemoveTag(string kind, string path, string tag)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(tag)) return false;
        var (table, linkTable, idCol) = TagTables(kind);
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                DELETE FROM {linkTable}
                WHERE {idCol} = (SELECT id FROM {table} WHERE full_path = $p)
                  AND tag_id   = (SELECT id FROM tags WHERE name = $n);";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$n", tag.Trim());
            return cmd.ExecuteNonQuery() > 0;
        }
        catch { return false; }
    }

    private IReadOnlyList<string> GetTags(string kind, string path)
    {
        var list = new List<string>();
        if (!IsReady) return list;
        var (table, linkTable, idCol) = TagTables(kind);
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT t.name FROM tags t
                JOIN {linkTable} l ON l.tag_id = t.id
                JOIN {table} e ON e.id = l.{idCol}
                WHERE e.full_path = $p
                ORDER BY t.name COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("$p", path);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(r.GetString(0));
        }
        catch { }
        return list;
    }

    public IReadOnlyList<MusicFile> SearchFilesByTag(string tag)
    {
        var list = new List<MusicFile>();
        if (!IsReady) return list;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT f.full_path, f.file_name, f.title, f.artist, f.album, f.genre, f.year,
                       f.duration_ticks, f.bitrate, f.sample_rate, f.file_size, f.file_modified_utc, f.rating,
                       (SELECT group_concat(t2.name, ', ') FROM file_tags l2 JOIN tags t2 ON t2.id = l2.tag_id WHERE l2.file_id = f.id) AS tags
                FROM files f
                JOIN file_tags l ON l.file_id = f.id
                JOIN tags t ON t.id = l.tag_id
                WHERE t.name = $n AND f.is_deleted = 0;";
            cmd.Parameters.AddWithValue("$n", tag.Trim());
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(ReadFileRow(r));
        }
        catch { }
        return list;
    }

    public IReadOnlyList<FolderRecord> SearchFoldersByTag(string tag)
    {
        var list = new List<FolderRecord>();
        if (!IsReady) return list;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT fo.full_path, fo.name, fo.parent_path, fo.created_utc, fo.modified_utc, fo.rating, fo.is_deleted,
                       (SELECT group_concat(t2.name, ', ') FROM folder_tags l2 JOIN tags t2 ON t2.id = l2.tag_id WHERE l2.folder_id = fo.id) AS tags
                FROM folders fo
                JOIN folder_tags l ON l.folder_id = fo.id
                JOIN tags t ON t.id = l.tag_id
                WHERE t.name = $n AND fo.is_deleted = 0;";
            cmd.Parameters.AddWithValue("$n", tag.Trim());
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(ReadFolderRow(r));
        }
        catch { }
        return list;
    }

    // --------------------------------------------------------------- Helpers

    private static (string table, string linkTable, string idCol) TagTables(string kind)
        => kind == "file"
            ? ("files", "file_tags", "file_id")
            : ("folders", "folder_tags", "folder_id");

    private static long? ScalarId(SqliteConnection conn, SqliteTransaction tx, string sql, string param)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$p", param);
        cmd.Parameters.AddWithValue("$n", param);
        var v = cmd.ExecuteScalar();
        return v == null || v == DBNull.Value ? null : Convert.ToInt64(v);
    }

    private static void ExecParam(SqliteConnection conn, SqliteTransaction tx, string sql, params (string, object)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    private static MusicFile ReadFileRow(SqliteDataReader r) => new()
    {
        FullPath = r.GetString(0),
        FileName = r.GetString(1),
        Title = r.IsDBNull(2) ? string.Empty : r.GetString(2),
        Artist = r.IsDBNull(3) ? string.Empty : r.GetString(3),
        Album = r.IsDBNull(4) ? string.Empty : r.GetString(4),
        Genre = r.IsDBNull(5) ? string.Empty : r.GetString(5),
        Year = r.IsDBNull(6) ? 0u : (uint)r.GetInt64(6),
        Duration = r.IsDBNull(7) ? TimeSpan.Zero : TimeSpan.FromTicks(r.GetInt64(7)),
        Bitrate = r.IsDBNull(8) ? 0 : r.GetInt32(8),
        SampleRate = r.IsDBNull(9) ? 0 : r.GetInt32(9),
        FileSize = r.IsDBNull(10) ? 0 : r.GetInt64(10),
        FileModifiedUtc = ParseTime(r.IsDBNull(11) ? null : r.GetString(11)),
        Rating = r.IsDBNull(12) ? null : r.GetInt32(12),
        Tags = r.IsDBNull(13) ? string.Empty : r.GetString(13)
    };

    private static FolderRecord ReadFolderRow(SqliteDataReader r) => new()
    {
        FullPath = r.GetString(0),
        Name = r.GetString(1),
        ParentPath = r.IsDBNull(2) ? null : r.GetString(2),
        CreatedUtc = r.IsDBNull(3) ? null : ParseTime(r.GetString(3)),
        ModifiedUtc = r.IsDBNull(4) ? null : ParseTime(r.GetString(4)),
        Rating = r.IsDBNull(5) ? null : r.GetInt32(5),
        IsDeleted = !r.IsDBNull(6) && r.GetInt64(6) != 0,
        Tags = r.IsDBNull(7) ? string.Empty : r.GetString(7)
    };

    private static string SafeTime(Func<DateTime> get)
    {
        try { return get().ToString("o", CultureInfo.InvariantCulture); }
        catch { return DateTime.MinValue.ToString("o", CultureInfo.InvariantCulture); }
    }

    private static DateTime ParseTime(string? s)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt : DateTime.MinValue;

    private const string SchemaSql = @"
        CREATE TABLE IF NOT EXISTS folders (
            id            INTEGER PRIMARY KEY,
            full_path     TEXT NOT NULL UNIQUE,
            name          TEXT NOT NULL,
            parent_path   TEXT,
            created_utc   TEXT,
            modified_utc  TEXT,
            rating        INTEGER,
            is_deleted    INTEGER NOT NULL DEFAULT 0,
            children_scanned INTEGER NOT NULL DEFAULT 0,
            files_scanned    INTEGER NOT NULL DEFAULT 0,
            last_scanned_utc TEXT
        );
        CREATE TABLE IF NOT EXISTS files (
            id            INTEGER PRIMARY KEY,
            full_path     TEXT NOT NULL UNIQUE,
            folder_path   TEXT NOT NULL,
            file_name     TEXT NOT NULL,
            title         TEXT, artist TEXT, album TEXT, genre TEXT,
            year          INTEGER,
            duration_ticks INTEGER,
            bitrate       INTEGER, sample_rate INTEGER,
            file_size     INTEGER,
            file_modified_utc TEXT,
            rating        INTEGER,
            is_deleted    INTEGER NOT NULL DEFAULT 0,
            last_scanned_utc TEXT
        );
        CREATE TABLE IF NOT EXISTS tags (
            id   INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE
        );
        CREATE TABLE IF NOT EXISTS file_tags (
            file_id INTEGER NOT NULL REFERENCES files(id)  ON DELETE CASCADE,
            tag_id  INTEGER NOT NULL REFERENCES tags(id)   ON DELETE CASCADE,
            PRIMARY KEY (file_id, tag_id)
        );
        CREATE TABLE IF NOT EXISTS folder_tags (
            folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
            tag_id    INTEGER NOT NULL REFERENCES tags(id)    ON DELETE CASCADE,
            PRIMARY KEY (folder_id, tag_id)
        );
        CREATE INDEX IF NOT EXISTS ix_files_folder    ON files(folder_path);
        CREATE INDEX IF NOT EXISTS ix_folders_parent  ON folders(parent_path);
        CREATE INDEX IF NOT EXISTS ix_file_tags_tag   ON file_tags(tag_id);
        CREATE INDEX IF NOT EXISTS ix_folder_tags_tag ON folder_tags(tag_id);";
}
