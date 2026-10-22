using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using MusicOrganiser.Dialogs;
using MusicOrganiser.Models;
using MusicOrganiser.Services;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser;

/// <summary>One checkable row in the folder-tree tag filter popup.</summary>
public class TagFilterOption : INotifyPropertyChanged
{
    public string Name { get; }
    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }
    public TagFilterOption(string name, bool isChecked = false)
    {
        Name = name;
        _isChecked = isChecked;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private FolderNode? _rightClickedFolder;
    private Playlist? _rightClickedPlaylist;

    // Folder-tree tag filter: the checked tag names, kept across popup re-opens (the popup's
    // CheckBox list itself is rebuilt every open to pick up newly-saved tags).
    private readonly HashSet<string> _selectedFolderTagFilters = new(StringComparer.OrdinalIgnoreCase);

    // Lyrics row-list auto-scroll (follows the playhead; see AutoScrollLyrics below).
    private ScrollViewer? _lyricsScrollViewer;
    private bool _lyricsProgrammaticScroll;
    private DateTime _lastManualLyricsScrollUtc = DateTime.MinValue;
    // ponytail: how long a manual scroll suspends auto-scroll for — the knob.
    private static readonly TimeSpan LyricsAutoScrollResumeDelay = TimeSpan.FromSeconds(5);

    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainViewModel();
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        // Waveform bar pulls live levels / the audible playhead itself each rendered frame.
        SeekBar.LiveSource = () => viewModel.AudioPlayer.GetLiveLevels();
        SeekBar.PositionSource = () =>
        {
            double total = viewModel.AudioPlayer.TotalDuration.TotalSeconds;
            return total > 0 ? viewModel.AudioPlayer.AudiblePosition.TotalSeconds / total * 100 : null;
        };
        viewModel.MusicFiles.CollectionChanged += (s, e) =>
            Dispatcher.BeginInvoke(new Action(UpdateAlbumCoverPosition),
                System.Windows.Threading.DispatcherPriority.Background);
        // Startup: bring the re-selected last-played row into view once the grid has filled.
        viewModel.SelectionRestored += CentreGridOn;
        Closed += (s, e) => ViewModel.Dispose();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Restore the folder that was open when the app last closed.
        await ViewModel.RestoreLastFolderAsync();

        // Scroll the tree to reveal the restored folder.
        if (!string.IsNullOrEmpty(ViewModel.CurrentFolderPath))
        {
            var path = ViewModel.CurrentFolderPath;
            await Dispatcher.BeginInvoke(new Action(() => ScrollTreeToFolder(path)),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    // One expander column (~8px) of horizontal indent per tree level; used to align the parent
    // of the selected node to the viewport's left edge.
    private const double TreeIndentPerLevel = 8;

    // The CoreAudio device-change callback misses some Bluetooth connects, so re-enumerate
    // whenever the user actually opens the picker — the list is then always current.
    private void OutputDeviceBox_DropDownOpened(object sender, EventArgs e)
    {
        ViewModel.RefreshOutputDevices();
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.FolderTree.FilterText = string.Empty;
        FilterBox.Focus();
    }

    // Selects the parent of the currently selected folder (root-level: just collapses it).
    // Selecting the parent auto-expands it (FolderNode.IsSelected), so it's already expanded
    // here (its child was visible) and UnfoldedBySelection comes out false — the band-folder
    // auto-descend (JumpIntoFirstAlbumAsync) only fires on an actual unfold, so it won't loop
    // straight back into the folder we just left.
    private void UpLevel_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTreeView.SelectedItem is not FolderNode current) return;

        var parent = current.Parent;
        if (parent == null)
        {
            current.IsExpanded = false;
            return;
        }

        parent.IsSelected = true;
        parent.IsExpanded = false; // fold the level we were browsing
        Dispatcher.BeginInvoke(new Action(() => ScrollTreeToFolder(parent.FullPath)),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    // Re-reads the tag vocabulary every open so newly-saved tags show up, preserving
    // previously checked selections.
    private void TagsFilterPopup_Opened(object sender, EventArgs e)
    {
        var allTags = DatabaseService.Instance.GetAllTagNames();
        var options = allTags
            .Select(t => new TagFilterOption(t, _selectedFolderTagFilters.Contains(t)))
            .ToList();
        TagsFilterItemsControl.ItemsSource = options;
    }

    private void TagFilterCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: TagFilterOption opt })
        {
            if (opt.IsChecked) _selectedFolderTagFilters.Add(opt.Name);
            else _selectedFolderTagFilters.Remove(opt.Name);
        }
        ApplyFolderTagFilter();
    }

    private void TagsFilterClear_Click(object sender, RoutedEventArgs e)
    {
        _selectedFolderTagFilters.Clear();
        if (TagsFilterItemsControl.ItemsSource is IEnumerable<TagFilterOption> options)
        {
            foreach (var o in options) o.IsChecked = false;
        }
        ApplyFolderTagFilter();
        TagsFilterPopup.IsOpen = false;
    }

    private void ApplyFolderTagFilter()
    {
        ViewModel.FolderTree.SelectedTags = _selectedFolderTagFilters.ToList();
        TagsFilterToggle.Content = _selectedFolderTagFilters.Count == 0
            ? "Tags"
            : $"Tags ({_selectedFolderTagFilters.Count})";
    }

    // Folder-tree rating filter: fixed checkbox list (Any/1-5), so unlike Tags there's nothing
    // to rebuild on popup open — Click just toggles the set and re-applies.
    private readonly HashSet<int> _selectedFolderRatingFilters = new();

    private void FolderRatingFilterCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string tag } cb && int.TryParse(tag, out var rating))
        {
            if (cb.IsChecked == true) _selectedFolderRatingFilters.Add(rating);
            else _selectedFolderRatingFilters.Remove(rating);
        }
        ApplyFolderRatingFilter();
    }

    private void FolderRatingFilterClear_Click(object sender, RoutedEventArgs e)
    {
        _selectedFolderRatingFilters.Clear();
        foreach (var cb in FolderRatingFilterPanel.Children.OfType<CheckBox>()) cb.IsChecked = false;
        ApplyFolderRatingFilter();
        FolderRatingFilterToggle.IsChecked = false;
    }

    private void ApplyFolderRatingFilter()
    {
        ViewModel.FolderTree.SelectedRatings = _selectedFolderRatingFilters.ToList();
        FolderRatingFilterToggle.Content = _selectedFolderRatingFilters.Count == 0
            ? "Rating"
            : $"Rating ({_selectedFolderRatingFilters.Count})";
    }

    // Files-grid rating filter — same fixed checkbox list, filtering MainViewModel.MusicFilesView.
    private void FileRatingFilterCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string tag } && int.TryParse(tag, out var rating))
            ViewModel.ToggleFileRatingFilter(rating);
        UpdateFileRatingFilterToggleLabel();
    }

    private void FileRatingFilterClear_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearFileRatingFilter();
        foreach (var cb in FileRatingFilterPanel.Children.OfType<CheckBox>()) cb.IsChecked = false;
        UpdateFileRatingFilterToggleLabel();
        FileRatingFilterToggle.IsChecked = false;
    }

    private void UpdateFileRatingFilterToggleLabel()
    {
        var n = ViewModel.SelectedFileRatings.Count;
        FileRatingFilterToggle.Content = n == 0 ? "Rating" : $"Rating ({n})";
    }

    private void ClearFileFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.FileFilterText = string.Empty;
        FileFilterBox.Focus();
    }

    private async void ManageTags_Click(object sender, RoutedEventArgs e)
    {
        TagsFilterPopup.IsOpen = false;

        var dlg = new TagManagerDialog { Owner = this };
        dlg.ShowDialog();
        if (!dlg.Changed) return;

        // A deleted/merged-away tag can no longer be selected in the filter.
        var validTags = new HashSet<string>(DatabaseService.Instance.GetAllTagNames(), StringComparer.OrdinalIgnoreCase);
        _selectedFolderTagFilters.RemoveWhere(t => !validTags.Contains(t));
        ApplyFolderTagFilter();

        // Loaded tree nodes' Tags strings are stale, and even an unchanged selection can gain
        // matches (a merge can move folders onto an already-selected tag) — force a full refresh.
        await ViewModel.FolderTree.RefreshAllFolderTagsAsync();
    }

    private void CoverHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateAlbumCoverPosition();

    private void AlbumCoverImage_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateAlbumCoverPosition();

    private ImageSource? _previousCover;

    /// <summary>Crossfades cover changes: the outgoing image is parked in the ghost and faded
    /// out while the new bound image fades in.</summary>
    private void AlbumCoverImage_TargetUpdated(object sender, DataTransferEventArgs e)
    {
        var incoming = AlbumCoverImage.Source;
        var outgoing = _previousCover;
        _previousCover = incoming;

        if (incoming == null)
        {
            AlbumCoverGhost.Source = null;
            AlbumCoverGhost.BeginAnimation(OpacityProperty, null);
            AlbumCoverGhost.Opacity = 0;
            return;
        }

        var dur = new Duration(TimeSpan.FromMilliseconds(1200));

        if (outgoing != null)
        {
            AlbumCoverGhost.Source = outgoing;
            AlbumCoverGhostOffset.Y = AlbumCoverImageOffset.Y;   // park the ghost where the outgoing cover sat
            AlbumCoverGhost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, dur));
        }

        AlbumCoverImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
    }

    /// <summary>
    /// Keeps the album cover vertically centred, but shifts it down so the file
    /// rows don't overlap it while there is still room beneath. Once the image
    /// reaches the bottom of the area, it stops and the semi-transparent rows
    /// overlay it instead. The shift is applied as a render transform, not a
    /// Margin, so it doesn't affect layout.
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

        // ponytail: must stay a RenderTransform, not a Margin — Margin feeds back into the
        // Uniform-stretch measure and shrinks the image on every pass.
        if (Math.Abs(AlbumCoverImageOffset.Y - top) > 0.5)
            AlbumCoverImageOffset.Y = top;
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
        // Playback moved to another track (Next, auto-advance, shuffle, the phone remote):
        // PlayFile already made it the selection — put that row in the middle of the list so a
        // shuffle jump into a long folder doesn't leave you hunting for what's playing.
        if (e.PropertyName == nameof(MainViewModel.NowPlaying) && ViewModel.NowPlaying is { } playing)
            CentreGridOn(playing);

        // Now-playing row highlight updates automatically via the NowPlayingConverter
        // MultiBinding (it watches DataContext.NowPlaying). Do NOT call Items.Refresh()
        // here: it regenerates every row container and wipes the StarRating cell display.

        if (e.PropertyName == nameof(MainViewModel.CurrentLyricIndex) || e.PropertyName == nameof(MainViewModel.KaraokeMode))
        {
            // Deferred to Background priority: the karaoke view may have just become visible
            // (KaraokeMode flip) or KaraokeWindow may have just been rebuilt for a new track —
            // either way the item containers PanKaraokeToCurrentLine measures need a layout
            // pass first (mirrors UpdateAlbumCoverPosition's own deferred BeginInvoke).
            Dispatcher.BeginInvoke(new Action(PanKaraokeToCurrentLine),
                System.Windows.Threading.DispatcherPriority.Background);

            // Karaoke mode swaps the row list out for the big view — re-check the playhead's own
            // visibility immediately rather than waiting for the next CurrentPosition tick (which
            // only fires while playing; toggling Karaoke while paused would otherwise leave a
            // stale line drawn over the big view).
            if (e.PropertyName == nameof(MainViewModel.KaraokeMode))
                UpdateLyricsPlayhead();
        }

        // Lyrics row list follows the playhead too (see AutoScrollLyrics) — unlike the earlier
        // per-line attempt this scrolls continuously with elapsed time (matching the
        // time-proportional row layout) rather than snapping on every CurrentLyricIndex tick,
        // and backs off for a few seconds after the user scrolls manually.
        if (e.PropertyName == nameof(MainViewModel.CurrentPosition))
        {
            AutoScrollLyrics();
            UpdateLyricsPlayhead();
            RefreshLyricsFollowState();
        }
    }

    // Auto-scroll is suspended — INDEFINITELY, no timeout — while a row is selected: the whole
    // point of selecting a line is to wait, possibly 30s+, for the vocal to come around before
    // tapping ⏺, and a timeout would yank the view away exactly then. Hovering the list also
    // suspends it (stops the target moving while the user's reaching for a row's buttons, even
    // before they click), and a recent manual scroll suspends it for the existing short window.
    private bool IsLyricsAutoScrollSuspended()
    {
        if (LyricsListBox.SelectedItem != null) return true;
        if (LyricsListBox.IsMouseOver) return true;
        return DateTime.UtcNow - _lastManualLyricsScrollUtc < LyricsAutoScrollResumeDelay;
    }

    // Keeps the lyrics row list scrolled so "now" sits vertically centered, using the same
    // content-Y-per-second as the row layout itself (RecomputeLyricRowGaps): since gaps have the
    // row height already subtracted out, content Y for time T is exactly
    // T * MainViewModel.LyricRowPxPerSecond. Skips while the panel's hidden, lyrics aren't
    // synced, playback is paused, or the user is working the list (IsLyricsAutoScrollSuspended).
    private void AutoScrollLyrics()
    {
        if (!ViewModel.ShowLyrics || !ViewModel.HasSyncedLyrics || !ViewModel.IsPlaying) return;
        if (IsLyricsAutoScrollSuspended()) return;
        ScrollLyricsToPlayhead();
    }

    private void ScrollLyricsToPlayhead()
    {
        _lyricsScrollViewer ??= FindScrollViewer(LyricsListBox);
        if (_lyricsScrollViewer == null) return;

        var targetOffset = Math.Max(0,
            ViewModel.CurrentPosition.TotalSeconds * MainViewModel.LyricRowPxPerSecond
            - _lyricsScrollViewer.ViewportHeight / 2.0);

        _lyricsProgrammaticScroll = true;
        _lyricsScrollViewer.ScrollToVerticalOffset(targetOffset);
        // ScrollChanged fires on the next layout pass, not synchronously — clear the flag after
        // one, mirroring the deferred-BeginInvoke idiom used elsewhere in this file.
        Dispatcher.BeginInvoke(new Action(() => _lyricsProgrammaticScroll = false),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void LyricsListBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_lyricsProgrammaticScroll)
            _lastManualLyricsScrollUtc = DateTime.UtcNow; // user-initiated — suspend auto-scroll briefly
        UpdateLyricsPlayhead(); // keep the line correct while the user drags the scrollbar/wheels
        RefreshLyricsFollowState();
    }

    private void LyricsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RefreshLyricsFollowState();

    // A Button's own MouseLeftButtonDown handling stops it bubbling to the ListBoxItem's usual
    // click-to-select logic, so the ⏺/−/+ row buttons wouldn't otherwise select their row — hook
    // the (always-bubbling) Click event at the ListBox instead and select explicitly, so the
    // freeze engages on the very first tap rather than needing a separate click on the row text.
    private void LyricsRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: LyricLine line })
            LyricsListBox.SelectedItem = line;
    }

    // Esc is the discoverable way out of an indefinite selection freeze without knowing about the
    // Follow button.
    private void LyricsListBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        ReleaseLyricsAutoScrollFreeze();
        e.Handled = true;
    }

    private void FollowLyricsButton_Click(object sender, RoutedEventArgs e) => ReleaseLyricsAutoScrollFreeze();

    // Clears the selection freeze and snaps the list back to the playhead immediately — even
    // while paused, where AutoScrollLyrics itself won't run (nothing to continuously follow) but
    // the user still expects the view to jump back on demand.
    private void ReleaseLyricsAutoScrollFreeze()
    {
        LyricsListBox.SelectedItem = null;
        if (ViewModel.ShowLyrics && ViewModel.HasSyncedLyrics)
            ScrollLyricsToPlayhead();
        UpdateLyricsPlayhead();
        RefreshLyricsFollowState();
    }

    // Pushes the current freeze state to the VM so the header's "Follow" toggle button can bind
    // to it with the same DataTrigger idiom as the Karaoke toggle (see MainWindow.xaml).
    private void RefreshLyricsFollowState() => ViewModel.LyricsFollowing = !IsLyricsAutoScrollSuspended();

    // Positions LyricsPlayheadLine at the current time's content-Y minus however far the list is
    // actually scrolled — NOT just pinned to the viewport centre, since that's only right while
    // AutoScrollLyrics is actively driving the scroll. Reading the ScrollViewer's real
    // VerticalOffset instead keeps it correct after a manual scroll or while paused. Content-Y
    // uses the same MainViewModel.LyricRowPxPerSecond scale as RecomputeLyricRowGaps.
    private void UpdateLyricsPlayhead()
    {
        if (!ViewModel.ShowLyrics || !ViewModel.HasSyncedLyrics || ViewModel.KaraokeMode)
        {
            LyricsPlayheadLine.Visibility = Visibility.Collapsed;
            return;
        }

        _lyricsScrollViewer ??= FindScrollViewer(LyricsListBox);
        if (_lyricsScrollViewer == null)
        {
            LyricsPlayheadLine.Visibility = Visibility.Collapsed;
            return;
        }

        var playheadY = ViewModel.CurrentPosition.TotalSeconds * MainViewModel.LyricRowPxPerSecond
            - _lyricsScrollViewer.VerticalOffset;

        if (playheadY < 0 || playheadY > _lyricsScrollViewer.ViewportHeight)
        {
            LyricsPlayheadLine.Visibility = Visibility.Collapsed;
            return;
        }

        LyricsPlayheadTransform.Y = playheadY;
        LyricsPlayheadLine.Visibility = Visibility.Visible;
    }

    // Keeps the karaoke big view's current line vertically centered by animating a
    // TranslateTransform on the (structurally stable — see KaraokeWindow's own comment)
    // ItemsControl, so the song appears to glide smoothly past rather than swapping instantly.
    private void PanKaraokeToCurrentLine()
    {
        var idx = ViewModel.CurrentLyricIndex;
        if (idx < 0 || idx >= KaraokeItemsControl.Items.Count) return;
        if (KaraokeItemsControl.ItemContainerGenerator.ContainerFromIndex(idx) is not FrameworkElement row) return;

        double rowCenter;
        try
        {
            rowCenter = row.TransformToAncestor(KaraokeViewport).Transform(new Point(0, row.ActualHeight / 2)).Y;
        }
        catch (InvalidOperationException)
        {
            return; // not yet in the visual tree / no common ancestor — try again next change
        }
        var viewportCenter = KaraokeViewport.ActualHeight / 2;

        // rowCenter already reflects whatever pan offset is currently applied (TransformToAncestor
        // walks the live render transform), so the delta needed is purely how far off-center it
        // still is — add that to the CURRENT transform value, don't replace it.
        var targetY = KaraokePanTransform.Y + (viewportCenter - rowCenter);

        var animation = new DoubleAnimation(KaraokePanTransform.Y, targetY, new Duration(TimeSpan.FromMilliseconds(350)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        KaraokePanTransform.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    private void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode folder)
        {
            ViewModel.LoadFolder(folder.FullPath);
            // Reclaim horizontal space: scroll so the selected node's parent sits at the left edge.
            Dispatcher.BeginInvoke(new Action(AlignSelectedHorizontally),
                System.Windows.Threading.DispatcherPriority.Background);
            _ = JumpIntoFirstAlbumAsync(folder);
        }
    }

    // Band folder = holds only album subfolders (no tracks of its own, nothing nested deeper).
    // Selecting one drops straight into the first album so there's no dead click — but only on
    // the unfold; re-selecting an already-open folder leaves the selection alone.
    private async Task JumpIntoFirstAlbumAsync(FolderNode folder)
    {
        if (!folder.UnfoldedBySelection) return;
        await folder.EnsureLoadedAsync();
        await folder.EnsureRefreshedAsync();
        if (!folder.IsSelected) return; // selection moved on while loading

        var children = folder.Children.Where(c => !string.IsNullOrEmpty(c.FullPath)).ToList();
        if (children.Count == 0) return;

        var isBandFolder = await Task.Run(() =>
        {
            try
            {
                if (Services.MusicMetadataService.EnumerateSupportedFiles(folder.FullPath).Any())
                    return false;
                return children.All(c =>
                {
                    try { return !Directory.EnumerateDirectories(c.FullPath).Any(); }
                    catch { return true; }
                });
            }
            catch { return false; }
        });

        if (isBandFolder && folder.IsSelected)
            children[0].IsSelected = true;
    }

    // Scrolls the tree horizontally so the selected node's parent (one level up) aligns to the
    // left edge — the deep indent otherwise pushes the current folder off to the right.
    private void AlignSelectedHorizontally()
    {
        var sv = FindScrollViewer(FolderTreeView);
        if (sv == null) return;
        var tvi = FindSelectedContainer(FolderTreeView);
        if (tvi == null) return;
        var x = tvi.TransformToAncestor(sv).Transform(new Point(0, 0)).X;
        sv.ScrollToHorizontalOffset(Math.Max(0, sv.HorizontalOffset + x - TreeIndentPerLevel));
    }

    // Finds the realized, currently-selected TreeViewItem (only realized containers exist under
    // virtualization; the selected one is on-screen).
    private static TreeViewItem? FindSelectedContainer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TreeViewItem { IsSelected: true } sel) return sel;
            var found = FindSelectedContainer(child);
            if (found != null) return found;
        }
        return null;
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
                // Virtualized panel: containers for off-screen items are never created by
                // layout passes alone — force-realize the target by index.
                var index = parent.Items.IndexOf(n);
                if (index >= 0 && FindVisualChild<VirtualizingPanel>(parent) is { } itemsHost)
                {
                    itemsHost.BringIndexIntoViewPublic(index);
                    parent.UpdateLayout();
                    tvi = parent.ItemContainerGenerator.ContainerFromItem(n) as TreeViewItem;
                }
            }
            if (tvi == null)
            {
                // Container not realized yet. The folder tree loads async (cache + disk
                // refresh), so keep retrying on later layout passes until the whole chain
                // materializes. ~60 Background passes ≈ a few seconds — enough for a slow load.
                if (attempt < 60)
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

        // Align the target's parent to the left edge so the deep indent doesn't hide it off-right.
        var x = tvi.TransformToAncestor(sv).Transform(new Point(0, 0)).X;
        sv.ScrollToHorizontalOffset(Math.Max(0, sv.HorizontalOffset + x - TreeIndentPerLevel));
    }

    /// <summary>Scrolls the file grid so <paramref name="file"/> sits in the middle of the viewport.
    /// Deferred to Background priority: the row it centres on may have only just been added by the
    /// folder load, and the offsets below need a layout pass first.</summary>
    private void CentreGridOn(MusicFile file) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var index = MusicFilesGrid.Items.IndexOf(file);
            if (index < 0) return;

            MusicFilesGrid.ScrollIntoView(file);    // realizes the row and guarantees visibility

            // ponytail: assumes the DataGrid's default item-based scrolling (offsets and
            // ViewportHeight counted in rows, not pixels) — true while RowHeight is fixed and
            // virtualization is left alone. Switch to pixel maths if ScrollUnit ever becomes Pixel.
            var sv = FindScrollViewer(MusicFilesGrid);
            if (sv == null || sv.ViewportHeight <= 0 || double.IsInfinity(sv.ViewportHeight)) return;
            sv.ScrollToVerticalOffset(Math.Max(0, index - (sv.ViewportHeight - 1) / 2));
        }), System.Windows.Threading.DispatcherPriority.Background);

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

    // Depth-first, so for an ItemsControl this finds its own items host before any
    // panel belonging to a nested (expanded) child item.
    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (FindVisualChild<T>(child) is { } found) return found;
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

    private void ClearRecentFolders_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearRecentPlayedFolders();
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

    // Wheel anywhere over the player panel adjusts the in-app volume (nothing in the
    // panel scrolls, so the wheel is free for this).
    private void PlayerPanel_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ViewModel.Volume = Math.Max(0, Math.Min(100, ViewModel.Volume + (e.Delta > 0 ? 5 : -5)));
        e.Handled = true;
    }

    // ponytail: Volume setter writes settings.json each call, so a drag saves per move.
    // Tiny file; debounce only if it ever shows up as lag.
    private void SetVolumeFromMouse(System.Windows.Controls.ProgressBar bar, Point pos)
    {
        if (bar.ActualWidth <= 0) return;
        var percentage = Math.Max(0, Math.Min(1, pos.X / bar.ActualWidth));
        ViewModel.Volume = (int)(percentage * 100);
    }

    private bool _draggingSystemVolume;

    private void SystemVolumeBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ProgressBar progressBar)
        {
            _draggingSystemVolume = true;
            progressBar.CaptureMouse();
            SetSystemVolumeFromMouse(progressBar, e.GetPosition(progressBar));
        }
    }

    private void SystemVolumeBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingSystemVolume && sender is System.Windows.Controls.ProgressBar progressBar)
            SetSystemVolumeFromMouse(progressBar, e.GetPosition(progressBar));
    }

    private void SystemVolumeBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ProgressBar progressBar)
        {
            _draggingSystemVolume = false;
            progressBar.ReleaseMouseCapture();
        }
    }

    private void SetSystemVolumeFromMouse(System.Windows.Controls.ProgressBar bar, Point pos)
    {
        if (bar.ActualWidth <= 0) return;
        var percentage = Math.Max(0, Math.Min(1, pos.X / bar.ActualWidth));
        ViewModel.SystemVolume = (int)(percentage * 100);
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

    private void FolderPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_rightClickedFolder != null)
            ViewModel.PlayFolderCommand.Execute(_rightClickedFolder);
    }

    private void FolderNewSubfolder_Click(object sender, RoutedEventArgs e)
    {
        if (_rightClickedFolder == null) return;

        var dlg = new TextInputDialog("New Subfolder", "Folder name:", "New Folder") { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var name = dlg.InputText;   // already trimmed
        if (name.Length == 0) return;

        try
        {
            Directory.CreateDirectory(Path.Combine(_rightClickedFolder.FullPath, name));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to create folder: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Expand first: Refresh() only reloads children when the node is open.
        _rightClickedFolder.IsExpanded = true;
        _rightClickedFolder.Refresh();
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

    private void FolderTag_Click(object sender, RoutedEventArgs e)
    {
        if (_rightClickedFolder == null) return;
        var folder = _rightClickedFolder;

        List<string> albums;
        try
        {
            albums = Directory.GetDirectories(folder.FullPath)
                .Select(d => Path.GetFileName(d) ?? string.Empty)
                .Where(n => n.Length > 0)
                .ToList();
        }
        catch
        {
            albums = new List<string>();
        }

        var dlg = new FolderTagDialog(folder.Name, folder.Tags,
            () => ViewModel.SuggestFolderTagsAsync(folder.Name, albums))
        {
            Owner = this
        };

        if (dlg.ShowDialog() == true)
        {
            folder.Tags = dlg.ResultTags;
        }
    }

    private void BulkTag_Click(object sender, RoutedEventArgs e)
    {
        if (_rightClickedFolder == null) return;
        var folder = _rightClickedFolder;

        var dlg = new BulkTagDialog(folder.FullPath,
            (band, albums) => ViewModel.SuggestFolderTagsAsync(band, albums),
            (path, tags) =>
            {
                // Reflect a saved row in an already-loaded tree node immediately (no-op for
                // unloaded ones) — fires on every Apply, not just when the dialog closes.
                var child = folder.Children.FirstOrDefault(c =>
                    c.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (child != null) child.Tags = tags;
            })
        {
            Owner = this
        };

        dlg.ShowDialog();
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
            await MoveFolderWithDuplicateCheck(_rightClickedFolder, folder);
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
            var success = await ViewModel.RunWithBusyOverlayAsync("Deleting folder...",
                () => ViewModel.FileOperations.DeleteFolderAsync(folderPath));
            if (success)
            {
                _rightClickedFolder.MarkAsDeleted();
                ViewModel.RemoveRecentPlayedFolderTree(folderPath);
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
            await MoveFolderWithDuplicateCheck(_rightClickedFolder, folder);
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
                // VM-level delete: drops the row and hands playback to the next track if this
                // one was playing (a folder reload here would rebuild the queue instead).
                await ViewModel.DeleteFileAsync(file.FullPath);
            }
        }
    }

    private async void FileConvertMp3_Click(object sender, RoutedEventArgs e)
    {
        var selectedFiles = GetSelectedFiles();
        var errors = new List<string>();
        foreach (var file in selectedFiles)
        {
            try { await ViewModel.ConvertToMp3Async(file.FullPath); }
            catch (Exception ex) { errors.Add($"{file.FileName}: {ex.Message}"); }
        }
        if (errors.Count > 0)
            MessageBox.Show(string.Join("\n", errors), "Convert to MP3", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void FileSplitCue_Click(object sender, RoutedEventArgs e)
    {
        var selectedFiles = GetSelectedFiles();
        if (selectedFiles.Count != 1)
        {
            MessageBox.Show("Select a single audio file to split.", "Split by .cue",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var path = selectedFiles[0].FullPath;
        var cue = await Task.Run(() => AudioToolsService.FindCue(path));
        if (cue == null)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Choose the .cue sheet",
                Filter = "Cue sheets (*.cue)|*.cue",
                InitialDirectory = Path.GetDirectoryName(path)
            };
            if (dlg.ShowDialog(this) != true) return;
            cue = dlg.FileName;
        }
        try
        {
            var created = await ViewModel.SplitByCueAsync(path, cue);
            MessageBox.Show($"Created {created.Count} FLAC file(s).", "Split by .cue",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Split by .cue", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private System.Threading.Tasks.Task CopySelectedFiles(string destinationFolder) => CopyFiles(GetSelectedFiles(), destinationFolder);
    private System.Threading.Tasks.Task MoveSelectedFiles(string destinationFolder) => MoveFiles(GetSelectedFiles(), destinationFolder);

    private async System.Threading.Tasks.Task CopyFiles(IList<MusicFile> selectedFiles, string destinationFolder)
    {
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

    private async System.Threading.Tasks.Task MoveFiles(IList<MusicFile> selectedFiles, string destinationFolder)
    {
        foreach (var file in selectedFiles)
        {
            // VM-level move: drops the row and hands playback to the next track if this one was
            // playing (a folder reload here would rebuild the queue instead).
            await ViewModel.MoveFileAsync(file.FullPath, destinationFolder);
        }
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
            await ViewModel.MoveFileAsync(file.FullPath, artistFolder);
        }
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
            await ViewModel.MoveFileAsync(file.FullPath, artistFolder);
        }
    }

    private List<MusicFile> GetSelectedFiles()
    {
        return MusicFilesGrid.SelectedItems.Cast<MusicFile>().ToList();
    }

    private void FileProperties_Click(object sender, RoutedEventArgs e)
    {
        // ponytail: multi-select shows the first file's properties; a per-file dialog each would be worse.
        var file = GetSelectedFiles().FirstOrDefault();
        if (file == null) return;
        new FilePropertiesDialog(file) { Owner = this }.ShowDialog();
    }

    // ponytail: one handler per menu, the MenuItem's Tag picks which part of the path.
    private void FileCopyText_Click(object sender, RoutedEventArgs e)
    {
        var files = GetSelectedFiles();
        if (files.Count == 0) return;

        var part = (sender as FrameworkElement)?.Tag as string;
        // Multi-select copies one path per line.
        CopyToClipboard(files.Select(f => PathPart(f.FullPath, part)));
    }

    private void FolderCopyText_Click(object sender, RoutedEventArgs e)
    {
        var node = _rightClickedFolder;
        if (node == null) return;

        var part = (sender as FrameworkElement)?.Tag as string;
        // Node.Name over Path.GetFileName: a drive root ("F:\\") has no file-name component.
        CopyToClipboard(new[] { part == "name" ? node.Name : PathPart(node.FullPath, part) });
    }

    private static string PathPart(string fullPath, string? part) => part switch
    {
        "name" => Path.GetFileName(fullPath),
        "dir" => Path.GetDirectoryName(fullPath) ?? fullPath,
        _ => fullPath,
    };

    private static void CopyToClipboard(IEnumerable<string> lines)
    {
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length == 0) return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)   // another app can hold the clipboard open
        {
            MessageBox.Show($"Couldn't copy to clipboard: {ex.Message}", "Copy",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
        await TransferFolderWithDuplicateCheck(sourcePath, destinationFolder, move: false);
    }

    // Folder move: shared dialog/transfer path, then reconcile the tree node with what is left on disk.
    private async System.Threading.Tasks.Task<bool> MoveFolderWithDuplicateCheck(FolderNode node, string destinationFolder)
    {
        var folderPath = node.FullPath;
        if (!await TransferFolderWithDuplicateCheck(folderPath, destinationFolder, move: true))
            return false;

        if (!Directory.Exists(folderPath))
        {
            node.MarkAsDeleted();
            // Clear music files if the moved folder was selected
            if (ViewModel.CurrentFolderPath.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase))
            {
                ViewModel.MusicFiles.Clear();
            }
        }
        else
        {
            node.Refresh(); // a merge left skipped duplicates behind
            // The grid still lists files that have moved away
            var current = ViewModel.CurrentFolderPath;
            if (SamePath(current, folderPath) || IsUnder(current, folderPath))
            {
                if (Directory.Exists(current)) ViewModel.LoadFolder(current);
                else ViewModel.MusicFiles.Clear();
            }
        }
        return true;
    }

    // Single entry point for every folder copy/move. No similar-named folder in the destination: plain
    // copy/move. Otherwise ask (merge / replace / keep both). Returns false on cancel, skip or failure.
    private async System.Threading.Tasks.Task<bool> TransferFolderWithDuplicateCheck(string sourcePath, string destinationFolder, bool move)
    {
        var candidates = ViewModel.FileOperations.FindSimilarFolders(sourcePath, destinationFolder);
        var failMessage = move ? "Failed to move folder." : "Failed to copy folder.";
        var busyText = move ? "Moving folder..." : "Copying folder...";
        bool success;

        if (candidates.Count == 0)
        {
            success = await ViewModel.RunWithBusyOverlayAsync(busyText, () => move
                ? ViewModel.FileOperations.MoveFolderAsync(sourcePath, destinationFolder)
                : ViewModel.FileOperations.CopyFolderAsync(sourcePath, destinationFolder));
            if (!success)
                MessageBox.Show(failMessage, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return success;
        }

        var dialog = new FolderDuplicateDialog(sourcePath, candidates, destinationFolder, move, ViewModel.FileOperations)
        {
            Owner = this
        };

        var chose = dialog.ShowDialog() == true;

        // Renames done inside the dialog happen on disk immediately, even if the dialog is then cancelled
        // (the folder tree nodes are left as they were: a renamed subfolder shows its old name until its parent is refreshed)
        if (dialog.RenamedAnything)
        {
            var current = ViewModel.CurrentFolderPath;
            if (!string.IsNullOrEmpty(current) &&
                (SamePath(current, sourcePath) || IsUnder(current, sourcePath) ||
                 candidates.Any(c => SamePath(current, c) || IsUnder(current, c))))
            {
                if (Directory.Exists(current)) ViewModel.LoadFolder(current);
                else ViewModel.MusicFiles.Clear();
            }
        }

        if (!chose)
        {
            return false; // Dialog was closed without a choice
        }

        var action = dialog.Result;
        if (action != FolderDuplicateAction.Custom && action != FolderDuplicateAction.KeepBoth && action != FolderDuplicateAction.IntoTarget)
        {
            return false;
        }

        success = await ViewModel.RunWithBusyOverlayAsync(busyText, () => action == FolderDuplicateAction.Custom
            ? ViewModel.FileOperations.ApplyFolderPlanAsync(sourcePath, dialog.TargetPath, move, dialog.Plan)
            : action == FolderDuplicateAction.IntoTarget
                // As a subfolder of the selected folder: Keep both with the target as the destination
                ? ViewModel.FileOperations.TransferFolderAsync(
                    sourcePath, dialog.TargetPath, move, FolderDuplicateAction.KeepBoth, null, dialog.NewName, null)
                : ViewModel.FileOperations.TransferFolderAsync(
                    sourcePath, destinationFolder, move, action, null, dialog.NewName, null));
        if (!success)
        {
            MessageBox.Show(failMessage, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        // The plan may have added, overwritten or deleted files in the target, so a grid showing it is stale
        if (action == FolderDuplicateAction.Custom)
        {
            var current = ViewModel.CurrentFolderPath;
            if (!string.IsNullOrEmpty(current) && (SamePath(current, dialog.TargetPath) || IsUnder(current, dialog.TargetPath)))
            {
                if (Directory.Exists(current)) ViewModel.LoadFolder(current);
                else ViewModel.MusicFiles.Clear();
            }
        }

        return true;
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

    #region Drag & drop (grid files / tree folders -> a tree folder)

    // ponytail: plain WPF DoDragDrop — no drag adorner, insertion line, or hover-to-auto-expand.
    // Move by default, Ctrl = copy (Explorer convention); the drop target is always a folder-tree
    // node, and both payloads land on the same Copy/Move helpers the context menus already use.
    private const string FilesDragFormat = "MusicOrganiser.Files";
    private const string FolderDragFormat = "MusicOrganiser.Folder";
    private Point _dragStart;

    private void DragSource_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _dragStart = e.GetPosition(null);

    private bool PastDragThreshold(MouseEventArgs e)
    {
        var now = e.GetPosition(null);
        return Math.Abs(now.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(now.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance;
    }

    private void MusicFilesGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !PastDragThreshold(e)) return;
        // Only rows drag: keeps the scrollbar, headers and empty space behaving normally.
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) == null) return;

        var files = GetSelectedFiles();
        if (files.Count == 0) return;

        DragDrop.DoDragDrop(MusicFilesGrid, new DataObject(FilesDragFormat, files),
            DragDropEffects.Move | DragDropEffects.Copy);
    }

    private void FolderTreeView_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !PastDragThreshold(e)) return;
        if (FindAncestor<ToggleButton>(e.OriginalSource as DependencyObject) != null) return;  // expander click

        var node = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as FolderNode;
        // Parent == null is a drive root (not movable); the Exists check rules out dummy/stale nodes.
        if (node?.Parent == null || !Directory.Exists(node.FullPath)) return;

        DragDrop.DoDragDrop(FolderTreeView, new DataObject(FolderDragFormat, node),
            DragDropEffects.Move | DragDropEffects.Copy);
    }

    /// <summary>The folder this drop would land in, or null when the drag can't be dropped here
    /// (unknown payload, missing folder, or a no-op/illegal folder move).</summary>
    private static FolderNode? DropTarget(object sender, DragEventArgs e)
    {
        if (sender is not TreeViewItem item || item.DataContext is not FolderNode target) return null;
        if (!Directory.Exists(target.FullPath)) return null;

        if (e.Data.GetDataPresent(FilesDragFormat))
        {
            var files = (IList<MusicFile>)e.Data.GetData(FilesDragFormat)!;
            // Files already living in the target folder have nowhere to go.
            return files.Any(f => !SamePath(Path.GetDirectoryName(f.FullPath), target.FullPath)) ? target : null;
        }

        if (e.Data.GetDataPresent(FolderDragFormat))
        {
            var src = (FolderNode)e.Data.GetData(FolderDragFormat)!;
            if (SamePath(src.FullPath, target.FullPath)) return null;                     // onto itself
            if (SamePath(Path.GetDirectoryName(src.FullPath), target.FullPath)) return null;  // already there
            if (IsUnder(target.FullPath, src.FullPath)) return null;                      // into its own subtree
            return target;
        }

        return null;
    }

    private static bool SamePath(string? a, string? b) => string.Equals(
        a?.TrimEnd(Path.DirectorySeparatorChar), b?.TrimEnd(Path.DirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string folder) =>
        path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static DragDropEffects EffectFor(DragEventArgs e) =>
        (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy : DragDropEffects.Move;

    private void FolderItem_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DropTarget(sender, e) == null ? DragDropEffects.None : EffectFor(e);
        e.Handled = true;   // innermost node wins; without this the drop lands on an ancestor too
    }

    private async void FolderItem_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var target = DropTarget(sender, e);
        if (target == null) return;

        var copy = EffectFor(e) == DragDropEffects.Copy;

        if (e.Data.GetDataPresent(FilesDragFormat))
        {
            var files = (IList<MusicFile>)e.Data.GetData(FilesDragFormat)!;
            if (copy) await CopyFiles(files, target.FullPath);
            else await MoveFiles(files, target.FullPath);
            return;
        }

        var src = (FolderNode)e.Data.GetData(FolderDragFormat)!;

        // A mis-drop relocates a whole tree, so folder moves ask first (file drops don't: the
        // grid reloads straight away and shows what happened).
        if (!copy && MessageBox.Show($"Move '{src.Name}' into '{target.Name}'?", "Confirm Move",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        if (copy)
        {
            await CopyFolderWithDuplicateCheck(src.FullPath, target.FullPath);
        }
        else if (!await MoveFolderWithDuplicateCheck(src, target.FullPath))
        {
            return;   // cancelled or failed (the helper already showed the error)
        }

        target.IsExpanded = true;   // Refresh only reloads children of an open node
        target.Refresh();
    }

    #endregion

    #region Now Playing file actions

    // Copy/Move/Delete acting on the *playing* file rather than the grid selection.
    // Copy/Move drop the same recent-folders + Browse... list the grid's context menu uses.
    private void DropdownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void NowPlayingCopy_Click(object sender, RoutedEventArgs e) =>
        ShowNowPlayingDestinationMenu(sender, ViewModel.RecentFolders.CopyFolders, async (file, folder) =>
        {
            ViewModel.RecentFolders.AddCopyFolder(folder);
            await CopyFiles(new[] { file }, folder);
        });

    private void NowPlayingMove_Click(object sender, RoutedEventArgs e) =>
        ShowNowPlayingDestinationMenu(sender, ViewModel.RecentFolders.MoveFolders, async (file, folder) =>
        {
            ViewModel.RecentFolders.AddMoveFolder(folder);
            await MoveFiles(new[] { file }, folder);
        });

    private async void NowPlayingDelete_Click(object sender, RoutedEventArgs e)
    {
        var file = ViewModel.NowPlaying;
        if (file == null) return;

        if (MessageBox.Show($"Are you sure you want to delete '{file.FileName}'?", "Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        // VM-level delete: releases the handle and continues with the next track in the queue.
        await ViewModel.DeleteFileAsync(file.FullPath);
    }

    private void ShowNowPlayingDestinationMenu(object sender, List<string> recentFolders,
        Func<MusicFile, string, System.Threading.Tasks.Task> action)
    {
        var file = ViewModel.NowPlaying;
        if (file == null || sender is not Button button) return;

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Top };
        foreach (var folder in recentFolders.Take(10).Where(Directory.Exists))
        {
            var item = new MenuItem { Header = folder };
            item.Click += async (_, _) => await action(file, folder);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());

        var browse = new MenuItem { Header = "Browse..." };
        browse.Click += async (_, _) =>
        {
            var folder = BrowseForFolder();
            if (folder != null) await action(file, folder);
        };
        menu.Items.Add(browse);
        menu.IsOpen = true;
    }

    #endregion
}
