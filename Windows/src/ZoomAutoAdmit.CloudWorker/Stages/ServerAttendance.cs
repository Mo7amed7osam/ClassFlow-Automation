using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZoomAutoAdmit.CloudWorker.Stages;

/// <summary>
/// The server said there is nothing it can hand over for this class, and why. Kept apart from an
/// empty list on purpose: an empty list would be written to the LMS as a whole class absent.
/// </summary>
public sealed class AttendanceUnavailableException(string reason) : Exception(reason);

/// <summary>
/// Who was present at a class, as the server matched it: the roster names of the students seen,
/// which is what the Windows app hands its attendance step.
///
/// Asked through the job, with the LMS lane's own token, so a device learns a class's attendance
/// only while it holds a job to write that class up.
/// </summary>
public sealed class ServerAttendanceNames(
    HttpClient http,
    Func<string?> deviceToken,
    Func<Guid?> currentJobId,
    Action<string>? log = null) : IAttendanceNames
{
    private readonly Action<string> _log = log ?? (_ => { });

    private sealed record Answer(
        [property: JsonPropertyName("present")] string[] Present,
        [property: JsonPropertyName("needsReview")] int NeedsReview,
        [property: JsonPropertyName("students")] int Students,
        [property: JsonPropertyName("snapshots")] int Snapshots);

    public async Task<IReadOnlyCollection<string>> PresentAsync(ClassStage stage, CancellationToken cancellationToken)
    {
        if (deviceToken() is not { Length: > 0 } token)
            throw new AttendanceUnavailableException("This worker has no device token yet, so it cannot ask for attendance.");
        if (currentJobId() is not { } job)
            throw new AttendanceUnavailableException("No job is being run, and attendance is only given for one that is.");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/agent/jobs/{job}/attendance");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Conflict)
            // The server's own sentence: which class, and what is missing (nothing collected, or
            // no roster). It names no person and holds no secret, so it goes on the class card.
            throw new AttendanceUnavailableException(await DetailsAsync(response, cancellationToken));
        if (!response.IsSuccessStatusCode)
            throw new AttendanceUnavailableException(
                $"The server would not give this class's attendance: {(int)response.StatusCode}.");

        var answer = await response.Content.ReadFromJsonAsync<Answer>(cancellationToken)
                     ?? throw new AttendanceUnavailableException("The server's answer held no attendance.");
        _log($"[attendance] {answer.Present.Length} of {answer.Students} present from {answer.Snapshots} read(s)"
             + (answer.NeedsReview > 0 ? $", {answer.NeedsReview} left for a person to review" : ""));
        return answer.Present;
    }

    internal static async Task<string> DetailsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (body.RootElement.TryGetProperty("details", out var details) && details.GetString() is { Length: > 0 } text)
                return text;
        }
        catch (JsonException) { }
        return $"The server refused: {(int)response.StatusCode}.";
    }
}

/// <summary>
/// Sends what the meeting lane sees to the server, one read at a time. The server matches the
/// names against the group's roster, as it does for the Windows agent's snapshots.
/// </summary>
public interface IAttendanceSnapshots
{
    Task SendAsync(ClassStage stage, IReadOnlyList<string> names, string trigger, bool complete, bool ended,
                   DateTimeOffset capturedAt, CancellationToken cancellationToken);
}

public sealed class ServerAttendanceSnapshots(HttpClient http, Func<string?> deviceToken, Action<string>? log = null)
    : IAttendanceSnapshots
{
    private readonly Action<string> _log = log ?? (_ => { });

    public async Task SendAsync(ClassStage stage, IReadOnlyList<string> names, string trigger, bool complete, bool ended,
                                DateTimeOffset capturedAt, CancellationToken cancellationToken)
    {
        if (deviceToken() is not { Length: > 0 } token) return;

        var body = new Dictionary<string, object?>
        {
            // One id per read, so a retry after a dropped connection is not counted twice.
            ["clientSnapshotId"] = $"worker:{stage.ClassPlanId}:{capturedAt.ToUnixTimeSeconds()}:{trigger}",
            ["session"] = new Dictionary<string, object?>
            {
                ["group"] = stage.Group,
                ["date"] = stage.Date.ToString("yyyy-MM-dd"),
                ["startTime"] = stage.StartTime?.ToString("HH':'mm"),
                ["meetingUrl"] = stage.MeetingUrl?.ToString(),
            },
            ["capturedAt"] = capturedAt.ToString("O"),
            ["source"] = "web",
            ["trigger"] = trigger,
            ["isComplete"] = complete,
            ["ended"] = ended,
            ["participants"] = names,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/attendance/snapshots")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            _log($"[attendance] sent {names.Count} name(s) ({trigger}{(complete ? "" : ", partial list")})");
        else
            _log($"[attendance] the server did not take the read: {(int)response.StatusCode} "
                 + await ServerAttendanceNames.DetailsAsync(response, cancellationToken));
    }
}
