using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MusicOrganiser.Models;
using MusicOrganiser.Services;

namespace MusicOrganiser.ViewModels;

public enum FolderSortOption
{
    NameAsc,
    NameDesc,
    CreationDateAsc,
    CreationDateDesc,
    ModifiedDateAsc,
    ModifiedDateDesc
}

public class FolderSortOptionItem
{
    public FolderSortOption Value { get; }
    public string DisplayName { get; }

    public FolderSortOptionItem(FolderSortOption value, string displayName)
    {
        Value = value;
        DisplayName = displayName;
    }

    public override string ToString() => DisplayName;
}

public class FolderNode : ViewModelBase
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isLoaded;
    private bool _isLoading;
    private TaskCompletionSource<bool>? _loadTcs;
    // The background filesystem-refresh pass kicked off by LoadChildrenAsync (see step 2).
    private Task? _refreshTask;
    private readonly bool _isDummy;
    private string _filterText = string.Empty;
    private FolderSortOption _sortOption = FolderSortOption.NameAsc;
    private bool _isDeleted;

    public string Name { get; }
    public string FullPath { get; }
    public FolderNode? Parent { get; set; }

    // User rating (1..5, null = unrated) and comma-separated tags, persisted to the cache.
    private bool _persistEnabled;
    private int? _rating;
    public int? Rating
    {
        get => _rating;
        set
        {
            if (SetProperty(ref _rating, value) && _persistEnabled)
                Persist(() => DatabaseService.Instance.SetFolderRating(FullPath, _rating));
        }
    }

    private string _tags = string.Empty;
    public string Tags
    {
        get => _tags;
        set
        {
            if (SetProperty(ref _tags, value ?? string.Empty) && _persistEnabled)
                Persist(() => DatabaseService.Instance.SetFolderTags(FullPath, _tags));
        }
    }

    /// <summary>Loads rating/tags from the cache without writing them back.</summary>
    public void ApplyCache(int? rating, string? tags)
    {
        _rating = rating;
        _tags = tags ?? string.Empty;
        OnPropertyChanged(nameof(Rating));
        OnPropertyChanged(nameof(Tags));
    }

    /// <summary>Enables write-through so subsequent user edits persist to the cache.</summary>
    public void EnablePersist() => _persistEnabled = true;

    private static void Persist(Action action)
    {
        try { action(); } catch { }
    }

    public bool IsDeleted
    {
        get => _isDeleted;
        set => SetProperty(ref _isDeleted, value);
    }
    public ObservableCollection<FolderNode> Children { get; } = new();

    public void MarkAsDeleted()
    {
        IsDeleted = true;
        foreach (var child in Children)
        {
            child.MarkAsDeleted();
        }
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (_filterText != value)
            {
                _filterText = value;
                foreach (var child in Children)
                {
                    child.FilterText = value;
                }
                OnPropertyChanged(nameof(FilterText));
                OnPropertyChanged(nameof(IsVisible));
            }
        }
    }

    public FolderSortOption SortOption
    {
        get => _sortOption;
        set
        {
            if (_sortOption != value)
            {
                _sortOption = value;
                foreach (var child in Children)
                {
                    child.SortOption = value;
                }
                OnPropertyChanged(nameof(SortOption));

                // Re-sort children if already loaded
                if (_isLoaded && Children.Count > 0 && !Children[0]._isDummy)
                {
                    ResortChildren();
                }
            }
        }
    }

    public bool IsVisible
    {
        get
        {
            if (_isDummy) return string.IsNullOrWhiteSpace(_filterText);
            if (string.IsNullOrWhiteSpace(_filterText)) return true;

            if (Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase))
                return true;

            // If a parent folder matched the filter, show its whole subtree unfiltered.
            if (AncestorMatches())
                return true;

            return Children.Any(c => c.IsVisible);
        }
    }

    private bool AncestorMatches()
    {
        var p = Parent;
        while (p != null)
        {
            if (!p._isDummy && p.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase))
                return true;
            p = p.Parent;
        }
        return false;
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value && !_isLoaded && !_isLoading)
            {
                _ = LoadChildrenAsync();
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value) && value && Children.Count > 0)
            {
                IsExpanded = true;
            }
        }
    }

    public FolderNode(string path, string? displayName = null, bool isDummy = false)
    {
        FullPath = path;
        Name = displayName ?? Path.GetFileName(path);
        if (string.IsNullOrEmpty(Name))
            Name = path;

        _isDummy = isDummy;
        _isLoaded = isDummy;

        if (!isDummy)
        {
            Children.Add(new FolderNode("", "Loading...", isDummy: true));
        }
    }

    // Sets the sort field on this node and every loaded descendant (cheap), collecting the nodes
    // whose child collection needs re-ordering. Split out from a resort so ApplySortAsync can
    // yield between the (expensive) collection moves.
    public void PrepareSort(FolderSortOption opt, List<FolderNode> toResort)
    {
        _sortOption = opt;
        OnPropertyChanged(nameof(SortOption));
        if (_isLoaded && Children.Count > 1 && !Children[0]._isDummy)
            toResort.Add(this);
        foreach (var child in Children)
            if (!child._isDummy) child.PrepareSort(opt, toResort);
    }

    public void ResortLoadedChildren()
    {
        if (_isLoaded && Children.Count > 1 && !Children[0]._isDummy)
            ResortChildren();
    }

    private void ResortChildren()
    {
        var nonDummy = Children.Where(c => !c._isDummy).ToList();
        if (nonDummy.Count < 2) return;

        var sorted = SortDirectories(nonDummy.Select(c => c.FullPath)).ToList();
        var byPath = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in nonDummy) byPath[c.FullPath] = c;

        // Move each node into its sorted slot. O(n) dictionary lookups instead of an O(n^2)
        // LINQ scan per element (the old version froze on large folders).
        for (int i = 0; i < sorted.Count; i++)
        {
            if (!byPath.TryGetValue(sorted[i], out var node)) continue;
            var currentIndex = Children.IndexOf(node);
            if (currentIndex != -1 && currentIndex != i)
                Children.Move(currentIndex, i);
        }
    }

    private IEnumerable<string> SortDirectories(IEnumerable<string> dirs)
    {
        return _sortOption switch
        {
            FolderSortOption.NameDesc => dirs.OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase),
            FolderSortOption.CreationDateAsc => dirs.OrderBy(d =>
            {
                try { return Directory.GetCreationTime(d); }
                catch { return DateTime.MaxValue; }
            }),
            FolderSortOption.CreationDateDesc => dirs.OrderByDescending(d =>
            {
                try { return Directory.GetCreationTime(d); }
                catch { return DateTime.MinValue; }
            }),
            FolderSortOption.ModifiedDateAsc => dirs.OrderBy(d =>
            {
                try { return Directory.GetLastWriteTime(d); }
                catch { return DateTime.MaxValue; }
            }),
            FolderSortOption.ModifiedDateDesc => dirs.OrderByDescending(d =>
            {
                try { return Directory.GetLastWriteTime(d); }
                catch { return DateTime.MinValue; }
            }),
            _ => dirs.OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase) // NameAsc
        };
    }

    // Builds a child node off the visual tree (safe on any thread — it isn't bound yet).
    private FolderNode MakeChildNode(string dir, FolderRecord? record)
    {
        var node = new FolderNode(dir)
        {
            Parent = this,
            FilterText = _filterText,
            SortOption = _sortOption
        };
        if (record != null)
            node.ApplyCache(record.Rating, record.Tags);
        node.EnablePersist();
        return node;
    }

    public async Task EnsureLoadedAsync()
    {
        if (_isLoaded) return;
        if (_isLoading)
        {
            if (_loadTcs != null) await _loadTcs.Task;
            return;
        }
        await LoadChildrenAsync();
    }

    // Awaits the background filesystem-refresh pass started by LoadChildrenAsync (if any).
    // Navigation uses this to pick up folders not yet present in the SQLite cache.
    public async Task EnsureRefreshedAsync()
    {
        var t = _refreshTask;
        if (t != null) await t;
    }

    private async Task LoadChildrenAsync()
    {
        if (_isLoaded || _isLoading) return;
        _isLoading = true;
        _loadTcs ??= new TaskCompletionSource<bool>();

        try
        {
            // 1. Cache-first: build ALL nodes from the SQLite cache, then add them in a
            // SINGLE UI-thread batch (one layout pass) instead of one dispatch per node.
            List<FolderRecord> cachedRecords;
            try { cachedRecords = DatabaseService.Instance.GetChildFolders(FullPath).ToList(); }
            catch { cachedRecords = new List<FolderRecord>(); }

            var cachedByPath = new Dictionary<string, FolderRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in cachedRecords)
                cachedByPath[rec.FullPath] = rec;
            var cachedPaths = cachedRecords.Select(c => c.FullPath).ToList();

            var cacheNodes = SortDirectories(cachedPaths)
                .Select(dir => MakeChildNode(dir, cachedByPath.TryGetValue(dir, out var rec) ? rec : null))
                .ToList();

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Children.Clear();
                foreach (var n in cacheNodes) Children.Add(n);
            });

            // 2. Filesystem refresh: enumerate + diff off-thread, then apply adds/removes in
            // ONE UI-thread batch. Runs in the BACKGROUND — NOT awaited here — so callers
            // (folder restore / navigation / expansion) return as soon as the cache pass has
            // populated the tree, instead of blocking on slow network enumeration + a
            // File.GetAttributes per subfolder. Cache-first pattern (CLAUDE.md #8).
            // ponytail: navigating to a folder created since the last scan (not yet in cache)
            // relies on EnsureRefreshedAsync() to await this pass — see NavigateToPathCoreAsync.
            _refreshTask = Task.Run(() =>
            {
                try
                {
                    var allDirs = Directory.EnumerateDirectories(FullPath)
                        .Where(dir =>
                        {
                            try
                            {
                                var attr = File.GetAttributes(dir);
                                return (attr & FileAttributes.Hidden) == 0 && (attr & FileAttributes.System) == 0;
                            }
                            catch { return false; }
                        })
                        .ToList();

                    try { DatabaseService.Instance.UpsertFolders(FullPath, allDirs); }
                    catch { }

                    // Warm the file cache for each visible subfolder in the background, so
                    // selecting one later serves instantly from the cache.
                    foreach (var dir in allDirs)
                        LibraryCacheService.Instance.Enqueue(dir);

                    var onDisk = new HashSet<string>(allDirs, StringComparer.OrdinalIgnoreCase);
                    var cachedSet = new HashSet<string>(cachedPaths, StringComparer.OrdinalIgnoreCase);

                    var gone = new HashSet<string>(
                        cachedPaths.Where(p => !onDisk.Contains(p)), StringComparer.OrdinalIgnoreCase);
                    foreach (var g in gone) { try { DatabaseService.Instance.MarkFolderDeleted(g); } catch { } }

                    // Nodes for folders not already shown from cache (built off the UI thread).
                    var newNodes = SortDirectories(allDirs.Where(d => !cachedSet.Contains(d)).ToList())
                        .Select(d => MakeChildNode(d, null))
                        .ToList();

                    if (gone.Count > 0 || newNodes.Count > 0)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            foreach (var node in Children
                                .Where(c => !c._isDummy && gone.Contains(c.FullPath)).ToList())
                                Children.Remove(node);
                            foreach (var n in newNodes) Children.Add(n);
                            if (Children.Count > 0 && !Children[0]._isDummy) ResortChildren();
                        });
                    }
                }
                catch
                {
                    // Ignore access errors
                }
            });

            _isLoaded = true;
            OnPropertyChanged(nameof(IsVisible));
        }
        finally
        {
            _isLoading = false;
            _loadTcs?.TrySetResult(true);
        }
    }

    public void Refresh()
    {
        _isLoaded = false;
        _isLoading = false;
        _loadTcs = null;
        Children.Clear();
        Children.Add(new FolderNode("", "Loading...", isDummy: true));

        if (_isExpanded)
        {
            _ = LoadChildrenAsync();
        }
    }

    public FolderNode? FindNode(string path)
    {
        if (string.Equals(FullPath, path, StringComparison.OrdinalIgnoreCase))
            return this;

        foreach (var child in Children)
        {
            var found = child.FindNode(path);
            if (found != null)
                return found;
        }
        return null;
    }

    public bool RemoveChild(FolderNode node)
    {
        if (Children.Remove(node))
            return true;

        foreach (var child in Children)
        {
            if (child.RemoveChild(node))
                return true;
        }
        return false;
    }
}

public class FolderTreeViewModel : ViewModelBase
{
    private string _filterText = string.Empty;
    private FolderSortOptionItem _selectedSortOption;

    public ObservableCollection<FolderNode> RootNodes { get; } = new();

    public List<FolderSortOptionItem> SortOptions { get; } = new()
    {
        new FolderSortOptionItem(FolderSortOption.NameAsc, "Name (A-Z)"),
        new FolderSortOptionItem(FolderSortOption.NameDesc, "Name (Z-A)"),
        new FolderSortOptionItem(FolderSortOption.CreationDateAsc, "Created (Oldest)"),
        new FolderSortOptionItem(FolderSortOption.CreationDateDesc, "Created (Newest)"),
        new FolderSortOptionItem(FolderSortOption.ModifiedDateAsc, "Modified (Oldest)"),
        new FolderSortOptionItem(FolderSortOption.ModifiedDateDesc, "Modified (Newest)")
    };

    private bool _isBusy;
    // Drives the window's busy overlay during long tree operations (sort change, folder restore).
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public FolderSortOptionItem SelectedSortOption
    {
        get => _selectedSortOption;
        set
        {
            if (SetProperty(ref _selectedSortOption, value) && value != null)
            {
                _ = ApplySortAsync(value.Value);
            }
        }
    }

    // Re-sorting the whole loaded tree on the UI thread froze the app. Do it in yielding chunks
    // so the busy overlay paints and animates instead of locking up.
    private async Task ApplySortAsync(FolderSortOption opt)
    {
        IsBusy = true;
        try
        {
            await Task.Yield(); // let the overlay render before the heavy work starts

            // Set the sort field on every loaded node (cheap) and collect the ones whose child
            // collection actually needs re-ordering.
            var toResort = new List<FolderNode>();
            foreach (var node in RootNodes)
                node.PrepareSort(opt, toResort);

            var n = 0;
            foreach (var node in toResort)
            {
                node.ResortLoadedChildren();
                if (++n % 20 == 0) await Task.Yield(); // keep the UI thread responsive
            }
        }
        finally { IsBusy = false; }
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (_filterText != value)
            {
                _filterText = value;
                foreach (var node in RootNodes)
                {
                    node.FilterText = value;
                }
                OnPropertyChanged(nameof(FilterText));
            }
        }
    }

    public FolderTreeViewModel()
    {
        _selectedSortOption = SortOptions[0];
        LoadDrives();
    }

    private void LoadDrives()
    {
        RootNodes.Clear();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                string name;
                if (drive.IsReady)
                {
                    var label = string.IsNullOrEmpty(drive.VolumeLabel) ? GetDriveTypeLabel(drive.DriveType) : drive.VolumeLabel;
                    name = $"{label} ({drive.Name.TrimEnd('\\')})";
                }
                else
                {
                    name = $"{GetDriveTypeLabel(drive.DriveType)} ({drive.Name.TrimEnd('\\')})";
                }

                RootNodes.Add(new FolderNode(drive.Name, name));
            }
            catch
            {
                try
                {
                    RootNodes.Add(new FolderNode(drive.Name, drive.Name));
                }
                catch
                {
                    // Skip completely inaccessible drives
                }
            }
        }
    }

    private static string GetDriveTypeLabel(DriveType driveType)
    {
        return driveType switch
        {
            DriveType.Fixed => "Local Disk",
            DriveType.Removable => "Removable Disk",
            DriveType.Network => "Network Drive",
            DriveType.CDRom => "CD/DVD Drive",
            DriveType.Ram => "RAM Disk",
            _ => "Drive"
        };
    }

    public void RefreshDrives()
    {
        LoadDrives();
    }

    public void MarkFolderAsDeleted(string path)
    {
        foreach (var root in RootNodes)
        {
            var node = root.FindNode(path);
            if (node != null)
            {
                node.MarkAsDeleted();
                return;
            }
        }
    }

    public async Task<bool> NavigateToPathAsync(string path)
    {
        IsBusy = true;
        try { return await NavigateToPathCoreAsync(path); }
        finally { IsBusy = false; }
    }

    private async Task<bool> NavigateToPathCoreAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var root = RootNodes.FirstOrDefault(r =>
            path.StartsWith(r.FullPath, StringComparison.OrdinalIgnoreCase));
        if (root == null) return false;

        var current = root;
        current.IsExpanded = true;
        await current.EnsureLoadedAsync();

        var rootPath = root.FullPath.TrimEnd('\\', '/');
        var rest = path.Length > rootPath.Length
            ? path.Substring(rootPath.Length).TrimStart('\\', '/')
            : string.Empty;

        if (string.IsNullOrEmpty(rest))
        {
            current.IsSelected = true;
            return true;
        }

        var parts = rest.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var accumPath = rootPath;

        for (int i = 0; i < parts.Length; i++)
        {
            accumPath = Path.Combine(accumPath + (i == 0 ? "\\" : ""), parts[i]);
            // Normalize: Path.Combine("F:", "Music") returns "F:Music" — workaround above ensures "F:\Music"
            var child = current.Children.FirstOrDefault(c =>
                string.Equals(c.FullPath.TrimEnd('\\', '/'), accumPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (child == null)
            {
                // Not in the cache-built children — wait for the background disk refresh
                // (covers a folder created since the last scan) and retry once.
                await current.EnsureRefreshedAsync();
                child = current.Children.FirstOrDefault(c =>
                    string.Equals(c.FullPath.TrimEnd('\\', '/'), accumPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
                if (child == null) return false;
            }

            current = child;
            if (i < parts.Length - 1)
            {
                current.IsExpanded = true;
                await current.EnsureLoadedAsync();
            }
        }

        current.IsSelected = true;
        return true;
    }

    public FolderNode? FindNode(string path)
    {
        foreach (var root in RootNodes)
        {
            var node = root.FindNode(path);
            if (node != null)
                return node;
        }
        return null;
    }

    public void RemoveFolder(string path)
    {
        foreach (var root in RootNodes)
        {
            var node = root.FindNode(path);
            if (node != null)
            {
                root.RemoveChild(node);
                return;
            }
        }
    }
}
