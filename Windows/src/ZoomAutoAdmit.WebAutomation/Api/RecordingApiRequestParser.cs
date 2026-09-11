using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZoomAutoAdmit.WebAutomation.Recordings;

namespace ZoomAutoAdmit.WebAutomation.Api;

/// <summary>
/// Turns a request body into a <see cref="RecordingLinkRequest"/>, or says exactly what is wrong.
///
/// Strict on purpose. An unknown field is refused rather than ignored, because a misspelt
/// "starttime" silently ignored would mean "any time that day". And the application finds the Zoom
/// recording itself, so a Drive link - under any of the names it has been seen under - is refused
/// with an explanation instead of being carried along unused.
/// </summary>
public static class RecordingApiRequestParser
{
    public static readonly IReadOnlyList<string> AllowedFields =
        ["group", "date", "startTime", "timeZone", "profile", "headed", "dryRun", "replaceExisting"];

    /// <summary>Fields that carry a recording file or link, which this API never takes.</summary>
    public static readonly IReadOnlyList<string> RefusedLinkFields =
        ["recordLink", "file", "fileName", "driveUrl", "googleDriveUrl", "link"];

    private static readonly Regex SafeGroup = new(@"^[A-Za-z0-9][A-Za-z0-9 _.-]{0,99}$", RegexOptions.CultureInvariant);

    /// <param name="localZone">This computer's zone, for a time sent in UTC. The machine's own by default.</param>
    public static bool TryParse(string body, out RecordingLinkRequest? request, out string error, TimeZoneInfo? localZone = null)
    {
        request = null;
        error = string.Empty;
        JsonDocument document;
        try { document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 }); }
        catch (JsonException) { error = "The body must be a JSON object."; return false; }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "The body must be a JSON object."; return false; }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name)) { error = $"'{property.Name}' is given more than once."; return false; }
                if (RefusedLinkFields.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                {
                    error = $"'{property.Name}' is not accepted: the application finds the Zoom recording itself. " +
                            "Send group, date and startTime instead.";
                    return false;
                }
                if (!AllowedFields.Contains(property.Name, StringComparer.Ordinal))
                {
                    error = $"'{property.Name}' is not a known field. Allowed: {string.Join(", ", AllowedFields)}.";
                    return false;
                }
            }

            if (!TryString(root, "group", out string? group, out error)) return false;
            group = group?.Trim();
            if (string.IsNullOrEmpty(group)) { error = "'group' is required."; return false; }
            if (!SafeGroup.IsMatch(group))
            {
                error = "'group' may contain only letters, digits, spaces, '_', '-' and '.', up to 100 characters.";
                return false;
            }

            if (!TryString(root, "date", out string? dateText, out error)) return false;
            DateOnly? date = null;
            if (dateText != null)
            {
                if (!DateOnly.TryParseExact(dateText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                { error = "'date' must be a date in the form yyyy-MM-dd."; return false; }
                date = parsedDate;
            }

            if (!TryString(root, "startTime", out string? timeText, out error)) return false;
            TimeOnly? startTime = null;
            if (timeText != null)
            {
                if (!TimeOnly.TryParseExact(timeText.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime))
                { error = "'startTime' must be a 24-hour time in the form HH:mm."; return false; }
                startTime = parsedTime;
            }

            if (!TryString(root, "timeZone", out string? zoneText, out error)) return false;
            string zone = zoneText?.Trim().ToLowerInvariant() ?? "local";
            if (zone is not ("local" or "utc")) { error = "'timeZone' must be \"local\" or \"utc\"."; return false; }
            if (zone == "utc")
            {
                // A UTC time can land on the next or previous local day, so both halves are needed.
                if (date == null || startTime == null)
                { error = "'timeZone': \"utc\" needs both 'date' and 'startTime'."; return false; }
                var utc = DateTime.SpecifyKind(date.Value.ToDateTime(startTime.Value), DateTimeKind.Utc);
                var local = TimeZoneInfo.ConvertTimeFromUtc(utc, localZone ?? TimeZoneInfo.Local);
                date = DateOnly.FromDateTime(local);
                startTime = TimeOnly.FromDateTime(local);
            }

            if (!TryString(root, "profile", out string? profile, out error)) return false;
            profile = profile?.Trim();
            if (profile != null && !profile.Equals("default", StringComparison.OrdinalIgnoreCase) &&
                !RecordingLinkProcessor.IsValidProfileName(profile))
            {
                error = "'profile' must be \"default\" or a browser profile name (letters, digits, '.', '_', '-').";
                return false;
            }

            if (!TryBool(root, "headed", out bool headed, out error)) return false;
            if (!TryBool(root, "dryRun", out bool dryRun, out error)) return false;
            if (!TryBool(root, "replaceExisting", out bool replaceExisting, out error)) return false;

            request = new RecordingLinkRequest
            {
                Group = group,
                Date = date,
                StartTime = startTime,
                Profile = string.IsNullOrEmpty(profile) ? null : profile,
                Headed = headed,
                DryRun = dryRun,
                ReplaceExisting = replaceExisting,
                KeepBrowserOpen = false,
            };
            return true;
        }
    }

    /// <summary>A string field, or null when absent or JSON null. An empty string is not "absent".</summary>
    private static bool TryString(JsonElement root, string name, out string? value, out string error)
    {
        value = null;
        error = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.String) { error = $"'{name}' must be a string."; return false; }
        value = element.GetString();
        // "" is what a template produces when its source is missing. Treating it as "absent" would
        // quietly mean "today" or "any time"; refusing it makes the missing value visible.
        if (value != null && value.Trim().Length == 0 && name != "group")
        { error = $"'{name}' is empty. Leave it out, or send null, to use the default."; return false; }
        return true;
    }

    private static bool TryBool(JsonElement root, string name, out bool value, out string error)
    {
        value = false;
        error = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        { error = $"'{name}' must be true or false."; return false; }
        value = element.GetBoolean();
        return true;
    }
}
