using ZoomAutoAdmit.Core.Formatting;

namespace ZoomAutoAdmit.Core.Meetings;

public enum MeetingLifecycleEventKind { Active, Ending }
public sealed record MeetingLifecycleEvent(MeetingLaunchContext Context, MeetingLifecycleEventKind Kind);

/// <summary>Instance-scoped optional observers; failures never change meeting decisions.</summary>
public sealed class MeetingLifecycleEvents
{
    public event Func<MeetingLifecycleEvent, Task>? Lifecycle;
    public event Action<Guid>? AdmissionVerified;

    public async Task PublishAsync(MeetingLaunchContext context, MeetingLifecycleEventKind kind)
    {
        foreach (Func<MeetingLifecycleEvent, Task> handler in Lifecycle?.GetInvocationList() ?? [])
        {
            try { await handler(new(context, kind)); }
            catch (Exception ex) { ConsoleLogger.Warn($"[ATTENDANCE] Lifecycle observer failed; sessionId={context.Session.SessionId}; {ex.Message}"); }
        }
    }

    public void PublishAdmission(Guid sessionId)
    {
        // Every engine reaches this one method after a confirmed admission, so today's tally is
        // kept here rather than at each of the seven places that admit somebody.
        // Every increment says which meeting it came from. A number that climbs while nothing
        // is running has a source, and this is what names it instead of leaving it a mystery.
        AdmissionControl.RecordAdmission();
        ConsoleLogger.Info($"[ADMISSION] Counted one admission; sessionId={sessionId}; total today={AdmissionControl.AdmittedToday()}");
        foreach (Action<Guid> handler in AdmissionVerified?.GetInvocationList() ?? [])
        {
            try { handler(sessionId); }
            catch (Exception ex) { ConsoleLogger.Warn($"[ATTENDANCE] Admission observer failed; sessionId={sessionId}; {ex.Message}"); }
        }
    }
}

/// <summary>Flows the owning session into monitor tasks without global log parsing.</summary>
public static class MeetingAdmissionScope
{
    private sealed record Binding(Guid SessionId, MeetingLifecycleEvents Events, MeetingLaunchContext? Context);
    private static readonly AsyncLocal<Binding?> Current = new();

    public static IDisposable Begin(Guid sessionId, MeetingLifecycleEvents events, MeetingLaunchContext? context = null)
    {
        var previous = Current.Value;
        Current.Value = new(sessionId, events, context);
        return new Scope(() => Current.Value = previous);
    }

    public static void NotifyVerified()
    {
        var binding = Current.Value;
        binding?.Events.PublishAdmission(binding.SessionId);
    }

    public static Task NotifyMonitorStoppedAsync()
    {
        var binding = Current.Value;
        return binding?.Context == null ? Task.CompletedTask :
            binding.Events.PublishAsync(binding.Context, MeetingLifecycleEventKind.Ending);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
