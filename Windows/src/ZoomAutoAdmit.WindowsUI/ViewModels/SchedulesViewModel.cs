using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using ZoomAutoAdmit.WindowsRuntime;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

/// <summary>One upcoming session as the panel shows it.</summary>
public sealed record UpcomingMeeting(string Name, string When, string Opens, string Countdown);

public sealed class SchedulesViewModel : ObservableObject, IDisposable
{
    private readonly IWindowsUiService _service;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private MeetingSchedule? _selectedSchedule;
    private Guid _editingId;
    private string _name = string.Empty;
    private string _meetingUrl = string.Empty;
    private WindowsMeetingAccountMetadata? _selectedAccount;
    private string _time = "09:00";
    private bool _enabled = true;
    private bool _monday;
    private bool _tuesday;
    private bool _wednesday;
    private bool _thursday;
    private bool _friday;
    private bool _saturday;
    private bool _sunday;
    private string _statusMessage = string.Empty;
    private string _executionStatus = "Scheduler ready.";
    private readonly IScheduleImportDialogs _dialogs;
    private bool _idle = true, _enableImported = true;
    private DateTime? _occurrenceDate;
    private string _importStatus = "Upload an Excel timetable to preview exact dates. Nothing is saved until you confirm.";
    private string _importGroup = "";
    private WindowsMeetingAccountMetadata? _importAccount;
    private string _importMeetingUrl = "";
    private string _scheduleFilter = "All";
    private string _coordinatorFilter = Everyone;
    private string _nextMeetingSummary = "No upcoming enabled meeting.";
    private string _nextMeetingCountdown = "—";
    private string _todaySummary = "No sessions today.";
    private readonly System.Threading.Timer? _clock;

    public SchedulesViewModel(IWindowsUiService service, IScheduleImportDialogs? dialogs = null)
    {
        _service = service;
        _dialogs = dialogs ?? new ScheduleImportDialogs();
        NewCommand = new RelayCommand(_ => { if (IsIdle) ClearEditor(); });
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync());
        DeleteCommand = new AsyncRelayCommand(_ => DeleteAsync());
        RefreshCommand = new AsyncRelayCommand(async _ => { if (IsIdle) await RefreshAsync(); });
        UploadCommand = new AsyncRelayCommand(async _ => { if (IsIdle) { var path = _dialogs.PickWorkbook(); if (path != null) await PreviewImportAsync(path); } });
        ConfirmImportCommand = new AsyncRelayCommand(_ => ConfirmImportAsync());
        SelectAllImportCommand = new RelayCommand(_ => SetAllImportRows(true));
        ClearImportSelectionCommand = new RelayCommand(_ => SetAllImportRows(false));
        ToggleEnabledCommand = new AsyncRelayCommand(parameter => ToggleEnabledAsync(parameter as MeetingSchedule));
        EnableAllShownCommand = new AsyncRelayCommand(_ => SetEnabledForShownAsync(true));
        DisableAllShownCommand = new AsyncRelayCommand(_ => SetEnabledForShownAsync(false));
        SetOpensWithForShownCommand = new AsyncRelayCommand(parameter => SetOpensWithForShownAsync(parameter as string));
        _service.StatusChanged += OnStatusChanged;
        // The countdown only ticks where there is a UI thread to post to; tests construct without one.
        if (_context != null)
            _clock = new System.Threading.Timer(state => _context.Post(posted => UpdateNextMeeting(), null), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public ObservableCollection<MeetingSchedule> Items { get; } = [];
    /// <summary>What the grid shows: Items narrowed by the day filter.</summary>
    public ObservableCollection<MeetingSchedule> FilteredItems { get; } = [];
    public IReadOnlyList<string> ScheduleFilters { get; } =
        ["All", "Today", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
    public string ScheduleFilter
    {
        get => _scheduleFilter;
        set { if (SetProperty(ref _scheduleFilter, value)) ApplyFilter(); }
    }

    /// <summary>
    /// Whose classes to show. This PC runs its own and, for an admin, other coordinators' as well,
    /// so with several people's timetables in one list the first question is always whose.
    /// </summary>
    public const string Everyone = "Everyone", ThisPc = "This PC only";
    public ObservableCollection<string> Coordinators { get; } = [Everyone, ThisPc];
    public string CoordinatorFilter
    {
        get => _coordinatorFilter;
        set { if (SetProperty(ref _coordinatorFilter, value ?? Everyone)) ApplyFilter(); }
    }
    /// <summary>The filter is worth showing only once somebody else's classes are on this PC.</summary>
    public bool HasCoordinators => Coordinators.Count > 2;

    public string FilterSummary => $"Showing {FilteredItems.Count} of {Items.Count} schedules.";
    public string NextMeetingSummary { get => _nextMeetingSummary; private set => SetProperty(ref _nextMeetingSummary, value); }
    public string NextMeetingCountdown { get => _nextMeetingCountdown; private set => SetProperty(ref _nextMeetingCountdown, value); }
    /// <summary>Today's enabled sessions and how many of them are still to come.</summary>
    public string TodaySummary { get => _todaySummary; private set => SetProperty(ref _todaySummary, value); }

    /// <summary>One line per upcoming session: its name, when it starts, and when it opens.</summary>
    public ObservableCollection<UpcomingMeeting> Upcoming { get; } = [];
    public bool HasUpcoming => Upcoming.Count > 0;
    public string UpcomingCount => Upcoming.Count switch
    {
        0 => "Nothing scheduled ahead",
        1 => "1 session ahead",
        _ => $"{Upcoming.Count} sessions ahead"
    };
    public string LeadNote => $"Every meeting opens {ScheduleTiming.StartLead.TotalMinutes:0} minutes before its time.";
    public ObservableCollection<WindowsMeetingAccountMetadata> Accounts { get; } = [];
    public MeetingSchedule? SelectedSchedule
    {
        get => _selectedSchedule;
        set
        {
            if (!SetProperty(ref _selectedSchedule, value) || value == null) return;
            _editingId = value.Id;
            Name = value.Name;
            MeetingUrl = value.MeetingUrl;
            SelectedAccount = Accounts.FirstOrDefault(account => account.AccountId.Equals(value.AccountId, StringComparison.OrdinalIgnoreCase));
            Time = value.Time.ToString("HH:mm", CultureInfo.InvariantCulture);
            Enabled = value.Enabled;
            OccurrenceDate = value.OccurrenceDate?.ToDateTime(TimeOnly.MinValue);
            Monday = value.Days.HasFlag(ScheduleDays.Monday);
            Tuesday = value.Days.HasFlag(ScheduleDays.Tuesday);
            Wednesday = value.Days.HasFlag(ScheduleDays.Wednesday);
            Thursday = value.Days.HasFlag(ScheduleDays.Thursday);
            Friday = value.Days.HasFlag(ScheduleDays.Friday);
            Saturday = value.Days.HasFlag(ScheduleDays.Saturday);
            Sunday = value.Days.HasFlag(ScheduleDays.Sunday);
            OpensWith = OpensWithLabel(value.PreferredEngine);
        }
    }

    /// <summary>What a class opens with first; the other one is tried when it fails.</summary>
    public IReadOnlyList<string> OpensWithChoices { get; } = ["Auto (Zoom app first)", "Zoom app", "Web (browser)"];
    public string OpensWith { get => _opensWith; set => SetProperty(ref _opensWith, value ?? OpensWithChoices[0]); }
    public string ImportOpensWith { get => _importOpensWith; set => SetProperty(ref _importOpensWith, value ?? OpensWithChoices[0]); }
    private string _opensWith = "Auto (Zoom app first)", _importOpensWith = "Auto (Zoom app first)";
    public static ZoomAutoAdmit.Core.Sessions.SessionEngineType? EngineOf(string? label) => label switch
    {
        "Zoom app" => ZoomAutoAdmit.Core.Sessions.SessionEngineType.Desktop,
        "Web (browser)" => ZoomAutoAdmit.Core.Sessions.SessionEngineType.Web,
        _ => null,
    };
    public static string OpensWithLabel(ZoomAutoAdmit.Core.Sessions.SessionEngineType? engine) => engine switch
    {
        ZoomAutoAdmit.Core.Sessions.SessionEngineType.Desktop => "Zoom app",
        ZoomAutoAdmit.Core.Sessions.SessionEngineType.Web => "Web (browser)",
        _ => "Auto (Zoom app first)",
    };
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string MeetingUrl { get => _meetingUrl; set => SetProperty(ref _meetingUrl, value); }
    public WindowsMeetingAccountMetadata? SelectedAccount { get => _selectedAccount; set { if (SetProperty(ref _selectedAccount, value) && value != null && _editingId == Guid.Empty) MeetingUrl = value.DefaultMeetingUrl ?? ""; } }
    public string Time { get => _time; set => SetProperty(ref _time, value); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public bool Monday { get => _monday; set => SetProperty(ref _monday, value); }
    public bool Tuesday { get => _tuesday; set => SetProperty(ref _tuesday, value); }
    public bool Wednesday { get => _wednesday; set => SetProperty(ref _wednesday, value); }
    public bool Thursday { get => _thursday; set => SetProperty(ref _thursday, value); }
    public bool Friday { get => _friday; set => SetProperty(ref _friday, value); }
    public bool Saturday { get => _saturday; set => SetProperty(ref _saturday, value); }
    public bool Sunday { get => _sunday; set => SetProperty(ref _sunday, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ExecutionStatus { get => _executionStatus; private set => SetProperty(ref _executionStatus, value); }
    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand UploadCommand { get; }
    public ICommand ConfirmImportCommand { get; }
    public ICommand ToggleEnabledCommand { get; }
    public ICommand EnableAllShownCommand { get; }
    public ICommand DisableAllShownCommand { get; }
    public ICommand SetOpensWithForShownCommand { get; }
    public ICommand SelectAllImportCommand { get; }
    public ICommand ClearImportSelectionCommand { get; }
    public bool IsIdle { get => _idle; private set => SetProperty(ref _idle, value); }
    public DateTime? OccurrenceDate { get => _occurrenceDate; set => SetProperty(ref _occurrenceDate, value); }
    public bool EnableImported { get => _enableImported; set => SetProperty(ref _enableImported, value); }
    public string ImportStatus { get => _importStatus; private set => SetProperty(ref _importStatus, value); }
    public ObservableCollection<ScheduleImportSelection> ImportRows { get; } = [];
    public string ImportSelectionSummary => ImportRows.Count == 0
        ? "No timetable loaded yet."
        : $"{ImportRows.Count(row => row.Include)} of {ImportRows.Count(row => row.CanImport)} importable dates selected — untick any date you do not want.";
    public WindowsMeetingAccountMetadata? ImportAccount
    {
        get => _importAccount;
        set { if (SetProperty(ref _importAccount, value)) ImportMeetingUrl = value?.DefaultMeetingUrl ?? ""; }
    }
    public string ImportMeetingUrl { get => _importMeetingUrl; set => SetProperty(ref _importMeetingUrl, value); }

    public async Task RefreshAsync()
    {
        try
        {
            var accounts = await _service.GetAccountsAsync();
            var schedules = await _service.GetSchedulesAsync();
            var accountId = SelectedAccount?.AccountId;
            var draftUrl = MeetingUrl;
            var importAccountId = ImportAccount?.AccountId;
            var importUrl = ImportMeetingUrl;
            Accounts.Clear();
            foreach (var account in accounts) Accounts.Add(account);
            SelectedAccount = Accounts.FirstOrDefault(a => a.AccountId == accountId);
            MeetingUrl = draftUrl;
            ImportAccount = Accounts.FirstOrDefault(a => a.AccountId == importAccountId);
            ImportMeetingUrl = importUrl;
            Items.Clear();
            foreach (var schedule in schedules) Items.Add(schedule);
            UpdateCoordinators();
            ApplyFilter();
            UpdateNextMeeting();
            StatusMessage = $"Refreshed — {Items.Count} schedules loaded; {FilteredItems.Count} shown.";
        }
        catch (Exception ex) { StatusMessage = "Refresh failed: " + ex.Message; }
    }

    public async Task SaveAsync()
    {
        if (!IsIdle) return;
        IsIdle = false;
        StatusMessage = "Saving schedule…";
        try
        {
            if (SelectedAccount == null) throw new InvalidOperationException("Select an account.");
            if (!TimeOnly.TryParseExact(Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime))
                throw new InvalidOperationException("Time must use HH:mm format.");
            ScheduleDays days = SelectedDays();
            var existing = Items.FirstOrDefault(item => item.Id == _editingId);
            var id = _editingId == Guid.Empty ? Guid.NewGuid() : _editingId;
            await _service.SaveScheduleAsync(new MeetingSchedule(
                id,
                Name.Trim(),
                MeetingUrl.Trim(),
                SelectedAccount.AccountId,
                parsedTime,
                days,
                Enabled,
                existing?.LastTriggeredDate,
                OccurrenceDate.HasValue ? DateOnly.FromDateTime(OccurrenceDate.Value) : null,
                SelectedAccount.GroupName ?? SelectedAccount.AccountId,
                EngineOf(OpensWith),
                // Whose class it is survives an edit here: a coordinator's class edited by hand is
                // still theirs, and still goes up on the LMS under their name.
                existing?.Coordinator,
                existing?.CoordinatorId));
            _editingId = id;
            await RefreshAsync();
            StatusMessage = "Done — schedule saved.";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsIdle = true; }
    }

    public async Task DeleteAsync()
    {
        if (!IsIdle) return;
        if (_editingId == Guid.Empty) { StatusMessage = "Select a saved schedule to delete."; return; }
        IsIdle = false; StatusMessage = "Deleting schedule…";
        try
        {
            bool deleted = await _service.DeleteScheduleAsync(_editingId);
            await RefreshAsync();
            ClearEditor();
            StatusMessage = deleted ? "Done — schedule deleted." : "Schedule was not found.";
        }
        catch (Exception ex) { StatusMessage = "Delete failed: " + ex.Message; }
        finally { IsIdle = true; }
    }

    public async Task PreviewImportAsync(string path)
    {
        if (!IsIdle) return;
        IsIdle = false; ImportRows.Clear(); OnPropertyChanged(nameof(ImportSelectionSummary)); ImportStatus = "Reading workbook…";
        try
        {
            var preview = await Task.Run(() => ScheduleWorkbookReader.Read(path));
            _importGroup = preview.GroupCode;
            // Every importable date starts ticked; excluded rows stay off and cannot be ticked.
            foreach (var row in preview.Rows)
            {
                var selection = new ScheduleImportSelection(row);
                selection.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ImportSelectionSummary));
                ImportRows.Add(selection);
            }
            OnPropertyChanged(nameof(ImportSelectionSummary));
            ImportStatus = $"{preview.GroupCode}: {ImportRows.Count} rows; {ImportRows.Count(r => r.CanImport)} online (all selected); {ImportRows.Count(r => !r.CanImport)} excluded. Untick anything you do not want, choose the account and meeting URL, then confirm. Past dates will be skipped.";
            // Exact ID only, never display-name or menu-position matching.
            var mapped = Accounts.FirstOrDefault(a =>
                (a.GroupName ?? a.AccountId).Equals(preview.GroupCode, StringComparison.OrdinalIgnoreCase));
            if (mapped != null) ImportAccount = mapped;
        }
        catch (Exception ex) { ImportStatus = "Import preview failed: " + ex.Message; }
        finally { IsIdle = true; }
    }

    public async Task ConfirmImportAsync(DateTime? localNow = null)
    {
        if (!IsIdle) return;
        IsIdle = false;
        int saved = 0, skipped = 0;
        try
        {
            if (ImportAccount == null) throw new InvalidOperationException("Select the target account first.");
            string accountId = ImportAccount.AccountId;
            string accountGroup = ImportAccount.GroupName ?? ImportAccount.AccountId;
            if (!accountGroup.Equals(_importGroup, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The selected profile belongs to {accountGroup}, not {_importGroup}.");
            bool enableImported = EnableImported;
            string url = ZoomAutoAdmit.WebAutomation.ZoomWebMeetingController.ValidateMeetingUrl(ImportMeetingUrl.Trim()).AbsoluteUri;
            var now = localNow ?? DateTime.Now;
            var existing = (await _service.GetSchedulesAsync()).ToList();
            var candidates = ImportRows.Where(r => r.CanImport && r.Include).Select(r => r.Row).ToArray();
            if (candidates.Length == 0) throw new InvalidOperationException("Select at least one date to import, or upload a valid timetable first.");
            foreach (var row in candidates)
            {
                if (row.Date!.Value.ToDateTime(row.StartTime!.Value) <= now || existing.Any(s =>
                    s.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase) && s.OccurrenceDate == row.Date && s.Time == row.StartTime))
                { skipped++; continue; }
                ImportStatus = $"Saving exact-date schedules… {saved} saved.";
                var schedule = new MeetingSchedule(Guid.NewGuid(), $"{_importGroup} • {row.SessionNumber} • {row.Topic}", url,
                    accountId, row.StartTime.Value, ScheduleDays.None, enableImported, OccurrenceDate: row.Date, GroupName: _importGroup,
                    PreferredEngine: EngineOf(ImportOpensWith));
                await _service.SaveScheduleAsync(schedule);
                existing.Add(schedule); saved++;
            }
            await RefreshAsync();
            ImportStatus = $"Done — {saved} of {candidates.Length} selected dates saved ({(EnableImported ? "enabled" : "disabled")}); {skipped} past/duplicate online dates skipped. Physical / No Session excluded. Times use this PC's local timezone.";
        }
        catch (Exception ex)
        {
            ImportStatus = $"Import stopped: {saved} confirmed saved. {ex.Message} Refresh before retrying; existing entries are not overwritten.";
            await RefreshAsync();
        }
        finally { IsIdle = true; }
    }

    /// <summary>Ticking the Enabled box in the list saves that one schedule; nothing else about it changes.</summary>
    public async Task ToggleEnabledAsync(MeetingSchedule? schedule)
    {
        if (schedule == null || !IsIdle) return;
        IsIdle = false;
        try
        {
            var updated = schedule with { Enabled = !schedule.Enabled };
            await _service.SaveScheduleAsync(updated);
            await RefreshAsync();
            if (_editingId == updated.Id) Enabled = updated.Enabled;
            StatusMessage = $"{updated.Name} — {(updated.Enabled ? "enabled" : "disabled")}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not change Enabled: " + ex.Message;
            await RefreshAsync();
        }
        finally { IsIdle = true; }
    }

    private void ApplyFilter()
    {
        var selected = SelectedSchedule;
        FilteredItems.Clear();
        foreach (var schedule in Items.Where(MatchesFilter)) FilteredItems.Add(schedule);
        OnPropertyChanged(nameof(FilterSummary));
        if (selected != null && !FilteredItems.Contains(selected)) SelectedSchedule = null;
    }

    private bool MatchesFilter(MeetingSchedule schedule)
    {
        if (!MatchesCoordinator(schedule)) return false;
        if (ScheduleFilter == "All") return true;
        if (ScheduleFilter == "Today") return OccursOn(schedule, DateOnly.FromDateTime(DateTime.Now));
        if (!Enum.TryParse<DayOfWeek>(ScheduleFilter, out var day)) return true;
        return schedule.OccurrenceDate.HasValue ? schedule.OccurrenceDate.Value.DayOfWeek == day : schedule.Days.Includes(day);
    }

    private bool MatchesCoordinator(MeetingSchedule schedule) => CoordinatorFilter switch
    {
        Everyone => true,
        ThisPc => string.IsNullOrEmpty(schedule.Coordinator),
        var name => name.Equals(schedule.Coordinator, StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>The people whose classes are on this PC now, kept in the filter as they come and go.</summary>
    private void UpdateCoordinators()
    {
        var found = Items.Select(s => s.Coordinator).Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToArray();
        if (Coordinators.Skip(2).SequenceEqual(found, StringComparer.OrdinalIgnoreCase)) return;
        while (Coordinators.Count > 2) Coordinators.RemoveAt(2);
        foreach (var name in found) Coordinators.Add(name);
        OnPropertyChanged(nameof(HasCoordinators));
        // Whoever was being looked at has gone: back to everyone, rather than an empty table.
        if (!Coordinators.Contains(CoordinatorFilter, StringComparer.OrdinalIgnoreCase)) CoordinatorFilter = Everyone;
    }

    /// <summary>The next time this schedule would start, or null when it is disabled or already past.</summary>
    public static DateTime? NextOccurrence(MeetingSchedule schedule, DateTime now)
    {
        if (!schedule.Enabled) return null;
        if (schedule.OccurrenceDate is { } date)
        {
            var exact = date.ToDateTime(schedule.Time);
            return exact > now ? exact : null;
        }
        if (schedule.Days == ScheduleDays.None) return null;
        for (int offset = 0; offset < 8; offset++)
        {
            var day = now.Date.AddDays(offset);
            if (!schedule.Days.Includes(day.DayOfWeek)) continue;
            var when = day + schedule.Time.ToTimeSpan();
            if (when > now) return when;
        }
        return null;
    }

    public void UpdateNextMeeting(DateTime? localNow = null)
    {
        var now = localNow ?? DateTime.Now;
        UpdateToday(now);

        // Every upcoming session is listed, not only the first: two meetings an hour apart are
        // both worth seeing, and both open early.
        var upcoming = Items
            .Select(schedule => (Schedule: schedule, When: NextOccurrence(schedule, now)))
            .Where(item => item.When.HasValue)
            .Select(item => (item.Schedule, When: item.When!.Value))
            .OrderBy(item => item.When)
            .ToArray();

        Upcoming.Clear();
        foreach (var item in upcoming.Take(6))
            Upcoming.Add(new UpcomingMeeting(
                item.Schedule.Name,
                DescribeMoment(item.When, now),
                $"opens {item.When - ScheduleTiming.StartLead:HH:mm}",
                DescribeCountdown(item.When - now)));
        OnPropertyChanged(nameof(HasUpcoming));
        OnPropertyChanged(nameof(UpcomingCount));

        if (upcoming.Length == 0)
        {
            NextMeetingSummary = Items.Count == 0 ? "No schedules yet." : "No upcoming enabled meeting.";
            NextMeetingCountdown = "—";
            return;
        }
        var next = upcoming[0];
        NextMeetingSummary = $"{next.Schedule.Name} — {DescribeMoment(next.When, now)}";
        NextMeetingCountdown = DescribeCountdown(next.When - ScheduleTiming.StartLead - now);
    }

    /// <summary>Runs on this date? Exact-date schedules answer for their own date, weekly ones for their ticked days.</summary>
    public static bool OccursOn(MeetingSchedule schedule, DateOnly date) =>
        schedule.OccurrenceDate.HasValue ? schedule.OccurrenceDate.Value == date : schedule.Days.Includes(date.DayOfWeek);

    private void UpdateToday(DateTime now)
    {
        var date = DateOnly.FromDateTime(now);
        var sessions = Items.Where(schedule => schedule.Enabled && OccursOn(schedule, date))
            .Select(schedule => now.Date + schedule.Time.ToTimeSpan())
            .OrderBy(when => when)
            .ToArray();
        if (sessions.Length == 0) { TodaySummary = "No sessions today."; return; }
        int left = sessions.Count(when => when > now);
        string times = string.Join(" · ", sessions.Select(when => when.ToString("HH:mm", CultureInfo.InvariantCulture)));
        string tail = left == 0 ? "all finished" : left == sessions.Length ? $"{left} to come" : $"{left} still to come";
        TodaySummary = $"{sessions.Length} session{(sessions.Length == 1 ? "" : "s")} today: {times} — {tail}.";
    }

    /// <summary>Turns every schedule currently shown on (or off) in one pass; the day filter decides the scope.</summary>
    public async Task SetEnabledForShownAsync(bool enabled)
    {
        if (!IsIdle) return;
        var targets = FilteredItems.Where(schedule => schedule.Enabled != enabled).ToArray();
        if (targets.Length == 0) { StatusMessage = enabled ? "Every shown schedule is already enabled." : "Every shown schedule is already disabled."; return; }
        IsIdle = false;
        int changed = 0;
        try
        {
            foreach (var schedule in targets)
            {
                StatusMessage = $"{(enabled ? "Enabling" : "Disabling")} schedules… {changed}/{targets.Length}";
                await _service.SaveScheduleAsync(schedule with { Enabled = enabled });
                changed++;
            }
            await RefreshAsync();
            StatusMessage = $"Done — {changed} schedule{(changed == 1 ? "" : "s")} {(enabled ? "enabled" : "disabled")}.";
        }
        catch (Exception ex)
        {
            await RefreshAsync();
            StatusMessage = $"Stopped after {changed} of {targets.Length}: {ex.Message}";
        }
        finally { IsIdle = true; }
    }

    /// <summary>
    /// What every shown class opens with, in one go. The choice is not a property of one class in
    /// practice - a PC where the Zoom app is unreliable wants Web for all of them - so it is set
    /// for whatever the filters are showing, which is how "all of this coordinator's" is said.
    /// </summary>
    public async Task SetOpensWithForShownAsync(string? label = null)
    {
        if (!IsIdle) return;
        var engine = EngineOf(label ?? OpensWith);
        var targets = FilteredItems.Where(schedule => schedule.PreferredEngine != engine).ToArray();
        if (targets.Length == 0) { StatusMessage = $"Every shown schedule already opens with {OpensWithLabel(engine)}."; return; }
        IsIdle = false;
        int changed = 0;
        try
        {
            foreach (var schedule in targets)
            {
                StatusMessage = $"Setting what each class opens with… {changed}/{targets.Length}";
                await _service.SaveScheduleAsync(schedule with { PreferredEngine = engine });
                changed++;
            }
            await RefreshAsync();
            StatusMessage = $"Done — {changed} schedule{(changed == 1 ? "" : "s")} now open with {OpensWithLabel(engine)}.";
        }
        catch (Exception ex)
        {
            await RefreshAsync();
            StatusMessage = $"Stopped after {changed} of {targets.Length}: {ex.Message}";
        }
        finally { IsIdle = true; }
    }

    private static string DescribeMoment(DateTime when, DateTime now)
    {
        string time = when.ToString("HH:mm", CultureInfo.InvariantCulture);
        int days = (when.Date - now.Date).Days;
        return days switch
        {
            0 => $"today {time}",
            1 => $"tomorrow {time}",
            _ => $"{when:ddd dd MMM} {time}"
        };
    }

    private static string DescribeCountdown(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "starting now";
        return remaining.Days > 0
            ? $"in {remaining.Days}d {remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"in {remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private void SetAllImportRows(bool include)
    {
        if (!IsIdle) return;
        foreach (var row in ImportRows) row.Include = include;
        OnPropertyChanged(nameof(ImportSelectionSummary));
        ImportStatus = include
            ? $"All {ImportRows.Count(row => row.CanImport)} importable dates selected."
            : "All dates unticked. Tick the ones you want before confirming.";
    }

    private ScheduleDays SelectedDays()
    {
        ScheduleDays days = ScheduleDays.None;
        if (Monday) days |= ScheduleDays.Monday;
        if (Tuesday) days |= ScheduleDays.Tuesday;
        if (Wednesday) days |= ScheduleDays.Wednesday;
        if (Thursday) days |= ScheduleDays.Thursday;
        if (Friday) days |= ScheduleDays.Friday;
        if (Saturday) days |= ScheduleDays.Saturday;
        if (Sunday) days |= ScheduleDays.Sunday;
        return days;
    }

    private void ClearEditor()
    {
        SelectedSchedule = null;
        _editingId = Guid.Empty;
        Name = string.Empty;
        MeetingUrl = string.Empty;
        SelectedAccount = Accounts.FirstOrDefault();
        Time = "09:00";
        Enabled = true;
        OccurrenceDate = null;
        Monday = Tuesday = Wednesday = Thursday = Friday = Saturday = Sunday = false;
        StatusMessage = "New schedule — fill the details and Save.";
    }

    private void OnStatusChanged(UiActionStatus status)
    {
        if (status.LastAction != "Scheduled meeting") return;
        string text = string.IsNullOrWhiteSpace(status.ErrorMessage)
            ? status.CurrentOperation
            : $"{status.CurrentOperation}: {status.ErrorMessage}";
        if (_context == null || SynchronizationContext.Current == _context) ExecutionStatus = text;
        else _context.Post(_ => ExecutionStatus = text, null);
    }

    public void Dispose()
    {
        _clock?.Dispose();
        _service.StatusChanged -= OnStatusChanged;
    }
}
