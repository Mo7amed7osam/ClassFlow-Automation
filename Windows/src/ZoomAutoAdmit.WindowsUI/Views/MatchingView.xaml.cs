using System.Windows.Controls;
using System.Windows;
using ZoomAutoAdmit.WindowsUI.ViewModels;
namespace ZoomAutoAdmit.WindowsUI.Views;
public partial class MatchingView : UserControl
{
    public MatchingView() { InitializeComponent(); Unloaded += (_, _) => ApiKey.Clear(); }
    private void ProviderChanged(object sender, SelectionChangedEventArgs e) => ApiKey?.Clear();
    private void KeyChanged(object sender, RoutedEventArgs e)
    {
        using var secret = ApiKey.SecurePassword;
        if (secret.Length > 0 && DataContext is MainViewModel vm) vm.AiMatching.NewKeyEntered();
    }
    private async void TestAndSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.AiMatching.IsIdle) return;
        var secret = ApiKey.Password;
        ApiKey.Clear();
        await vm.AiMatching.TestAndSaveAsync(secret);
    }
}
