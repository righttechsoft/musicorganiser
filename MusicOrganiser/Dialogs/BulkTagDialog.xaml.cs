using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MusicOrganiser.Services;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Dialogs;

/// <summary>One row in the bulk-tag grid: an immediate subfolder (band) plus its AI-suggested
/// tags (editable before Apply) and a status string. INotifyPropertyChanged via ViewModelBase
/// so the DataGrid reflects results as they arrive from background AI calls.</summary>
public class BulkTagRow : ViewModelBase
{
    public string BandName { get; }
    public string FolderPath { get; }

    private bool _include = true;
    public bool Include
    {
        get => _include;
        set => SetProperty(ref _include, value);
    }

    private string _tagsText = string.Empty;
    public string TagsText
    {
        get => _tagsText;
        set => SetProperty(ref _tagsText, value);
    }

    private string _status = "Pending";
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    private bool _isSaved;
    public bool IsSaved
    {
        get => _isSaved;
        set => SetProperty(ref _isSaved, value);
    }

    public BulkTagRow(string bandName, string folderPath)
    {
        BandName = bandName;
        FolderPath = folderPath;
    }
}

/// <summary>Bulk-tags every untagged immediate subfolder of a right-clicked folder, treating
/// each as a band. Processes in batches of <see cref="BatchSize"/> with bounded AI concurrency;
/// Apply saves the checked rows, Next batch continues. The AI call itself is injected via
/// [suggestTags] (band, albums) so this dialog has no service dependencies of its own.</summary>
public partial class BulkTagDialog : Window
{
    // ponytail: hand-picked, ~1 min/batch at 3-way concurrency — small enough that a batch
    // finishes in a reasonable time, large enough that Apply/Next batch isn't a constant chore.
    private const int BatchSize = 20;
    private const int Concurrency = 3;

    private readonly Func<string, IReadOnlyList<string>, Task<IReadOnlyList<string>?>> _suggestTags;
    private readonly Action<string, string>? _onApplied;
    private readonly List<string> _remaining = new();
    private readonly Dictionary<string, string> _appliedTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _totalSubfolders;
    private bool _closed;
    private BandPreviewDialog? _previewDialog;

    public ObservableCollection<BulkTagRow> Rows { get; } = new();

    /// <summary>Folder path to saved tags CSV, accumulated across every Apply click this session.</summary>
    public IReadOnlyDictionary<string, string> AppliedTags => _appliedTags;

    /// <summary>Fired once per row, right when that row is saved (not when the dialog closes),
    /// so a caller (e.g. the folder tree) can reflect the new tags immediately.</summary>
    public BulkTagDialog(string parentFolderPath, Func<string, IReadOnlyList<string>, Task<IReadOnlyList<string>?>> suggestTags,
        Action<string, string>? onApplied = null)
    {
        InitializeComponent();
        _suggestTags = suggestTags;
        _onApplied = onApplied;
        RowsGrid.ItemsSource = Rows;

        List<string> subfolders;
        try { subfolders = Directory.GetDirectories(parentFolderPath).ToList(); }
        catch { subfolders = new List<string>(); }

        _totalSubfolders = subfolders.Count;
        _remaining.AddRange(subfolders.Where(p => DatabaseService.Instance.GetFolderTags(p).Count == 0));

        Title = $"Bulk tag subfolders — {Path.GetFileName(parentFolderPath.TrimEnd('\\', '/'))}";
        CountText.Text = $"{_remaining.Count} untagged of {_totalSubfolders}";

        Closed += (_, _) =>
        {
            _closed = true;
            _previewDialog?.Close();
        };
        Loaded += async (_, _) => await LoadNextBatchAsync();
    }

    private async Task LoadNextBatchAsync()
    {
        if (_closed || _remaining.Count == 0) return;

        var batch = _remaining.Take(BatchSize).ToList();
        _remaining.RemoveRange(0, batch.Count);

        Rows.Clear();
        foreach (var path in batch)
            Rows.Add(new BulkTagRow(Path.GetFileName(path) ?? path, path));

        ApplyButton.IsEnabled = false;
        NextBatchButton.IsEnabled = false;
        CountText.Text = $"Searching {Rows.Count} folder(s)... ({_remaining.Count} more untagged after this batch)";

        using var gate = new SemaphoreSlim(Concurrency);
        var tasks = Rows.Select(row => ProcessRowAsync(row, gate)).ToList();
        await Task.WhenAll(tasks);

        if (_closed) return;

        ApplyButton.IsEnabled = Rows.Count > 0;
        CountText.Text = $"Done. {_remaining.Count} untagged remaining.";
    }

    private async Task ProcessRowAsync(BulkTagRow row, SemaphoreSlim gate)
    {
        await gate.WaitAsync();
        try
        {
            if (_closed) return;
            row.Status = "Searching...";

            List<string> albums;
            try
            {
                albums = Directory.GetDirectories(row.FolderPath)
                    .Select(d => Path.GetFileName(d) ?? string.Empty)
                    .Where(n => n.Length > 0)
                    .ToList();
            }
            catch { albums = new List<string>(); }

            IReadOnlyList<string>? suggested;
            try { suggested = await _suggestTags(row.BandName, albums); }
            catch { suggested = null; }

            if (_closed) return;

            if (suggested == null)
            {
                row.Status = "Failed";
                row.Include = false;
            }
            else if (suggested.Count == 0)
            {
                row.Status = "Not identified";
                row.Include = false;
            }
            else
            {
                row.TagsText = string.Join(", ", suggested);
                row.Status = "Done";
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rows)
        {
            if (!row.Include) continue;
            var csv = row.TagsText.Trim();
            if (csv.Length == 0) continue;

            DatabaseService.Instance.SetFolderTags(row.FolderPath, csv);
            // Read back the authoritative persisted value (SetFolderTags trims/dedupes) so the
            // row shows exactly what was saved, not just what was typed.
            var saved = string.Join(", ", DatabaseService.Instance.GetFolderTags(row.FolderPath));
            _appliedTags[row.FolderPath] = saved;
            _onApplied?.Invoke(row.FolderPath, saved);

            row.TagsText = saved;
            row.Status = "Saved";
            row.IsSaved = true;
        }

        ApplyButton.IsEnabled = false;
        NextBatchButton.IsEnabled = _remaining.Count > 0;
        CountText.Text = _remaining.Count > 0
            ? $"Applied. {_remaining.Count} untagged remaining."
            : "Applied. No more untagged subfolders.";
    }

    private async void NextBatch_Click(object sender, RoutedEventArgs e)
    {
        NextBatchButton.IsEnabled = false;
        await LoadNextBatchAsync();
    }

    private void PreviewBand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not BulkTagRow row) return;

        _previewDialog?.Close();

        var dlg = new BandPreviewDialog(row.BandName, row.FolderPath) { Owner = this };
        dlg.Closed += (_, _) =>
        {
            if (ReferenceEquals(_previewDialog, dlg)) _previewDialog = null;
        };
        _previewDialog = dlg;
        dlg.Show();
    }
}
