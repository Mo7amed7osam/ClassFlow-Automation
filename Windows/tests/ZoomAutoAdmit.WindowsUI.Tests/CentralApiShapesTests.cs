using System.Text.Json;
using Xunit;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.Tests;

/// <summary>The app's pages read the server's own answers (Backend/central_backend/dashboard.py, auth.py, admin.py).</summary>
public sealed class CentralApiShapesTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ARecordingsPageIsReadWithItsLinkAndLmsState()
    {
        const string body = """
        {"items":[{"id":"r1","group":"CAI5_AIS4_S8","date":"2026-09-09","startTime":"17:00","fileName":"s.mp4","type":"recording",
          "driveLink":null,"zoomLink":"https://us06web.zoom.us/rec/share/FAKE","source":"zoom","lmsStatus":"pending","lmsUpdatedAt":null,
          "createdAt":"2026-09-13T14:19:07Z","updatedAt":"2026-09-13T14:19:07Z","linkStatus":{"link":"zoom","lms":"pending","label":"Zoom only · pending"},
          "lastJob":{"jobId":"j1","type":"recording.process","status":"queued","group":"CAI5_AIS4_S8","date":"2026-09-09","attempts":0,
            "createdAt":"2026-09-13T14:20:00Z","assignedAt":null,"startedAt":null,"finishedAt":null,"errorCode":null,"alreadyExists":null,
            "recordingId":"r1","dryRun":false,"replaceExisting":false}}],
         "total":1,"page":1,"pageSize":100,"sort":"session"}
        """;
        var page = JsonSerializer.Deserialize<CentralRecordingPage>(body, Json)!;
        var r = Assert.Single(page.Items);
        Assert.Equal("Zoom", r.SourceText);
        Assert.Equal("Processing", r.LmsText);                 // a queued attach job
        Assert.True(r.JobOpen);
        Assert.Equal("https://us06web.zoom.us/rec/share/FAKE", r.Link);
        Assert.Equal("2026-09-09 · 17:00", r.When);
    }

    [Fact]
    public void TheSignedInAccountGroupsAndUsersAreRead()
    {
        var me = JsonSerializer.Deserialize<CentralMe>("""
            {"id":"u1","username":"omar","displayName":"Omar","role":"coordinator","allGroups":false,
             "groups":[{"id":"g1","name":"CAI5_AIS4_S7","displayName":null,"archived":false}],"expiresAt":"2026-09-14T23:00:00Z"}
            """, Json)!;
        Assert.False(me.IsAdmin);
        Assert.Equal("CAI5_AIS4_S7", Assert.Single(me.Groups!).Name);

        var groups = JsonDocument.Parse("""
            {"groups":[{"id":"g1","group":"CAI5_AIS4_S7","displayName":null,"archived":false,"recordings":2,"lastSessionDate":"2026-09-11",
              "lastUpdatedAt":"2026-09-13T14:19:07Z","pending":1,"onLms":1,"missingLink":0,
              "coordinators":[{"id":"u1","username":"omar","displayName":"Omar","status":"active"}]}],"count":1}
            """).RootElement.GetProperty("groups").Deserialize<List<CentralGroup>>(Json)!;
        Assert.Equal("omar", Assert.Single(Assert.Single(groups).Coordinators!).Username);

        var users = JsonSerializer.Deserialize<CentralUserList>("""
            {"users":[{"id":"u1","username":"omar","displayName":"Omar","role":"coordinator","status":"pending","createdAt":"2026-09-13T10:00:00Z",
              "approvedAt":null,"lastLoginAt":null,"groups":[]}],"count":1,"counts":{"pending":1,"active":0,"rejected":0,"disabled":0}}
            """, Json)!;
        Assert.Equal("—", Assert.Single(users.Users).GroupsText);
    }
}
