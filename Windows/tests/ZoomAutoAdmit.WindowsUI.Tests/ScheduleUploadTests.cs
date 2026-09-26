using System.IO.Compression;
using System.Xml.Linq;
using ZoomAutoAdmit.WindowsRuntime;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.WindowsUI.ViewModels;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

public sealed class ScheduleUploadTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ZoomScheduleUploadTests", Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task PreviewDoesNotSaveAndImportPreservesDatesKeepsPhysicalAsSuchAndSkipsDuplicates()
    {
        var service = new UiService(); using var vm = new SchedulesViewModel(service);
        await vm.RefreshAsync(); await vm.PreviewImportAsync(Workbook());
        Assert.Equal(4, vm.ImportRows.Count); Assert.Empty(service.Schedules);
        Assert.Equal("CAI5_AIS4_S8", vm.ImportAccount!.AccountId);
        await vm.ConfirmImportAsync(new DateTime(2025, 1, 1));
        Assert.Equal(2, service.Schedules.Count);
        var online = service.Schedules.Single(s => s.Mode == "Online");
        Assert.Equal(DateOnly.FromDateTime(DateTime.FromOADate(46227)), online.OccurrenceDate);
        Assert.Equal(new TimeOnly(19, 0), online.Time); Assert.Equal(ScheduleDays.None, online.Days); Assert.True(online.Enabled);
        // The physical class comes in as one: run and completed on the LMS, no Zoom meeting.
        var physical = service.Schedules.Single(s => s.Mode == "Physical");
        Assert.Equal(DateOnly.FromDateTime(DateTime.FromOADate(46228)), physical.OccurrenceDate);
        Assert.Equal(new TimeOnly(18, 0), physical.Time);
        await vm.ConfirmImportAsync(new DateTime(2025, 1, 1));
        Assert.Equal(2, service.Schedules.Count); Assert.Contains("duplicate", vm.ImportStatus);
    }

    [Fact]
    public async Task AClassImportedBeforeTheTypeWasKeptLearnsItFromTheTimetable()
    {
        var service = new UiService(); using var vm = new SchedulesViewModel(service);
        var day = DateOnly.FromDateTime(DateTime.FromOADate(46228));
        service.Schedules.Add(new(Guid.NewGuid(), "CAI5_AIS4_S8 • 2 • Technical", "https://zoom.us/j/93181040158", "CAI5_AIS4_S8",
            new TimeOnly(18, 0), ScheduleDays.None, true, null, day, "CAI5_AIS4_S8"));
        await vm.RefreshAsync(); await vm.PreviewImportAsync(Workbook());
        await vm.ConfirmImportAsync(new DateTime(2025, 1, 1));
        Assert.Equal("Physical", service.Schedules.Single(s => s.OccurrenceDate == day).Mode);
        Assert.Contains("now marked", vm.ImportStatus);
    }
    [Fact]
    public async Task PastMeetingsAreNotRescheduledAndMissingAccountIsVisible()
    {
        var service = new UiService(); using var vm = new SchedulesViewModel(service);
        await vm.PreviewImportAsync(Workbook()); await vm.ConfirmImportAsync();
        Assert.Empty(service.Schedules); Assert.Contains("Select", vm.ImportStatus);
        await vm.RefreshAsync(); vm.ImportAccount = vm.Accounts[0];
        await vm.ConfirmImportAsync(new DateTime(2030, 1, 1));
        Assert.Empty(service.Schedules); Assert.Contains("2 past/duplicate", vm.ImportStatus);
    }
    [Fact]
    public async Task EveryImportableDateStartsSelectedAndUntickedDatesAreNotSaved()
    {
        var service = new UiService(); using var vm = new SchedulesViewModel(service);
        await vm.RefreshAsync(); await vm.PreviewImportAsync(Workbook());
        Assert.All(vm.ImportRows, row => Assert.Equal(row.CanImport, row.Include));
        Assert.Contains("selected", vm.ImportSelectionSummary);

        // An excluded row (physical / missing time) can never be ticked back on.
        var excluded = vm.ImportRows.First(row => !row.CanImport);
        excluded.Include = true;
        Assert.False(excluded.Include);

        foreach (var row in vm.ImportRows.Where(row => row.CanImport)) row.Include = false;
        await vm.ConfirmImportAsync(new DateTime(2025, 1, 1));
        Assert.Empty(service.Schedules);
        Assert.Contains("Select at least one date", vm.ImportStatus);

        vm.SelectAllImportCommand.Execute(null);
        await vm.ConfirmImportAsync(new DateTime(2025, 1, 1));
        Assert.Equal(2, service.Schedules.Count);
        Assert.All(service.Schedules, schedule => Assert.True(schedule.Enabled));
    }

    [Fact]
    public async Task DayFilterNarrowsTheListWithoutLosingSchedules()
    {
        var service = new UiService(); using var vm = new SchedulesViewModel(service);
        var monday = new DateOnly(2026, 9, 7);        // A Monday.
        var wednesday = new DateOnly(2026, 9, 9);
        service.Schedules.Add(new(Guid.NewGuid(), "Exact Monday", "https://zoom.us/j/1", "a", new TimeOnly(19, 0), ScheduleDays.None, true, null, monday));
        service.Schedules.Add(new(Guid.NewGuid(), "Exact Wednesday", "https://zoom.us/j/2", "a", new TimeOnly(19, 0), ScheduleDays.None, true, null, wednesday));
        service.Schedules.Add(new(Guid.NewGuid(), "Weekly Monday", "https://zoom.us/j/3", "a", new TimeOnly(9, 0), ScheduleDays.Monday, true));
        await vm.RefreshAsync();

        Assert.Equal(3, vm.FilteredItems.Count);
        vm.ScheduleFilter = "Monday";
        Assert.Equal(new[] { "Exact Monday", "Weekly Monday" }, vm.FilteredItems.Select(item => item.Name));
        Assert.Equal(3, vm.Items.Count);   // Filtering never drops a saved schedule.
        Assert.Contains("2 of 3", vm.FilterSummary);
        vm.ScheduleFilter = "All";
        Assert.Equal(3, vm.FilteredItems.Count);
    }

    [Fact]
    public async Task NextMeetingPicksTheEarliestEnabledOccurrenceAndCountsDown()
    {
        var service = new UiService(); using var vm = new SchedulesViewModel(service);
        var now = new DateTime(2026, 9, 7, 8, 0, 0);   // Monday morning.
        var soon = new MeetingSchedule(Guid.NewGuid(), "Soon", "https://zoom.us/j/1", "a", new TimeOnly(9, 30), ScheduleDays.None, true, null, DateOnly.FromDateTime(now));
        service.Schedules.Add(new(Guid.NewGuid(), "Later today", "https://zoom.us/j/2", "a", new TimeOnly(19, 0), ScheduleDays.None, true, null, DateOnly.FromDateTime(now)));
        service.Schedules.Add(soon);
        service.Schedules.Add(new(Guid.NewGuid(), "Disabled earlier", "https://zoom.us/j/3", "a", new TimeOnly(8, 30), ScheduleDays.None, false, null, DateOnly.FromDateTime(now)));
        await vm.RefreshAsync();

        vm.UpdateNextMeeting(now);
        Assert.Contains("Soon", vm.NextMeetingSummary);
        Assert.Contains("today 09:30", vm.NextMeetingSummary);
        // The countdown is to the moment the meeting opens, a quarter of an hour before
        // the time on the schedule, which is when the room actually goes up.
        Assert.Equal("in 01:15:00", vm.NextMeetingCountdown);
        Assert.Equal(2, vm.Upcoming.Count);
        Assert.Equal("Soon", vm.Upcoming[0].Name);
        Assert.Equal("opens 09:15", vm.Upcoming[0].Opens);
        Assert.Equal("Later today", vm.Upcoming[1].Name);
        Assert.Equal("opens 18:45", vm.Upcoming[1].Opens);
        Assert.Null(SchedulesViewModel.NextOccurrence(soon with { Enabled = false }, now));
    }

    [Fact]
    public async Task TodayLineCountsBothSessionsAndTheNextOneRollsOverWhenTheFirstIsDone()
    {
        var service = new UiService(); using var vm = new SchedulesViewModel(service);
        var today = new DateOnly(2026, 9, 7);
        service.Schedules.Add(new(Guid.NewGuid(), "Session 27", "https://zoom.us/j/1", "a", new TimeOnly(14, 0), ScheduleDays.None, true, null, today));
        service.Schedules.Add(new(Guid.NewGuid(), "Session 28", "https://zoom.us/j/2", "a", new TimeOnly(19, 0), ScheduleDays.None, true, null, today));
        service.Schedules.Add(new(Guid.NewGuid(), "Session 29", "https://zoom.us/j/3", "a", new TimeOnly(19, 0), ScheduleDays.None, true, null, today.AddDays(1)));
        await vm.RefreshAsync();

        vm.UpdateNextMeeting(today.ToDateTime(new TimeOnly(8, 0)));
        Assert.Equal("2 sessions today: 14:00 · 19:00 — 2 to come.", vm.TodaySummary);
        Assert.Contains("Session 27", vm.NextMeetingSummary);

        // Between the two: the first is done, the card moves to the second one the same day.
        vm.UpdateNextMeeting(today.ToDateTime(new TimeOnly(15, 30)));
        Assert.Contains("1 still to come", vm.TodaySummary);
        Assert.Contains("Session 28", vm.NextMeetingSummary);
        Assert.Contains("today 19:00", vm.NextMeetingSummary);

        // After the last one: today is finished and tomorrow's session is announced.
        vm.UpdateNextMeeting(today.ToDateTime(new TimeOnly(20, 0)));
        Assert.Contains("all finished", vm.TodaySummary);
        Assert.Contains("Session 29", vm.NextMeetingSummary);
        Assert.Contains("tomorrow 19:00", vm.NextMeetingSummary);
    }

    [Fact]
    public async Task EnableAllShownOnlyTouchesTheFilteredSchedules()
    {
        var service = new UiService(); using var vm = new SchedulesViewModel(service);
        var monday = new DateOnly(2026, 9, 7);
        service.Schedules.Add(new(Guid.NewGuid(), "Monday one", "https://zoom.us/j/1", "a", new TimeOnly(14, 0), ScheduleDays.None, false, null, monday));
        service.Schedules.Add(new(Guid.NewGuid(), "Monday two", "https://zoom.us/j/2", "a", new TimeOnly(19, 0), ScheduleDays.None, false, null, monday));
        service.Schedules.Add(new(Guid.NewGuid(), "Wednesday", "https://zoom.us/j/3", "a", new TimeOnly(19, 0), ScheduleDays.None, false, null, monday.AddDays(2)));
        await vm.RefreshAsync();
        vm.ScheduleFilter = "Monday";

        await vm.SetEnabledForShownAsync(true);

        Assert.Equal(2, service.Schedules.Count(item => item.Enabled));
        Assert.False(service.Schedules.Single(item => item.Name == "Wednesday").Enabled);
        Assert.Contains("2 schedules enabled", vm.StatusMessage);
    }

    [Fact]
    public async Task EnabledTickInTheListSavesThatScheduleOnly()
    {
        var service = new UiService(); using var vm = new SchedulesViewModel(service);
        var id = Guid.NewGuid();
        service.Schedules.Add(new(id, "Session 27", "https://zoom.us/j/1", "a", new TimeOnly(19, 0), ScheduleDays.None, false, null, new DateOnly(2026, 9, 7)));
        service.Schedules.Add(new(Guid.NewGuid(), "Session 28", "https://zoom.us/j/2", "a", new TimeOnly(14, 0), ScheduleDays.None, false, null, new DateOnly(2026, 9, 8)));
        await vm.RefreshAsync();

        await vm.ToggleEnabledAsync(vm.Items.First(item => item.Id == id));

        Assert.True(service.Schedules.Single(item => item.Id == id).Enabled);
        Assert.False(service.Schedules.Single(item => item.Name == "Session 28").Enabled);
        Assert.Equal("https://zoom.us/j/1", service.Schedules.Single(item => item.Id == id).MeetingUrl);
        Assert.Contains("enabled", vm.StatusMessage);
    }

    [Fact]
    public void RejectsFormulaBeforeAnyImport()
    { Assert.Throws<InvalidDataException>(() => ScheduleWorkbookReader.Read(Workbook(formula: true))); }

    [Fact]
    public async Task ImportMappingCannotReuseAnUnrelatedScheduleEditorsUrl()
    {
        var service = new UiService(); using var vm = new SchedulesViewModel(service);
        await vm.RefreshAsync();
        vm.SelectedSchedule = new(Guid.NewGuid(), "Other meeting", "https://zoom.us/j/11111111111", "other", new TimeOnly(9, 0), ScheduleDays.Monday, false);
        await vm.PreviewImportAsync(Workbook());
        Assert.Equal("https://zoom.us/j/11111111111", vm.MeetingUrl);
        Assert.Equal("https://zoom.us/j/93181040158", vm.ImportMeetingUrl);
        await vm.ConfirmImportAsync(new DateTime(2025, 1, 1));
        Assert.All(service.Schedules, schedule => Assert.Equal("https://zoom.us/j/93181040158", schedule.MeetingUrl));
    }

    [Fact]
    public void SuppliedRealTimetableIsValidatedWhenAvailable()
    {
        var path = Environment.GetEnvironmentVariable("ZOOM_SCHEDULE_TEMPLATE");
        var preview = ScheduleWorkbookReader.Read(string.IsNullOrWhiteSpace(path) ? Workbook() : path);
        Assert.Equal("CAI5_AIS4_S8", preview.GroupCode);
        Assert.Equal(string.IsNullOrWhiteSpace(path) ? 2 : 64, preview.Rows.Count(r => r.CanImport));
        if (!string.IsNullOrWhiteSpace(path))
        {
            Assert.Equal(66, preview.Rows.Count); Assert.Equal(9, preview.Rows.Count(r => r.Type == "Physical"));
            Assert.Equal(2, preview.Rows.Count(r => r.Type == "N/A"));
        }
    }
    [Fact]
    public async Task RepeatedSaveEditsSameScheduleAndDeleteHasFeedback()
    {
        var service = new UiService(); using var vm = new SchedulesViewModel(service);
        await vm.RefreshAsync(); vm.NewCommand.Execute(null); vm.Name = "One class"; vm.Monday = true;
        await vm.SaveAsync(); await vm.SaveAsync(); Assert.Single(service.Schedules);
        await vm.DeleteAsync(); Assert.Empty(service.Schedules); Assert.Contains("Done", vm.StatusMessage);
        await vm.DeleteAsync(); Assert.Contains("Select", vm.StatusMessage);
    }

    private string Workbook(bool formula = false)
    {
        Directory.CreateDirectory(_folder); string path = Path.Combine(_folder, Guid.NewGuid() + ".xlsx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Part(string name, string content) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(content); }
        Part("xl/workbook.xml", "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Sheet1' r:id='r1'/></sheets></workbook>");
        Part("xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='r1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet1.xml'/></Relationships>");
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XElement Row(int n, params string[] values) => new(ns + "row", new XAttribute("r", n), values.Select((v, i) => new XElement(ns + "c", new XAttribute("r", $"{(char)('A' + i)}{n}"), new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", v)))));
        var sheet = new XElement(ns + "worksheet", new XElement(ns + "sheetData", Row(1, "Round Code"), Row(2, "CAI5_AIS4_S8"),
            Row(6, "Session No.", "Date", "Session Type", "Session Content", "Slot"),
            Row(7, "1", "46227", "Online", "Technical", "7:00 pm - 10:00 pm"),
            Row(8, "2", "46228", "Physical", "Technical", "6:00 pm - 9:00 pm"),
            Row(9, "3", "46229", "N/A", "No Session", ""), Row(10, "4", "46227", "Online", "Technical", "7:00 pm - 10:00 pm")));
        if (formula) sheet.Descendants(ns + "c").First().Add(new XElement(ns + "f", "1+1"));
        Part("xl/worksheets/sheet1.xml", sheet.ToString()); return path;
    }
    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }

    private sealed class UiService : IWindowsUiService
    {
        public List<MeetingSchedule> Schedules { get; } = [];
        public event Action<UiActionStatus>? StatusChanged { add { } remove { } }
        public event Action<LiveMeeting>? MeetingBecameLive { add { } remove { } }
        public UiActionStatus CurrentStatus => new("Test", "Ready", "", false, DateTimeOffset.Now);
        public Task<IReadOnlyList<WindowsMeetingAccountMetadata>> GetAccountsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WindowsMeetingAccountMetadata>>([new("CAI5_AIS4_S8", "S8", "") { DefaultMeetingUrl = "https://zoom.us/j/93181040158" }]);
        public Task<IReadOnlyList<MeetingSchedule>> GetSchedulesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MeetingSchedule>>(Schedules.ToArray());
        public Task SaveScheduleAsync(MeetingSchedule schedule, CancellationToken cancellationToken = default) { Schedules.RemoveAll(s => s.Id == schedule.Id); Schedules.Add(schedule); return Task.CompletedTask; }
        public Task<bool> DeleteScheduleAsync(Guid scheduleId, CancellationToken cancellationToken = default) => Task.FromResult(Schedules.RemoveAll(s => s.Id == scheduleId) > 0);
        public Task SaveAccountAsync(WindowsMeetingAccountMetadata account, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiOperationResult> SwitchAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionDisplayInfo> StartMeetingAsync(string accountId, string meetingUrl, EnginePreference preference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> StopMeetingAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionDisplayInfo>> GetActiveSessionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
