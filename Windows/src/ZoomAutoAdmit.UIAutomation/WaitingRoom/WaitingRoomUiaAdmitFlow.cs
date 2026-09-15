using ZoomAutoAdmit.Core.Formatting;

namespace ZoomAutoAdmit.UIAutomation.WaitingRoom;

public sealed record WaitingRoomUiaParticipant(string Key, string DisplayName);

public interface IWaitingRoomUiaSession
{
    IReadOnlyList<WaitingRoomUiaParticipant> ReadWaitingParticipants();
    bool RevealRowActions(WaitingRoomUiaParticipant participant);
    bool TryInvokeRowAction(WaitingRoomUiaParticipant participant, string actionName);
}

/// <summary>
/// Executes admission actions only through the UI Automation participant-row scope.
/// Detection and monitor cadence remain owned by the existing Auto Admit monitor.
/// </summary>
public sealed class WaitingRoomUiaAdmitFlow
{
    private readonly Action<TimeSpan> _wait;
    private readonly Action<string> _admitted;

    /// <param name="admitted">Told each name let in; by default it is counted and written down.</param>
    public WaitingRoomUiaAdmitFlow(Action<TimeSpan>? wait = null, Action<string>? admitted = null)
    {
        _wait = wait ?? (delay => Thread.Sleep(delay));
        _admitted = admitted ?? (wait == null ? name => ZoomAutoAdmit.Core.Meetings.MeetingAdmissionScope.NotifyVerified(name) : _ => { });
    }

    public bool TryAdmitWaitingParticipants(
        IWaitingRoomUiaSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        bool admittedAny = false;
        var participants = session.ReadWaitingParticipants();
        foreach (var original in participants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConsoleLogger.Info($"[WAITING_ROOM] Participant detected: {original.DisplayName}");

            if (TryInvokeAdmit(session, original))
            {
                admittedAny = true;
                continue;
            }

            ConsoleLogger.Info($"[WAITING_ROOM] Hovering participant row: {original.DisplayName}");
            session.RevealRowActions(original);
            _wait(TimeSpan.FromMilliseconds(350));

            var refreshed = ResolveSameRow(session, original);
            if (refreshed != null && TryInvokeAdmit(session, refreshed))
            {
                ConsoleLogger.Info($"[WAITING_ROOM] Actions revealed: {refreshed.DisplayName}");
                admittedAny = true;
                continue;
            }

            ConsoleLogger.Warn($"[WAITING_ROOM] Admit not found after hover: {original.DisplayName}");
            if (refreshed == null)
                continue;

            ConsoleLogger.Info($"[WAITING_ROOM] View required: {original.DisplayName}");
            if (!session.TryInvokeRowAction(refreshed, "View"))
                continue;
            ConsoleLogger.Info($"[WAITING_ROOM] View clicked, retrying Admit: {original.DisplayName}");
            _wait(TimeSpan.FromMilliseconds(350));

            refreshed = ResolveSameRow(session, original);
            if (refreshed != null && TryInvokeAdmit(session, refreshed))
                admittedAny = true;
        }

        return admittedAny;
    }

    private bool TryInvokeAdmit(
        IWaitingRoomUiaSession session,
        WaitingRoomUiaParticipant participant)
    {
        if (!session.TryInvokeRowAction(participant, "Admit")) return false;
        ConsoleLogger.Info($"[WAITING_ROOM] Admit button found: {participant.DisplayName}");
        ConsoleLogger.Success($"[WAITING_ROOM] Admit invoked successfully: {participant.DisplayName}");
        // Counted, and written down by name (the waiting room and attendance pages list it).
        _admitted(participant.DisplayName);
        return true;
    }

    private static WaitingRoomUiaParticipant? ResolveSameRow(
        IWaitingRoomUiaSession session,
        WaitingRoomUiaParticipant original)
    {
        var current = session.ReadWaitingParticipants();
        return current.FirstOrDefault(item => item.Key.Equals(original.Key, StringComparison.Ordinal))
            ?? current.FirstOrDefault(item => item.DisplayName.Equals(original.DisplayName, StringComparison.OrdinalIgnoreCase));
    }
}
