using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MusicOrganiser.Services;

namespace MusicOrganiser.Dialogs;

/// <summary>Lists every folder tag with how many folders carry it, and lets the user delete a
/// tag or merge it into another — both irreversible, both confirmed via MessageBox. Talks to
/// DatabaseService directly (same style as MainWindow's own tag-filter popup, which reads
/// GetAllTagNames() straight from the DB) — a dumb list dialog with no injected services.
/// The caller is responsible for refreshing anything it cached from the tag vocabulary
/// (loaded tree nodes' Tags strings, an active tag filter) after the dialog closes;
/// <see cref="Changed"/> reports whether anything actually changed.</summary>
public partial class TagManagerDialog : Window
{
    private record TagRow(string Name, int FolderCount);

    public bool Changed { get; private set; }

    public TagManagerDialog()
    {
        InitializeComponent();
        LoadTags();
    }

    private void LoadTags()
    {
        var usage = DatabaseService.Instance.GetFolderTagUsage();
        var rows = usage.Select(u => new TagRow(u.Name, u.FolderCount)).ToList();
        TagsGrid.ItemsSource = rows;
        StatusText.Text = rows.Count == 0 ? "No folder tags yet." : string.Empty;
        UpdateMergeTargets();
    }

    private void TagsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMergeTargets();

    // The merge-target list excludes whatever's currently selected in the grid.
    private void UpdateMergeTargets()
    {
        var selected = (TagsGrid.SelectedItem as TagRow)?.Name;
        var items = ((IEnumerable<TagRow>)(TagsGrid.ItemsSource ?? Array.Empty<TagRow>()))
            .Select(r => r.Name)
            .Where(n => !string.Equals(n, selected, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        MergeTargetBox.ItemsSource = items;
        if (items.Count > 0) MergeTargetBox.SelectedIndex = 0;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (TagsGrid.SelectedItem is not TagRow row) return;

        var msg = row.FolderCount == 0
            ? $"Delete the unused tag \"{row.Name}\"? This cannot be undone."
            : $"Delete \"{row.Name}\" from {row.FolderCount} folder{(row.FolderCount == 1 ? "" : "s")}? This cannot be undone.";
        if (MessageBox.Show(this, msg, "Delete tag", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        DatabaseService.Instance.DeleteFolderTag(row.Name);
        Changed = true;
        LoadTags();
    }

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (TagsGrid.SelectedItem is not TagRow row) return;
        if (MergeTargetBox.SelectedItem is not string target || string.IsNullOrWhiteSpace(target)) return;

        var msg = $"Merge \"{row.Name}\" into \"{target}\"? Every folder tagged \"{row.Name}\" will be " +
                  $"tagged \"{target}\" instead, and \"{row.Name}\" will be removed. This cannot be undone.";
        if (MessageBox.Show(this, msg, "Merge tag", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        DatabaseService.Instance.MergeFolderTag(row.Name, target);
        Changed = true;
        LoadTags();
    }
}
