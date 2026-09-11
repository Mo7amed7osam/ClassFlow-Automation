using System.Diagnostics;
using System.Xml.Linq;
using System.Globalization;
using System.Security.Principal;

namespace ZoomAutoAdmit.WindowsRuntime.Scheduling;

public sealed class WindowsTaskSchedulerService : IWindowsTaskScheduler
{
    private readonly string? _customExecutablePath;

    public WindowsTaskSchedulerService(string? customExecutablePath = null)
    {
        _customExecutablePath = customExecutablePath;
    }

    public static string GetTaskName(Guid scheduleId) => $@"ZoomAutoAdmit\Schedule_{scheduleId:N}";

    public static string GetLauncherScriptPath(Guid scheduleId) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZoomAutoAdmit",
            "Schedules",
            $"launch_{scheduleId:N}.cmd");

    public async Task RegisterTaskAsync(MeetingSchedule schedule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        string taskName = GetTaskName(schedule.Id);

        if (!schedule.Enabled)
        {
            await DeleteTaskAsync(schedule.Id, cancellationToken);
            return;
        }

        if (schedule.OccurrenceDate.HasValue)
        {
            await RegisterOneTimeAsync(schedule, cancellationToken);
            return;
        }

        try
        {
            string timeString = schedule.Time.ToString("HH:mm");
            string exePath = ResolveInspectorExecutablePath();
            string launcherPath = GetLauncherScriptPath(schedule.Id);
            string launcherDir = Path.GetDirectoryName(launcherPath)!;
            Directory.CreateDirectory(launcherDir);

            string scriptContent = exePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? $"@echo off\r\ndotnet \"{exePath}\" meeting-start --schedule-id {schedule.Id}\r\n"
                : $"@echo off\r\n\"{exePath}\" meeting-start --schedule-id {schedule.Id}\r\n";

            await File.WriteAllTextAsync(launcherPath, scriptContent, System.Text.Encoding.UTF8, cancellationToken);

            string schedulePart;
            if ((schedule.Days & ScheduleDays.EveryDay) == ScheduleDays.EveryDay)
            {
                schedulePart = $"/SC DAILY /ST {timeString}";
            }
            else
            {
                var daysList = new List<string>();
                if (schedule.Days.HasFlag(ScheduleDays.Monday)) daysList.Add("MON");
                if (schedule.Days.HasFlag(ScheduleDays.Tuesday)) daysList.Add("TUE");
                if (schedule.Days.HasFlag(ScheduleDays.Wednesday)) daysList.Add("WED");
                if (schedule.Days.HasFlag(ScheduleDays.Thursday)) daysList.Add("THU");
                if (schedule.Days.HasFlag(ScheduleDays.Friday)) daysList.Add("FRI");
                if (schedule.Days.HasFlag(ScheduleDays.Saturday)) daysList.Add("SAT");
                if (schedule.Days.HasFlag(ScheduleDays.Sunday)) daysList.Add("SUN");

                if (daysList.Count == 0) daysList.Add("MON");

                schedulePart = $"/SC WEEKLY /D {string.Join(",", daysList)} /ST {timeString}";
            }

            string commandLine = $"/Create /TN \"{taskName}\" /TR \"\\\"{launcherPath}\\\"\" {schedulePart} /F";

            var (exitCode, stdout, stderr) = await RunSchtasksAsync(commandLine, cancellationToken);
            if (exitCode == 0)
            {
                WindowsSchedulerLog.Write(
                    "SCHEDULE_REGISTERED",
                    $"Task: {taskName}, Name: '{schedule.Name}', Time: {timeString}, Days: {schedule.Days}, Account: {schedule.AccountId}, Url: {schedule.MeetingUrl}");
            }
            else
            {
                string error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                WindowsSchedulerLog.Write("ERROR", $"Failed to register Windows Scheduled Task '{taskName}': {error.Trim()}");
            }
        }
        catch (Exception ex)
        {
            WindowsSchedulerLog.Write("ERROR", $"Exception while registering task '{taskName}': {ex.Message}");
        }
    }

    public async Task DeleteTaskAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        string taskName = GetTaskName(scheduleId);
        string launcherPath = GetLauncherScriptPath(scheduleId);
        if (File.Exists(launcherPath))
        {
            try { File.Delete(launcherPath); } catch { }
        }

        try
        {
            string commandLine = $"/Delete /TN \"{taskName}\" /F";
            var (exitCode, stdout, stderr) = await RunSchtasksAsync(commandLine, cancellationToken);
            if (exitCode != 0 && !stdout.Contains("ERROR: The system cannot find the file specified", StringComparison.OrdinalIgnoreCase)
                             && !stderr.Contains("ERROR: The system cannot find the file specified", StringComparison.OrdinalIgnoreCase))
            {
                string error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                WindowsSchedulerLog.Write("ERROR", $"Failed to delete Windows Scheduled Task '{taskName}': {error.Trim()}");
            }
        }
        catch (Exception ex)
        {
            WindowsSchedulerLog.Write("ERROR", $"Exception while deleting task '{taskName}': {ex.Message}");
        }
    }

    /// <summary>
    /// The program a schedule's task will actually start, or null when no task is registered for
    /// it. A task that runs a launcher script is followed into the script, because the script is
    /// where the program's path lives - the task itself only ever names the script.
    /// </summary>
    public async Task<string?> ReadTaskTargetAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        var (exitCode, stdout, _) = await RunSchtasksAsync(
            $"/Query /TN \"{GetTaskName(scheduleId)}\" /XML", cancellationToken);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return null;
        return ExtractTaskTarget(stdout, path => File.Exists(path) ? File.ReadAllText(path) : null);
    }

    /// <summary>
    /// Reads the program out of a task's XML. Three shapes exist: the program named directly, a
    /// launcher script that names it, and "dotnet" with the program as its first argument. An
    /// empty string means the task names something that could not be followed, which is as broken
    /// as a missing program and is reported the same way.
    /// </summary>
    public static string ExtractTaskTarget(string taskXml, Func<string, string?> readScript)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XDocument document;
        try { document = XDocument.Parse(taskXml.TrimStart('\uFEFF')); }
        catch (System.Xml.XmlException) { return string.Empty; }

        var exec = document.Descendants(ns + "Exec").FirstOrDefault();
        string command = Unquote(exec?.Element(ns + "Command")?.Value);
        string arguments = exec?.Element(ns + "Arguments")?.Value ?? string.Empty;
        if (command.Length == 0) return string.Empty;

        if (command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            string? script = readScript(command);
            return script == null ? command : FirstQuotedProgram(script) ?? string.Empty;
        }

        if (Path.GetFileName(command).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(command).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return FirstQuotedProgram(arguments) ?? string.Empty;

        return command;
    }

    private static string Unquote(string? value) => (value ?? string.Empty).Trim().Trim('"').Trim();

    /// <summary>The first quoted .exe or .dll in a line of script, which is the program it runs.</summary>
    private static string? FirstQuotedProgram(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            text, "\"([^\"]+\\.(?:exe|dll))\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    public string BuildTaskRunCommand(MeetingSchedule schedule)
    {
        string exePath = ResolveInspectorExecutablePath();
        if (exePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return $"dotnet \"{exePath}\" meeting-start --account-id \"{schedule.AccountId}\" --meeting-url \"{schedule.MeetingUrl}\" --schedule-id {schedule.Id}";
        }

        return $"\"{exePath}\" meeting-start --account-id \"{schedule.AccountId}\" --meeting-url \"{schedule.MeetingUrl}\" --schedule-id {schedule.Id}";
    }

    public static string BuildOneTimeTaskXml(MeetingSchedule schedule, string executablePath)
    {
        if (!schedule.OccurrenceDate.HasValue) throw new ArgumentException("An exact date is required.");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var isDll = executablePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var task = new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "Triggers", new XElement(ns + "TimeTrigger",
                new XElement(ns + "StartBoundary", schedule.OccurrenceDate.Value.ToDateTime(schedule.Time).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
                new XElement(ns + "Enabled", "true"))),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "Author"),
                new XElement(ns + "UserId", WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Windows user identity is unavailable.")),
                new XElement(ns + "LogonType", "InteractiveToken"), new XElement(ns + "RunLevel", "LeastPrivilege"))),
            new XElement(ns + "Settings", new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", "false"), new XElement(ns + "StopIfGoingOnBatteries", "false"),
                new XElement(ns + "StartWhenAvailable", "false"), new XElement(ns + "Enabled", "true"), new XElement(ns + "ExecutionTimeLimit", "PT0S")),
            new XElement(ns + "Actions", new XAttribute("Context", "Author"), new XElement(ns + "Exec",
                new XElement(ns + "Command", isDll ? "dotnet.exe" : executablePath),
                new XElement(ns + "Arguments", (isDll ? $"\"{executablePath}\" " : "") + $"meeting-start --schedule-id {schedule.Id:D}"))));
        return task.ToString();
    }

    private async Task RegisterOneTimeAsync(MeetingSchedule schedule, CancellationToken token)
    {
        string xmlPath = Path.Combine(Path.GetTempPath(), $"zoom-schedule-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(xmlPath, BuildOneTimeTaskXml(schedule, ResolveInspectorExecutablePath()), System.Text.Encoding.Unicode, token);
            var (code, _, _) = await RunSchtasksAsync($"/Create /TN \"{GetTaskName(schedule.Id)}\" /XML \"{xmlPath}\" /F", token);
            if (code != 0) throw new InvalidOperationException($"Windows task registration failed (exit {code}). The schedule is saved locally; check Windows task permissions.");
            WindowsSchedulerLog.Write("SCHEDULE_REGISTERED", $"One-time schedule: {schedule.Id}; Date: {schedule.OccurrenceDate:yyyy-MM-dd}");
        }
        finally { if (File.Exists(xmlPath)) File.Delete(xmlPath); }
    }

    public string ResolveInspectorExecutablePath()
    {
        if (!string.IsNullOrEmpty(_customExecutablePath) && File.Exists(_customExecutablePath))
        {
            return Path.GetFullPath(_customExecutablePath);
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string exeInBase = Path.Combine(baseDir, "ZoomAutoAdmit.Inspector.exe");
        if (File.Exists(exeInBase)) return Path.GetFullPath(exeInBase);

        string dllInBase = Path.Combine(baseDir, "ZoomAutoAdmit.Inspector.dll");
        if (File.Exists(dllInBase)) return Path.GetFullPath(dllInBase);

        string[] candidatePaths =
        [
            Path.Combine(baseDir, "..", "..", "..", "..", "ZoomAutoAdmit.Inspector", "bin", "Debug", "net8.0-windows10.0.19041.0", "ZoomAutoAdmit.Inspector.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "ZoomAutoAdmit.Inspector", "bin", "Release", "net8.0-windows10.0.19041.0", "ZoomAutoAdmit.Inspector.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "src", "ZoomAutoAdmit.Inspector", "bin", "Debug", "net8.0-windows10.0.19041.0", "ZoomAutoAdmit.Inspector.exe"),
            Path.Combine(baseDir, "..", "..", "..", "src", "ZoomAutoAdmit.Inspector", "bin", "Debug", "net8.0-windows10.0.19041.0", "ZoomAutoAdmit.Inspector.exe")
        ];

        foreach (var candidate in candidatePaths)
        {
            try
            {
                string fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath)) return fullPath;
            }
            catch { }
        }

        if (Environment.ProcessPath is { } currentProc && currentProc.EndsWith("ZoomAutoAdmit.Inspector.exe", StringComparison.OrdinalIgnoreCase))
        {
            return currentProc;
        }

        return Path.GetFullPath(exeInBase);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunSchtasksAsync(
        string commandLine,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("schtasks.exe", commandLine)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }
}
