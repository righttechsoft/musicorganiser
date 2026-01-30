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

    private async Task MergeDirectoriesAsync(
        string sourceDir,
        string destDir,
        Func<string, string, FileDuplicateAction>? onFileDuplicate)
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
                            await Task.Run(() => File.Copy(sourceFile, destFile, true));
                            break;
                        case FileDuplicateAction.Cancel:
                            return; // Stop the entire merge operation
                    }
                }
                // If no callback, skip the duplicate file
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
            await MergeDirectoriesAsync(sourceSubDir, destSubDir, onFileDuplicate);
        }
    }

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
