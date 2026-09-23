using System.Windows.Controls;
namespace ZoomAutoAdmit.WindowsUI.Views;
public partial class AccountsView : UserControl
{
    public AccountsView() => InitializeComponent();

    private async void SavePassword(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.AccountsViewModel model) return;
        // Only a password that was kept somewhere leaves the box: clearing it either way is how a
        // password typed again and again looked as though it had been saved each time.
        if (await model.SavePasswordAsync(ZoomPassword.Password)) ZoomPassword.Clear();
    }

    private void ForgetPassword(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.AccountsViewModel model) model.ForgetPassword();
        ZoomPassword.Clear();
    }
}
