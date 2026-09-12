using System.Text.Json;
using System.Text.Json.Nodes;
using ZoomAutoAdmit.WebAutomation.Api;
using ZoomAutoAdmit.WebAutomation.Recordings;

namespace ZoomAutoAdmit.CentralAgent;

public sealed record AgentJobError(string Code, string Message, bool Retryable, int? RetryAfterSeconds = null);

public sealed record JobOutcome(bool Succeeded, JsonObject? Result, AgentJobError? Error)
{
    public static JobOutcome Success(JsonObject result) => new(true, result, null);
    public static JobOutcome Failure(string code, string message, bool retryable = false, int? retryAfterSeconds = null) =>
        new(false, null, new AgentJobError(code, message, retryable, retryAfterSeconds));
}

/// <summary>Runs one type of central job on this PC.</summary>
public interface IJobHandler
{
    string JobType { get; }
    Task<JobOutcome> ExecuteAsync(JsonElement payload, CancellationToken cancellationToken);
}

/// <summary>
/// recording.process: the same path the local recording API takes - the payload is read by the
/// API's own <see cref="RecordingApiRequestParser"/> and handed to
/// <see cref="IRecordingLinkProcessor.AttachProvidedLinkAsync"/>, which takes the dashboard
/// profile's lock and attaches the link. Nothing of the LMS automation is repeated here.
/// </summary>
public sealed class RecordingProcessJobHandler(IRecordingLinkProcessor processor) : IJobHandler
{
    public const int BusyRetryAfterSeconds = 60;

    public string JobType => "recording.process";

    public async Task<JobOutcome> ExecuteAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return JobOutcome.Failure("invalidPayload", "The payload is not a JSON object.");
        if (!RecordingApiRequestParser.TryParse(payload.GetRawText(), out var request, out string error))
            return JobOutcome.Failure("invalidPayload", error);

        var outcome = await processor.AttachProvidedLinkAsync(request!, cancellationToken);
        var result = new JsonObject
        {
            ["group"] = outcome.Group,
            ["date"] = outcome.Date.ToString("yyyy-MM-dd"),
        };
        if (outcome.StartTime is { } time) result["startTime"] = time.ToString("HH':'mm");

        switch (outcome.Status)
        {
            case RecordingLinkStatus.Attached:
                result["alreadyExists"] = false;
                result["message"] = "Recording link attached successfully.";
                return JobOutcome.Success(result);
            case RecordingLinkStatus.AlreadyExists:
                result["alreadyExists"] = true;
                result["message"] = outcome.Message;
                return JobOutcome.Success(result);
            case RecordingLinkStatus.DryRun:
                result["alreadyExists"] = false;
                result["dryRun"] = true;
                result["message"] = outcome.Message;
                return JobOutcome.Success(result);
            case RecordingLinkStatus.Busy:
                // Another dashboard operation on this PC (the app, a scheduled meeting, the local API)
                // holds the profile: nothing was done, so trying again later is safe.
                return JobOutcome.Failure("busy", outcome.Message, retryable: true, BusyRetryAfterSeconds);
            default:
                return JobOutcome.Failure(outcome.Reason ?? "lmsFailed", outcome.Message);
        }
    }
}
