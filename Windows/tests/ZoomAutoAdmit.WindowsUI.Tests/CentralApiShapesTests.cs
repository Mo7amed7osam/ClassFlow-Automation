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

    [Fact]
    public void TheCoordinatorsWhoseClassesThisPcRunsAreReadWithTheirTwoAccounts()
    {
        // Backend/central_backend/delegated_runs.py, GET /api/v1/admin/delegations
        var listed = JsonSerializer.Deserialize<CentralDelegationList>("""
            {"delegations":[
              {"coordinatorId":"u1","username":"mona","displayName":"Mona","status":"active","enabled":true,
               "groups":[{"id":"g1","name":"CAI5_AIS4_S7","displayName":null,"archived":false}],
               "lmsAccount":{"id":"a1","label":"Mona","email":"mona@example.com","role":"coordinator","active":true},
               "lmsAccounts":[{"id":"a1","label":"Mona","email":"mona@example.com","role":"coordinator","active":true}],
               "zoomAccountId":"z1","zoomAccount":"CAI5_AIS4_S7",
               "zoomAccounts":[{"id":"z1","accountId":"CAI5_AIS4_S7","label":"S7","zoomEmail":"mona@zoom.example.com",
                 "group":"CAI5_AIS4_S7","meetingUrl":"https://zoom.us/j/91473108490","preferredEngine":null,"active":true}],
               "classes":{"planned":4,"done":1,"skipped":0,"needsLink":2},
               "updatedAt":"2026-09-19T10:00:00Z"},
              {"coordinatorId":"u2","username":"sami","displayName":"Sami","status":"active","enabled":false,
               "groups":[],"lmsAccount":null,"lmsAccounts":[],"zoomAccountId":null,"zoomAccount":null,"zoomAccounts":[],
               "classes":{"planned":0,"done":0,"skipped":0,"needsLink":0},"updatedAt":null}]}
            """, Json)!;

        var mona = listed.Delegations[0];
        Assert.True(mona.IsReady);
        Assert.Equal("CAI5_AIS4_S7", mona.GroupsText);
        Assert.Equal("mona@example.com", mona.LmsAccount!.Email);
        Assert.Equal(2, mona.Classes!.NeedsLink);
        // Her own Zoom account, and the link a class of that group opens with.
        Assert.Equal("CAI5_AIS4_S7", mona.Zoom!.AccountId);
        Assert.Equal("https://zoom.us/j/91473108490", mona.Zoom.MeetingUrl);
        Assert.Equal("mona@zoom.example.com", mona.Zoom.ZoomEmail);

        var sami = listed.Delegations[1];
        Assert.False(sami.IsReady);                       // turned off, and neither account of their own
        Assert.Null(sami.Zoom);
        Assert.Equal("no groups", sami.GroupsText);
    }

    [Fact]
    public void TheClassesToRunAreReadWithWhoseTheyAreAndWhatIsStillMissing()
    {
        // Backend/central_backend/delegated_runs.py, GET /api/v1/admin/run-plan
        var plan = JsonSerializer.Deserialize<CentralRunPlan>("""
            {"classes":[
              {"id":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","coordinatorId":"u1","group":"CAI5_AIS4_S7","date":"2026-09-22",
               "startTime":"19:00","title":"36 • Technical","meetingUrl":"https://zoom.us/j/91473108490",
               "zoomAccount":"CAI5_AIS4_S7","preferredEngine":"web","source":"lms","status":"planned","note":null,
               "needsLink":false,"importedAt":"2026-09-19T10:00:00Z","updatedAt":"2026-09-19T10:00:00Z"},
              {"id":"3f2504e0-4f89-11d3-9a0c-0305e82c3302","coordinatorId":"u1","group":"CAI5_AIS4_S7","date":"2026-09-29",
               "startTime":null,"title":null,"meetingUrl":null,"zoomAccount":null,"preferredEngine":null,
               "source":"lms","status":"planned","note":null,"needsLink":true,"importedAt":null,
               "updatedAt":"2026-09-19T10:00:00Z"}],
             "coordinators":[{"coordinatorId":"u1","displayName":"Mona","username":"mona","enabled":true,
               "zoomAccount":"CAI5_AIS4_S7","zoomAccounts":[],
               "lmsAccount":{"id":"a1","label":"Mona","email":"mona@example.com","role":"coordinator","active":true}}]}
            """, Json)!;

        var first = plan.ClassList[0];
        Assert.Equal(new DateOnly(2026, 9, 22), first.Day);
        Assert.Equal(new TimeOnly(19, 0), first.Start);
        Assert.Equal("web", first.PreferredEngine);
        Assert.False(first.NeedsLink);

        var second = plan.ClassList[1];
        Assert.Null(second.Start);                        // a class with no time on the LMS
        Assert.True(second.NeedsLink);

        Assert.Equal("Mona", Assert.Single(plan.CoordinatorList).DisplayName);
    }

    [Fact]
    public void ACoordinatorsSignInNeverPrintsItself()
    {
        // The app writes plenty of log lines; this one must never carry a password into one.
        var secret = JsonSerializer.Deserialize<CentralCoordinatorSecret>("""
            {"id":"a1","coordinatorId":"u1","email":"mona@example.com","role":"coordinator","label":"Mona",
             "password":"made-up password for tests"}
            """, Json)!;
        Assert.Equal("mona@example.com", secret.Email);
        Assert.DoesNotContain("made-up", secret.ToString());
        Assert.DoesNotContain("made-up", $"{secret}");
    }
}
