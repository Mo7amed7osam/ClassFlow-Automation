using System.Text.Json;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;
using ZoomAutoAdmit.WindowsUI.Services;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

/// <summary>
/// The classes this PC opens by itself, kept against the signed-in person's dashboard account. With
/// them there, signing in on a new PC - a cloud one - is all it takes to find the timetable again.
/// </summary>
public sealed class ScheduleServerSyncTests
{
    private sealed class FakeApi : IScheduleSyncApi
    {
        public bool IsSignedIn { get; set; } = true;
        public List<(List<JsonElement> Schedules, string Device)> Sent { get; } = [];
        /// <summary>What the server already holds, for a PC that has no classes of its own.</summary>
        public List<JsonElement> Kept { get; } = [];

        public Task<List<JsonElement>> SchedulesAsync(CancellationToken token) => Task.FromResult(Kept.ToList());

        public Task<JsonElement> SaveSchedulesAsync(IEnumerable<object> schedules, string deviceName, CancellationToken token)
        {
            Sent.Add(([.. schedules.Select(s => (JsonElement)s)], deviceName));
            return Task.FromResult(JsonDocument.Parse("""{"count":0}""").RootElement);
        }
    }

    private static MeetingSchedule Class(string name, bool enabled = true, string? coordinatorId = null,
        DateOnly? date = null, SessionEngineType? engine = null) =>
        new(Guid.NewGuid(), name, "https://zoom.us/j/91473108490", "CAI5_AIS4_S7", new TimeOnly(19, 0),
            date == null ? ScheduleDays.Tuesday : ScheduleDays.None, enabled,
            OccurrenceDate: date, GroupName: "CAI5_AIS4_S7", PreferredEngine: engine,
            Coordinator: coordinatorId == null ? null : "Mona", CoordinatorId: coordinatorId);

    private static ScheduleServerSync Sync(FakeApi api) => new(api, _ => { });

    [Fact]
    public async Task NothingHappensBeforeSomebodyIsSignedIn()
    {
        var api = new FakeApi { IsSignedIn = false };
        Assert.Null(await Sync(api).SyncAsync([Class("mine")], (_, _) => Task.CompletedTask));
        Assert.Empty(api.Sent);
    }

    [Fact]
    public async Task OnlyThisPersonsOwnClassesTravel()
    {
        var api = new FakeApi();
        await Sync(api).SyncAsync(
            [Class("mine"), Class("Mona's", coordinatorId: "u-mona")], (_, _) => Task.CompletedTask);

        var (sent, device) = Assert.Single(api.Sent);
        Assert.Equal("mine", Assert.Single(sent).GetProperty("name").GetString());
        Assert.Equal(Environment.MachineName, device);
    }

    [Fact]
    public async Task AClassComesBackExactlyAsItWent()
    {
        var api = new FakeApi();
        var original = Class("CAI5_AIS4_S7 • 36", enabled: false, date: new DateOnly(2026, 9, 22), engine: SessionEngineType.Web);
        await Sync(api).SyncAsync([original], (_, _) => Task.CompletedTask);

        // What the server kept is now all a fresh PC has to go on.
        api.Kept.AddRange(api.Sent[0].Schedules);
        var here = new List<MeetingSchedule>();
        string? said = await Sync(api).SyncAsync([], (schedule, _) => { here.Add(schedule); return Task.CompletedTask; });

        var restored = Assert.Single(here);
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.MeetingUrl, restored.MeetingUrl);
        Assert.Equal(original.AccountId, restored.AccountId);
        Assert.Equal(original.Time, restored.Time);
        Assert.Equal(original.Days, restored.Days);
        Assert.Equal(original.OccurrenceDate, restored.OccurrenceDate);
        Assert.Equal(original.GroupName, restored.GroupName);
        Assert.Equal(SessionEngineType.Web, restored.PreferredEngine);
        Assert.False(restored.Enabled);                       // a class turned off stays off
        Assert.Contains("came from your dashboard account", said);
    }

    [Fact]
    public async Task ARestoredClassIsNotTakenAsAlreadyOpenedToday()
    {
        var api = new FakeApi();
        var opened = Class("mine") with { LastTriggeredDate = DateOnly.FromDateTime(DateTime.Now) };
        await Sync(api).SyncAsync([opened], (_, _) => Task.CompletedTask);
        api.Kept.AddRange(api.Sent[0].Schedules);

        var here = new List<MeetingSchedule>();
        await Sync(api).RestoreAsync((schedule, _) => { here.Add(schedule); return Task.CompletedTask; });

        // Otherwise today's class would be skipped on the new PC, having never run there.
        Assert.Null(Assert.Single(here).LastTriggeredDate);
    }

    [Fact]
    public async Task ARestoredClassIsThisPersonsOwn()
    {
        var api = new FakeApi();
        // Somebody else's class that found its way into the kept list is taken back as this
        // person's own, never as one run on behalf of a coordinator who is no longer turned on.
        api.Kept.Add(JsonSerializer.SerializeToElement(Class("was Mona's", coordinatorId: "u-mona"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));

        var here = new List<MeetingSchedule>();
        await Sync(api).RestoreAsync((schedule, _) => { here.Add(schedule); return Task.CompletedTask; });

        var restored = Assert.Single(here);
        Assert.Null(restored.Coordinator);
        Assert.Null(restored.CoordinatorId);
    }

    [Fact]
    public async Task APcWithNoClassesNeverEmptiesWhatTheServerKept()
    {
        var api = new FakeApi();
        api.Kept.Add(JsonSerializer.SerializeToElement(new { id = Guid.NewGuid(), name = "kept" }));
        await Sync(api).SyncAsync([], (_, _) => Task.CompletedTask);
        Assert.Empty(api.Sent);
    }

    [Fact]
    public async Task APcThatOnlyRunsOtherPeoplesClassesSendsNothingAndTakesWhatIsThere()
    {
        var api = new FakeApi();
        api.Kept.Add(JsonSerializer.SerializeToElement(Class("kept"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        var here = new List<MeetingSchedule>();

        // Its only classes are a coordinator's, which are rebuilt from the run plan and never sent.
        await Sync(api).SyncAsync([Class("Mona's", coordinatorId: "u-mona")], (s, _) => { here.Add(s); return Task.CompletedTask; });

        Assert.Empty(api.Sent);
        Assert.Equal("kept", Assert.Single(here).Name);
    }

    [Fact]
    public async Task AClassWithNoLinkOrAccountIsLeftBehindRatherThanFailingTheRest()
    {
        var api = new FakeApi();
        api.Kept.Add(JsonSerializer.SerializeToElement(new { id = Guid.NewGuid(), name = "no link", meetingUrl = "", accountId = "S7" }));
        api.Kept.Add(JsonSerializer.SerializeToElement(new { not = "a schedule at all" }));
        api.Kept.Add(JsonSerializer.SerializeToElement(new
        {
            id = Guid.NewGuid(), name = "good", meetingUrl = "https://zoom.us/j/91473108490", accountId = "S7",
            time = "19:00:00", days = "Tuesday", enabled = true,
        }));

        var here = new List<MeetingSchedule>();
        int restored = await Sync(api).RestoreAsync((schedule, _) => { here.Add(schedule); return Task.CompletedTask; });

        Assert.Equal(1, restored);
        Assert.Equal("good", Assert.Single(here).Name);
    }

    [Fact]
    public async Task TheSameTimetableIsNotSentAgainForNothing()
    {
        var api = new FakeApi();
        var sync = Sync(api);
        var mine = new[] { Class("mine") };
        Assert.Equal(1, await sync.PushAsync(mine));
        Assert.Null(await sync.PushAsync(mine));
        Assert.Single(api.Sent);
    }

    [Fact]
    public async Task AChangedTimetableGoesAtOnce()
    {
        var api = new FakeApi();
        var sync = Sync(api);
        var one = Class("mine");
        await sync.PushAsync([one]);
        await sync.PushAsync([one with { Enabled = false }]);
        Assert.Equal(2, api.Sent.Count);
    }
}
