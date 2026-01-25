using System;
using System.IO;
using System.Threading.Tasks;

namespace MusicOrganiser.Services;

public class FileOperationsService
{
    private readonly AudioPlayerService _audioPlayer;

    public FileOperationsService(AudioPlayerService audioPlayer)
    {
        _audioPlayer = audioPlayer;
    }

    public async Task<bool> CopyFileAsync(string sourcePath, string destinationFolder)
    {
        try
        {
            var fileName = Path.GetFileName(sourcePath);
            var destPath = Path.Combine(destinationFolder, fileName);
            destPath = GetUniqueFilePath(destPath);

            await Task.Run(() => File.Copy(sourcePath, destPath, false));
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

    public async Task<bool> CopyFolderAsync(string sourcePath, string destinationFolder)
    {
        try
        {
            var folderName = Path.GetFileName(sourcePath);
            var destPath = Path.Combine(destinationFolder, folderName);
            destPath = GetUniqueFolderPath(destPath);

            await Task.Run(() => CopyDirectory(sourcePath, destPath));
            return true;
        }
        catch
        {
            return false;
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
}
