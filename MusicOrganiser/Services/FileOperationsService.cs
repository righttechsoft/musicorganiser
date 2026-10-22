using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MusicOrganiser.Models;

namespace MusicOrganiser.Services;

public class FileOperationsService
{
    private readonly AudioPlayerService _audioPlayer;

    public FileOperationsService(AudioPlayerService audioPlayer)
    {
        _audioPlayer = audioPlayer;
    }

    #region Duplicate Checking

    public bool FileExistsAtDestination(string sourcePath, string destFolder)
    {
        var fileName = Path.GetFileName(sourcePath);
        var destPath = Path.Combine(destFolder, fileName);
        return File.Exists(destPath);
    }

    public bool FolderExistsAtDestination(string sourcePath, string destFolder)
    {
        var folderName = Path.GetFileName(sourcePath);
        var destPath = Path.Combine(destFolder, folderName);
        return Directory.Exists(destPath);
    }

    public string GetDestinationPath(string sourcePath, string destFolder)
    {
        var name = Path.GetFileName(sourcePath);
        return Path.Combine(destFolder, name);
    }

    #endregion

    #region File Operations

    public async Task<bool> CopyFileAsync(string sourcePath, string destinationFolder)
    {
        return await CopyFileAsync(sourcePath, destinationFolder, false);
    }

    public async Task<bool> CopyFileAsync(string sourcePath, string destinationFolder, bool overwrite)
    {
        try
        {
            var fileName = Path.GetFileName(sourcePath);
            var destPath = Path.Combine(destinationFolder, fileName);

            if (!overwrite)
            {
                destPath = GetUniqueFilePath(destPath);
            }

            await Task.Run(() => File.Copy(sourcePath, destPath, overwrite));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> MoveFileAsync(string sourcePath, string destinationFolder)
    {
        try
        {
            // Stop player if playing this file
            if (_audioPlayer.IsPlayingFile(sourcePath))
            {
                _audioPlayer.StopAndReleaseFile();
            }

            var fileName = Path.GetFileName(sourcePath);
            var destPath = Path.Combine(destinationFolder, fileName);
            destPath = GetUniqueFilePath(destPath);

            await WaveformService.CancelUnderAsync(sourcePath);   // a waveform decode holds the file open
            await Task.Run(() => File.Move(sourcePath, destPath));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> DeleteFileAsync(string filePath)
    {
        try
        {
            // Stop player if playing this file
            if (_audioPlayer.IsPlayingFile(filePath))
            {
                _audioPlayer.StopAndReleaseFile();
            }

            await WaveformService.CancelUnderAsync(filePath);   // a waveform decode holds the file open
            await Task.Run(() => File.Delete(filePath));
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Folder Operations

    public async Task<bool> CopyFolderAsync(string sourcePath, string destinationFolder)
    {
        return await CopyFolderAsync(sourcePath, destinationFolder, FolderDuplicateAction.Skip, null);
    }

    public async Task<bool> CopyFolderAsync(
        string sourcePath,
        string destinationFolder,
        FolderDuplicateAction action,
        Func<string, string, FileDuplicateAction>? onFileDuplicate)
    {
        try
        {
            var folderName = Path.GetFileName(sourcePath);
            var destPath = Path.Combine(destinationFolder, folderName);

            if (Directory.Exists(destPath))
            {
                switch (action)
                {
                    case FolderDuplicateAction.Skip:
                        return true; // Skip silently

                    case FolderDuplicateAction.Replace:
                        // Delete existing folder first
                        if (_audioPlayer.CurrentFilePath != null &&
                            _audioPlayer.CurrentFilePath.StartsWith(destPath, StringComparison.OrdinalIgnoreCase))
                        {
                            _audioPlayer.StopAndReleaseFile();
                        }
                        await Task.Run(() => Directory.Delete(destPath, true));
                        await Task.Run(() => CopyDirectory(sourcePath, destPath));
                        return true;

                    case FolderDuplicateAction.Merge:
                        await MergeDirectoriesAsync(sourcePath, destPath, onFileDuplicate);
                        return true;

                    case FolderDuplicateAction.Cancel:
                        return false;
                }
            }
            else
            {
                // No conflict, just copy
                await Task.Run(() => CopyDirectory(sourcePath, destPath));
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // Returns false when the user cancelled (stops the whole merge, nested levels included).
    private async Task<bool> MergeDirectoriesAsync(
        string sourceDir,
        string destDir,
        Func<string, string, FileDuplicateAction>? onFileDuplicate,
        bool move = false)
    {
        Directory.CreateDirectory(destDir);

        // Copy files
        foreach (var sourceFile in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(sourceFile);
            var destFile = Path.Combine(destDir, fileName);

            if (File.Exists(destFile))
            {
                // File exists - ask what to do
                if (onFileDuplicate != null)
                {
                    var action = onFileDuplicate(sourceFile, destFile);
                    switch (action)
                    {
                        case FileDuplicateAction.Skip:
                            continue;
                        case FileDuplicateAction.Overwrite:
                            if (move) await Task.Run(() => File.Move(sourceFile, destFile, true));
                            else await Task.Run(() => File.Copy(sourceFile, destFile, true));
                            break;
                        case FileDuplicateAction.Cancel:
                            return false; // Stop the entire merge operation
                    }
                }
                // If no callback, skip the duplicate file
            }
            else if (move)
            {
                await Task.Run(() => File.Move(sourceFile, destFile, false));
            }
            else
            {
                await Task.Run(() => File.Copy(sourceFile, destFile, false));
            }
        }

        // Recursively merge subdirectories
        foreach (var sourceSubDir in Directory.GetDirectories(sourceDir))
        {
            var subDirName = Path.GetFileName(sourceSubDir);
            var destSubDir = Path.Combine(destDir, subDirName);
            if (!await MergeDirectoriesAsync(sourceSubDir, destSubDir, onFileDuplicate, move))
                return false;
        }

        return true;
    }

    #region Similar-folder transfer (merge / replace / keep both)

#if DEBUG
    static FileOperationsService()
    {
        System.Diagnostics.Debug.Assert(IsSimilarFolderName("Album (2019) [FLAC]", "album"));
        System.Diagnostics.Debug.Assert(IsSimilarFolderName("Abbey Road", "The Beatles - Abbey Road"));
        System.Diagnostics.Debug.Assert(!IsSimilarFolderName("Help", "Helplessness Blues"));
        System.Diagnostics.Debug.Assert(!IsSimilarFolderName("Foo", "Bar"));
    }
#endif

    // ponytail: naive normalize + contains, no edit distance. Upgrade = Levenshtein / token-set ratio if it misses real dupes.
    public static bool IsSimilarFolderName(string a, string b)
    {
        var na = NormalizeFolderName(a);
        var nb = NormalizeFolderName(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        if (na == nb) return true;
        var (shorter, longer) = na.Length <= nb.Length ? (na, nb) : (nb, na);
        return shorter.Length >= 5 && longer.Contains(shorter, StringComparison.Ordinal);
    }

    private static string NormalizeFolderName(string name)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), @"\([^)]*\)|\[[^\]]*\]", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\b(19|20)\d{2}\b", " ");
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    private static string FullTrim(string p) =>
        Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool SamePathFull(string a, string b) =>
        string.Equals(FullTrim(a), FullTrim(b), StringComparison.OrdinalIgnoreCase);

    // True when path lies strictly inside folder (compares with a trailing separator, so "Album2" is not under "Album").
    private static bool IsInside(string path, string folder) =>
        FullTrim(path).StartsWith(FullTrim(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public List<string> FindSimilarFolders(string sourcePath, string destinationFolder)
    {
        try
        {
            if (SamePathFull(destinationFolder, sourcePath) || IsInside(destinationFolder, sourcePath))
                return new List<string>();

            var name = Path.GetFileName(FullTrim(sourcePath));
            var result = new List<string>();
            foreach (var dir in Directory.GetDirectories(destinationFolder))
            {
                if (SamePathFull(dir, sourcePath) || IsInside(sourcePath, dir)) continue; // source itself / its ancestors
                var dirName = Path.GetFileName(FullTrim(dir));
                if (string.Equals(dirName, name, StringComparison.OrdinalIgnoreCase) || IsSimilarFolderName(name, dirName))
                    result.Add(dir);
            }

            return result
                .OrderBy(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static (int newCount, int existingCount) CompareFolderFiles(string sourcePath, string targetPath)
    {
        var targetFiles = Directory.EnumerateFiles(targetPath, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(targetPath, f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int newCount = 0, existing = 0;
        foreach (var f in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            if (targetFiles.Contains(Path.GetRelativePath(sourcePath, f))) existing++;
            else newCount++;
        }
        return (newCount, existing);
    }

    // Also reached from the HTTP API, so every path argument is validated before anything is touched.
    public async Task<bool> TransferFolderAsync(
        string sourcePath,
        string destinationFolder,
        bool move,
        FolderDuplicateAction action,
        string? targetPath,
        string? newName,
        Func<string, string, FileDuplicateAction>? onFileDuplicate)
    {
        try
        {
            if (action == FolderDuplicateAction.Skip) return true;
            if (action == FolderDuplicateAction.Cancel) return false;

            if (!Directory.Exists(sourcePath) || !Directory.Exists(destinationFolder)) return false;
            if (SamePathFull(destinationFolder, sourcePath) || IsInside(destinationFolder, sourcePath)) return false;

            var folderName = Path.GetFileName(FullTrim(sourcePath));

            if (action == FolderDuplicateAction.KeepBoth)
            {
                var name = (newName ?? folderName).Trim();
                if (name.Length == 0 || name == "." || name == ".." ||
                    name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
                var keepPath = Path.Combine(destinationFolder, name);
                if (Directory.Exists(keepPath) || File.Exists(keepPath)) return false;

                await ReleaseUnderAsync(sourcePath, move);
                if (move) await Task.Run(() => Directory.Move(sourcePath, keepPath));
                else await Task.Run(() => CopyDirectory(sourcePath, keepPath));
                return true;
            }

            // Merge / Replace: the target must be an existing direct child of the destination, unrelated to the source.
            if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath)) return false;
            var parent = Path.GetDirectoryName(FullTrim(targetPath));
            if (parent == null || !SamePathFull(parent, destinationFolder)) return false;
            if (SamePathFull(targetPath, sourcePath) || IsInside(sourcePath, targetPath) || IsInside(targetPath, sourcePath))
                return false;

            if (action == FolderDuplicateAction.Merge)
            {
                await ReleaseUnderAsync(sourcePath, move);
                await WaveformService.CancelUnderAsync(targetPath);   // merge can overwrite target files
                // A merge the user cancelled part-way is not a failure.
                await MergeDirectoriesAsync(sourcePath, targetPath, onFileDuplicate, move);
                if (move) await Task.Run(() => DeleteEmptyDirectories(sourcePath));
                return true;
            }

            if (action == FolderDuplicateAction.Replace)
            {
                // An exact-name folder that is not the chosen target would be clobbered/left in the way: refuse before deleting.
                var finalPath = Path.Combine(destinationFolder, folderName);
                if ((Directory.Exists(finalPath) || File.Exists(finalPath)) && !SamePathFull(finalPath, targetPath))
                    return false;

                await ReleaseUnderAsync(targetPath, true);
                await ReleaseUnderAsync(sourcePath, move);
                // Bring the source in first, delete second: a failed cross-volume move or partial copy must not leave the target gone.
                var tempPath = Path.Combine(destinationFolder, "~incoming-" + Guid.NewGuid().ToString("N"));
                try
                {
                    if (move) await Task.Run(() => Directory.Move(sourcePath, tempPath));
                    else await Task.Run(() => CopyDirectory(sourcePath, tempPath));
                }
                catch
                {
                    if (!move)
                    {
                        try { Directory.Delete(tempPath, true); } catch { }
                    }
                    return false;
                }
                await Task.Run(() => Directory.Delete(targetPath, true));
                await Task.Run(() => Directory.Move(tempPath, finalPath));
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    // Executes the per-file plan chosen in the folder dialog. All validation happens up front (nothing is touched on a
    // violation); once running, a failure part-way returns false and files already processed stay processed.
    public async Task<bool> ApplyFolderPlanAsync(string sourcePath, string targetPath, bool move, IReadOnlyList<FolderPlanItem> plan)
    {
        try
        {
            if (!Directory.Exists(sourcePath) || !Directory.Exists(targetPath)) return false;
            if (SamePathFull(targetPath, sourcePath) || IsInside(targetPath, sourcePath) || IsInside(sourcePath, targetPath))
                return false;

            var steps = new List<(string Src, string Dst, FolderPlanAction Action)>();
            foreach (var item in plan)
            {
                var rel = item.RelativePath;
                if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel) ||
                    rel.Split('\\', '/').Contains("..")) return false;
                var src = Path.GetFullPath(Path.Combine(sourcePath, rel));
                var dst = Path.GetFullPath(Path.Combine(targetPath, rel));
                if (!IsInside(src, sourcePath) || !IsInside(dst, targetPath)) return false;
                if (item.Action == FolderPlanAction.Transfer ? !File.Exists(src) : !File.Exists(dst)) return false;
                steps.Add((src, dst, item.Action));
            }

            if (steps.Count == 0) return true;

            await ReleaseUnderAsync(sourcePath, move);
            await ReleaseUnderAsync(targetPath, true);

            await Task.Run(() =>
            {
                foreach (var (src, dst, action) in steps)
                {
                    if (action == FolderPlanAction.DeleteTarget)
                    {
                        File.Delete(dst);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    if (move) File.Move(src, dst, true);
                    else File.Copy(src, dst, true);
                }
                if (move) DeleteEmptyDirectories(sourcePath); // skipped files keep their directories alive
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> RenameAsync(string path, string newName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newName) || newName == "." || newName == ".." ||
                newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

            var full = FullTrim(path);
            var parent = Path.GetDirectoryName(full);
            if (parent == null) return false;
            var isDir = Directory.Exists(full);
            if (!isDir && !File.Exists(full)) return false;

            var dest = Path.Combine(parent, newName);
            var caseOnly = SamePathFull(full, dest);
            if (!caseOnly && (File.Exists(dest) || Directory.Exists(dest))) return false;

            await ReleaseUnderAsync(full, true);
            await Task.Run(() =>
            {
                if (!isDir) File.Move(full, dest);
                else if (caseOnly)
                {
                    // Directory.Move refuses a case-only rename, so hop through a temp name
                    var tmp = full + "~rename-" + Guid.NewGuid().ToString("N");
                    Directory.Move(full, tmp);
                    Directory.Move(tmp, dest);
                }
                else Directory.Move(full, dest);
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Frees everything we hold open under the path before it is moved/overwritten/deleted/renamed:
    // the player's handle plus any in-flight waveform decode.
    private async Task ReleaseUnderAsync(string path, bool when)
    {
        ReleasePlayerUnder(path, when);
        if (when) await WaveformService.CancelUnderAsync(path);
    }

    private void ReleasePlayerUnder(string folder, bool when)
    {
        if (when && _audioPlayer.CurrentFilePath != null &&
            (IsInside(_audioPlayer.CurrentFilePath, folder) || SamePathFull(_audioPlayer.CurrentFilePath, folder)))
        {
            _audioPlayer.StopAndReleaseFile();
        }
    }

    // Bottom-up; a directory that still holds anything (files left by skipped duplicates) is kept.
    private static void DeleteEmptyDirectories(string dir)
    {
        foreach (var sub in Directory.GetDirectories(dir))
            DeleteEmptyDirectories(sub);
        if (!Directory.EnumerateFileSystemEntries(dir).Any())
            Directory.Delete(dir);
    }

    #endregion

    public async Task<bool> MoveFolderAsync(string sourcePath, string destinationFolder)
    {
        try
        {
            // Stop player if playing any file from this folder
            if (_audioPlayer.CurrentFilePath != null &&
                _audioPlayer.CurrentFilePath.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                _audioPlayer.StopAndReleaseFile();
            }

            var folderName = Path.GetFileName(sourcePath);
            var destPath = Path.Combine(destinationFolder, folderName);
            destPath = GetUniqueFolderPath(destPath);

            await WaveformService.CancelUnderAsync(sourcePath);   // a waveform decode holds the file open
            await Task.Run(() => Directory.Move(sourcePath, destPath));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> DeleteFolderAsync(string folderPath)
    {
        try
        {
            // Stop player if playing any file from this folder
            if (_audioPlayer.CurrentFilePath != null &&
                _audioPlayer.CurrentFilePath.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase))
            {
                _audioPlayer.StopAndReleaseFile();
            }

            await WaveformService.CancelUnderAsync(folderPath);   // a waveform decode holds the file open
            await Task.Run(() => Directory.Delete(folderPath, true));
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Helpers

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, false);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectory(dir, destSubDir);
        }
    }

    private static string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var counter = 1;

        while (File.Exists(path))
        {
            path = Path.Combine(dir, $"{name} ({counter}){ext}");
            counter++;
        }

        return path;
    }

    private static string GetUniqueFolderPath(string path)
    {
        if (!Directory.Exists(path)) return path;

        var parent = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileName(path);
        var counter = 1;

        while (Directory.Exists(path))
        {
            path = Path.Combine(parent, $"{name} ({counter})");
            counter++;
        }

        return path;
    }

    #endregion
}
