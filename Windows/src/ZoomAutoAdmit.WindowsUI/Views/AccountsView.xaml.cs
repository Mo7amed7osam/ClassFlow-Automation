using System.Windows.Controls;
namespace ZoomAutoAdmit.WindowsUI.Views;
public partial class AccountsView : UserControl
{
    public AccountsView() => InitializeComponent();

    private void SavePassword(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.AccountsViewModel model) model.SavePassword(ZoomPassword.Password);
        ZoomPassword.Clear();
    }

    private void ForgetPassword(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.AccountsViewModel model) model.ForgetPassword();
        ZoomPassword.Clear();
    }
}
