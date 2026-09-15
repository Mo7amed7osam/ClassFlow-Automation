using System.Windows;
using System.Windows.Controls;
using ZoomAutoAdmit.WindowsUI.ViewModels;

namespace ZoomAutoAdmit.WindowsUI.Views;

public partial class CentralRecordingsView : UserControl
{
    public CentralRecordingsView()
    {
        InitializeComponent();
        Loaded += async (_, _) => { if (Model is { } m) await m.RefreshAsync(); };
    }

    private CentralViewModel? Model => (DataContext as MainViewModel)?.Central;

    // Read from the PasswordBox and cleared at once: a binding would keep it alive.
    private async void SignIn(object sender, RoutedEventArgs e)
    {
        string password = Password.Password;
        Password.Clear();
        if (Model is { } m) await m.SignInAsync(password, Remember.IsChecked == true);
    }
}
