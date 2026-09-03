using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZoomAutoAdmit.Attendance;

public sealed class JsonAttendanceSnapshotStore : IAttendanceSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string RootDirectory { get; }

    public JsonAttendanceSnapshotStore(string? rootDirectory = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZoomAutoAdmit", "Attendance"));
    }

    public Task SaveAsync(AttendanceSnapshot snapshot, CancellationToken cancellationToken) =>
        WriteAsync(snapshot.SessionId, snapshot.Timestamp, "json", snapshot, cancellationToken);

    // Issues use their own extension so they can never be read back as attendance.
    public Task SaveIssueAsync(AttendanceCaptureIssue issue, CancellationToken cancellationToken) =>
        WriteAsync(issue.SessionId, issue.Timestamp, "issue", issue, cancellationToken);

    private async Task WriteAsync<T>(Guid sessionId, DateTimeOffset timestamp, string extension, T payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(RootDirectory, sessionId.ToString("D"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory,
            $"{timestamp.UtcDateTime:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.{extension}");
        var temporary = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
