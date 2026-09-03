using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZoomAutoAdmit.WindowsRuntime.Scheduling;

public sealed class WindowsMeetingScheduleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly IWindowsTaskScheduler? _taskScheduler;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public WindowsMeetingScheduleStore(string? path = null, IWindowsTaskScheduler? taskScheduler = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZoomAutoAdmit",
            "Schedules",
            "schedules.json"));
        _taskScheduler = taskScheduler;
    }

    public async Task<IReadOnlyList<MeetingSchedule>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try { return await ReadUnlockedAsync(cancellationToken); }
        finally { _fileLock.Release(); }
    }

    public async Task UpsertAsync(MeetingSchedule schedule, CancellationToken cancellationToken = default)
    {
        Validate(schedule);
        bool shouldSyncTask = true;
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var schedules = await ReadUnlockedAsync(cancellationToken);
            int index = schedules.FindIndex(candidate => candidate.Id == schedule.Id);
            if (index >= 0)
            {
                var existing = schedules[index];
                if (existing.Enabled == schedule.Enabled &&
                    existing.Time == schedule.Time &&
                    existing.Days == schedule.Days &&
                    existing.OccurrenceDate == schedule.OccurrenceDate &&
                    existing.MeetingUrl == schedule.MeetingUrl &&
                    existing.AccountId == schedule.AccountId &&
                    existing.Name == schedule.Name)
                {
                    // Only metadata (e.g. LastTriggeredDate) changed
                    shouldSyncTask = false;
                }
                schedules[index] = schedule;
            }
            else schedules.Add(schedule);
            await WriteUnlockedAsync(schedules, cancellationToken);
        }
        finally { _fileLock.Release(); }

        if (shouldSyncTask && _taskScheduler != null)
        {
            await _taskScheduler.RegisterTaskAsync(schedule, cancellationToken);
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        bool deleted;
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var schedules = await ReadUnlockedAsync(cancellationToken);
            if (schedules.RemoveAll(schedule => schedule.Id == id) == 0) return false;
            await WriteUnlockedAsync(schedules, cancellationToken);
            deleted = true;
        }
        finally { _fileLock.Release(); }

        if (deleted && _taskScheduler != null)
        {
            await _taskScheduler.DeleteTaskAsync(id, cancellationToken);
        }
        return deleted;
    }

    private async Task<List<MeetingSchedule>> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<MeetingSchedule>>(
            stream,
            JsonOptions,
            cancellationToken) ?? [];
    }

    private async Task WriteUnlockedAsync(List<MeetingSchedule> schedules, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $"schedules.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    schedules.OrderBy(schedule => schedule.Name).ToList(),
                    JsonOptions,
                    cancellationToken);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void Validate(MeetingSchedule schedule)
    {
        if (schedule.Id == Guid.Empty) throw new ArgumentException("Schedule ID is required.");
        if (string.IsNullOrWhiteSpace(schedule.Name)) throw new ArgumentException("Schedule name is required.");
        if (string.IsNullOrWhiteSpace(schedule.AccountId)) throw new ArgumentException("Account is required.");
        if (!schedule.OccurrenceDate.HasValue && schedule.Days == ScheduleDays.None) throw new ArgumentException("Select at least one day.");
        if (!Uri.TryCreate(schedule.MeetingUrl, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("A valid HTTPS meeting URL is required.");
    }
}
