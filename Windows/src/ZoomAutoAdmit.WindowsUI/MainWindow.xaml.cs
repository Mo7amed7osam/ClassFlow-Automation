using System.Windows;
using System.Windows.Controls;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>
    /// Puts on the look this machine last chose. The window is born as Midnight dark so its
    /// default never depends on a file, and the app calls this once at startup.
    /// </summary>
    public void RestoreSavedDesign() => Apply(UiPreferences.Load());

    public bool IsDarkTheme => _preference.Dark;
    public UiDesign Design => _preference.Design;

    private UiPreference _preference = new(UiDesign.Midnight, true);

    /// <summary>Swaps the palette dictionary. Only colours change; no layout or behaviour does.</summary>
    private void Apply(UiPreference preference)
    {
        Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri(preference.ThemePath, UriKind.Relative)
        };
        _preference = preference;
        UiPreferences.Save(preference);
        if (DesignName != null) DesignName.Text = DescribeDesign(preference.Design);
    }

    public void ApplyTheme(bool dark) => Apply(_preference with { Dark = dark });

    private static string DescribeDesign(UiDesign design) => design switch
    {
        UiDesign.Ice => "Ice",
        UiDesign.Teal => "Deep teal",
        UiDesign.Slate => "Warm slate",
        _ => "Midnight"
    };

    private void ToggleTheme(object sender, RoutedEventArgs e) => ApplyTheme(!IsDarkTheme);
    private void UseDarkTheme(object sender, RoutedEventArgs e) => ApplyTheme(true);
    private void UseLightTheme(object sender, RoutedEventArgs e) => ApplyTheme(false);

    private void PickDesign(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement source) return;
        if (source.ContextMenu is not { } menu) return;
        menu.PlacementTarget = source;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ChooseDesign(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        if (!Enum.TryParse<UiDesign>(tag, out var design)) return;
        // Ice is a light design and Midnight a dark one; picking either starts in the mode it was
        // drawn for. The sun button still switches modes afterwards.
        bool dark = design switch { UiDesign.Ice => false, UiDesign.Midnight => true, _ => _preference.Dark };
        Apply(new UiPreference(design, dark));
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
