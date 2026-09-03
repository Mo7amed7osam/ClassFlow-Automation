using System.Windows;
using System.Windows.Controls;
using ZoomAutoAdmit.WindowsUI.ViewModels;

namespace ZoomAutoAdmit.WindowsUI.Views;

public partial class StartMeetingView : UserControl
{
    public StartMeetingView() => InitializeComponent();

    /// <summary>
    /// Hands the typed password to the view model and clears the box straight away.
    ///
    /// A PasswordBox keeps its value out of the binding system on purpose, so this passes it once,
    /// by hand, rather than exposing it as a bindable property that WPF would hold on to.
    /// </summary>
    private void SaveLmsLogin(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        var box = LmsPassword;
        main.Lms.SaveLogin(box.Password);
        box.Clear();
    }
}
