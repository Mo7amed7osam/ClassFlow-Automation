using System.Globalization;

namespace ZoomAutoAdmit.Core.Models;

public class CliOptions
{
    public string Command { get; set; } = "inspect";
    public bool CommandExplicitlySet { get; set; }
    public bool ShowAll { get; set; }
    public string? Query { get; set; }
    public int? TargetProcessId { get; set; }
    public long? TargetWindowHandle { get; set; }
    public int MaxDepth { get; set; } = 15;
    public int MaxElements { get; set; } = 800;
    public int DelaySeconds { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 20;
    public bool TimeoutExplicitlySet { get; set; }
    public bool ShowHelp { get; set; }
    public bool Debug { get; set; }
    public string Engine { get; set; } = "windows";
    public string WebProfile { get; set; } = "default";
    public string? MeetingUrl { get; set; }
    public string? AccountId { get; set; }
    public string? TargetEmail { get; set; }
    public Guid? ScheduleId { get; set; }
    public bool WebHeaded { get; set; }
    /// <summary>The dashboard group whose session should be started, e.g. CAI5_AIS4_S7.</summary>
    public string? LmsGroup { get; set; }
    /// <summary>Ask for the LMS email and password and keep them for later runs.</summary>
    public bool SaveLmsLogin { get; set; }
    /// <summary>Walk every step but never press Run Session.</summary>
    public bool DryRun { get; set; }
    /// <summary>Write over a recording link the session already carries.</summary>
    public bool ReplaceExisting { get; set; }
    /// <summary>Mark every student the dashboard lists as Joined.</summary>
    public bool AllPresent { get; set; }
    /// <summary>The students who were present, separated by ';'.</summary>
    public string? PresentNames { get; set; }
    /// <summary>Correct an attendance already taken, through "View details".</summary>
    public bool CorrectAttendance { get; set; }
    /// <summary>Which day of sessions to look at. Today when it is not given.</summary>
    public DateOnly? LmsDay { get; set; }
    /// <summary>The session's own start time, which separates two classes on the same day.</summary>
    public TimeOnly? LmsTime { get; set; }
    public int WebPollIntervalMilliseconds { get; set; } = 750;

    private static readonly HashSet<string> KnownCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "inspect",
        "processes",
        "find",
        "account-menu-inspect",
        "keyboard-switch-debug",
        "uia-hwnd-inspect",
        "account-menu-capture",
        "profile-menu-watch",
        "meeting-watch",
        "waiting-toast-watch",
        "toast-watch",
        "ocr-smoke",
        "waiting-toast-admit-once",
        "waiting-row-hover-watch",
        "waiting-room-auto-admit",
        "background-zoom-test",
        "meeting-start",
        "diagnose-zoom",
        "roles-probe",
        "web-dom-probe",
        "lms-run-session",
        "lms-record-link",
        "lms-attendance",
        "repair-schedules"
    };

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();

        if (args.Length == 0)
        {
            return options;
        }

        int index = 0;

        while (index < args.Length)
        {
            var arg = args[index].Trim();
            string strippedArg = arg.TrimStart('-');

            if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || 
                arg.Equals("-h", StringComparison.OrdinalIgnoreCase) || 
                arg.Equals("/?"))
            {
                options.ShowHelp = true;
            }
            else if (KnownCommands.Contains(strippedArg))
            {
                options.Command = strippedArg.ToLowerInvariant();
                options.CommandExplicitlySet = true;
            }
            else if (arg.Equals("--all", StringComparison.OrdinalIgnoreCase) || arg.Equals("-a", StringComparison.OrdinalIgnoreCase))
            {
                options.ShowAll = true;
                options.MaxDepth = 35;
                options.MaxElements = 3000;
            }
            else if (arg.Equals("--debug", StringComparison.OrdinalIgnoreCase))
            {
                options.Debug = true;
            }
            else if (arg.Equals("--engine", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length)
                {
                    options.Engine = args[++index].Trim().ToLowerInvariant();
                }
            }
            else if (arg.Equals("--profile", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length)
                {
                    options.WebProfile = args[++index].Trim();
                }
            }
            // web-dom-probe: open one thing before dumping, named by its accessible label.
            else if (arg.Equals("--click", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length)
                {
                    options.Query = args[++index].Trim();
                }
            }
            else if (arg.Equals("--meeting-url", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length)
                {
                    options.MeetingUrl = args[++index].Trim();
                    if (!options.CommandExplicitlySet)
                    {
                        options.Command = "waiting-room-auto-admit";
                    }
                }
            }
            else if (arg.Equals("--account-id", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length)
                {
                    options.AccountId = args[++index].Trim();
                }
            }
            else if (arg.Equals("--target-email", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length) options.TargetEmail = args[++index].Trim();
            }
            else if (arg.Equals("--schedule-id", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length && Guid.TryParse(args[index + 1], out var schedId))
                {
                    options.ScheduleId = schedId;
                    index++;
                }
            }
            else if (arg.Equals("--headed", StringComparison.OrdinalIgnoreCase))
            {
                options.WebHeaded = true;
            }
            else if (arg.Equals("--group", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length) options.LmsGroup = args[++index].Trim();
            }
            else if (arg.Equals("--save-login", StringComparison.OrdinalIgnoreCase))
            {
                options.SaveLmsLogin = true;
            }
            else if (arg.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                options.DryRun = true;
            }
            // lms-record-link: write over a recording link the session already has.
            else if (arg.Equals("--replace", StringComparison.OrdinalIgnoreCase))
            {
                options.ReplaceExisting = true;
            }
            else if (arg.Equals("--all-present", StringComparison.OrdinalIgnoreCase))
            {
                options.AllPresent = true;
            }
            else if (arg.Equals("--present", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length) options.PresentNames = args[++index];
            }
            else if (arg.Equals("--correct", StringComparison.OrdinalIgnoreCase))
            {
                options.CorrectAttendance = true;
            }
            else if (arg.Equals("--day", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length && DateOnly.TryParse(args[index + 1], out var day))
                {
                    options.LmsDay = day;
                    index++;
                }
            }
            else if (arg.Equals("--time", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length && TimeOnly.TryParse(args[index + 1], out var time))
                {
                    options.LmsTime = time;
                    index++;
                }
            }
            else if (arg.Equals("--poll-ms", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length && int.TryParse(args[index + 1], out int pollMilliseconds))
                {
                    options.WebPollIntervalMilliseconds = Math.Clamp(pollMilliseconds, 500, 1000);
                    index++;
                }
            }
            else if (arg.Equals("--delay", StringComparison.OrdinalIgnoreCase) || arg.Equals("-w", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length && int.TryParse(args[index + 1], out var delay))
                {
                    options.DelaySeconds = delay;
                    index++;
                }
            }
            else if (arg.Equals("--timeout", StringComparison.OrdinalIgnoreCase) || arg.Equals("-t", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length && int.TryParse(args[index + 1], out var timeout))
                {
                    options.TimeoutSeconds = options.Command.Equals("waiting-room-auto-admit", StringComparison.OrdinalIgnoreCase)
                        ? Math.Clamp(timeout, 0, 86400)
                        : Math.Clamp(timeout, 5, 120);
                    options.TimeoutExplicitlySet = true;
                    index++;
                }
            }
            else if (arg.Equals("--max-depth", StringComparison.OrdinalIgnoreCase) || arg.Equals("-d", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length && int.TryParse(args[index + 1], out var depth))
                {
                    options.MaxDepth = depth;
                    index++;
                }
            }
            else if (arg.Equals("--max-elements", StringComparison.OrdinalIgnoreCase) || arg.Equals("-m", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length && int.TryParse(args[index + 1], out var maxElem))
                {
                    options.MaxElements = maxElem;
                    index++;
                }
            }
            else if (arg.Equals("--pid", StringComparison.OrdinalIgnoreCase) || arg.Equals("-p", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length && int.TryParse(args[index + 1], out var pid))
                {
                    options.TargetProcessId = pid;
                    index++;
                }
            }
            else if (arg.Equals("--hwnd", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length)
                {
                    string value = args[index + 1].Trim();
                    bool parsed = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? long.TryParse(value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long handle)
                        : long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out handle);
                    if (parsed && handle > 0)
                    {
                        options.TargetWindowHandle = handle;
                        index++;
                    }
                }
            }
            else if (options.Command.Equals("find", StringComparison.OrdinalIgnoreCase) && options.Query == null && !arg.StartsWith("-", StringComparison.Ordinal))
            {
                options.Query = arg;
            }
            index++;
        }

        if (options.Command.Equals("waiting-room-auto-admit", StringComparison.OrdinalIgnoreCase) && !options.TimeoutExplicitlySet)
        {
            options.TimeoutSeconds = 0; // 0 = continuous until Ctrl+C
        }

        return options;
    }

    public InspectionOptions ToInspectionOptions() =>
        new()
        {
            MaxDepth = MaxDepth,
            MaxElements = MaxElements,
            IncludeAllDetails = ShowAll,
            SearchFilter = Query,
            TargetProcessId = TargetProcessId
        };
}
