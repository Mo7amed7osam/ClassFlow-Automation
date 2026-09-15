using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;
using ZoomAutoAdmit.Attendance;

namespace ZoomAutoAdmit.CentralAgent.Tests;

/// <summary>The attendance a meeting leaves on disk reaches the backend: once, in order, and never with a secret in the log.</summary>
public sealed class AttendanceUploaderTests : IDisposable
{
    private const string Token = "zaad_00000000-0000-0000-0000-000000000001.device-secret";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"attendance-upload-{Guid.NewGuid():N}");
    private readonly List<string> _log = [];
    private readonly Backend _backend = new();
    private string Root => Path.Combine(_folder, "Attendance");
    private string State => Path.Combine(_folder, "Central", "attendance-uploads.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>Answers every POST with the next scripted status (201 when none is left); keeps the requests.</summary>
    private sealed class Backend : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Request, JsonElement Body)> Requests = [];
        public readonly Queue<object> Script = new();   // HttpStatusCode or an exception to throw

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string text = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, JsonDocument.Parse(text).RootElement.Clone()));
            var next = Script.Count > 0 ? Script.Dequeue() : HttpStatusCode.Created;
            if (next is Exception ex) throw ex;
            return new HttpResponseMessage((HttpStatusCode)next) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }

    private AttendanceUploader Uploader(string? token = Token) =>
        new(new Uri("https://central.example.com/"), new MemoryTokenStore(token), new HttpClient(_backend), Root, State, _log.Add);

    private static readonly DateTimeOffset Start = new(2026, 9, 14, 15, 2, 0, TimeSpan.Zero);     // 18:02 in Cairo

    private static AttendanceMeetingMetadata Meeting(string group = "CAI5_AIS4_S7") =>
        new(group, group, "https://us06web.zoom.us/j/81234567890", Start, "Desktop");

    private async Task Snapshot(Guid session, int minute, SnapshotTrigger trigger, AttendanceMeetingMetadata? meeting, params string[] names)
    {
        await new JsonAttendanceSnapshotStore(Root).SaveAsync(new AttendanceSnapshot(session, Start.AddMinutes(minute), AttendanceSource.Desktop,
            names.Select(n => new ParticipantPresence(n)).ToList(), trigger, IsComplete: false, Note: null) { Meeting = meeting }, CancellationToken.None);
    }

    [Fact]
    public async Task ASnapshotBecomesTheBackendsSnapshotOfTheGroupsSession()
    {
        var session = Guid.NewGuid();
        await Snapshot(session, 1, SnapshotTrigger.MeetingStart, Meeting(), "Mohab Osama", "  ", "محمد أسامة");

        var pass = await Uploader().UploadPendingAsync(CancellationToken.None);

        Assert.Equal(new AttendanceUploader.Pass(1, 0, false), pass);
        var (request, body) = Assert.Single(_backend.Requests);
        Assert.Equal("https://central.example.com/api/v1/attendance/snapshots", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);
        var s = body.GetProperty("session");
        Assert.Equal($"win:{session:D}", s.GetProperty("ref").GetString());
        Assert.Equal("CAI5_AIS4_S7", s.GetProperty("group").GetString());
        Assert.Equal("2026-09-14", s.GetProperty("date").GetString());
        Assert.Equal("18:02", s.GetProperty("startTime").GetString());                  // Cairo, not UTC
        Assert.Equal("https://us06web.zoom.us/j/81234567890", s.GetProperty("meetingUrl").GetString());
        Assert.Equal(Start.AddMinutes(1), body.GetProperty("capturedAt").GetDateTimeOffset());
        Assert.Equal(new[] { "Mohab Osama", "محمد أسامة" }, body.GetProperty("participants").EnumerateArray().Select(n => n.GetString()!));
        Assert.Equal("desktop", body.GetProperty("source").GetString());
        Assert.Equal("meeting_start", body.GetProperty("trigger").GetString());
        Assert.False(body.GetProperty("isComplete").GetBoolean());
        Assert.False(body.GetProperty("ended").GetBoolean());
        Assert.StartsWith($"win:{session:N}:", body.GetProperty("clientSnapshotId").GetString());
    }

    [Fact]
    public async Task EachSnapshotIsSentOnceInTheOrderItWasTaken()
    {
        var session = Guid.NewGuid();
        await Snapshot(session, 30, SnapshotTrigger.Interval, Meeting(), "B");
        await Snapshot(session, 1, SnapshotTrigger.MeetingStart, Meeting(), "A");

        Assert.Equal(2, (await Uploader().UploadPendingAsync(CancellationToken.None)).Sent);
        Assert.Equal(0, (await Uploader().UploadPendingAsync(CancellationToken.None)).Sent);     // a new agent run too
        Assert.Equal(new[] { "A", "B" }, _backend.Requests.Select(r => r.Body.GetProperty("participants")[0].GetString()!));
        Assert.Equal("scheduled", _backend.Requests[1].Body.GetProperty("trigger").GetString());
    }

    [Fact]
    public async Task WhileTheBackendIsAwaySnapshotsWaitAndGoLaterInOrder()
    {
        var session = Guid.NewGuid();
        await Snapshot(session, 1, SnapshotTrigger.MeetingStart, Meeting(), "A");
        await Snapshot(session, 16, SnapshotTrigger.Interval, Meeting(), "B");
        var uploader = Uploader();
        _backend.Script.Enqueue(new HttpRequestException("down", null, HttpStatusCode.BadGateway));

        var first = await uploader.UploadPendingAsync(CancellationToken.None);
        Assert.Equal(new AttendanceUploader.Pass(0, 0, true), first);
        Assert.Single(_backend.Requests);                                               // stopped at the first, B not tried
        _backend.Script.Enqueue(new HttpRequestException("down", null, HttpStatusCode.BadGateway));
        Assert.True((await uploader.UploadPendingAsync(CancellationToken.None)).Waiting);
        Assert.Single(_log, l => l.Contains("attendance_upload_waiting"));              // once, not every 30 seconds

        Assert.Equal(2, (await uploader.UploadPendingAsync(CancellationToken.None)).Sent);
        Assert.Equal(new[] { "A", "A", "A", "B" }, _backend.Requests.Select(r => r.Body.GetProperty("participants")[0].GetString()!));
        Assert.Contains(_log, l => l.Contains("attendance_upload_resumed"));
    }

    [Fact]
    public async Task ASnapshotTheBackendWillNeverTakeIsSkippedNotRetriedForever()
    {
        var session = Guid.NewGuid();
        await Snapshot(session, 1, SnapshotTrigger.MeetingStart, Meeting(), "A");
        await Snapshot(session, 2, SnapshotTrigger.Admission, Meeting(), "B");
        _backend.Script.Enqueue(HttpStatusCode.UnprocessableEntity);

        Assert.Equal(new AttendanceUploader.Pass(1, 1, false), await Uploader().UploadPendingAsync(CancellationToken.None));
        Assert.Equal(new AttendanceUploader.Pass(0, 0, false), await Uploader().UploadPendingAsync(CancellationToken.None));
        Assert.Contains(_log, l => l.Contains("attendance_refused") && l.Contains("http422"));
    }

    [Fact]
    public async Task WithoutAUsableGroupNothingIsSent()
    {
        await Snapshot(Guid.NewGuid(), 1, SnapshotTrigger.MeetingStart, meeting: null, "A");
        await Snapshot(Guid.NewGuid(), 1, SnapshotTrigger.MeetingStart, Meeting("bad/group"), "A");

        Assert.Equal(new AttendanceUploader.Pass(0, 2, false), await Uploader().UploadPendingAsync(CancellationToken.None));
        Assert.Empty(_backend.Requests);
        Assert.Contains(_log, l => l.Contains("noMeeting"));
        Assert.Contains(_log, l => l.Contains("groupNotUsable"));
    }

    [Fact]
    public async Task TheLastReadEndsTheSessionAndAFailedLastReadStillDoes()
    {
        var ended = Guid.NewGuid();
        await Snapshot(ended, 1, SnapshotTrigger.MeetingStart, Meeting(), "A");
        await Snapshot(ended, 90, SnapshotTrigger.MeetingEnd, Meeting(), "A");
        var failed = Guid.NewGuid();
        await Snapshot(failed, 1, SnapshotTrigger.MeetingStart, Meeting("CAI5_AIS4_S8"), "A");
        var store = new JsonAttendanceSnapshotStore(Root);
        await store.SaveIssueAsync(new AttendanceCaptureIssue(failed, Start.AddMinutes(95), SnapshotTrigger.MeetingEnd, "No list"), CancellationToken.None);
        await store.SaveIssueAsync(new AttendanceCaptureIssue(failed, Start.AddMinutes(20), SnapshotTrigger.Interval, "No list"), CancellationToken.None);

        Assert.Equal(4, (await Uploader().UploadPendingAsync(CancellationToken.None)).Sent);     // the interval issue: nothing to send

        var bodies = _backend.Requests.Select(r => r.Body).ToList();
        Assert.Equal(4, bodies.Count);
        Assert.True(bodies.Single(b => b.GetProperty("session").GetProperty("ref").GetString() == $"win:{ended:D}" && b.GetProperty("ended").GetBoolean())
            .GetProperty("participants").GetArrayLength() == 1);
        var marker = bodies.Single(b => b.GetProperty("session").GetProperty("ref").GetString() == $"win:{failed:D}" && b.GetProperty("ended").GetBoolean());
        Assert.Equal("CAI5_AIS4_S8", marker.GetProperty("session").GetProperty("group").GetString());
        Assert.Equal(0, marker.GetProperty("participants").GetArrayLength());
        Assert.False(marker.GetProperty("isComplete").GetBoolean());                    // nobody counts as having left
        Assert.Equal(Start.AddMinutes(95), marker.GetProperty("capturedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task ARefusedTokenWaitsAndNothingSecretIsLogged()
    {
        await Snapshot(Guid.NewGuid(), 1, SnapshotTrigger.MeetingStart, Meeting(), "Mohab Osama");
        _backend.Script.Enqueue(HttpStatusCode.Unauthorized);

        Assert.True((await Uploader().UploadPendingAsync(CancellationToken.None)).Waiting);
        Assert.True((await Uploader(token: null).UploadPendingAsync(CancellationToken.None)).Waiting);
        Assert.Single(_backend.Requests);                                                // no request without a token
        string log = string.Join("\n", _log);
        Assert.Contains("unauthorized", log);
        Assert.Contains("noDeviceToken", log);
        Assert.DoesNotContain("device-secret", log);
        Assert.DoesNotContain("Mohab", log);                                            // names are not logged either
        Assert.DoesNotContain("zoom.us", File.Exists(State) ? File.ReadAllText(State) : "");
    }

    [Fact]
    public async Task OldHistoryIsLeftAloneAndAMissingFolderIsFine()
    {
        Assert.Equal(new AttendanceUploader.Pass(0, 0, false), await Uploader().UploadPendingAsync(CancellationToken.None));
        var session = Guid.NewGuid();
        await Snapshot(session, 1, SnapshotTrigger.MeetingStart, Meeting(), "A");
        foreach (string file in Directory.EnumerateFiles(Path.Combine(Root, session.ToString("D"))))
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-40));

        Assert.Equal(0, (await Uploader().UploadPendingAsync(CancellationToken.None)).Sent);
        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public void AnUnreadableFileIsRefusedAndManyNamesAreCut()
    {
        Assert.Equal("unreadable", AttendanceUploader.Build("s/f.json", "{not json", AttendanceUploader.Cairo).Reason);
        var many = JsonSerializer.Serialize(new
        {
            sessionId = Guid.NewGuid(), timestamp = Start, trigger = "Interval", isComplete = true,
            participants = Enumerable.Range(0, 1500).Select(i => new { name = $"Name {i}" }),
            meeting = new { accountId = "CAI5_AIS4_S7", scheduledStart = Start, meetingUrl = "https://zoom.us/j/1?pwd=SECRET" },
        });
        var (body, _) = AttendanceUploader.Build("s/f.json", many, AttendanceUploader.Cairo);
        Assert.Equal(AttendanceUploader.MaximumNames, body!["participants"]!.AsArray().Count);
        Assert.True(body["isComplete"]!.GetValue<bool>());
        Assert.Equal("https://zoom.us/j/1", body["session"]!["meetingUrl"]!.GetValue<string>());   // never the passcode
    }

    [Fact]
    public async Task RunStopsWhenAsked()
    {
        using var stop = new CancellationTokenSource();
        var uploader = new AttendanceUploader(new Uri("https://central.example.com/"), new MemoryTokenStore(Token), new HttpClient(_backend),
            Root, State, _log.Add, delay: (_, token) => { stop.Cancel(); return Task.Delay(Timeout.Infinite, token); });
        await uploader.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(10));
    }
}
