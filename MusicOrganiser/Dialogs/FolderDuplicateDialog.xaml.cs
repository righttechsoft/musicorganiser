using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MusicOrganiser.Models;
using MusicOrganiser.Services;

namespace MusicOrganiser.Dialogs;

internal enum RowCategory { New, Different, Same, TargetOnly }

// Skip doubles as "keep" on a target-only row
internal enum RowAction { Transfer, Skip, Delete }

// One line of the comparison: a source item paired with its target counterpart by relative path.
internal sealed class PlanRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public string SourceRel = "";   // relative path under the source root ("" = no source side)
    public string TargetRel = "";   // relative path under the target root ("" = no target side)
    public string RelPath => SourceRel.Length > 0 ? SourceRel : TargetRel;
    public bool HasSource => SourceRel.Length > 0;
    public bool HasTarget => TargetRel.Length > 0;

    public bool IsFolder { get; init; }
    public int Depth { get; init; }
    public bool Move { get; init; }
    public RowCategory Category { get; init; }
    public string CategoryName => Category.ToString();

    public string SourceName { get; init; } = "";
    public string TargetName { get; init; } = "";
    public string SourceSize { get; init; } = "";
    public string TargetSize { get; init; } = "";
    public string SourceDuration { get; init; } = "";
    public string TargetDuration { get; init; } = "";

    public Thickness Indent => new(Depth * 16, 0, 0, 0);

    private RowAction _action;
    public RowAction Action
    {
        get => _action;
        set { _action = value; Raise(nameof(Glyph), nameof(Tooltip)); }
    }

    // Folder rows carry no operation of their own: they show the common action of the rows below them
    private RowAction? _folderState;
    private bool _folderHasAction;
    public void SetFolderState(RowAction? state, bool hasAction)
    {
        _folderState = state;
        _folderHasAction = hasAction;
        Raise(nameof(Glyph), nameof(Tooltip), nameof(ActionVisibility));
    }

    public Visibility ActionVisibility => IsFolder && !_folderHasAction ? Visibility.Collapsed : Visibility.Visible;

    public string Glyph => IsFolder ? (_folderState is RowAction s ? GlyphOf(s) : "•") : GlyphOf(_action);

    public static string GlyphOf(RowAction a) => a switch
    {
        RowAction.Transfer => "→",
        RowAction.Delete => "✕",
        _ => "–"
    };

    public string Tooltip
    {
        get
        {
            if (IsFolder && _folderState == null) return "Mixed - click to set every item in this folder";
            var a = IsFolder ? _folderState!.Value : _action;
            var verb = Move ? "Move" : "Copy";
            var prefix = IsFolder ? "Every item in this folder: " : "";
            return prefix + a switch
            {
                RowAction.Transfer => HasTarget && !IsFolder ? $"{verb} and overwrite the target file" : $"{verb} to the target",
                RowAction.Delete => "Delete from the target (permanent)",
                _ => Category == RowCategory.TargetOnly && !IsFolder ? "Keep in the target" : "Skip"
            };
        }
    }

    // In-place rename: 0 = none, 1 = source name cell, 2 = target name cell
    private int _editingSide;
    public int EditingSide
    {
        get => _editingSide;
        set { _editingSide = value; Raise(nameof(EditingSide)); }
    }

    private string _editText = "";
    public string EditText
    {
        get => _editText;
        set { _editText = value; Raise(nameof(EditText)); }
    }
}

public partial class FolderDuplicateDialog : Window
{
    public FolderDuplicateAction Result { get; private set; } = FolderDuplicateAction.Cancel;

    public string TargetPath { get; private set; } = string.Empty;
    public string NewName { get; private set; } = string.Empty;

    /// <summary>File-level steps to run when <see cref="Result"/> is Custom (skipped rows are omitted).</summary>
    public IReadOnlyList<FolderPlanItem> Plan { get; private set; } = System.Array.Empty<FolderPlanItem>();

    /// <summary>True once any file/folder was renamed on disk from the dialog (even if the dialog is then cancelled).</summary>
    public bool RenamedAnything { get; private set; }

    private readonly string _sourcePath;
    private readonly string _destinationFolder;
    private readonly IReadOnlyList<string> _candidates;
    private readonly bool _move;
    private readonly FileOperationsService _ops;

    private string _targetRoot = string.Empty;
    private List<PlanRow> _rows = new();
    private bool _replacePreset;   // active preset: Merge (default) or Replace
    private bool _renaming;
    private int _loadId;
    private FolderComparisonInfo? _sourceInfo;

    public FolderDuplicateDialog(string sourcePath, IReadOnlyList<string> candidates, string destinationFolder, bool move, FileOperationsService ops)
    {
        InitializeComponent();

        _sourcePath = sourcePath;
        _candidates = candidates;
        _destinationFolder = destinationFolder;
        _move = move;
        _ops = ops;

        var sourceName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        SourceFolderName.Text = sourceName;

        var verb = move ? "Move" : "Copy";
        ExecuteButton.Content = verb;
        IntoTargetButton.Content = $"{verb} into subfolder";
        KeepBothButton.Content = $"{verb} as new folder";
        var intoTip = new ToolTip();
        IntoTargetButton.ToolTip = intoTip;
        IntoTargetButton.ToolTipOpening += (_, _) =>
            intoTip.Content = $"{verb} as '{NewNameBox.Text.Trim()}' inside '{Path.GetFileName(_targetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}'";
        LegendText.Text = $"→ {verb} to the target (overwrites an existing file)     – Skip / keep     ✕ Delete from the target     |     Into subfolder: bring the folder in inside the selected existing one     |     F2 or double-click a name to rename";

        // Default new name: the source name if free, else the first free "Name (n)"
        var name = sourceName;
        var candidate = name;
        for (var i = 1; Directory.Exists(Path.Combine(destinationFolder, candidate)); i++)
            candidate = $"{name} ({i})";
        NewNameBox.Text = candidate;

        foreach (var c in candidates)
            TargetCombo.Items.Add(Path.GetFileName(c.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        TargetCombo.SelectedIndex = 0;
    }

    private void TargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Reload(false);

    // Builds the comparison off the UI thread; only the latest request may apply its result.
    // keepOverrides carries the user's per-row choices across a rebuild (after a rename).
    private async void Reload(bool keepOverrides)
    {
        var i = TargetCombo.SelectedIndex;
        if (i < 0 || i >= _candidates.Count) return;

        var id = ++_loadId;
        var targetPath = _candidates[i];
        var old = keepOverrides
            ? _rows.Where(r => !r.IsFolder).GroupBy(r => r.RelPath.ToLowerInvariant()).ToDictionary(g => g.Key, g => (g.First().Category, g.First().Action))
            : new Dictionary<string, (RowCategory Category, RowAction Action)>();
        SetLoading(true);
        try
        {
            var (srcInfo, rows, hidden, tgtInfo) = await System.Threading.Tasks.Task.Run(() =>
            {
                _sourceInfo ??= FolderComparisonInfo.FromPath(_sourcePath);
                var list = new List<PlanRow>();
                var hid = 0;
                BuildRows(_sourcePath, targetPath, "", "", 0, _move, list, ref hid);
                return (_sourceInfo, list, hid, FolderComparisonInfo.FromPath(targetPath));
            });
            if (id != _loadId) return;

            SourceFolderName.Text = srcInfo.FolderName;
            SourceFileCount.Text = srcInfo.FileCount.ToString();
            SourceTotalSize.Text = srcInfo.TotalSizeFormatted;
            TargetFileCount.Text = tgtInfo.FileCount.ToString();
            TargetTotalSize.Text = tgtInfo.TotalSizeFormatted;

            foreach (var r in rows.Where(r => !r.IsFolder))
                r.Action = old.TryGetValue(r.RelPath.ToLowerInvariant(), out var o) && o.Category == r.Category
                    ? o.Action
                    : PresetAction(r);

            _targetRoot = targetPath;
            _rows = rows;
            // ponytail: flat indented list, no expand/collapse. Add collapsing if folders get huge.
            PlanGrid.ItemsSource = rows;
            RefreshFolders();
            UpdateSummary();

            HiddenNote.Text = hidden == 1
                ? "1 other folder in the target is not shown and will not be touched."
                : $"{hidden} other folders in the target are not shown and will not be touched.";
            HiddenNote.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            if (id != _loadId) return;
            // Comparison failed (folder vanished?): show nothing, the actions report their own errors
            _rows = new List<PlanRow>();
            PlanGrid.ItemsSource = null;
            UpdateSummary();
        }
        SetLoading(false);
    }

    private void SetLoading(bool loading)
    {
        LoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        TargetCombo.IsEnabled = PlanGrid.IsEnabled = !loading;
        MergePresetButton.IsEnabled = ReplacePresetButton.IsEnabled = !loading;
        ExecuteButton.IsEnabled = IntoTargetButton.IsEnabled = KeepBothButton.IsEnabled = !loading;
    }

    #region Building the rows

    private static void BuildRows(string? srcDir, string? tgtDir, string srcRel, string tgtRel, int depth, bool move,
        List<PlanRow> rows, ref int hidden)
    {
        var ci = System.StringComparer.OrdinalIgnoreCase;
        var srcSubs = Children(srcDir, true);
        var tgtSubs = Children(tgtDir, true);

        foreach (var name in srcSubs.Keys.Union(tgtSubs.Keys, ci).OrderBy(n => n, ci))
        {
            // A target folder with no counterpart in the source is not relevant: hidden, never touched
            if (!srcSubs.TryGetValue(name, out var sp)) { hidden++; continue; }
            tgtSubs.TryGetValue(name, out var tp);

            var sName = Path.GetFileName(sp);
            var tName = tp == null ? "" : Path.GetFileName(tp);
            var sRel = Path.Combine(srcRel, sName);
            var tRel = tp == null ? "" : Path.Combine(tgtRel, tName);
            rows.Add(new PlanRow
            {
                SourceRel = sRel, TargetRel = tRel, IsFolder = true, Depth = depth, Move = move,
                Category = tp == null ? RowCategory.New : RowCategory.Same,
                SourceName = sName, TargetName = tName
            });
            BuildRows(sp, tp, sRel, tRel, depth + 1, move, rows, ref hidden);
        }

        var srcFiles = Children(srcDir, false);
        var tgtFiles = Children(tgtDir, false);
        foreach (var name in srcFiles.Keys.Union(tgtFiles.Keys, ci).OrderBy(n => n, ci))
        {
            srcFiles.TryGetValue(name, out var sp);
            tgtFiles.TryGetValue(name, out var tp);
            var sSize = sp == null ? -1 : new FileInfo(sp).Length;
            var tSize = tp == null ? -1 : new FileInfo(tp).Length;
            var sName = sp == null ? "" : Path.GetFileName(sp);
            var tName = tp == null ? "" : Path.GetFileName(tp);

            rows.Add(new PlanRow
            {
                SourceRel = sp == null ? "" : Path.Combine(srcRel, sName),
                TargetRel = tp == null ? "" : Path.Combine(tgtRel, tName),
                Depth = depth, Move = move,
                Category = sp == null ? RowCategory.TargetOnly : tp == null ? RowCategory.New
                    : sSize == tSize ? RowCategory.Same : RowCategory.Different,
                SourceName = sName, TargetName = tName,
                SourceSize = sp == null ? "" : new FolderFileEntry { FileSize = sSize }.FileSizeFormatted,
                TargetSize = tp == null ? "" : new FolderFileEntry { FileSize = tSize }.FileSizeFormatted,
                SourceDuration = sp == null ? "" : DurationText(sp),
                TargetDuration = tp == null ? "" : DurationText(tp)
            });
        }
    }

    // name (case-insensitive) -> full path of the immediate subfolders or files
    private static Dictionary<string, string> Children(string? dir, bool folders)
    {
        var d = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        if (dir == null) return d;
        foreach (var p in folders ? Directory.GetDirectories(dir) : Directory.GetFiles(dir))
            d[Path.GetFileName(p)] = p;
        return d;
    }

    private static string DurationText(string path)
    {
        if (!MusicMetadataService.IsSupportedFile(path)) return "";
        try
        {
            using var tagFile = TagLib.File.Create(path);
            return new FolderFileEntry { Duration = tagFile.Properties.Duration }.DurationFormatted;
        }
        catch
        {
            return ""; // Ignore tag reading errors
        }
    }

    #endregion

    #region Actions, presets, summary

    private RowAction PresetAction(PlanRow r) => r.Category switch
    {
        RowCategory.New => RowAction.Transfer,
        RowCategory.Different => _replacePreset ? RowAction.Transfer : RowAction.Skip,
        // Replace on a move transfers identical files too, so the source copy goes away
        RowCategory.Same => _replacePreset && _move ? RowAction.Transfer : RowAction.Skip,
        _ => _replacePreset ? RowAction.Delete : RowAction.Skip
    };

    private void MergePreset_Click(object sender, RoutedEventArgs e) => ApplyPreset(false);
    private void ReplacePreset_Click(object sender, RoutedEventArgs e) => ApplyPreset(true);

    private void ApplyPreset(bool replace)
    {
        _replacePreset = replace;
        foreach (var r in _rows.Where(r => !r.IsFolder)) r.Action = PresetAction(r);
        RefreshFolders();
        UpdateSummary();
    }

    private static bool IsValid(PlanRow f, RowAction a) => a switch
    {
        RowAction.Transfer => f.Category != RowCategory.TargetOnly,
        RowAction.Delete => f.Category == RowCategory.TargetOnly,
        _ => true
    };

    private static readonly RowAction[] AllActions = { RowAction.Transfer, RowAction.Skip, RowAction.Delete };

    // The file rows below a folder row (rows are a preorder flattening, so they are the deeper rows that follow it)
    private IEnumerable<PlanRow> FilesBelow(int folderIndex)
    {
        var depth = _rows[folderIndex].Depth;
        for (var j = folderIndex + 1; j < _rows.Count && _rows[j].Depth > depth; j++)
            if (!_rows[j].IsFolder) yield return _rows[j];
    }

    private RowAction? CommonAction(List<PlanRow> files)
    {
        foreach (var a in AllActions)
        {
            var valid = files.Where(f => IsValid(f, a)).ToList();
            if (valid.Count > 0 && valid.All(f => f.Action == a)) return a;
        }
        return null;
    }

    private void RefreshFolders()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (!_rows[i].IsFolder) continue;
            var files = FilesBelow(i).ToList();
            _rows[i].SetFolderState(CommonAction(files), files.Count > 0);
        }
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not PlanRow row) return;

        if (!row.IsFolder)
        {
            var cycle = row.Category == RowCategory.TargetOnly
                ? new[] { RowAction.Skip, RowAction.Delete }
                : new[] { RowAction.Transfer, RowAction.Skip };
            row.Action = cycle[(System.Array.IndexOf(cycle, row.Action) + 1) % cycle.Length];
        }
        else
        {
            var files = FilesBelow(_rows.IndexOf(row)).ToList();
            var avail = AllActions.Where(a => files.Any(f => IsValid(f, a))).ToArray();
            if (avail.Length == 0) return;
            var current = CommonAction(files);
            var next = current == null ? avail[0] : avail[(System.Array.IndexOf(avail, current.Value) + 1) % avail.Length];
            foreach (var f in files.Where(f => IsValid(f, next))) f.Action = next;
        }

        RefreshFolders();
        UpdateSummary();
    }

    private (int Moved, int Overwrite, int Delete, int Skipped) Counts()
    {
        int moved = 0, over = 0, del = 0, skip = 0;
        foreach (var r in _rows.Where(r => !r.IsFolder))
        {
            if (r.Action == RowAction.Delete) del++;
            else if (r.Action == RowAction.Skip) skip++;
            else if (r.Category == RowCategory.New) moved++;
            else over++;
        }
        return (moved, over, del, skip);
    }

    private void UpdateSummary()
    {
        var (moved, over, del, skip) = Counts();
        var parts = new List<string>();
        if (moved > 0) parts.Add($"{moved} to {(_move ? "move" : "copy")}");
        if (over > 0) parts.Add($"{over} to overwrite");
        if (del > 0) parts.Add($"{del} to delete");
        if (skip > 0) parts.Add($"{skip} skipped");
        SummaryText.Text = parts.Count > 0 ? string.Join(" · ", parts) : "No files";
    }

    #endregion

    #region Rename in place

    private void BeginEdit(PlanRow row, int side)
    {
        if (_renaming) return;
        foreach (var r in _rows.Where(r => r.EditingSide != 0)) r.EditingSide = 0;
        row.EditText = side == 1 ? row.SourceName : row.TargetName;
        row.EditingSide = side;
    }

    private void SourceName_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && ((FrameworkElement)sender).DataContext is PlanRow r && r.HasSource)
        {
            BeginEdit(r, 1);
            e.Handled = true;
        }
    }

    private void TargetName_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && ((FrameworkElement)sender).DataContext is PlanRow r && r.HasTarget)
        {
            BeginEdit(r, 2);
            e.Handled = true;
        }
    }

    private void PlanGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && e.OriginalSource is not TextBox && PlanGrid.SelectedItem is PlanRow r)
        {
            BeginEdit(r, r.HasSource ? 1 : 2);
            e.Handled = true;
        }
    }

    private void EditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue || sender is not TextBox tb || tb.DataContext is not PlanRow row) return;
        Dispatcher.BeginInvoke(() =>
        {
            tb.Focus();
            // Select the name without its extension for files
            var dot = tb.Text.LastIndexOf('.');
            tb.Select(0, row.IsFolder || dot <= 0 ? tb.Text.Length : dot);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void EditBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not PlanRow row) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitRename(row);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true; // otherwise the Cancel button (IsCancel) closes the dialog
            row.EditingSide = 0;
        }
    }

    private void EditBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PlanRow row && row.EditingSide != 0)
            CommitRename(row);
    }

    // Renames on disk immediately, then re-runs the comparison (pairing can change).
    // Note: ratings/tags are path-keyed in the DB, so a renamed track loses them exactly like a moved one does today.
    private async void CommitRename(PlanRow row)
    {
        var side = row.EditingSide;
        if (side == 0) return;
        row.EditingSide = 0;

        var oldName = side == 1 ? row.SourceName : row.TargetName;
        var newName = (row.EditText ?? "").Trim();
        if (newName.Length == 0 || newName == oldName) return;

        var path = Path.Combine(side == 1 ? _sourcePath : _targetRoot, side == 1 ? row.SourceRel : row.TargetRel);
        bool ok;
        _renaming = true;
        try { ok = await _ops.RenameAsync(path, newName); }
        finally { _renaming = false; }

        if (!ok)
        {
            MessageBox.Show(this, $"Could not rename '{oldName}' to '{newName}'. The name may be invalid or already in use.",
                "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RenamedAnything = true;
        Reload(true);
    }

    #endregion

    #region Buttons

    private void ExecuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_renaming) return;

        var plan = _rows.Where(r => !r.IsFolder && r.Action != RowAction.Skip)
            .Select(r => new FolderPlanItem(r.RelPath, r.Action == RowAction.Delete ? FolderPlanAction.DeleteTarget : FolderPlanAction.Transfer))
            .ToList();
        if (plan.Count == 0)
        {
            MessageBox.Show(this, "Nothing is selected to " + (_move ? "move" : "copy") + " or delete. Choose an action for at least one file, or Cancel.",
                "Nothing to do", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (_, over, del, _) = Counts();
        if (over > 0 || del > 0)
        {
            var ps = new List<string>();
            if (over > 0) ps.Add($"overwrite {over}");
            if (del > 0) ps.Add($"permanently delete {del}");
            var targetName = Path.GetFileName(_targetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (MessageBox.Show(this, $"This will {string.Join(" and ", ps)} files in '{targetName}'. Continue?",
                    "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }

        Result = FolderDuplicateAction.Custom;
        TargetPath = _targetRoot;
        Plan = plan;
        DialogResult = true;
    }

    // Brings the source in as a subfolder of the selected existing folder (e.g. an album into its band folder)
    private void IntoTargetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_renaming) return;

        var name = NewNameBox.Text.Trim();
        var inside = Path.Combine(_targetRoot, name);
        string? error = null;
        if (name.Length == 0 || name == "." || name == "..") error = "Enter a name for the subfolder.";
        else if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) error = "The name contains characters that are not allowed in a folder name.";
        else if (Directory.Exists(inside) || File.Exists(inside))
            error = $"'{name}' already exists inside '{Path.GetFileName(_targetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}'. Change the name.";

        if (error != null)
        {
            MessageBox.Show(this, error, "Into subfolder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = FolderDuplicateAction.IntoTarget;
        TargetPath = _targetRoot;
        NewName = name;
        DialogResult = true;
    }

    private void KeepBothButton_Click(object sender, RoutedEventArgs e)
    {
        if (_renaming) return;

        var name = NewNameBox.Text.Trim();
        string? error = null;
        if (name.Length == 0 || name == "." || name == "..") error = "Enter a name for the new folder.";
        else if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) error = "The name contains characters that are not allowed in a folder name.";
        else if (Directory.Exists(Path.Combine(_destinationFolder, name)) || File.Exists(Path.Combine(_destinationFolder, name)))
            error = $"'{name}' already exists in the destination. Choose another name.";

        if (error != null)
        {
            MessageBox.Show(error, "New folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = FolderDuplicateAction.KeepBoth;
        NewName = name;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_renaming) return;
        Result = FolderDuplicateAction.Cancel;
        DialogResult = false;
    }

    #endregion
}
