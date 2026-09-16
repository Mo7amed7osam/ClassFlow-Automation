using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;
using ZoomAutoAdmit.Core.Central;

namespace ZoomAutoAdmit.CentralAgent.Tests;

/// <summary>
/// What a PC did reaches the server: in order, once, and kept on the PC until the server has it.
/// </summary>
public sealed class ActivityUploaderTests : IDisposable
{
    private const string Token = "zaad_00000000-0000-0000-0000-000000000001.device-secret";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"activity-{Guid.NewGuid():N}");
    private readonly Backend _backend = new();
    private readonly List<string> _log = [];

    private ActivityLog Log() => new(_folder);

    private ActivityUploader Uploader(string? token = Token) =>
        new(new Uri("https://central.example.com/"), new MemoryTokenStore(token), new HttpClient(_backend), Log(), _log.Add);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>Answers every POST with the next scripted status (200 when none is left); keeps the requests.</summary>
    private sealed class Backend : HttpMessageHandler
    {
        public readonly List<JsonElement> Bodies = [];
        public readonly List<string?> Authorizations = [];
        public readonly Queue<object> Script = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString());
            Bodies.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone());
            var next = Script.Count > 0 ? Script.Dequeue() : HttpStatusCode.OK;
            if (next is Exception ex) throw ex;
            return new HttpResponseMessage((HttpStatusCode)next) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }

    private static string[] Kinds(JsonElement body) =>
        [.. body.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("kind").GetString()!)];

    [Fact]
    public async Task WhatThePcDidIsSentOldestFirstAndThenForgotten()
    {
        var log = Log();
        log.Write("class.opened", "done", "S7: the 19:00 class is live.", "CAI5_AIS4_S7", new DateOnly(2026, 9, 16),
            at: new DateTimeOffset(2026, 9, 16, 19, 0, 0, TimeSpan.FromHours(3)));
        log.Write("lms.TakeAttendance", "done", "21 present.", "CAI5_AIS4_S7", new DateOnly(2026, 9, 16),
            at: new DateTimeOffset(2026, 9, 16, 20, 30, 0, TimeSpan.FromHours(3)));

        var pass = await Uploader().SendPendingAsync(default);

        Assert.Equal(new ActivityUploader.Pass(Sent: 2, Refused: 0, Waiting: false), pass);
        Assert.Equal(new[] { "class.opened", "lms.TakeAttendance" }, Kinds(Assert.Single(_backend.Bodies)));
        Assert.Equal($"Bearer {Token}", Assert.Single(_backend.Authorizations));
        Assert.Empty(Log().Pending());                                   // nothing is sent twice
    }

    [Fact]
    public async Task AServerThatIsAwayKeepsEverythingForTheNextTime()
    {
        Log().Write("class.opened", "done", "live", "CAI5_AIS4_S7", new DateOnly(2026, 9, 16));
        _backend.Script.Enqueue(new HttpRequestException("no route"));

        var pass = await Uploader().SendPendingAsync(default);

        Assert.True(pass.Waiting);
        Assert.Equal(0, pass.Sent);
        Assert.Single(Log().Pending());
        Assert.Contains(_log, line => line.Contains("activity_send_waiting"));

        // And it all goes as soon as the server answers again.
        Assert.Equal(1, (await Uploader().SendPendingAsync(default)).Sent);
        Assert.Empty(Log().Pending());
    }

    [Fact]
    public async Task ANoteTheServerWillNeverTakeDoesNotBlockTheRest()
    {
        Log().Write("lms.TakeAttendance", "done", "21 present.", "CAI5_AIS4_S7", new DateOnly(2026, 9, 16));
        _backend.Script.Enqueue(HttpStatusCode.BadRequest);

        var pass = await Uploader().SendPendingAsync(default);

        Assert.Equal(1, pass.Refused);
        Assert.False(pass.Waiting);
        Assert.Empty(Log().Pending());
        Assert.Contains(_log, line => line.Contains("activity_refused"));
    }

    [Fact]
    public async Task WithoutADeviceTokenNothingIsSentAndNothingIsLost()
    {
        Log().Write("class.opened", "done", "live", "CAI5_AIS4_S7", new DateOnly(2026, 9, 16));

        var pass = await Uploader(token: null).SendPendingAsync(default);

        Assert.True(pass.Waiting);
        Assert.Empty(_backend.Bodies);
        Assert.Single(Log().Pending());
    }
}
