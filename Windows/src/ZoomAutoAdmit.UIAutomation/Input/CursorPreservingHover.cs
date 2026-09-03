using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ZoomAutoAdmit.UIAutomation.Input;

public interface ICursorController
{
    (int X, int Y) GetPosition();
    void MoveTo(int x, int y);
}

public sealed class CursorPreservingHover
{
    private readonly ICursorController _cursor;

    public CursorPreservingHover(ICursorController cursor)
    {
        _cursor = cursor;
    }

    public T Run<T>(int hoverX, int hoverY, Func<T> whileHovered)
    {
        var original = _cursor.GetPosition();
        try
        {
            _cursor.MoveTo(hoverX, hoverY);
            return whileHovered();
        }
        finally
        {
            _cursor.MoveTo(original.X, original.Y);
        }
    }
}

public sealed class CursorPreservingSession
{
    private readonly ICursorController _cursor;
    public CursorPreservingSession(ICursorController cursor) => _cursor = cursor;

    public T Run<T>(Func<(int X, int Y), T> whileActive)
    {
        var original = _cursor.GetPosition();
        try { return whileActive(original); }
        finally { _cursor.MoveTo(original.X, original.Y); }
    }
}

public sealed record SyntheticHoverTrace(
    (int X, int Y) NeutralPoint,
    IReadOnlyList<(int X, int Y)> MovementPoints,
    (int X, int Y) FinalPoint);

public static class WaitingRowHoverPath
{
    public static (int X, int Y) GetEntryPoint(
        ZoomAutoAdmit.Core.Models.WaitingParticipantRowCandidate row)
    {
        if (row == null) throw new ArgumentNullException(nameof(row));
        int left = checked((int)Math.Round(row.RowBounds.X));
        int right = checked((int)Math.Round(row.RowBounds.X + row.RowBounds.Width));
        int top = checked((int)Math.Round(row.RowBounds.Y));
        int bottom = checked((int)Math.Round(row.RowBounds.Y + row.RowBounds.Height));
        int inset = checked((int)Math.Round(Math.Clamp(row.RowBounds.Width * 0.04, 8, 18)));
        int x = Math.Clamp(left + inset, left + 1, Math.Max(left + 1, right - 1));
        int y = Math.Clamp(
            checked((int)Math.Round(row.SafeHoverPoint.Y)),
            top + 1,
            Math.Max(top + 1, bottom - 1));
        return (x, y);
    }
}

public sealed class SyntheticHoverActivator
{
    private readonly ICursorController _cursor;
    private readonly Action<int> _wait;

    public SyntheticHoverActivator(ICursorController cursor, Action<int>? wait = null)
    {
        _cursor = cursor;
        _wait = wait ?? Thread.Sleep;
    }

    public SyntheticHoverTrace Activate(
        (int X, int Y) neutral,
        (int X, int Y) final,
        int steps = 6)
    {
        _cursor.MoveTo(neutral.X, neutral.Y);
        _wait(75);
        var points = new List<(int X, int Y)> { neutral };
        int verticalSteps = Math.Max(2, steps / 2);
        for (int index = 1; index <= verticalSteps; index++)
        {
            double progress = index / (double)verticalSteps;
            var point = (
                neutral.X,
                (int)Math.Round(neutral.Y + (final.Y - neutral.Y) * progress));
            _cursor.MoveTo(point.Item1, point.Item2);
            points.Add(point);
            _wait(15);
        }

        int horizontalSteps = Math.Max(2, steps);
        for (int index = 1; index <= horizontalSteps; index++)
        {
            double progress = index / (double)horizontalSteps;
            var point = (
                (int)Math.Round(neutral.X + (final.X - neutral.X) * progress),
                final.Y);
            _cursor.MoveTo(point.Item1, point.Item2);
            points.Add(point);
            _wait(15);
        }

        _cursor.MoveTo(final.X + 3, final.Y);
        points.Add((final.X + 3, final.Y));
        _wait(20);
        _cursor.MoveTo(final.X - 3, final.Y);
        points.Add((final.X - 3, final.Y));
        _wait(20);
        _cursor.MoveTo(final.X, final.Y);
        points.Add(final);
        _wait(400);
        return new SyntheticHoverTrace(neutral, points, final);
    }
}

public static class HoverActivationPolicy
{
    public const int MaximumAttempts = 2;
    public const double MinimumChangedPixelsPercentage = 0.50;

    public static bool IsActivated(double changedPixelsPercentage) =>
        changedPixelsPercentage >= MinimumChangedPixelsPercentage;

    public static bool CanAttempt(int oneBasedAttempt) =>
        oneBasedAttempt >= 1 && oneBasedAttempt <= MaximumAttempts;
}

public sealed class HoverThenSingleClickExecutor
{
    private readonly ICursorController _cursor;
    private readonly IMouseInput _mouseInput;

    public HoverThenSingleClickExecutor(ICursorController cursor, IMouseInput mouseInput)
    {
        _cursor = cursor;
        _mouseInput = mouseInput;
    }

    public bool TryRun(int hoverX, int hoverY, Func<(int X, int Y)?> locateFreshTarget)
    {
        var hover = new CursorPreservingHover(_cursor);
        return hover.Run(hoverX, hoverY, () =>
        {
            var target = locateFreshTarget();
            if (target == null) return false;

            var click = new SingleClickExecutor(_mouseInput);
            return click.TryClick(target.Value.X, target.Value.Y);
        });
    }

    public HoverSingleClickResult RunWithPostClick(
        int hoverX,
        int hoverY,
        Func<(int X, int Y)?> locateFreshTarget,
        Func<bool> verifyAfterClickWhileHovering)
    {
        var hover = new CursorPreservingHover(_cursor);
        return hover.Run(hoverX, hoverY, () =>
        {
            var target = locateFreshTarget();
            if (target == null) return new HoverSingleClickResult(false, false);

            var click = new SingleClickExecutor(_mouseInput);
            if (!click.TryClick(target.Value.X, target.Value.Y))
                return new HoverSingleClickResult(false, false);
            return new HoverSingleClickResult(true, verifyAfterClickWhileHovering());
        });
    }
}

public sealed record HoverSingleClickResult(bool ClickSent, bool PostClickVerified);

public sealed class WindowsCursorController : ICursorController
{
    public (int X, int Y) GetPosition()
    {
        if (!GetCursorPos(out var point))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetCursorPos failed.");
        }
        return (point.X, point.Y);
    }

    public void MoveTo(int x, int y)
    {
        if (!SetCursorPos(x, y))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetCursorPos failed.");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
}
