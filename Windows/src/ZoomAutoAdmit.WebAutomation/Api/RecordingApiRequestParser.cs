using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZoomAutoAdmit.WebAutomation.Recordings;

namespace ZoomAutoAdmit.WebAutomation.Api;

/// <summary>
/// Turns a request body into a <see cref="ProvidedRecordLinkRequest"/>, or says exactly what is wrong.
///
///   { "group": "AST5_DAT1_S1",
///     "recordLink": "https://drive.google.com/file/d/…/view?usp=sharing",
///     "date": "2026-09-03",
///     "replaceExisting": false }
///
/// The recording link is the Google Drive link from the recordings sheet and is written to the
/// dashboard exactly as sent (surrounding spaces aside). Strict on purpose: an unknown field is
/// refused rather than ignored, because a misspelt field silently ignored is a request that does
/// something other than what was meant.
/// </summary>
public static class RecordingApiRequestParser
{
    public static readonly IReadOnlyList<string> AllowedFields =
        ["group", "recordLink", "date", "startTime", "replaceExisting", "profile", "dryRun", "headed"];

    /// <summary>Other names the recording link has been seen under, answered with the right one.</summary>
    private static readonly IReadOnlyList<string> LinkAliases =
        ["link", "url", "driveUrl", "googleDriveUrl", "recordingUrl", "recordingLink", "shareLink"];

    private static readonly Regex SafeGroup = new(@"^[A-Za-z0-9][A-Za-z0-9 _.-]{0,99}$", RegexOptions.CultureInvariant);

    public static bool TryParse(string body, out ProvidedRecordLinkRequest? request, out string error)
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
                if (AllowedFields.Contains(property.Name, StringComparer.Ordinal)) continue;
                error = HintFor(property.Name) ?? $"'{property.Name}' is not a known field. Allowed: {string.Join(", ", AllowedFields)}.";
                return false;
            }

            // ---- group
            if (!TryString(root, "group", out string? group, out error)) return false;
            group = group?.Trim();
            if (string.IsNullOrEmpty(group)) { error = "'group' is required."; return false; }
            if (!SafeGroup.IsMatch(group))
            {
                error = "'group' may contain only letters, digits, spaces, '_', '-' and '.', up to 100 characters.";
                return false;
            }

            // ---- recordLink
            if (!TryString(root, "recordLink", out string? link, out error)) return false;
            link = link?.Trim();
            if (string.IsNullOrEmpty(link)) { error = "'recordLink' is required: the recording's Google Drive link."; return false; }
            if (!CheckDriveLink(link, out error)) return false;

            // ---- date
            if (!TryString(root, "date", out string? dateText, out error)) return false;
            DateOnly? date = null;
            if (dateText != null)
            {
                if (!DateOnly.TryParseExact(dateText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                { error = "'date' must be a date in the form yyyy-MM-dd."; return false; }
                date = parsedDate;
            }

            // ---- startTime: only to tell apart two dashboard sessions of the group on one day
            if (!TryString(root, "startTime", out string? timeText, out error)) return false;
            TimeOnly? startTime = null;
            if (timeText != null)
            {
                if (!TimeOnly.TryParseExact(timeText.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime))
                { error = "'startTime' must be a 24-hour local time in the form HH:mm."; return false; }
                startTime = parsedTime;
            }

            // ---- profile: accepted so an existing n8n node that still sends it keeps working
            if (!TryString(root, "profile", out string? profile, out error)) return false;
            profile = profile?.Trim();
            if (profile != null && !profile.Equals("default", StringComparison.OrdinalIgnoreCase) &&
                !RecordingLinkProcessor.IsValidProfileName(profile))
            {
                error = "'profile' must be \"default\" or a browser profile name (letters, digits, '.', '_', '-').";
                return false;
            }

            if (!TryBool(root, "replaceExisting", out bool replaceExisting, out error)) return false;
            if (!TryBool(root, "dryRun", out bool dryRun, out error)) return false;
            if (!TryBool(root, "headed", out bool headed, out error)) return false;

            request = new ProvidedRecordLinkRequest
            {
                Group = group,
                RecordLink = link,
                Date = date,
                StartTime = startTime,
                ReplaceExisting = replaceExisting,
                DryRun = dryRun,
                Headed = headed,
            };
            return true;
        }
    }

    /// <summary>
    /// The recording link has to be a Google Drive link to one file. Each way it can be wrong gets
    /// its own message, so the n8n execution log says what to fix.
    /// </summary>
    public static bool CheckDriveLink(string link, out string error)
    {
        error = string.Empty;
        if (link.Length > RecordingLinks.MaximumLength)
        { error = $"'recordLink' is longer than {RecordingLinks.MaximumLength} characters."; return false; }
        if (link.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        { error = "'recordLink' contains spaces or control characters."; return false; }
        if (Regex.IsMatch(link, @"^[A-Za-z]:[\\/]") || link.StartsWith(@"\\", StringComparison.Ordinal) ||
            link.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        { error = "'recordLink' is a file path, not a link. Send the recording's Google Drive link."; return false; }
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        { error = "'recordLink' is not a valid URL."; return false; }
        if (uri.Scheme != Uri.UriSchemeHttps)
        { error = "'recordLink' must use https."; return false; }
        if (!RecordingLinks.IsGoogleDriveFileLink(link))
        {
            error = "'recordLink' must be a Google Drive link to one file, like " +
                    "https://drive.google.com/file/d/<file id>/view?usp=sharing.";
            return false;
        }
        return true;
    }

    private static string? HintFor(string field)
    {
        if (LinkAliases.Contains(field, StringComparer.OrdinalIgnoreCase))
            return $"'{field}' is not a known field: send the recording's Google Drive link as 'recordLink'.";
        if (field.Equals("file", StringComparison.OrdinalIgnoreCase) || field.Equals("fileName", StringComparison.OrdinalIgnoreCase))
            return $"'{field}' is not needed: only the recording's link is, as 'recordLink'. The file is never downloaded or uploaded.";
        if (field.Equals("timeZone", StringComparison.OrdinalIgnoreCase))
            return "'timeZone' is no longer used: attaching a link does not need the recording's time.";
        return null;
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
        // quietly mean "today"; refusing it makes the missing value visible.
        if (value != null && value.Trim().Length == 0 && name is not ("group" or "recordLink"))
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
