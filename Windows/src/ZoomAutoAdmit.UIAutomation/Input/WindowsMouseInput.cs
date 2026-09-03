using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ZoomAutoAdmit.UIAutomation.Input;

public interface IMouseInput
{
    void LeftClickOncePreservingCursor(int x, int y);
    void ScrollWheelPreservingCursor(int x, int y, int wheelDelta);
}

/// <summary>
/// Sends one left click and then refuses, so a single decision to admit can never turn into a
/// burst of clicks if the caller retries.
/// </summary>
/// <remarks>
/// The refusal lasts for the lifetime of the instance, so one belongs to one admit attempt.
/// Keeping an instance in a field makes a watcher click for the first participant only and
/// silently ignore everyone after them - build a new one per attempt.
/// </remarks>
public sealed class SingleClickExecutor
{
    private readonly IMouseInput _mouseInput;
    private int _clickAttempted;

    public SingleClickExecutor(IMouseInput mouseInput)
    {
        _mouseInput = mouseInput;
    }

    public bool TryClick(int x, int y)
    {
        if (Interlocked.Exchange(ref _clickAttempted, 1) != 0)
        {
            return false;
        }

        _mouseInput.LeftClickOncePreservingCursor(x, y);
        return true;
    }
}

public sealed class WindowsMouseInput : IMouseInput
{
    private const uint InputMouse = 0;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventWheel = 0x0800;

    public void DirectClick(int x, int y)
    {
        try
        {
            SetCursorPos(x, y);
            Thread.Sleep(30);

            var inputs = new[]
            {
                CreateMouseInput(MouseEventLeftDown),
                CreateMouseInput(MouseEventLeftUp)
            };

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }
        catch { }
    }

    public void LeftClickOncePreservingCursor(int x, int y)
    {
        bool hasOriginal = GetCursorPos(out var original);

        try
        {
            SetCursorPos(x, y);

            var inputs = new[]
            {
                CreateMouseInput(MouseEventLeftDown),
                CreateMouseInput(MouseEventLeftUp)
            };

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }
        finally
        {
            if (hasOriginal)
            {
                try { SetCursorPos(original.X, original.Y); }
                catch { }
            }
        }
    }

    public void ScrollWheelPreservingCursor(int x, int y, int wheelDelta)
    {
        bool hasOriginal = GetCursorPos(out var original);

        try
        {
            SetCursorPos(x, y);

            var input = new INPUT
            {
                Type = InputMouse,
                Union = new INPUTUNION
                {
                    Mouse = new MOUSEINPUT
                    {
                        Flags = MouseEventWheel,
                        MouseData = unchecked((uint)wheelDelta)
                    }
                }
            };

            var inputs = new[] { input };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }
        finally
        {
            if (hasOriginal)
            {
                try { SetCursorPos(original.X, original.Y); }
                catch { }
            }
        }
    }

    private static INPUT CreateMouseInput(uint flags) => new()
    {
        Type = InputMouse,
        Union = new INPUTUNION
        {
            Mouse = new MOUSEINPUT { Flags = flags }
        }
    };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, INPUT[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public INPUTUNION Union; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT Mouse; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}
