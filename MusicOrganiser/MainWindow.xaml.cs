using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using MusicOrganiser.Dialogs;
using MusicOrganiser.Models;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private FolderNode? _rightClickedFolder;

    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainViewModel();
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Closed += (s, e) => ViewModel.Dispose();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.NowPlaying))
        {
            MusicFilesGrid.Items.Refresh();
        }
    }

    private void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode folder)
        {
            ViewModel.LoadFolder(folder.FullPath);
        }
    }

    private void TreeViewItem_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem item && !e.Handled)
        {
            _rightClickedFolder = item.DataContext as FolderNode;
            item.IsSelected = true;
            item.Focus();
            e.Handled = true;
        }
    }

    private void MusicFilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedFile != null)
        {
            ViewModel.PlayFileCommand.Execute(ViewModel.SelectedFile);
        }
    }

    private void ProgressBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ProgressBar progressBar && ViewModel.TotalDuration.TotalSeconds > 0)
        {
            var clickPosition = e.GetPosition(progressBar);
            var percentage = clickPosition.X / progressBar.ActualWidth;
            var newPosition = TimeSpan.FromSeconds(percentage * ViewModel.TotalDuration.TotalSeconds);
            ViewModel.AudioPlayer.Seek(newPosition);
        }
    }

    private void VolumeBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ProgressBar progressBar)
        {
            var clickPosition = e.GetPosition(progressBar);
            var percentage = clickPosition.X / progressBar.ActualWidth;
            ViewModel.Volume = (int)(percentage * 100);
        }
    }

    #region Folder Context Menu

    private void FolderContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        // Don't overwrite _rightClickedFolder - it was set in the right-click handler
        BuildRecentFoldersMenu(FolderCopyToMenu, ViewModel.RecentFolders.CopyFolders, FolderCopyToRecent_Click);
        BuildRecentFoldersMenu(FolderMoveToMenu, ViewModel.RecentFolders.MoveFolders, FolderMoveToRecent_Click);
    }

    private async void FolderCopyBrowse_Click(object sender, RoutedEventArgs e)
    {
        var folder = BrowseForFolder();
        if (folder != null && _rightClickedFolder != null)
        {
            ViewModel.RecentFolders.AddCopyFolder(folder);
            await CopyFolderWithDuplicateCheck(_rightClickedFolder.FullPath, folder);
        }
    }

    private async void FolderMoveBrowse_Click(object sender, RoutedEventArgs e)
    {
        var folder = BrowseForFolder();
        if (folder != null && _rightClickedFolder != null)
        {
            ViewModel.RecentFolders.AddMoveFolder(folder);
            var folderPath = _rightClickedFolder.FullPath;
            var success = await ViewModel.FileOperations.MoveFolderAsync(folderPath, folder);
            if (success)
            {
                _rightClickedFolder.MarkAsDeleted();
                // Clear music files if the moved folder was selected
                if (ViewModel.CurrentFolderPath.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    ViewModel.MusicFiles.Clear();
                }
            }
            else
                MessageBox.Show("Failed to move folder.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void FolderCopyBrowseArtist_Click(object sender, RoutedEventArgs e)
    {
        if (_rightClickedFolder == null) return;

        var folder = BrowseForFolder();
        if (folder != null)
        {
            ViewModel.RecentFolders.AddCopyFolder(folder);
            await CopyFolderFilesToArtistFolders(_rightClickedFolder.FullPath, folder);
        }
    }

    private async void FolderMoveBrowseArtist_Click(object sender, RoutedEventArgs e)
    {
        if (_rightClickedFolder == null) return;

        var folder = BrowseForFolder();
        if (folder != null)
        {
            ViewModel.RecentFolders.AddMoveFolder(folder);
            var folderPath = _rightClickedFolder.FullPath;
            await MoveFolderFilesToArtistFolders(folderPath, folder);

            // Mark as deleted since all files were moved out
            _rightClickedFolder.MarkAsDeleted();
            if (ViewModel.CurrentFolderPath.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase))
            {
                ViewModel.MusicFiles.Clear();
            }
        }
    }

    private async void FolderDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_rightClickedFolder == null) return;

        var result = MessageBox.Show(
            $"Are you sure you want to delete '{_rightClickedFolder.Name}'?\n\nThis will permanently delete the folder and all its contents.",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            var folderPath = _rightClickedFolder.FullPath;
            var success = await ViewModel.FileOperations.DeleteFolderAsync(folderPath);
            if (success)
            {
                _rightClickedFolder.MarkAsDeleted();
                // Clear music files if the deleted folder was selected
                if (ViewModel.CurrentFolderPath.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    ViewModel.MusicFiles.Clear();
                }
            }
            else
                MessageBox.Show("Failed to delete folder.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void FolderCopyToRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string folder && _rightClickedFolder != null)
        {
            ViewModel.RecentFolders.AddCopyFolder(folder);
            await CopyFolderWithDuplicateCheck(_rightClickedFolder.FullPath, folder);
        }
    }

    private async void FolderMoveToRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string folder && _rightClickedFolder != null)
        {
            ViewModel.RecentFolders.AddMoveFolder(folder);
            var folderPath = _rightClickedFolder.FullPath;
            var success = await ViewModel.FileOperations.MoveFolderAsync(folderPath, folder);
            if (success)
            {
                _rightClickedFolder.MarkAsDeleted();
                // Clear music files if the moved folder was selected
                if (ViewModel.CurrentFolderPath.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    ViewModel.MusicFiles.Clear();
                }
            }
            else
                MessageBox.Show("Failed to move folder.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region File Context Menu

    private void FileContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        BuildRecentFoldersMenu(FileCopyToMenu, ViewModel.RecentFolders.CopyFolders, FileCopyToRecent_Click);
        BuildRecentFoldersMenu(FileMoveToMenu, ViewModel.RecentFolders.MoveFolders, FileMoveToRecent_Click);
    }

    private async void FileCopyBrowse_Click(object sender, RoutedEventArgs e)
    {
        var folder = BrowseForFolder();
        if (folder != null)
        {
            ViewModel.RecentFolders.AddCopyFolder(folder);
            await CopySelectedFiles(folder);
        }
    }

    private async void FileMoveBrowse_Click(object sender, RoutedEventArgs e)
    {
        var folder = BrowseForFolder();
        if (folder != null)
        {
            ViewModel.RecentFolders.AddMoveFolder(folder);
            await MoveSelectedFiles(folder);
        }
    }

    private async void FileCopyBrowseArtist_Click(object sender, RoutedEventArgs e)
    {
        var selectedFiles = GetSelectedFiles();
        if (selectedFiles.Count == 0) return;

        var folder = BrowseForFolder();
        if (folder != null)
        {
            ViewModel.RecentFolders.AddCopyFolder(folder);
            await CopySelectedFilesToArtistFolders(folder);
        }
    }

    private async void FileMoveBrowseArtist_Click(object sender, RoutedEventArgs e)
    {
        var selectedFiles = GetSelectedFiles();
        if (selectedFiles.Count == 0) return;

        var folder = BrowseForFolder();
        if (folder != null)
        {
            ViewModel.RecentFolders.AddMoveFolder(folder);
            await MoveSelectedFilesToArtistFolders(folder);
        }
    }

    private async void FileDelete_Click(object sender, RoutedEventArgs e)
    {
        var selectedFiles = GetSelectedFiles();
        if (selectedFiles.Count == 0) return;

        var message = selectedFiles.Count == 1
            ? $"Are you sure you want to delete '{selectedFiles[0].FileName}'?"
            : $"Are you sure you want to delete {selectedFiles.Count} files?";

        var result = MessageBox.Show(
            message,
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            foreach (var file in selectedFiles)
            {
                await ViewModel.FileOperations.DeleteFileAsync(file.FullPath);
            }
            ViewModel.LoadFolder(ViewModel.CurrentFolderPath);
        }
    }

    private async void FileCopyToRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string folder)
        {
            ViewModel.RecentFolders.AddCopyFolder(folder);
            await CopySelectedFiles(folder);
        }
    }

    private async void FileMoveToRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string folder)
        {
            ViewModel.RecentFolders.AddMoveFolder(folder);
            await MoveSelectedFiles(folder);
        }
    }

    private async System.Threading.Tasks.Task CopySelectedFiles(string destinationFolder)
    {
        var selectedFiles = GetSelectedFiles();
        FileDuplicateAction? applyToAll = null;

        foreach (var file in selectedFiles)
        {
            var destPath = Path.Combine(destinationFolder, file.FileName);

            if (File.Exists(destPath))
            {
                // Check if we already have an "apply to all" action
                FileDuplicateAction action;
                bool setApplyToAll = false;

                if (applyToAll.HasValue)
                {
                    action = applyToAll.Value;
                }
                else
                {
                    // Show duplicate dialog
                    var sourceInfo = FileComparisonInfo.FromPath(file.FullPath);
                    var targetInfo = FileComparisonInfo.FromPath(destPath);
                    var showApplyToAll = selectedFiles.Count > 1;

                    var dialog = new FileDuplicateDialog(sourceInfo, targetInfo, showApplyToAll)
                    {
                        Owner = this
                    };

                    if (dialog.ShowDialog() != true)
                    {
                        // Dialog was closed without a choice (window X button)
                        break;
                    }

                    action = dialog.Result;
                    setApplyToAll = dialog.ApplyToAll;

                    if (setApplyToAll && action != FileDuplicateAction.Cancel)
                    {
                        applyToAll = action;
                    }
                }

                switch (action)
                {
                    case FileDuplicateAction.Cancel:
                        return; // Stop entire operation
                    case FileDuplicateAction.Skip:
                        continue; // Skip this file
                    case FileDuplicateAction.Overwrite:
                        await ViewModel.FileOperations.CopyFileAsync(file.FullPath, destinationFolder, true);
                        break;
                }
            }
            else
            {
                // No conflict, copy normally
                await ViewModel.FileOperations.CopyFileAsync(file.FullPath, destinationFolder, false);
            }
        }
    }

    private async System.Threading.Tasks.Task MoveSelectedFiles(string destinationFolder)
    {
        var selectedFiles = GetSelectedFiles();
        foreach (var file in selectedFiles)
        {
            await ViewModel.FileOperations.MoveFileAsync(file.FullPath, destinationFolder);
        }
        ViewModel.LoadFolder(ViewModel.CurrentFolderPath);
    }

    private async System.Threading.Tasks.Task CopySelectedFilesToArtistFolders(string baseFolder)
    {
        var selectedFiles = GetSelectedFiles();
        FileDuplicateAction? applyToAll = null;

        foreach (var file in selectedFiles)
        {
            var artistFolder = GetArtistFolder(baseFolder, file.Artist);
            Directory.CreateDirectory(artistFolder);

            var destPath = Path.Combine(artistFolder, file.FileName);

            if (File.Exists(destPath))
            {
                FileDuplicateAction action;

                if (applyToAll.HasValue)
                {
                    action = applyToAll.Value;
                }
                else
                {
                    var sourceInfo = FileComparisonInfo.FromPath(file.FullPath);
                    var targetInfo = FileComparisonInfo.FromPath(destPath);
                    var showApplyToAll = selectedFiles.Count > 1;

                    var dialog = new FileDuplicateDialog(sourceInfo, targetInfo, showApplyToAll)
                    {
                        Owner = this
                    };

                    if (dialog.ShowDialog() != true)
                    {
                        break;
                    }

                    action = dialog.Result;

                    if (dialog.ApplyToAll && action != FileDuplicateAction.Cancel)
                    {
                        applyToAll = action;
                    }
                }

                switch (action)
                {
                    case FileDuplicateAction.Cancel:
                        return;
                    case FileDuplicateAction.Skip:
                        continue;
                    case FileDuplicateAction.Overwrite:
                        await ViewModel.FileOperations.CopyFileAsync(file.FullPath, artistFolder, true);
                        break;
                }
            }
            else
            {
                await ViewModel.FileOperations.CopyFileAsync(file.FullPath, artistFolder, false);
            }
        }
    }

    private async System.Threading.Tasks.Task MoveSelectedFilesToArtistFolders(string baseFolder)
    {
        var selectedFiles = GetSelectedFiles();

        foreach (var file in selectedFiles)
        {
            var artistFolder = GetArtistFolder(baseFolder, file.Artist);
            Directory.CreateDirectory(artistFolder);
            await ViewModel.FileOperations.MoveFileAsync(file.FullPath, artistFolder);
        }

        ViewModel.LoadFolder(ViewModel.CurrentFolderPath);
    }

    private static string GetArtistFolder(string baseFolder, string artist)
    {
        // Use "Unknown Artist" if artist is empty or whitespace
        var folderName = string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist;

        // Remove invalid characters from folder name
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            folderName = folderName.Replace(c, '_');
        }

        return Path.Combine(baseFolder, folderName);
    }

    private async System.Threading.Tasks.Task CopyFolderFilesToArtistFolders(string sourceFolder, string baseFolder)
    {
        var musicFiles = ViewModel.MetadataService.GetMusicFiles(sourceFolder).ToList();
        FileDuplicateAction? applyToAll = null;

        foreach (var file in musicFiles)
        {
            var artistFolder = GetArtistFolder(baseFolder, file.Artist);
            Directory.CreateDirectory(artistFolder);

            var destPath = Path.Combine(artistFolder, file.FileName);

            if (File.Exists(destPath))
            {
                FileDuplicateAction action;

                if (applyToAll.HasValue)
                {
                    action = applyToAll.Value;
                }
                else
                {
                    var sourceInfo = FileComparisonInfo.FromPath(file.FullPath);
                    var targetInfo = FileComparisonInfo.FromPath(destPath);
                    var showApplyToAll = musicFiles.Count > 1;

                    var dialog = new FileDuplicateDialog(sourceInfo, targetInfo, showApplyToAll)
                    {
                        Owner = this
                    };

                    if (dialog.ShowDialog() != true)
                    {
                        break;
                    }

                    action = dialog.Result;

                    if (dialog.ApplyToAll && action != FileDuplicateAction.Cancel)
                    {
                        applyToAll = action;
                    }
                }

                switch (action)
                {
                    case FileDuplicateAction.Cancel:
                        return;
                    case FileDuplicateAction.Skip:
                        continue;
                    case FileDuplicateAction.Overwrite:
                        await ViewModel.FileOperations.CopyFileAsync(file.FullPath, artistFolder, true);
                        break;
                }
            }
            else
            {
                await ViewModel.FileOperations.CopyFileAsync(file.FullPath, artistFolder, false);
            }
        }
    }

    private async System.Threading.Tasks.Task MoveFolderFilesToArtistFolders(string sourceFolder, string baseFolder)
    {
        var musicFiles = ViewModel.MetadataService.GetMusicFiles(sourceFolder).ToList();

        foreach (var file in musicFiles)
        {
            var artistFolder = GetArtistFolder(baseFolder, file.Artist);
            Directory.CreateDirectory(artistFolder);
            await ViewModel.FileOperations.MoveFileAsync(file.FullPath, artistFolder);
        }
    }

    private List<MusicFile> GetSelectedFiles()
    {
        return MusicFilesGrid.SelectedItems.Cast<MusicFile>().ToList();
    }

    #endregion

    #region Helpers

    private async System.Threading.Tasks.Task CopyFolderWithDuplicateCheck(string sourcePath, string destinationFolder)
    {
        var destPath = Path.Combine(destinationFolder, Path.GetFileName(sourcePath));

        if (Directory.Exists(destPath))
        {
            // Show folder duplicate dialog
            var sourceInfo = FolderComparisonInfo.FromPath(sourcePath);
            var targetInfo = FolderComparisonInfo.FromPath(destPath);

            var dialog = new FolderDuplicateDialog(sourceInfo, targetInfo)
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
            {
                return; // Dialog was closed without a choice
            }

            var action = dialog.Result;
            if (action == FolderDuplicateAction.Cancel)
            {
                return;
            }

            // For merge operations, we need a callback to handle file duplicates
            FileDuplicateAction? mergeApplyToAll = null;
            Func<string, string, FileDuplicateAction>? onFileDuplicate = null;

            if (action == FolderDuplicateAction.Merge)
            {
                onFileDuplicate = (sourceFile, targetFile) =>
                {
                    // If we have an "apply to all" action, use it
                    if (mergeApplyToAll.HasValue)
                    {
                        return mergeApplyToAll.Value;
                    }

                    // Show file duplicate dialog on UI thread
                    FileDuplicateAction fileAction = FileDuplicateAction.Skip;

                    Dispatcher.Invoke(() =>
                    {
                        var srcInfo = FileComparisonInfo.FromPath(sourceFile);
                        var tgtInfo = FileComparisonInfo.FromPath(targetFile);

                        var fileDialog = new FileDuplicateDialog(srcInfo, tgtInfo, true)
                        {
                            Owner = this
                        };

                        if (fileDialog.ShowDialog() == true)
                        {
                            fileAction = fileDialog.Result;
                            if (fileDialog.ApplyToAll && fileAction != FileDuplicateAction.Cancel)
                            {
                                mergeApplyToAll = fileAction;
                            }
                        }
                        else
                        {
                            fileAction = FileDuplicateAction.Cancel;
                        }
                    });

                    return fileAction;
                };
            }

            var success = await ViewModel.FileOperations.CopyFolderAsync(sourcePath, destinationFolder, action, onFileDuplicate);
            if (!success)
            {
                MessageBox.Show("Failed to copy folder.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            // No conflict, copy normally
            var success = await ViewModel.FileOperations.CopyFolderAsync(sourcePath, destinationFolder);
            if (!success)
            {
                MessageBox.Show("Failed to copy folder.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BuildRecentFoldersMenu(MenuItem parentMenu, List<string> recentFolders, RoutedEventHandler clickHandler)
    {
        parentMenu.Items.Clear();

        foreach (var folder in recentFolders.Take(10))
        {
            if (Directory.Exists(folder))
            {
                var item = new MenuItem
                {
                    Header = folder,
                    Tag = folder
                };
                item.Click += clickHandler;
                parentMenu.Items.Add(item);
            }
        }

        if (parentMenu.Items.Count > 0)
        {
            parentMenu.Items.Add(new Separator());
        }

        // Add Browse... item
        var browseItem = new MenuItem { Header = "Browse..." };
        browseItem.Click += parentMenu == FileCopyToMenu || parentMenu == FolderCopyToMenu
            ? (parentMenu == FileCopyToMenu ? FileCopyBrowse_Click : FolderCopyBrowse_Click)
            : (parentMenu == FileMoveToMenu ? FileMoveBrowse_Click : FolderMoveBrowse_Click);
        parentMenu.Items.Add(browseItem);

        // Add Browse/{artist}... item
        var browseArtistItem = new MenuItem { Header = "Browse/{artist}..." };
        browseArtistItem.Click += parentMenu == FileCopyToMenu || parentMenu == FolderCopyToMenu
            ? (parentMenu == FileCopyToMenu ? FileCopyBrowseArtist_Click : FolderCopyBrowseArtist_Click)
            : (parentMenu == FileMoveToMenu ? FileMoveBrowseArtist_Click : FolderMoveBrowseArtist_Click);
        parentMenu.Items.Add(browseArtistItem);
    }

    private string? BrowseForFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Destination Folder"
        };

        if (dialog.ShowDialog() == true)
        {
            return dialog.FolderName;
        }
        return null;
    }

    #endregion
}
