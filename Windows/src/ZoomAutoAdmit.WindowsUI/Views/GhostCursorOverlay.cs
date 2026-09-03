using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZoomAutoAdmit.WindowsUI.Views;

/// <summary>Native WPF interpretation of the supplied GhostCursor: no web host, input capture or desktop cursor movement.</summary>
public sealed class GhostCursorOverlay : FrameworkElement
{
    public static readonly DependencyProperty IsEffectEnabledProperty = DependencyProperty.Register(
        // Off unless the person asks for it in Settings. The trail animates on every pointer move,
    // which is the last thing a window full of live tables needs by default.
    nameof(IsEffectEnabled), typeof(bool), typeof(GhostCursorOverlay), new PropertyMetadata(false, OnEnabledChanged));
    public bool IsEffectEnabled { get => (bool)GetValue(IsEffectEnabledProperty); set => SetValue(IsEffectEnabledProperty, value); }
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Point> _trail = [];
    private readonly Brush _smoke;
    private Window? _window;
    private Point _head, _target;
    private double _lastMove, _opacity;
    public bool IsAnimating => _timer.IsEnabled;
    public bool HasVisibleTrail => _trail.Count > 0 && _opacity > 0;

    public GhostCursorOverlay()
    {
        IsHitTestVisible = false; Focusable = false; ClipToBounds = true;
        _smoke = new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(125, 180, 151, 207), 0),
            new(Color.FromArgb(65, 131, 141, 255), .35),
            new(Color.FromArgb(15, 116, 124, 249), .7),
            new(Colors.Transparent, 1)
        });
        _smoke.Freeze();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, Tick, Dispatcher);
        _timer.Stop();
        Loaded += Attach; Unloaded += Detach;
    }
    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    { if (!(bool)e.NewValue) ((GhostCursorOverlay)d).Stop(); }
    private void Attach(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window == null) return;
        _window.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(Move), true);
        _window.Deactivated += Deactivate;
    }
    private void Detach(object sender, RoutedEventArgs e)
    {
        if (_window != null)
        {
            _window.RemoveHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(Move));
            _window.Deactivated -= Deactivate;
            _window = null;
        }
        Stop();
    }
    private void Deactivate(object? sender, EventArgs e) => Stop();
    private void Move(object sender, MouseEventArgs e) => TrackPointer(e.GetPosition(this));
    public void TrackPointer(Point point)
    {
        if (!IsLoaded || !IsEffectEnabled) return;
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return;
        if (_trail.Count == 0) _head = point;
        _target = point; _lastMove = _clock.Elapsed.TotalMilliseconds; _opacity = 1;
        if (!_timer.IsEnabled) _timer.Start();
    }
    private void Tick(object? sender, EventArgs e)
    {
        if (!IsEffectEnabled || !IsVisible) { Stop(); return; }
        _head += (_target - _head) * .5;
        _trail.Insert(0, _head);
        if (_trail.Count > 50) _trail.RemoveAt(50);
        var idle = _clock.Elapsed.TotalMilliseconds - _lastMove;
        _opacity = Math.Clamp(1 - (idle - 1000) / 1500, 0, 1);
        if (_opacity <= 0) Stop(); else InvalidateVisual();
    }
    private void Stop() { _timer.Stop(); _trail.Clear(); _opacity = 0; InvalidateVisual(); }
    protected override Size ArrangeOverride(Size finalSize) => finalSize;
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        // Bounded translucent wisps; input keeps reaching every underlying control.
        for (int i = _trail.Count - 1; i >= 0; i -= 2)
        {
            double weight = Math.Pow(1 - i / 50.0, 2);
            var p = _trail[i];
            var phase = _clock.Elapsed.TotalSeconds * 1.7 + i * .4;
            double radius = 20 + weight * 28;
            drawing.PushOpacity(_opacity * weight * .22);
            drawing.DrawEllipse(_smoke, null, new Point(p.X + Math.Sin(phase) * 12, p.Y + Math.Cos(phase) * 9), radius, radius * .72);
            drawing.Pop();
        }
    }
}
