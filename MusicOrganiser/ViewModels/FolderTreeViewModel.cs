using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

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
    private readonly bool _isDummy;
    private string _filterText = string.Empty;
    private FolderSortOption _sortOption = FolderSortOption.NameAsc;
    private bool _isDeleted;

    public string Name { get; }
    public string FullPath { get; }

    public bool IsDeleted
    {
        get => _isDeleted;
        set => SetProperty(ref _isDeleted, value);
    }
    public ObservableCollection<FolderNode> Children { get; } = new();

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

            return Children.Any(c => c.IsVisible);
        }
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
        set => SetProperty(ref _isSelected, value);
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

    private void ResortChildren()
    {
        var sorted = SortDirectories(Children.Where(c => !c._isDummy).Select(c => c.FullPath)).ToList();

        // Reorder children based on sorted list
        for (int i = 0; i < sorted.Count; i++)
        {
            var currentIndex = Children.Select((c, idx) => new { c, idx })
                .FirstOrDefault(x => x.c.FullPath == sorted[i])?.idx ?? -1;

            if (currentIndex != -1 && currentIndex != i)
            {
                Children.Move(currentIndex, i);
            }
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

    private async Task LoadChildrenAsync()
    {
        if (_isLoaded || _isLoading) return;
        _isLoading = true;

        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() => Children.Clear());

            await Task.Run(async () =>
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

                    var sortedDirs = SortDirectories(allDirs);

                    foreach (var dir in sortedDirs)
                    {
                        var currentFilter = _filterText;
                        var currentSort = _sortOption;

                        await Application.Current.Dispatcher.InvokeAsync(
                            () =>
                            {
                                var node = new FolderNode(dir)
                                {
                                    FilterText = currentFilter,
                                    SortOption = currentSort
                                };
                                Children.Add(node);
                            },
                            System.Windows.Threading.DispatcherPriority.Background);
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
        }
    }

    public void Refresh()
    {
        _isLoaded = false;
        _isLoading = false;
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

    public FolderSortOptionItem SelectedSortOption
    {
        get => _selectedSortOption;
        set
        {
            if (SetProperty(ref _selectedSortOption, value) && value != null)
            {
                foreach (var node in RootNodes)
                {
                    node.SortOption = value.Value;
                }
            }
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
                node.IsDeleted = true;
                return;
            }
        }
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
