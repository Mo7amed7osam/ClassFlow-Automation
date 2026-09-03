using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.UIAutomation.Inspection;

namespace ZoomAutoAdmit.Attendance;

/// <summary>Read-only UIA reader bound to one meeting window and one Joined-list root.</summary>
public sealed class DesktopAttendanceParticipantSource : IAttendanceParticipantSource
{
    private readonly IntPtr _meetingWindow;
    private readonly int _processId;
    private readonly string _joinedListAutomationId;
    private readonly string _participantNameAutomationId;
    public AttendanceSource Source => AttendanceSource.Desktop;

    // Explicit UIA IDs come from inspection of the current Zoom layout, never screen coordinates.
    public DesktopAttendanceParticipantSource(IntPtr meetingWindow, int processId,
        string joinedListAutomationId, string participantNameAutomationId)
    {
        if (meetingWindow == IntPtr.Zero) throw new ArgumentException("Meeting HWND is required.");
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        ArgumentException.ThrowIfNullOrWhiteSpace(joinedListAutomationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(participantNameAutomationId);
        _meetingWindow = meetingWindow;
        _processId = processId;
        _joinedListAutomationId = joinedListAutomationId;
        _participantNameAutomationId = participantNameAutomationId;
    }

    public async Task<ParticipantReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<ParticipantReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Own STA and UIA connection; never shares the admission engine's automation instance.
        var thread = new Thread(() =>
        {
            try
            {
                using var automation = new UIA3Automation();
                var window = automation.FromHandle(_meetingWindow);
                if (window.Properties.ProcessId.Value != _processId)
                    throw new InvalidOperationException("Attendance meeting window ownership changed.");
                var lists = window.FindAllDescendants(cf => cf.ByAutomationId(_joinedListAutomationId));
                if (lists.Length != 1 || lists[0].Properties.IsOffscreen.ValueOrDefault)
                    throw new InvalidOperationException("Exactly one exposed Joined list is required for attendance.");
                var root = ReadTree(lists[0], 0, new int[1], cancellationToken);
                completion.TrySetResult(ExtractParticipants(root, _participantNameAutomationId));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { completion.TrySetCanceled(cancellationToken); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "Attendance UIA read" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // Do not abandon/restart stuck native UIA calls on every tick. The dedicated worker is
        // read-only; native UIA calls must return before it can observe cancellation.
        return await completion.Task;
    }

    private static InspectElementInfo ReadTree(AutomationElement element, int depth, int[] count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 30 || ++count[0] > 10000)
            throw new InvalidOperationException("Attendance UIA traversal limit reached.");
        var node = FlaUiElementExtractor.ExtractElementInfo(element, depth);
        foreach (var child in element.FindAllChildren()) node.Children.Add(ReadTree(child, depth + 1, count, token));
        return node;
    }

    public static ParticipantReadResult ExtractParticipants(InspectElementInfo joinedList, string nameAutomationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameAutomationId);
        if (joinedList.Name.Contains("waiting room", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Attendance must be scoped to Joined, not Waiting Room.");
        var names = new List<ParticipantPresence>();
        void Visit(InspectElementInfo node)
        {
            if (node.DiagnosticError != null) throw new InvalidOperationException("Incomplete UIA read: " + node.DiagnosticError);
            if (node.ControlType is "Button" or "MenuItem" or "Menu") return;
            if (node.AutomationId == nameAutomationId)
            {
                if (string.IsNullOrWhiteSpace(node.Name)) throw new InvalidOperationException("Participant name unavailable.");
                names.Add(new(node.Name));
                return;
            }
            foreach (var child in node.Children) Visit(child);
        }
        Visit(joinedList);
        if (names.Count == 0)
            throw new InvalidOperationException("No participant names exposed; cannot distinguish empty from unavailable UIA rows.");
        return new(names, false, "UIA-exposed Joined-list names only; virtualized/collapsed rows may not be exposed.");
    }
}
