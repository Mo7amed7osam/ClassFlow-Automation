using System.Windows.Controls;
namespace ZoomAutoAdmit.WindowsUI.Views;
public partial class AccountsView : UserControl
{
    public AccountsView() => InitializeComponent();

    /// <summary>
    /// The page's own model. The page itself is given the main window's model and its panel binds
    /// to that model's Accounts, so asking the page alone found nothing - and Save password quietly
    /// did nothing, every time it was pressed (found 2026-09-26).
    /// </summary>
    private ViewModels.AccountsViewModel? Model(object sender) =>
        (sender as System.Windows.FrameworkElement)?.DataContext as ViewModels.AccountsViewModel
        ?? DataContext as ViewModels.AccountsViewModel
        ?? (DataContext as ViewModels.MainViewModel)?.Accounts;

    private async void SavePassword(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Model(sender) is not { } model) return;
        // Only a password that was kept somewhere leaves the box: clearing it either way is how a
        // password typed again and again looked as though it had been saved each time.
        if (await model.SavePasswordAsync(ZoomPassword.Password)) ZoomPassword.Clear();
    }

    private void ForgetPassword(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Model(sender) is { } model) model.ForgetPassword();
        ZoomPassword.Clear();
    }
}
