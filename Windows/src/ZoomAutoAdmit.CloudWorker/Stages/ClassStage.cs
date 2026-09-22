using System.Text.Json;

namespace ZoomAutoAdmit.CloudWorker.Stages;

/// <summary>
/// One stage of one class, as the backend sends it.
///
/// Every stage carries the same thing, because every stage asks the same question: which class,
/// and whose accounts. The server checked this already (central_backend/validation.py); it is
/// checked again here because a payload that reached the wrong worker, or an older one, must fail
/// with a sentence rather than a NullReferenceException halfway through opening a meeting.
///
/// There is no sign-in here. <see cref="ZoomAccountId"/> and <see cref="LmsAccountId"/> name an
/// account; what they point at is kept encrypted on the server and asked for one run at a time.
/// </summary>
public sealed record ClassStage
{
    public required Guid ClassPlanId { get; init; }
    public required string Group { get; init; }
    public required DateOnly Date { get; init; }
    public required Guid CoordinatorId { get; init; }

    /// <summary>When the class is, as the LMS lists it. Absent for a class with no time of its own.</summary>
    public TimeOnly? StartTime { get; init; }

    /// <summary>Where the meeting opens. Present for class.open; the stages that join one already live do not need it.</summary>
    public Uri? MeetingUrl { get; init; }

    /// <summary>The class's name in the timetable, which says what kind of session it is - and so
    /// who teaches it and is made co-host.</summary>
    public string? Title { get; init; }

    public Guid? ZoomAccountId { get; init; }

    /// <summary>Whose sign-in writes this class up. Always present for an LMS stage.</summary>
    public Guid? LmsAccountId { get; init; }

    /// <summary>
    /// How long to hold the meeting. A class that outlives this is left; a worker that lost touch
    /// with the backend must not sit in an empty meeting for ever, holding the only slot it has.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Do everything up to the point of changing something, then stop and say what would have happened.</summary>
    public bool DryRun { get; init; }

    /// <summary>How this class is named in a log line. No account id, and nothing secret.</summary>
    public string Describe() =>
        StartTime is { } at
            ? $"{Group} on {Date:yyyy-MM-dd} at {at:HH\\:mm}"
            : $"{Group} on {Date:yyyy-MM-dd}";

    public static bool TryParse(JsonElement payload, out ClassStage? stage, out string error)
    {
        stage = null;
        error = "";
        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "The payload is not a JSON object.";
            return false;
        }

        if (!Guid(payload, "classPlanId", required: true, out var plan, out error)) return false;
        if (!Guid(payload, "coordinatorId", required: true, out var coordinator, out error)) return false;
        if (!Guid(payload, "zoomAccountId", required: false, out var zoomAccount, out error)) return false;
        if (!Guid(payload, "lmsAccountId", required: false, out var lmsAccount, out error)) return false;

        if (!payload.TryGetProperty("group", out var groupValue) || groupValue.ValueKind != JsonValueKind.String
            || groupValue.GetString() is not { Length: > 0 } group)
        {
            error = "'group' is required.";
            return false;
        }

        if (!payload.TryGetProperty("date", out var dateValue) || dateValue.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(dateValue.GetString(), "yyyy-MM-dd", out var date))
        {
            error = "'date' must be yyyy-MM-dd.";
            return false;
        }

        TimeOnly? start = null;
        if (payload.TryGetProperty("startTime", out var startValue) && startValue.ValueKind == JsonValueKind.String)
        {
            if (!TimeOnly.TryParseExact(startValue.GetString(), "HH:mm", out var parsed))
            {
                error = "'startTime' must be HH:mm when given.";
                return false;
            }
            start = parsed;
        }

        Uri? meeting = null;
        if (payload.TryGetProperty("meetingUrl", out var urlValue) && urlValue.ValueKind == JsonValueKind.String)
        {
            if (!Uri.TryCreate(urlValue.GetString(), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            {
                error = "'meetingUrl' must be an https URL.";
                return false;
            }
            meeting = url;
        }

        stage = new ClassStage
        {
            ClassPlanId = plan!.Value,
            Group = group,
            Date = date,
            CoordinatorId = coordinator!.Value,
            StartTime = start,
            MeetingUrl = meeting,
            Title = payload.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
                ? title.GetString()
                : null,
            ZoomAccountId = zoomAccount,
            LmsAccountId = lmsAccount,
            Duration = payload.TryGetProperty("durationMinutes", out var minutes)
                       && minutes.ValueKind == JsonValueKind.Number
                       && minutes.TryGetInt32(out int m) && m > 0
                ? TimeSpan.FromMinutes(m)
                : null,
            DryRun = payload.TryGetProperty("dryRun", out var dry) && dry.ValueKind == JsonValueKind.True,
        };
        return true;
    }

    private static bool Guid(JsonElement payload, string field, bool required, out Guid? value, out string error)
    {
        value = null;
        error = "";
        if (!payload.TryGetProperty(field, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            if (!required) return true;
            error = $"'{field}' is required.";
            return false;
        }
        if (element.ValueKind != JsonValueKind.String || !System.Guid.TryParse(element.GetString(), out var parsed))
        {
            error = $"'{field}' must be a UUID.";
            return false;
        }
        value = parsed;
        return true;
    }
}
