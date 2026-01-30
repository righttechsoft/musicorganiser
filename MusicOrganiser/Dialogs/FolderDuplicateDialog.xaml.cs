using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using MusicOrganiser.Models;
using MusicOrganiser.Services;

namespace MusicOrganiser.Dialogs;

public partial class FolderDuplicateDialog : Window
{
    public FolderDuplicateAction Result { get; private set; } = FolderDuplicateAction.Cancel;

    public FolderDuplicateDialog(FolderComparisonInfo source, FolderComparisonInfo target)
    {
        InitializeComponent();

        // Populate source folder info
        SourceFolderName.Text = source.FolderName;
        SourceFileCount.Text = source.FileCount.ToString();
        SourceTotalSize.Text = source.TotalSizeFormatted;

        // Populate target folder info
        TargetFolderName.Text = target.FolderName;
        TargetFileCount.Text = target.FileCount.ToString();
        TargetTotalSize.Text = target.TotalSizeFormatted;

        // Build file comparison list
        var entries = BuildFileComparisonList(source.FullPath, target.FullPath);
        FileComparisonGrid.ItemsSource = entries;
    }

    private static ObservableCollection<FolderFileEntry> BuildFileComparisonList(string sourcePath, string targetPath)
    {
        var entries = new ObservableCollection<FolderFileEntry>();

        // Get files from source folder (recursively)
        var sourceFiles = GetAllFiles(sourcePath)
            .Select(f => GetRelativePath(f, sourcePath))
            .ToHashSet();

        // Get files from target folder (recursively)
        var targetFiles = GetAllFiles(targetPath)
            .Select(f => GetRelativePath(f, targetPath))
            .ToHashSet();

        // Add source files with their status
        foreach (var relPath in sourceFiles.OrderBy(f => f))
        {
            var fullPath = Path.Combine(sourcePath, relPath);
            var fileInfo = new FileInfo(fullPath);
            var status = targetFiles.Contains(relPath) ? "Exists" : "New";

            // Check if the file is different
            if (status == "Exists")
            {
                var targetFilePath = Path.Combine(targetPath, relPath);
                var targetFileInfo = new FileInfo(targetFilePath);
                if (fileInfo.Length != targetFileInfo.Length)
                {
                    status = "Different";
                }
            }

            var entry = new FolderFileEntry
            {
                FileName = Path.GetFileName(relPath),
                RelativePath = relPath,
                FileSize = fileInfo.Length,
                Status = status
            };

            // Try to read duration for audio files
            if (MusicMetadataService.IsSupportedFile(fullPath))
            {
                try
                {
                    using var tagFile = TagLib.File.Create(fullPath);
                    entry.Duration = tagFile.Properties.Duration;
                }
                catch
                {
                    // Ignore tag reading errors
                }
            }

            entries.Add(entry);
        }

        // Add target-only files
        foreach (var relPath in targetFiles.Except(sourceFiles).OrderBy(f => f))
        {
            var fullPath = Path.Combine(targetPath, relPath);
            var fileInfo = new FileInfo(fullPath);

            var entry = new FolderFileEntry
            {
                FileName = Path.GetFileName(relPath),
                RelativePath = relPath,
                FileSize = fileInfo.Length,
                Status = "Target Only"
            };

            if (MusicMetadataService.IsSupportedFile(fullPath))
            {
                try
                {
                    using var tagFile = TagLib.File.Create(fullPath);
                    entry.Duration = tagFile.Properties.Duration;
                }
                catch
                {
                    // Ignore tag reading errors
                }
            }

            entries.Add(entry);
        }

        return entries;
    }

    private static IEnumerable<string> GetAllFiles(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }

    private static string GetRelativePath(string fullPath, string basePath)
    {
        if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString()))
            basePath += Path.DirectorySeparatorChar;

        return fullPath.Substring(basePath.Length);
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        Result = FolderDuplicateAction.Skip;
        DialogResult = true;
    }

    private void MergeButton_Click(object sender, RoutedEventArgs e)
    {
        Result = FolderDuplicateAction.Merge;
        DialogResult = true;
    }

    private void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This will permanently delete the existing folder and all its contents.\n\nAre you sure you want to continue?",
            "Confirm Replace",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            Result = FolderDuplicateAction.Replace;
            DialogResult = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = FolderDuplicateAction.Cancel;
        DialogResult = false;
    }
}
