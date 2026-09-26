namespace ZoomAutoAdmit.CloudWorker.Stages;

/// <summary>One read of the meeting's Joined list: the names, and whether the whole list was read.</summary>
public sealed record MeetingRead(IReadOnlyList<string> Names, bool Complete);

/// <summary>
/// Who is in the meeting, read while class.run holds it and sent to the server.
///
/// The Windows app does the same from the same list: a read shortly after the meeting opens, one
/// at a steady interval, and one as it ends. The server keeps every read and matches the names
/// against the group's roster, so the attendance an hour and a half in and the late joiners three
/// hours in are both worked out from everything seen - not from whoever happened to be there at
/// the moment the LMS stage ran.
///
/// A read that fails is logged and skipped. Attendance is never allowed to end the meeting it is
/// reading: a class that is held with one read missing is far better than a class that is closed.
/// </summary>
public sealed class MeetingAttendance(
    Func<CancellationToken, Task<MeetingRead>> read,
    IAttendanceSnapshots snapshots,
    ClassStage stage,
    Action<string>? log = null,
    TimeSpan? first = null,
    TimeSpan? every = null,
    Func<DateTimeOffset>? now = null)
{
    /// <summary>After the meeting opens: long enough for the people waiting to be let in.</summary>
    public static readonly TimeSpan FirstRead = TimeSpan.FromMinutes(2);

    /// <summary>Between reads. Reads every fifteen minutes, matching the business cadence.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly Action<string> _log = log ?? (_ => { });
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>How many reads reached the server.</summary>
    public int Sent { get; private set; }

    /// <summary>The most people seen in one read, for the class card.</summary>
    public int MostSeen { get; private set; }

    /// <summary>Reads until the token is cancelled. Never throws for a failed read.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(first ?? FirstRead, cancellationToken);
            await TakeAsync("meetingStart", ended: false, cancellationToken);
            while (true)
            {
                await Task.Delay(every ?? Interval, cancellationToken);
                await TakeAsync("interval", ended: false, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The class is over, or the worker is draining; the final read is the caller's.
        }
    }

    /// <summary>The read as the meeting ends, which also tells the server the meeting is over.</summary>
    public Task FinalAsync(CancellationToken cancellationToken) => TakeAsync("meetingEnd", ended: true, cancellationToken);

    private async Task TakeAsync(string trigger, bool ended, CancellationToken cancellationToken)
    {
        MeetingRead seen;
        try
        {
            seen = await read(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception problem)
        {
            _log($"[attendance] could not read the participants list ({trigger}): {problem.Message}");
            return;
        }

        MostSeen = Math.Max(MostSeen, seen.Names.Count);
        try
        {
            await snapshots.SendAsync(stage, seen.Names, trigger, seen.Complete, ended, _now(), cancellationToken);
            Sent++;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception problem)
        {
            // The server is unreachable for a moment. The next read carries everybody again, so
            // one lost read costs nothing unless it is the last.
            _log($"[attendance] could not send the read ({trigger}): {problem.GetType().Name}");
        }
    }
}
