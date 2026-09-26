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

    private bool Shown => ShowZoomPassword.IsChecked == true;

    /// <summary>What is in the password box, whichever of the two is showing.</summary>
    private string Typed => Shown ? ZoomPasswordShown.Text : ZoomPassword.Password;

    private void ClearTyped()
    {
        ZoomPassword.Clear();
        ZoomPasswordShown.Clear();
    }

    private async void SavePassword(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Model(sender) is not { } model) return;
        // Only a password that was kept somewhere leaves the box: clearing it either way is how a
        // password typed again and again looked as though it had been saved each time. Shown, it
        // stays in view - it is the account's own saved password now.
        if (await model.SavePasswordAsync(Typed) && !Shown) ClearTyped();
    }

    private void ForgetPassword(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Model(sender) is { } model) model.ForgetPassword();
        ClearTyped();
    }

    /// <summary>
    /// Another account chosen: the box is emptied, so a password typed for one account is never
    /// shown against, or saved for, the next (it stayed in the box before). With "Show password"
    /// ticked, the new account's saved password is shown instead - the box stays ticked.
    /// </summary>
    private void AccountChanged(object sender, SelectionChangedEventArgs e)
    {
        ClearTyped();
        if (Shown && Model(sender) is { } model) ZoomPasswordShown.Text = model.SavedPassword() ?? "";
    }

    private void ShowPasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Shown)
        {
            string typed = ZoomPassword.Password;
            ZoomPasswordShown.Text = typed.Length > 0 ? typed : Model(sender)?.SavedPassword() ?? "";
            ZoomPassword.Visibility = System.Windows.Visibility.Collapsed;
            ZoomPasswordShown.Visibility = System.Windows.Visibility.Visible;
        }
        else
        {
            ZoomPassword.Password = ZoomPasswordShown.Text;
            ZoomPasswordShown.Visibility = System.Windows.Visibility.Collapsed;
            ZoomPassword.Visibility = System.Windows.Visibility.Visible;
        }
    }
}
