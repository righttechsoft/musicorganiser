using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
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
            var success = await ViewModel.FileOperations.CopyFolderAsync(_rightClickedFolder.FullPath, folder);
            if (!success)
                MessageBox.Show("Failed to copy folder.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                _rightClickedFolder.IsDeleted = true;
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
                _rightClickedFolder.IsDeleted = true;
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
            var success = await ViewModel.FileOperations.CopyFolderAsync(_rightClickedFolder.FullPath, folder);
            if (!success)
                MessageBox.Show("Failed to copy folder.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                _rightClickedFolder.IsDeleted = true;
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
        foreach (var file in selectedFiles)
        {
            await ViewModel.FileOperations.CopyFileAsync(file.FullPath, destinationFolder);
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

    private List<MusicFile> GetSelectedFiles()
    {
        return MusicFilesGrid.SelectedItems.Cast<MusicFile>().ToList();
    }

    #endregion

    #region Helpers

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

        var browseItem = new MenuItem { Header = "Browse..." };
        browseItem.Click += parentMenu == FileCopyToMenu || parentMenu == FolderCopyToMenu
            ? (parentMenu == FileCopyToMenu ? FileCopyBrowse_Click : FolderCopyBrowse_Click)
            : (parentMenu == FileMoveToMenu ? FileMoveBrowse_Click : FolderMoveBrowse_Click);
        parentMenu.Items.Add(browseItem);
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
