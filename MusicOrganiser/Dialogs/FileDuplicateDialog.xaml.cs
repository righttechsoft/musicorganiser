using System.Windows;
using MusicOrganiser.Models;

namespace MusicOrganiser.Dialogs;

public partial class FileDuplicateDialog : Window
{
    public FileDuplicateAction Result { get; private set; } = FileDuplicateAction.Cancel;
    public bool ApplyToAll => ApplyToAllCheckBox.IsChecked == true;
    public bool ShowApplyToAll { get; set; }

    public FileDuplicateDialog(FileComparisonInfo source, FileComparisonInfo target, bool showApplyToAll = false)
    {
        InitializeComponent();
        ShowApplyToAll = showApplyToAll;
        DataContext = this;

        // Populate source info
        SourceFileName.Text = source.FileName;
        SourceFileSize.Text = source.FileSizeFormatted;
        SourceModified.Text = source.ModifiedDateFormatted;
        SourceDuration.Text = source.DurationFormatted;
        SourceBitrate.Text = source.BitrateFormatted;
        SourceArtist.Text = source.Artist;
        SourceTitle.Text = source.Title;
        SourceAlbum.Text = source.Album;

        // Populate target info
        TargetFileName.Text = target.FileName;
        TargetFileSize.Text = target.FileSizeFormatted;
        TargetModified.Text = target.ModifiedDateFormatted;
        TargetDuration.Text = target.DurationFormatted;
        TargetBitrate.Text = target.BitrateFormatted;
        TargetArtist.Text = target.Artist;
        TargetTitle.Text = target.Title;
        TargetAlbum.Text = target.Album;
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        Result = FileDuplicateAction.Skip;
        DialogResult = true;
    }

    private void OverwriteButton_Click(object sender, RoutedEventArgs e)
    {
        Result = FileDuplicateAction.Overwrite;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = FileDuplicateAction.Cancel;
        DialogResult = false;
    }
}
