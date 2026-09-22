using System.Text.Json;
using System.Text.Json.Nodes;
using ZoomAutoAdmit.CentralAgent;

namespace ZoomAutoAdmit.CloudWorker.Stages;

/// <summary>
/// What every stage of a class does before it does its own work: read the payload, refuse one it
/// cannot act on, and turn whatever happened into an answer the backend can record.
///
/// The refusals are deliberately not retryable. A payload that is missing a field will still be
/// missing it in a minute, and a job that retries on it only hides the mistake behind a queue.
/// </summary>
public abstract class ClassStageHandler(Action<string>? log = null, Func<DateTimeOffset>? now = null) : IJobHandler
{
    private readonly Action<string> _log = log ?? (_ => { });

    /// <summary>The clock the freshness rule reads. Injectable so it can be tested at a moment
    /// rather than at whatever time the suite happens to run.</summary>
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    public abstract string JobType { get; }

    protected void Log(string message) => _log($"[{JobType}] {message}");

    public async Task<JobOutcome> ExecuteAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!ClassStage.TryParse(payload, out var stage, out string error))
            return JobOutcome.Failure("invalidPayload", error);

        if (Requires(stage!) is { } missing)
            return JobOutcome.Failure("invalidPayload", missing);

        if (TooLate(stage!) is { } late)
            return JobOutcome.Failure("classIsOver", late);

        Log($"{stage!.Describe()}{(stage.DryRun ? " (dry run)" : "")}");
        try
        {
            return await RunAsync(stage, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The worker is draining. Nothing was finished, so the job goes back to the queue
            // rather than being recorded as a failure of the class.
            return JobOutcome.Failure("workerStopping", "The worker was asked to stop before this finished.",
                                      retryable: true, retryAfterSeconds: 30);
        }
        catch (Exception problem)
        {
            // The message, never the stack: a stack from inside a browser driver says nothing to
            // whoever reads the class card, and can carry a URL with a token in it.
            Log($"failed: {problem.GetType().Name}");
            return JobOutcome.Failure("stageFailed", $"{problem.GetType().Name}: {problem.Message}");
        }
    }

    /// <summary>What this particular stage needs beyond the common fields, or null when it has it.</summary>
    protected virtual string? Requires(ClassStage stage) => null;

    /// <summary>
    /// How long after a class this stage is still worth doing. A queued job waits for a worker
    /// however long that takes, so without this a worker that was busy, offline or being
    /// redeployed comes back and opens yesterday's meeting - students are let into a room for a
    /// class that ended hours ago, and the server has no idea anything is wrong.
    ///
    /// The stages that read Zoom's own pages are the exception: the report and the recording only
    /// exist after the class, and reading them late is exactly right.
    /// </summary>
    protected virtual TimeSpan? Freshness => TimeSpan.FromMinutes(30);

    private string? TooLate(ClassStage stage)
    {
        if (Freshness is not { } window || stage.StartTime is not { } at) return null;
        // The class's own clock, in the zone class times are written in. A worker's clock is UTC
        // in a container; comparing a local class time against it would refuse every class in
        // summer and none in winter.
        var began = new DateTimeOffset(stage.Date.ToDateTime(at), CairoOffset(stage.Date, at));
        var over = began + (stage.Duration ?? TimeSpan.FromMinutes(180)) + window;
        var moment = _now();
        return moment <= over
            ? null
            : $"This class ended on {began:yyyy-MM-dd} at {began:HH':'mm} and it is now "
              + $"{moment:yyyy-MM-dd HH':'mm} UTC. Opening it now would let people into a room "
              + "for a class that is over, so it was left alone.";
    }

    /// <summary>This handler's clock, the one the freshness rule reads.</summary>
    protected DateTimeOffset Now => _now();

    /// <summary>When the class starts, in the zone class times are written in; midnight when it has no time.</summary>
    protected static DateTimeOffset StartOf(ClassStage stage)
    {
        var at = stage.StartTime ?? TimeOnly.MinValue;
        return new DateTimeOffset(stage.Date.ToDateTime(at), CairoOffset(stage.Date, at));
    }

    private static TimeSpan CairoOffset(DateOnly date, TimeOnly at)
    {
        // tzdata is in the image and in the Windows build's dependencies, so this resolves on both.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");
        return zone.GetUtcOffset(DateTime.SpecifyKind(date.ToDateTime(at), DateTimeKind.Unspecified));
    }

    protected abstract Task<JobOutcome> RunAsync(ClassStage stage, CancellationToken cancellationToken);

    /// <summary>The class this answer is about, so a result reads on its own without the payload beside it.</summary>
    protected static JsonObject Answer(ClassStage stage)
    {
        var result = new JsonObject
        {
            ["classPlanId"] = stage.ClassPlanId.ToString(),
            ["group"] = stage.Group,
            ["date"] = stage.Date.ToString("yyyy-MM-dd"),
        };
        if (stage.StartTime is { } at) result["startTime"] = at.ToString("HH':'mm");
        if (stage.DryRun) result["dryRun"] = true;
        return result;
    }
}
