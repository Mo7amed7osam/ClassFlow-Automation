using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using System.Text;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.Inspector.Runtime;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>
/// A class must never lose its admission because the app stopped. When the app starts while a Zoom
/// meeting is already open (the app was closed or fell over mid-class), it takes the meeting back:
///
///   * admission restarts at once in its own process (Inspector waiting-room-auto-admit), outside
///     the app, so the app going down again cannot stop it;
///   * the meeting's class is found in today's schedules, and the attendance reads, the co-host
///     roles and the LMS follow-ups start again for it, exactly as if it had just gone live.
///
/// Nothing is done when no meeting window exists.
/// </summary>
public static class MeetingAdoption
{
    public static async Task AdoptLiveMeetingAsync(WindowsRuntimeBootstrapper bootstrapper, CancellationToken token = default)
    {
        try
        {
            if (!ZoomMeetingIsOpen()) return;
            WindowsUiRuntimeLog.Write("ADOPT", "A Zoom meeting is already open; taking it back.");
            ConsoleLogger.Info("[ADOPT] A Zoom meeting is already open: admission, attendance, roles and the LMS steps resume for it.");
            StartStandaloneAdmission();

            var now = DateTime.Now;
            var schedules = await bootstrapper.ScheduleStore.ListAsync(token);
            var today = DateOnly.FromDateTime(now);
            var current = schedules
                .Where(s => s.OccurrenceDate == today || (s.OccurrenceDate == null && s.Days.Includes(now.DayOfWeek)))
                .Select(s => (Schedule: s, Start: today.ToDateTime(s.Time)))
                .Where(x => x.Start - ScheduleTiming.StartLead <= now && now <= x.Start.AddHours(3.5))
                .OrderBy(x => Math.Abs((x.Start - now).TotalMinutes))
                .FirstOrDefault();
            if (current.Schedule == null)
            {
                ConsoleLogger.Info("[ADOPT] No scheduled class matches this meeting; only admission was restarted.");
                return;
            }
            var s = current.Schedule;
            var accounts = await bootstrapper.AccountManager.ListConfiguredAsync(token);
            var account = accounts.FirstOrDefault(a => a.AccountId.Equals(s.AccountId, StringComparison.OrdinalIgnoreCase));
            var meeting = new ScheduledMeeting(new Uri(s.MeetingUrl), s.AccountId, DateTimeOffset.Now,
                GroupId: s.GroupName ?? s.AccountId,
                ScheduledStartTime: new DateTimeOffset(current.Start, DateTimeOffset.Now.Offset));
            var session = new MeetingSession(Guid.NewGuid(), meeting, DateTimeOffset.UtcNow);
            var context = new MeetingLaunchContext(session,
                new MeetingAccount(s.AccountId, account?.DisplayName ?? s.AccountId, account?.CredentialReference ?? ""),
                SessionEngineType.Desktop, null);
            // Attendance, roles and the LMS bridge all start from this event, as for a new meeting.
            await bootstrapper.LifecycleEvents.PublishAsync(context, MeetingLifecycleEventKind.Active);
            ConsoleLogger.Success($"[ADOPT] {s.GroupName ?? s.AccountId} {current.Start:HH:mm}: attendance, co-host and LMS steps resumed.");
        }
        catch (Exception ex) { ConsoleLogger.Warn($"[ADOPT] Taking the open meeting back failed: {ex.Message}"); }
    }

    /// <summary>The standalone admission monitor, unless one is already running. Not tied to the app's lifetime.</summary>
    public static void StartStandaloneAdmission()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("ZoomAutoAdmit.Inspector"))
            {
                try { if (CommandLineOf(p.Id).Contains("waiting-room-auto-admit", StringComparison.OrdinalIgnoreCase)) return; }
                catch { }
            }
            string exe = Path.Combine(AppContext.BaseDirectory, "ZoomAutoAdmit.Inspector.exe");
            if (!File.Exists(exe)) { ConsoleLogger.Warn("[ADOPT] ZoomAutoAdmit.Inspector.exe is not next to the app; admission was not restarted."); return; }
            Process.Start(new ProcessStartInfo(exe, "waiting-room-auto-admit")
            {
                UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized, WorkingDirectory = AppContext.BaseDirectory,
            });
            ConsoleLogger.Success("[ADOPT] Admission restarted in its own process for the open meeting.");
        }
        catch (Exception ex) { ConsoleLogger.Warn($"[ADOPT] Admission could not be restarted: {ex.Message}"); }
    }

    private static string CommandLineOf(int pid)
    {
        using var searcher = new System.Management.ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
        foreach (var o in searcher.Get()) return o["CommandLine"]?.ToString() ?? "";
        return "";
    }

    /// <summary>Zoom's meeting window exists (visible, or hidden while this PC shares its screen).</summary>
    public static bool ZoomMeetingIsOpen()
    {
        var zoom = Process.GetProcessesByName("Zoom").Select(p => p.Id).ToHashSet();
        if (zoom.Count == 0) return false;
        bool found = false;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (!zoom.Contains((int)pid)) return true;
            var cls = new StringBuilder(128);
            GetClassName(h, cls, 128);
            if (cls.ToString() == "ConfMultiTabContentWndClass") { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
