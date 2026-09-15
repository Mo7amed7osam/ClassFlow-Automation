using System.Windows;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI;

public partial class MainWindow : Window
{
    /// <summary>Below this width the sidebar keeps only its icons.</summary>
    public const double CompactBelow = 1080;

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false, (o, e) => ((MainWindow)o).OnCompactChanged((bool)e.NewValue)));

    /// <summary>Raised after night/day changes, so pages drawn outside WPF (the attendance page) can follow.</summary>
    public static event Action<bool>? ThemeChanged;
    public static bool CurrentIsDark { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => IsCompact = ActualWidth < CompactBelow;
        // The log lists are shown here, so lines written on other threads come through this window's thread.
        DataContextChanged += (_, e) => { if (e.NewValue is ViewModels.MainViewModel main) main.Logs.ShowOn(Dispatcher); };
        ShowGlyph();
    }

    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    private void OnCompactChanged(bool compact)
    {
        if (compact) SidebarColumn.Width = new GridLength(68);
        else SidebarColumn.SetResourceReference(System.Windows.Controls.ColumnDefinition.WidthProperty, "SidebarWidth");
        SidebarBody.Margin = compact ? new Thickness(10, 14, 10, 12) : new Thickness(12, 14, 12, 12);
        PageArea.Margin = compact ? new Thickness(18, 0, 16, 14) : new Thickness(28, 0, 24, 18);
    }

    /// <summary>Puts on night or day as this machine last chose. The window is born in day mode.</summary>
    public void RestoreSavedDesign() => Apply(UiPreferences.Load());

    public bool IsDarkTheme => _preference.Dark;

    private UiPreference _preference = new(false);

    /// <summary>Swaps the palette dictionary. Only colours change; no layout or behaviour does.</summary>
    private void Apply(UiPreference preference)
    {
        Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri(preference.ThemePath, UriKind.Relative)
        };
        _preference = preference;
        UiPreferences.Save(preference);
        ShowGlyph();
        CurrentIsDark = preference.Dark;
        ThemeChanged?.Invoke(preference.Dark);
    }

    // The button shows what it switches to: a moon by day, a sun by night.
    private void ShowGlyph() { if (ThemeGlyph != null) ThemeGlyph.Text = _preference.Dark ? "\uE706" : "\uE708"; }

    public void ApplyTheme(bool dark) => Apply(_preference with { Dark = dark });

    private void ToggleTheme(object sender, RoutedEventArgs e) => ApplyTheme(!IsDarkTheme);
    private void UseDarkTheme(object sender, RoutedEventArgs e) => ApplyTheme(true);
    private void UseLightTheme(object sender, RoutedEventArgs e) => ApplyTheme(false);

    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
