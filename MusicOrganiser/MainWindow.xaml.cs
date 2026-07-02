using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MusicOrganiser.Dialogs;
using MusicOrganiser.Models;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private FolderNode? _rightClickedFolder;
    private Playlist? _rightClickedPlaylist;

    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainViewModel();
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.MusicFiles.CollectionChanged += (s, e) =>
            Dispatcher.BeginInvoke(new Action(UpdateAlbumCoverPosition),
                System.Windows.Threading.DispatcherPriority.Background);
        Closed += (s, e) => ViewModel.Dispose();
    }

    private void CoverHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateAlbumCoverPosition();

    private void AlbumCoverImage_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateAlbumCoverPosition();

    /// <summary>
    /// Keeps the album cover vertically centred, but shifts it down so the file
    /// rows don't overlap it while there is still room beneath. Once the image
    /// reaches the bottom of the area, it stops and the semi-transparent rows
    /// overlay it instead.
    /// </summary>
    private void UpdateAlbumCoverPosition()
    {
        if (AlbumCoverImage.Source == null)
            return;

        double area = CoverHost.ActualHeight;
        double imgH = AlbumCoverImage.ActualHeight;
        if (area <= 0 || imgH <= 0)
            return;

        double maxTop = Math.Max(0, area - imgH);
        double centeredTop = maxTop / 2;
        double rowsBottom = GetRowsBottom();

        double top = Math.Min(maxTop, Math.Max(centeredTop, rowsBottom));

        var m = AlbumCoverImage.Margin;
        if (Math.Abs(m.Top - top) > 0.5)
            AlbumCoverImage.Margin = new Thickness(m.Left, top, m.Right, m.Bottom);
    }

    private double GetRowsBottom()
    {
        int count = MusicFilesGrid.Items.Count;
        if (count == 0)
            return 0;

        // Measure the bottom of the last realized row relative to the cover host.
        for (int i = count - 1; i >= 0; i--)
        {
            if (MusicFilesGrid.ItemContainerGenerator.ContainerFromIndex(i) is DataGridRow row && row.IsVisible)
            {
                try
                {
                    // Add one row height as a gap between the last file and the cover.
                    var p = row.TransformToAncestor(CoverHost).Transform(new Point(0, row.ActualHeight));
                    return p.Y + row.ActualHeight;
                }
                catch
                {
                    return 0;
                }
            }
        }

        // No rows realized in view: the list fills/overflows the area.
        return CoverHost.ActualHeight;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Now-playing row highlight updates automatically via the NowPlayingConverter
        // MultiBinding (it watches DataContext.NowPlaying). Do NOT call Items.Refresh()
        // here: it regenerates every row container and wipes the StarRating cell display.
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

    private async void RecentFolder_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string path)
        {
            if (!Directory.Exists(path))
            {
                ViewModel.RemoveRecentPlayedFolder(path);
                return;
            }

            ViewModel.LoadFolder(path);
            await ViewModel.FolderTree.NavigateToPathAsync(path);
            await Dispatcher.BeginInvoke(new Action(() => ScrollTreeToFolder(path)),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Scrolls the folder tree so the node for <paramref name="path"/> is visible,
    /// realizing virtualized containers along the way.
    /// </summary>
    private void ScrollTreeToFolder(string path, int attempt = 0)
    {
        var node = ViewModel.FolderTree.FindNode(path);
        if (node == null) return;

        // Build the root -> target chain using parent links.
        var chain = new List<FolderNode>();
        for (var n = node; n != null; n = n.Parent)
            chain.Add(n);
        chain.Reverse();

        ItemsControl? parent = FolderTreeView;
        TreeViewItem? tvi = null;

        foreach (var n in chain)
        {
            if (parent == null) return;

            tvi = parent.ItemContainerGenerator.ContainerFromItem(n) as TreeViewItem;
            if (tvi == null)
            {
                parent.UpdateLayout();
                tvi = parent.ItemContainerGenerator.ContainerFromItem(n) as TreeViewItem;
            }
            if (tvi == null)
            {
                // Container not realized yet (virtualization). Ancestors expanded above
                // are now closer to the target, so retry on a later layout pass.
                if (attempt < 5)
                    Dispatcher.BeginInvoke(new Action(() => ScrollTreeToFolder(path, attempt + 1)),
                        System.Windows.Threading.DispatcherPriority.Background);
                return;
            }

            if (n != node)
            {
                tvi.IsExpanded = true;
                tvi.BringIntoView();
                tvi.UpdateLayout();
            }
            parent = tvi;
        }

        if (tvi == null) return;
        tvi.UpdateLayout();

        var sv = FindScrollViewer(FolderTreeView);
        if (sv == null)
        {
            tvi.BringIntoView();
            return;
        }

        // Centre the target row in the viewport.
        var offset = tvi.TransformToAncestor(sv).Transform(new Point(0, 0)).Y;
        var rowHeight = tvi.ActualHeight;
        var target = sv.VerticalOffset + offset - (sv.ViewportHeight - rowHeight) / 2;
        sv.ScrollToVerticalOffset(Math.Max(0, target));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    private void RemoveRecentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string path)
        {
            ViewModel.RemoveRecentPlayedFolder(path);
        }
    }

    private void MusicFilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedFile != null)
        {
            ViewModel.PlayFileCommand.Execute(ViewModel.SelectedFile);
        }
    }

    private void CoverHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (AlbumCoverImage.Source == null) return;

        // Let clicks on a data row do normal grid selection; only the bare cover toggles zoom.
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) != null) return;

        var p = e.GetPosition(AlbumCoverImage);
        if (p.X < 0 || p.Y < 0 || p.X > AlbumCoverImage.ActualWidth || p.Y > AlbumCoverImage.ActualHeight)
            return;

        CoverZoomOverlay.Visibility = Visibility.Visible;
    }

    private void CoverZoomOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => CoverZoomOverlay.Visibility = Visibility.Collapsed;

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
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

    private bool _draggingVolume;

    private void VolumeBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ProgressBar progressBar)
        {
            _draggingVolume = true;
            progressBar.CaptureMouse();
            SetVolumeFromMouse(progressBar, e.GetPosition(progressBar));
        }
    }

    private void VolumeBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingVolume && sender is System.Windows.Controls.ProgressBar progressBar)
            SetVolumeFromMouse(progressBar, e.GetPosition(progressBar));
    }

    private void VolumeBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ProgressBar progressBar)
        {
            _draggingVolume = false;
            progressBar.ReleaseMouseCapture();
        }
    }

    // ponytail: Volume setter writes settings.json each call, so a drag saves per move.
    // Tiny file; debounce only if it ever shows up as lag.
    private void SetVolumeFromMouse(System.Windows.Controls.ProgressBar bar, Point pos)
    {
        if (bar.ActualWidth <= 0) return;
        var percentage = Math.Max(0, Math.Min(1, pos.X / bar.ActualWidth));
        ViewModel.Volume = (int)(percentage * 100);
    }

    #region Folder Context Menu

    private void FolderContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        // Don't overwrite _rightClickedFolder - it was set in the right-click handler
        BuildRecentFoldersMenu(FolderCopyToMenu, ViewModel.RecentFolders.CopyFolders, FolderCopyToRecent_Click);
        BuildRecentFoldersMenu(FolderMoveToMenu, ViewModel.RecentFolders.MoveFolders, FolderMoveToRecent_Click);
        BuildAddToPlaylistMenu(FolderAddToPlaylistMenu, playlist =>
        {
            if (_rightClickedFolder != null)
                _ = ViewModel.AddFolderToPlaylistAsync(playlist.Id, _rightClickedFolder.FullPath);
        });
    }

    private void FolderRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_rightClickedFolder == null) return;

        _rightClickedFolder.Refresh();

        // Reload music files if this folder is currently selected
        if (_rightClickedFolder.FullPath.Equals(ViewModel.CurrentFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            ViewModel.LoadFolder(_rightClickedFolder.FullPath);
        }
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
        BuildAddToPlaylistMenu(FileAddToPlaylistMenu, playlist =>
        {
            var files = GetSelectedFiles();
            if (files.Count > 0)
                ViewModel.AddFilesToPlaylist(playlist.Id, files.Select(f => f.FullPath));
        });
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

    #region Playlists

    private void NewPlaylist_Click(object sender, RoutedEventArgs e) => PromptCreatePlaylist();

    private void NewPlaylistFromSelection_Click(object sender, RoutedEventArgs e)
    {
        var files = GetSelectedFiles();
        if (files.Count == 0) return;

        var playlist = PromptCreatePlaylist();
        if (playlist != null)
            ViewModel.AddFilesToPlaylist(playlist.Id, files.Select(f => f.FullPath));
    }

    private void PlaylistListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistListBox.SelectedItem is Playlist p && p.Id != ViewModel.CurrentPlaylistId)
            ViewModel.LoadPlaylist(p);
    }

    private void PlaylistItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
            _rightClickedPlaylist = item.DataContext as Playlist;
    }

    private void PlaylistRename_Click(object sender, RoutedEventArgs e)
    {
        if (_rightClickedPlaylist == null) return;

        var dialog = new TextInputDialog("Rename Playlist", "Playlist name:", _rightClickedPlaylist.Name)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
            ViewModel.RenamePlaylist(_rightClickedPlaylist, dialog.InputText);
    }

    private void PlaylistDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_rightClickedPlaylist == null) return;

        var result = MessageBox.Show(
            $"Delete playlist '{_rightClickedPlaylist.Name}'?\n\nThe music files themselves are not deleted.",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
            ViewModel.DeletePlaylist(_rightClickedPlaylist);
    }

    private void RemoveFromPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var files = GetSelectedFiles();
        if (files.Count > 0)
            ViewModel.RemoveFromCurrentPlaylist(files);
    }

    private void MoveEntryUp_Click(object sender, RoutedEventArgs e)
        => ViewModel.MoveEntry(ViewModel.SelectedFile, -1);

    private void MoveEntryDown_Click(object sender, RoutedEventArgs e)
        => ViewModel.MoveEntry(ViewModel.SelectedFile, +1);

    private Playlist? PromptCreatePlaylist()
    {
        var dialog = new TextInputDialog("New Playlist", "Playlist name:", "New Playlist")
        {
            Owner = this
        };
        return dialog.ShowDialog() == true ? ViewModel.CreatePlaylist(dialog.InputText) : null;
    }

    private void BuildAddToPlaylistMenu(MenuItem parentMenu, Action<Playlist> onPlaylistChosen)
    {
        parentMenu.Items.Clear();

        if (ViewModel.Playlists.Count == 0)
        {
            parentMenu.Items.Add(new MenuItem { Header = "(no playlists)", IsEnabled = false });
        }
        else
        {
            foreach (var playlist in ViewModel.Playlists)
            {
                var item = new MenuItem { Header = playlist.Name, Tag = playlist };
                item.Click += (s, _) =>
                {
                    if (s is MenuItem mi && mi.Tag is Playlist p)
                        onPlaylistChosen(p);
                };
                parentMenu.Items.Add(item);
            }
        }

        parentMenu.Items.Add(new Separator());

        var newItem = new MenuItem { Header = "New Playlist..." };
        newItem.Click += (_, _) =>
        {
            var created = PromptCreatePlaylist();
            if (created != null)
                onPlaylistChosen(created);
        };
        parentMenu.Items.Add(newItem);
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
