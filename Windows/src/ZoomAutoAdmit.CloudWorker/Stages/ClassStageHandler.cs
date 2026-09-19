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
public abstract class ClassStageHandler(Action<string>? log = null) : IJobHandler
{
    private readonly Action<string> _log = log ?? (_ => { });

    public abstract string JobType { get; }

    protected void Log(string message) => _log($"[{JobType}] {message}");

    public async Task<JobOutcome> ExecuteAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!ClassStage.TryParse(payload, out var stage, out string error))
            return JobOutcome.Failure("invalidPayload", error);

        if (Requires(stage!) is { } missing)
            return JobOutcome.Failure("invalidPayload", missing);

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
