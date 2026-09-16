using System.Diagnostics;
using System.IO;
using System.Management;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using ZoomAutoAdmit.UIAutomation.Meetings;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;
using ZoomAutoAdmit.WindowsUI.Infrastructure;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>A version of the app the server has published.</summary>
public sealed record AppUpdateOffer(Version Version, string Sha256, long Size);

/// <summary>
/// The app keeping itself up to date from the central server, so a fix reaches every coordinator
/// without anyone sending the installer again.
///
/// It asks the server now and then (GET api/v1/app/latest) and, when a newer version is published,
/// the Dashboard offers "Update now". Pressing it downloads the installer, checks it against the
/// published size and SHA-256, and hands over to it (--update), which swaps the new version in and
/// opens the app again. Nothing is ever installed on its own, and never while a class is running
/// or about to open: the installer has to close the app's processes, which would end the class.
///
/// The server's own PC is left out - it is updated by whoever runs the server - and so is a copy
/// that was not installed with the setup (a developer's build).
/// </summary>
public sealed class AppUpdater
{
    // Every ten minutes: a version published just after a three-hourly check stayed invisible to the
    // coordinators for hours (2026-09-17). The check is one small request to the server.
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(10);
    /// <summary>A class opens 15 minutes before its time; this leaves room for the update itself.</summary>
    private static readonly TimeSpan ClassSoon = ScheduleTiming.StartLead + TimeSpan.FromMinutes(20);
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ZoomAutoAdmit";

    private readonly object _gate = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;
    private AppUpdateOffer? _offer;
    private string _status = "";
    private double? _progress;

    public static Version Current { get; } = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

    public AppUpdateOffer? Offer { get { lock (_gate) return _offer; } }
    public string Status { get { lock (_gate) return _status; } }
    /// <summary>0..1 while downloading, else null.</summary>
    public double? Progress { get { lock (_gate) return _progress; } }
    public bool IsWorking => _busy.CurrentCount == 0;

    public event Action? Changed;

    /// <summary>The folder this copy was installed into by the setup, or null for any other copy.</summary>
    public static string? InstalledFolder()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is not string location || location.Length == 0) return null;
            string installed = Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar);
            string running = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            return installed.Equals(running, StringComparison.OrdinalIgnoreCase) ? installed : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    /// <summary>Asks the server whether a newer version is published. Quiet when it cannot be asked.</summary>
    public async Task CheckAsync(RecordingsDashboardSettings settings, CentralApiClient api, bool force = false, CancellationToken token = default)
    {
        if (settings.Mode == DashboardMode.Server || InstalledFolder() == null) { if (Offer != null || Status.Length > 0) Set(null, "", null); return; }
        if (!force && DateTimeOffset.Now - _lastCheck < CheckEvery) return;
        if (api.Me == null) return;
        if (!await _busy.WaitAsync(0, token)) return;
        try
        {
            _lastCheck = DateTimeOffset.Now;
            var latest = await api.GetAsync<JsonElement>("api/v1/app/latest", token);
            if (latest.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String &&
                Version.TryParse(v.GetString(), out var version) && version > Current &&
                latest.GetProperty("sha256").GetString() is { Length: 64 } sha &&
                latest.GetProperty("size").GetInt64() is > 0 and var size)
            {
                Set(new AppUpdateOffer(version, sha.ToLowerInvariant(), size), $"Version {version} is ready (you have {Current}).", null);
                WindowsUiRuntimeLog.Write("UPDATE", $"Version {version} is published; this copy is {Current}.");
            }
            else Set(null, force ? $"You have the latest version ({Current})." : "", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (force) Set(Offer, CentralApiException.Explain(ex), null);
        }
        finally { _busy.Release(); }
    }

    /// <summary>Why the update must wait, or null when it can go ahead now.</summary>
    public static async Task<string?> WhyNotNowAsync(CancellationToken token = default)
    {
        if (ZoomDesktopMeetingEnder.MeetingWindow() != IntPtr.Zero)
            return "A Zoom meeting is open. Update after the class.";
        if (CommandLines("ZoomAutoAdmit.Inspector.exe").Any(c => c.Contains("meeting-start", StringComparison.OrdinalIgnoreCase)))
            return "A class is being opened or run right now. Update after the class.";
        // Only a class's own browser: the LMS browsers (lms-…) and hidden ones (the Zoom report, My
        // Recordings) are not a class, and blocked every update a coordinator tried (2026-09-17).
        if (ZoomAutoAdmit.Core.Meetings.LiveMeetings.List().Any(meeting => meeting.Engine.Equals("Web", StringComparison.OrdinalIgnoreCase)) ||
            CommandLines("chrome.exe").Concat(CommandLines("msedge.exe")).Any(IsClassBrowser))
            return "A class is running in the browser. Update after the class.";
        try
        {
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            foreach (var schedule in await new WindowsMeetingScheduleStore().ListAsync(token))
            {
                if (!schedule.Enabled) continue;
                bool isToday = schedule.OccurrenceDate is { } once ? once == today : schedule.Days.Includes(now.DayOfWeek);
                if (!isToday) continue;
                var classAt = today.ToDateTime(schedule.Time);
                if (classAt > now && classAt - now <= ClassSoon)
                    return $"{schedule.GroupName ?? schedule.Name} opens at {classAt - ScheduleTiming.StartLead:HH:mm}. Update after the class.";
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// Downloads the offered version, checks it and starts it with --update. True means the installer
    /// is running and this app must now close; false comes with the reason in <see cref="Status"/>.
    /// </summary>
    public async Task<bool> InstallAsync(CentralApiClient api, CancellationToken token = default)
    {
        var offer = Offer;
        string? folder = InstalledFolder();
        if (offer == null || folder == null) { Set(offer, "There is no update to install.", null); return false; }
        if (await WhyNotNowAsync(token) is { } wait) { Set(offer, wait, null); return false; }
        if (!await _busy.WaitAsync(0, token)) return false;
        string target = Path.Combine(Path.GetTempPath(), "ZoomAutoAdmit-Update", $"ZoomAutoAdmit-Setup-{offer.Version}.exe");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // An earlier update's installer (170 MB each) is not kept: only the one being fetched stays.
            foreach (var old in Directory.GetFiles(Path.GetDirectoryName(target)!).Where(f => !f.StartsWith(target, StringComparison.OrdinalIgnoreCase)))
                try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            string partial = target + ".partial";
            Set(offer, $"Downloading version {offer.Version}…", 0);
            await using (var file = File.Create(partial))
            {
                var received = new Progress<long>(bytes =>
                    Set(offer, $"Downloading version {offer.Version}… {bytes / 1048576} of {offer.Size / 1048576} MB", Math.Min(1, bytes / (double)offer.Size)));
                await api.DownloadAsync("api/v1/app/download", file, received, token);
            }

            Set(offer, "Checking the download…", 1);
            long length = new FileInfo(partial).Length;
            string sha;
            await using (var read = File.OpenRead(partial)) sha = Convert.ToHexString(await SHA256.HashDataAsync(read, token)).ToLowerInvariant();
            if (length != offer.Size || sha != offer.Sha256)
            {
                File.Delete(partial);
                Set(offer, "The download was incomplete or damaged, so nothing was installed. Try again.", null);
                return false;
            }
            File.Move(partial, target, overwrite: true);

            // One last look: a class may have started while it was downloading.
            if (await WhyNotNowAsync(token) is { } late) { Set(offer, late, null); return false; }

            WindowsUiRuntimeLog.Write("UPDATE", $"Installing version {offer.Version} over {Current}.");
            var start = new ProcessStartInfo(target) { UseShellExecute = false };
            start.ArgumentList.Add("--update");
            start.ArgumentList.Add(folder);
            Process.Start(start);
            Set(offer, $"Installing version {offer.Version}… the app will open again by itself.", null);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WindowsUiErrorLog.Write("The update could not be installed.", ex);
            Set(offer, $"The update could not be downloaded: {CentralApiException.Explain(ex)}", null);
            return false;
        }
        finally { _busy.Release(); }
    }

    private void Set(AppUpdateOffer? offer, string status, double? progress)
    {
        lock (_gate) { _offer = offer; _status = status; _progress = progress; }
        Changed?.Invoke();
    }

    /// <summary>A browser on one of the app's class profiles, shown on screen: not an LMS profile, not hidden.</summary>
    public static bool IsClassBrowser(string commandLine)
    {
        var match = System.Text.RegularExpressions.Regex.Match(commandLine, @"ZoomAutoAdmit\\Profiles\\([^\\""\s]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        if (match.Groups[1].Value.StartsWith("lms-", StringComparison.OrdinalIgnoreCase)) return false;
        return !commandLine.Contains("--headless", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> CommandLines(string exe)
    {
        var lines = new List<string>();
        try
        {
            using var search = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE Name = '{exe}'");
            foreach (ManagementObject process in search.Get())
                using (process)
                    if (process["CommandLine"] is string line) lines.Add(line);
        }
        catch (ManagementException) { }
        return lines;
    }
}
