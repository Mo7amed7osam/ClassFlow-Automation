using System.Text.RegularExpressions;
using ZoomAutoAdmit.WebAutomation.Zoom;

namespace ZoomAutoAdmit.WebAutomation.Recordings;

public enum RecordingLinkKind
{
    None,
    /// <summary>A Zoom cloud recording share link, as copied from My Recordings.</summary>
    ZoomShare,
    /// <summary>A Google Drive file link, as the recordings sheet lists them.</summary>
    GoogleDrive,
}

/// <summary>
/// Which links may be written on a session as its recording. Anything the dashboard shows to
/// students as "the recording" has to be one of these; everything else is refused before a browser
/// is even opened.
///
/// A Google Drive link is accepted only as a link to one file, the way Drive shares a video:
///   https://drive.google.com/file/d/{id}[/view|/preview|/edit][?...]
///   https://drive.google.com/open?id={id}
/// Folders, download links (/uc), other Google hosts, http, other ports, credentials in the URL,
/// whitespace and local paths are all refused. The link itself is never changed.
/// </summary>
public static class RecordingLinks
{
    public const int MaximumLength = 2048;
    private const string DriveHost = "drive.google.com";

    private static readonly Regex DriveFilePath = new(
        @"^/file/d/(?<id>[A-Za-z0-9_-]{20,100})(?:/(?:view|preview|edit))?/?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex DriveFileId = new(@"^[A-Za-z0-9_-]{20,100}$", RegexOptions.CultureInvariant);

    public static RecordingLinkKind Classify(string? value)
    {
        if (IsGoogleDriveFileLink(value)) return RecordingLinkKind.GoogleDrive;
        if (ZoomRecordingLinkReader.IsShareLink(value)) return RecordingLinkKind.ZoomShare;
        return RecordingLinkKind.None;
    }

    /// <summary>Zoom's share links as before, or a Google Drive file link.</summary>
    public static bool IsAttachable(string? value) => Classify(value) != RecordingLinkKind.None;

    public static bool IsGoogleDriveFileLink(string? value) => DriveFileIdOf(value) != null;

    /// <summary>The Drive file id a link points at, or null when it is not an acceptable Drive file link.</summary>
    public static string? DriveFileIdOf(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength) return null;
        // No spaces or control characters anywhere: a URL with them is not the one that was meant.
        if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!uri.Host.Equals(DriveHost, StringComparison.OrdinalIgnoreCase)) return null;
        if (!uri.IsDefaultPort || uri.UserInfo.Length > 0) return null;

        var file = DriveFilePath.Match(uri.AbsolutePath);
        if (file.Success) return file.Groups["id"].Value;

        if (uri.AbsolutePath.Equals("/open", StringComparison.Ordinal))
        {
            string? id = QueryValue(uri.Query, "id");
            if (id != null && DriveFileId.IsMatch(id)) return id;
        }
        return null;
    }

    /// <summary>
    /// Enough of a link to recognise it in a log, not enough to open it: a share link opens the
    /// recording to anyone who has it.
    /// </summary>
    public static string Preview(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(none)";
        if (DriveFileIdOf(value) is { } id) return $"drive.google.com/file/d/{id[..Math.Min(6, id.Length)]}...";
        return value.Length <= 40 ? value : value[..40] + "...";
    }

    private static string? QueryValue(string query, string name)
    {
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals > 0 && pair[..equals] == name) return Uri.UnescapeDataString(pair[(equals + 1)..]);
        }
        return null;
    }
}
