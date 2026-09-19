using System.Text.Json;
using System.Text.Json.Serialization;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>What this app needs from the server to keep a person's own classes there.</summary>
public interface IScheduleSyncApi
{
    bool IsSignedIn { get; }
    Task<List<JsonElement>> SchedulesAsync(CancellationToken token);
    Task<JsonElement> SaveSchedulesAsync(IEnumerable<object> schedules, string deviceName, CancellationToken token);
}

/// <summary>
/// The classes this PC opens by itself, kept against the signed-in person's dashboard account.
///
/// Everything else a PC needs already comes from the server - who is signed in, their LMS sign-in,
/// their Zoom accounts, the coordinators whose classes they run and those coordinators' timetables.
/// Their own classes were the last thing still living only in one place. With them here too,
/// signing in on a new PC (a cloud one, say) is all it takes: the classes are there, and they open
/// by themselves as they did before.
///
/// Only their own classes travel. The ones this PC runs for other coordinators are rebuilt from the
/// run plan every few minutes, and sending them here as well would give them two owners.
///
/// It goes both ways, on the same rule as the Zoom accounts: a PC with classes of its own is the
/// one that knows, and sends them; a PC with none takes what the server kept. So a fresh PC is
/// filled in and a working one is never quietly emptied.
/// </summary>
public sealed class ScheduleServerSync(IScheduleSyncApi api, Action<string>? log = null)
{
    /// <summary>The app's own shape for a class, so what goes up comes back exactly as it was.</summary>
    private static readonly JsonSerializerOptions Shape = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>How often the classes are sent again without anything having changed.</summary>
    public static readonly TimeSpan SendEvery = TimeSpan.FromHours(1);

    private readonly Action<string> _log = log ?? (message => ConsoleLogger.Info($"[SCHEDULES] {message}"));
    private DateTimeOffset _lastSent = DateTimeOffset.MinValue;
    private string _lastShape = "";

    /// <summary>A class of this person's own, not one they run for somebody else.</summary>
    public static bool IsTheirOwn(MeetingSchedule schedule) => string.IsNullOrEmpty(schedule.CoordinatorId);

    /// <summary>
    /// Both directions, whichever this PC needs. Returns what to tell the person, or null when
    /// there was nothing to do.
    /// </summary>
    public async Task<string?> SyncAsync(
        IReadOnlyList<MeetingSchedule> here,
        Func<MeetingSchedule, CancellationToken, Task> save,
        CancellationToken token = default)
    {
        if (!api.IsSignedIn) return null;
        var mine = here.Where(IsTheirOwn).ToArray();
        if (mine.Length == 0)
        {
            // Take, and stop there: sending an empty list afterwards would delete on the server the
            // classes this PC has just failed to take.
            int restored = await RestoreAsync(save, token);
            return restored > 0 ? $"{restored} class(es) came from your dashboard account and open by themselves here." : null;
        }
        int? sent = await PushAsync(mine, token);
        return sent is > 0 ? $"{sent} class(es) of this PC were saved to your dashboard account." : null;
    }

    /// <summary>Sends this PC's own classes, when they have changed or an hour has passed.</summary>
    public async Task<int?> PushAsync(IReadOnlyList<MeetingSchedule> mine, CancellationToken token = default)
    {
        if (!api.IsSignedIn || mine.Count == 0) return null;
        var rows = mine.Select(schedule => (object)JsonSerializer.SerializeToElement(schedule, Shape)).ToArray();
        string shape = string.Join("|", mine.OrderBy(s => s.Id).Select(s =>
            $"{s.Id:N}:{s.Name}:{s.MeetingUrl}:{s.AccountId}:{s.Time}:{s.Days}:{s.Enabled}:{s.OccurrenceDate}:{s.PreferredEngine}"));
        if (shape == _lastShape && DateTimeOffset.Now - _lastSent < SendEvery) return null;

        await api.SaveSchedulesAsync(rows, Environment.MachineName, token);
        _lastShape = shape;
        _lastSent = DateTimeOffset.Now;
        _log($"{rows.Length} class(es) of this PC were saved to your dashboard account.");
        return rows.Length;
    }

    /// <summary>
    /// The classes the server kept for this person, written here and registered so they open by
    /// themselves. Only ever called for a PC with none of its own, so nothing is overwritten.
    /// </summary>
    public async Task<int> RestoreAsync(Func<MeetingSchedule, CancellationToken, Task> save, CancellationToken token = default)
    {
        var theirs = await api.SchedulesAsync(token);
        int restored = 0;
        foreach (var element in theirs)
        {
            MeetingSchedule? schedule;
            try { schedule = JsonSerializer.Deserialize<MeetingSchedule>(element, Shape); }
            catch (JsonException ex) { _log($"One class could not be read back: {ex.Message}"); continue; }
            // A class kept by a newer version of the app, or one that never opened here: it is not
            // worth failing the rest of the timetable over.
            if (schedule == null || string.IsNullOrWhiteSpace(schedule.MeetingUrl) || string.IsNullOrWhiteSpace(schedule.AccountId)) continue;
            try
            {
                // Its own classes only, and never marked as already opened today on this PC.
                await save(schedule with { Coordinator = null, CoordinatorId = null, LastTriggeredDate = null }, token);
                restored++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"{schedule.Name} could not be added here: {ex.Message}");
            }
        }
        if (restored > 0)
        {
            _lastShape = "";        // what is here is what the server has; the next pass decides
            _log($"{restored} class(es) came from your dashboard account.");
        }
        return restored;
    }

    /// <summary>The next send goes even if nothing changed (a class was just added or removed).</summary>
    public void Changed() => _lastShape = "";
}
