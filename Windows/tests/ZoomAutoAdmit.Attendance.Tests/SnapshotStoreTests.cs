using System.Text.Json;
using Xunit;

namespace ZoomAutoAdmit.Attendance.Tests;

public class SnapshotStoreTests
{
    [Fact]
    public async Task StoresCamelCaseSnapshotsAtomicallyWithoutOverwritingAndSeparatesSessions()
    {
        var root = Path.Combine(Path.GetTempPath(), "attendance-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonAttendanceSnapshotStore(root);
            var snapshot = new AttendanceSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow,
                AttendanceSource.Web, [new("Raw name"), new("Raw name")], SnapshotTrigger.Manual, false, "rendered only");
            await Task.WhenAll(store.SaveAsync(snapshot, default), store.SaveAsync(snapshot, default),
                store.SaveAsync(snapshot with { SessionId = Guid.NewGuid() }, default));
            Assert.Equal(2, Directory.GetDirectories(root).Length);
            var files = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories);
            Assert.Equal(3, files.Length);
            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(files[0]));
            Assert.Equal("Web", json.RootElement.GetProperty("source").GetString());
            Assert.Equal(2, json.RootElement.GetProperty("participants").GetArrayLength());
            Assert.True(json.RootElement.TryGetProperty("sessionId", out _));
            Assert.True(json.RootElement.TryGetProperty("timestamp", out _));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(snapshot, new(true)));
            Assert.Equal(3, Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
