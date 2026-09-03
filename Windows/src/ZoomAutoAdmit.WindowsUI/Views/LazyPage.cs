using System.Windows;
using System.Windows.Controls;

namespace ZoomAutoAdmit.WindowsUI.Views;

/// <summary>
/// Holds a page that is not built until it is first shown.
///
/// A TabControl declares all of its pages up front, so every one of the fourteen used to be
/// constructed while the window was still opening — fourteen pages of cards, grids and effects
/// for the one page anybody was looking at. Putting each page inside a DataTemplate here keeps it
/// as markup until the tab is selected. Once built, the page is kept, so returning to a tab is
/// instant and it still holds its scroll position, selection and typed text.
/// </summary>
public sealed class LazyPage : ContentControl
{
    private bool _built;

    public LazyPage()
    {
        // Two triggers, because either can be the last one to arrive: the first tab is already
        // visible when the window opens, but the view model is attached a moment later.
        IsVisibleChanged += (_, _) => BuildIfReady();
        DataContextChanged += (_, _) => BuildIfReady();
    }

    /// <summary>True once the page inside has actually been created.</summary>
    public bool IsBuilt => _built;

    private void BuildIfReady()
    {
        if (_built || !IsVisible || DataContext is null) return;
        _built = true;
        // The template binds against the window's view model, so that is what it is given.
        // Setting Content is what makes WPF instantiate ContentTemplate.
        Content = DataContext;
    }
}
