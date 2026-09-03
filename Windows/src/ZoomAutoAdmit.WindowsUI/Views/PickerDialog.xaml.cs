using System.Windows;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.Views;

public partial class PickerDialog : Window
{
    public PickerDialog(string title, string prompt, IReadOnlyList<PickerOption> options)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        Options.ItemsSource = options;
        Options.SelectedIndex = 0;
    }

    /// <summary>The chosen option's key; null until someone confirms a selection.</summary>
    public string? SelectedKey { get; private set; }

    private void ChooseClicked(object sender, RoutedEventArgs e)
    {
        if (Options.SelectedItem is not PickerOption option) return;
        SelectedKey = option.Key;
        DialogResult = true;
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => DialogResult = false;
}
