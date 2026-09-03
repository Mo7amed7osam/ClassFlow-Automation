using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace ZoomAutoAdmit.WindowsUI.Infrastructure;

/// <summary>
/// Letter tracking for the small upper-case labels. WPF has no letter-spacing property, so the
/// characters are joined with hair spaces instead. The untouched words stay on the element as its
/// automation name, so a screen reader still reads "SESSION SUMMARY", not "S E S S I O N".
/// </summary>
public static class Typography
{
    private const char HairSpace = ' ';

    public static readonly DependencyProperty TrackingProperty = DependencyProperty.RegisterAttached(
        "Tracking", typeof(bool), typeof(Typography), new PropertyMetadata(false, OnTrackingChanged));

    public static void SetTracking(DependencyObject element, bool value) => element.SetValue(TrackingProperty, value);
    public static bool GetTracking(DependencyObject element) => (bool)element.GetValue(TrackingProperty);

    private static void OnTrackingChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock text || !GetTracking(element)) return;
        if (text.IsLoaded) Apply(text);
        else text.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock text) return;
        text.Loaded -= OnLoaded;
        Apply(text);
    }

    private static void Apply(TextBlock text)
    {
        // A bound label changes at runtime; rewriting it here would fight the binding.
        if (BindingOperations.GetBindingExpressionBase(text, TextBlock.TextProperty) != null) return;
        string original = text.Text;
        if (string.IsNullOrWhiteSpace(original) || original.Contains(HairSpace)) return;
        AutomationProperties.SetName(text, original);
        text.Text = string.Join(HairSpace.ToString(), original.ToCharArray());
    }
}
