using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MusicOrganiser.Dialogs;

/// <summary>Lets the user pick tags for a folder: current tags start checked, an AI
/// fetch (kicked off on Loaded) appends genre suggestions as new checkboxes, and a
/// text box lets the user add custom tags. Dumb by design — the AI call is injected
/// via [fetchSuggestions] so this dialog has no service dependencies.</summary>
public partial class FolderTagDialog : Window
{
    private readonly Func<Task<IReadOnlyList<string>?>> _fetchSuggestions;
    private readonly HashSet<string> _currentTags;
    private bool _closed;

    public string ResultTags { get; private set; } = string.Empty;

    public FolderTagDialog(string folderName, string currentTagsCsv, Func<Task<IReadOnlyList<string>?>> fetchSuggestions)
    {
        InitializeComponent();
        Title = $"Tag folder — {folderName}";
        _fetchSuggestions = fetchSuggestions;

        _currentTags = (currentTagsCsv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in _currentTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            AddTagCheckBox(tag, isNew: false);

        StatusText.Text = string.Empty;
        Loaded += FolderTagDialog_Loaded;
        Closed += (_, _) => _closed = true;
    }

    private async void FolderTagDialog_Loaded(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Searching genre...";
        IReadOnlyList<string>? suggestions = null;
        try
        {
            suggestions = await _fetchSuggestions();
        }
        catch
        {
            suggestions = null;
        }

        if (_closed) return;

        if (suggestions == null)
        {
            StatusText.Text = "AI unavailable (no API key?) — edit tags manually.";
            return;
        }

        var added = 0;
        foreach (var tag in suggestions)
        {
            if (_currentTags.Contains(tag)) continue;
            AddTagCheckBox(tag, isNew: true);
            _currentTags.Add(tag);
            added++;
        }

        StatusText.Text = added > 0 ? $"Added {added} suggested tag{(added == 1 ? "" : "s")}." : "No new suggestions.";
    }

    private void AddTagCheckBox(string tag, bool isNew)
    {
        var checkBox = new CheckBox
        {
            Content = tag,
            IsChecked = true,
            Margin = new Thickness(0, 0, 12, 8),
            Tag = tag
        };
        if (isNew)
            checkBox.Foreground = Brushes.SteelBlue;
        TagsPanel.Children.Add(checkBox);
    }

    private void AddTag_Click(object sender, RoutedEventArgs e) => AddCustomTag();

    private void NewTagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddCustomTag();
            e.Handled = true;
        }
    }

    private void AddCustomTag()
    {
        var tag = NewTagBox.Text.Trim();
        NewTagBox.Clear();
        if (string.IsNullOrEmpty(tag) || _currentTags.Contains(tag)) return;

        AddTagCheckBox(tag, isNew: false);
        _currentTags.Add(tag);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var tags = new List<string>();
        foreach (var child in TagsPanel.Children)
        {
            if (child is CheckBox { IsChecked: true } cb && cb.Tag is string tag)
                tags.Add(tag);
        }
        ResultTags = string.Join(", ", tags.Distinct(StringComparer.OrdinalIgnoreCase));
        DialogResult = true;
    }
}
