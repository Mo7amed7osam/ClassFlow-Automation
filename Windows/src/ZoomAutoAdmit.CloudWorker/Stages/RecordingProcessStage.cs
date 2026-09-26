using System.Text.Json;
using System.Text.Json.Nodes;
using ZoomAutoAdmit.CentralAgent;
using ZoomAutoAdmit.WebAutomation.Api;
using ZoomAutoAdmit.WebAutomation.Lms;
using ZoomAutoAdmit.WebAutomation.Recordings;
using ZoomAutoAdmit.WebAutomation.Zoom;

namespace ZoomAutoAdmit.CloudWorker.Stages;

/// <summary>
/// Attaches a Drive recording discovered by the server's read-only Sheet sync.  The account id is
/// transport metadata: it is removed before the shared strict recording parser sees the request,
/// then used only to request this job's short-lived LMS sign-in.
/// </summary>
public sealed class RecordingProcessStage(ILmsAccounts accounts, Action<string>? log = null) : IJobHandler
{
    private readonly Action<string> _log = log ?? (_ => { });
    public string JobType => "recording.process";

    public async Task<JobOutcome> ExecuteAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return JobOutcome.Failure("invalidPayload", "The payload is not a JSON object.");
        JsonObject? document;
        try { document = JsonNode.Parse(payload.GetRawText()) as JsonObject; }
        catch (JsonException) { return JobOutcome.Failure("invalidPayload", "The payload is not valid JSON."); }
        if (document is null || !Guid.TryParse(document["lmsAccountId"]?.GetValue<string>(), out var accountId))
            return JobOutcome.Failure("invalidPayload", "'lmsAccountId' is required for a cloud recording attachment.");
        document.Remove("lmsAccountId");
        if (!RecordingApiRequestParser.TryParse(document.ToJsonString(), out var request, out var error))
            return JobOutcome.Failure("invalidPayload", error);

        var credential = await accounts.ForAsync(accountId, cancellationToken);
        if (credential is null)
            return JobOutcome.Failure("noLmsSignIn", "The server would not give this recording job its LMS sign-in.");

        var processor = new RecordingLinkProcessor(
            new NeverReadZoomSource(), new LmsRecordingTarget(new LmsSessionRunner(credential)),
            new ProfileOperationLock(), dashboardProfile: _ => $"lms-{accountId}", log: _log);
        var outcome = await processor.AttachProvidedLinkAsync(request!, cancellationToken);
        var result = new JsonObject
        {
            ["group"] = outcome.Group,
            ["date"] = outcome.Date.ToString("yyyy-MM-dd"),
        };
        if (outcome.StartTime is { } time) result["startTime"] = time.ToString("HH':'mm");
        if (outcome.Status == RecordingLinkStatus.Attached) return JobOutcome.Success(result);
        if (outcome.Status == RecordingLinkStatus.AlreadyExists)
        {
            result["alreadyExists"] = true;
            return JobOutcome.Success(result);
        }
        if (outcome.Status == RecordingLinkStatus.DryRun)
        {
            result["dryRun"] = true;
            return JobOutcome.Success(result);
        }
        if (outcome.Status == RecordingLinkStatus.Busy)
            return JobOutcome.Failure("busy", outcome.Message, retryable: true, retryAfterSeconds: 60);
        if (outcome.Status == RecordingLinkStatus.LmsFailed && outcome.Reason == "sessionNotFinished")
            return JobOutcome.Failure("sessionNotFinished", outcome.Message, retryable: true, retryAfterSeconds: 300);
        return JobOutcome.Failure(outcome.Reason ?? "lmsFailed", outcome.Message);
    }

    private sealed class NeverReadZoomSource : IRecordingLinkSource
    {
        public Task<ZoomRecordingLinkResult> ReadAsync(string group, string profile, DateOnly day, TimeOnly? startTime,
            bool headed, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A provided Drive link must never open Zoom.");
    }
}
