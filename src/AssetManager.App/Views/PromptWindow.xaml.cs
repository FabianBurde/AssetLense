using System.Windows;
using AssetManager.App.Services;

namespace AssetManager.App.Views;

/// <summary>A small "type a name" dialog.</summary>
public partial class PromptWindow : Window
{
    private PromptWindow(string title, string label, string initial)
    {
        InitializeComponent();
        ThemeService.Current?.Attach(this);

        Title = title;
        LabelText.Text = label;
        InputBox.Text = initial;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(InputBox.Text))
            DialogResult = true;
    }

    /// <summary>Returns the trimmed text, or null if the dialog was cancelled.</summary>
    public static string? Ask(Window? owner, string title, string label, string initial)
    {
        var window = new PromptWindow(title, label, initial) { Owner = owner };
        return window.ShowDialog() == true ? window.InputBox.Text.Trim() : null;
    }
}
