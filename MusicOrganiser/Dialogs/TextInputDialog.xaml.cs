using System.Windows;
using System.Windows.Input;

namespace MusicOrganiser.Dialogs;

/// <summary>Simple single-line text prompt (used for creating/renaming playlists).</summary>
public partial class TextInputDialog : Window
{
    public string InputText => InputBox.Text.Trim();

    public TextInputDialog(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Accept();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Accept();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text)) return;
        DialogResult = true;
    }
}
