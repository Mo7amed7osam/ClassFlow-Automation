using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.Inspector.Commands;
using ZoomAutoAdmit.Inspector.Engines;

namespace ZoomAutoAdmit.Inspector;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = CliOptions.Parse(args);

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        try
        {
            return options.Command.ToLowerInvariant() switch
            {
                "inspect" => InspectCommand.Execute(options),
                "processes" => ProcessesCommand.Execute(),
                "find" => FindCommand.Execute(options),
                "account-menu-inspect" => AccountMenuInspectCommand.Execute(options),
                "keyboard-switch-debug" => KeyboardSwitchDebugCommand.Execute(options),
                "uia-hwnd-inspect" => UiaHwndInspectCommand.Execute(options),
                "roles-probe" => RolesProbeCommand.Execute(options),
                "account-menu-capture" => AccountMenuCaptureCommand.Execute(options),
                "profile-menu-watch" => ProfileMenuWatchCommand.Execute(options),
                "meeting-watch" => MeetingWatchCommand.Execute(options),
                "waiting-toast-watch" or "toast-watch" => WaitingToastWatchCommand.Execute(options),
                "ocr-smoke" => OcrSmokeCommand.Execute(),
                "waiting-toast-admit-once" => WaitingToastAdmitOnceCommand.Execute(options),
                "waiting-row-hover-watch" => WaitingRowHoverWatchCommand.Execute(options),
                "waiting-room-auto-admit" => await AutoAdmitEngineFactory
                    .Create(options.Engine)
                    .RunAsync(options),
                "meeting-start" => await MeetingStartCommand.ExecuteAsync(options),
                "diagnose-zoom" => ExecuteDiagnoseZoom(),
                "background-zoom-test" => BackgroundZoomTestCommand.Execute(options),
                "web-dom-probe" => await WebDomProbeCommand.ExecuteAsync(options),
                "lms-run-session" => await LmsSessionCommand.ExecuteAsync(options),
                "lms-record-link" => await LmsRecordLinkCommand.ExecuteAsync(options),
                "lms-attendance" => await LmsAttendanceCommand.ExecuteAsync(options),
                "repair-schedules" => await RepairSchedulesCommand.ExecuteAsync(options),
                _ => HandleUnknownCommand(options.Command)
            };
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error($"Fatal error executing command '{options.Command}': {ex.Message}");
            ConsoleLogger.Debug(ex.ToString());
            return 1;
        }
    }

    private static int ExecuteDiagnoseZoom()
    {
        var discovery = new ZoomAutoAdmit.UIAutomation.Discovery.ZoomProcessDiscovery();
        var candidates = discovery.FindCandidates(logInfo: false);
        Console.WriteLine($"Found {candidates.Count} Zoom candidate processes.");

        foreach (var c in candidates)
        {
            Console.WriteLine($"--- PID: {c.ProcessId} ({c.ProcessName}) ---");
            foreach (var w in c.Windows)
            {
                var role = ZoomAutoAdmit.UIAutomation.Window.ZoomWindowManager.ClassifyZoomWindow(w.Handle);
                bool isVis = ZoomAutoAdmit.UIAutomation.Interop.NativeMethods.IsWindowVisible(w.Handle);
                ZoomAutoAdmit.UIAutomation.Interop.NativeMethods.GetWindowRect(w.Handle, out var rect);
                Console.WriteLine($"  HWND=0x{w.Handle.ToInt64():X8} | Vis={isVis,-5} | Role={role,-18} | Class='{w.ClassName}' | Title='{w.Title}' | Bounds=[{rect.Left},{rect.Top} {rect.Right-rect.Left}x{rect.Bottom-rect.Top}]");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"IsActiveMeetingPresent: {ZoomAutoAdmit.UIAutomation.Window.ZoomWindowManager.IsActiveMeetingPresent()}");
        Console.WriteLine($"FindMainZoomMeetingWindow: 0x{ZoomAutoAdmit.UIAutomation.Window.ZoomWindowManager.FindMainZoomMeetingWindow().ToInt64():X}");
        Console.WriteLine($"FindParticipantsWindow: 0x{ZoomAutoAdmit.UIAutomation.Window.ZoomWindowManager.FindParticipantsWindow().ToInt64():X}");
        Console.WriteLine($"HasReturnToMeetingButton: {ZoomAutoAdmit.UIAutomation.Window.ZoomWindowManager.HasReturnToMeetingButton()}");
        return 0;
    }

    private static int HandleUnknownCommand(string command)
    {
        ConsoleLogger.Error($"Unknown command '{command}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("                    Zoom Auto Admit (Windows 11 / .NET 8)                       ");
        Console.WriteLine("================================================================================");
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project Windows/src/ZoomAutoAdmit.Inspector/ZoomAutoAdmit.Inspector.csproj -- <command> [options]");
        Console.WriteLine("  .\\run-auto-admit.ps1 [command] [options]");
        Console.WriteLine();
        Console.WriteLine("Primary Commands:");
        Console.WriteLine("  waiting-room-auto-admit   Continuously auto-admit participants (Windows UI or Web engine)");
        Console.WriteLine("  web-dom-probe             Print the Zoom Web client's frames and buttons (read-only)");
        Console.WriteLine("  lms-run-session           Press Run Session on the DEPI dashboard for --group");
        Console.WriteLine("  lms-record-link           Copy the group's Zoom recording link onto its finished session");
        Console.WriteLine("  meeting-start             Run the complete allocated meeting lifecycle");
        Console.WriteLine("  background-zoom-test      Safe diagnostic probe of background window capture & input");
        Console.WriteLine("  inspect                   Print the Zoom UI Automation element tree (read-only)");
        Console.WriteLine("  uia-hwnd-inspect          Print UIA tree for --hwnd plus visible same-process popups");
        Console.WriteLine("  roles-probe               READ-ONLY: report Participants/Joined/More/Make Co-host exposure");
        Console.WriteLine("  keyboard-switch-debug    LIVE debug-only account switch using Profile click + keyboard");
        Console.WriteLine("    --target-email <EMAIL> Required exact saved Zoom account email (changes active account)");
        Console.WriteLine("  processes                 Enumerate candidate Zoom processes and window handles");
        Console.WriteLine("  find \"<search-term>\"      Search Zoom UIA tree by Name, AutomationId, ClassName");
        Console.WriteLine("  meeting-watch             Diagnostic watcher for meeting window & Waiting Room");
        Console.WriteLine("  waiting-toast-watch       Native Windows OCR diagnostic for Waiting Room toasts");
        Console.WriteLine("  waiting-row-hover-watch   READ-ONLY: verify Participants panel individual Admit hover");
        Console.WriteLine();
        Console.WriteLine("Options & Default Values:");
        Console.WriteLine("  --engine <windows|web>    Auto-admit engine to use (default: windows)");
        Console.WriteLine("  --meeting-url <URL>       HTTPS Zoom meeting URL (required when engine is web)");
        Console.WriteLine("  --account-id <ID>         Configured account ID (required for meeting-start)");
        Console.WriteLine("  --hwnd <0xHEX|DECIMAL>    Native window handle for uia-hwnd-inspect");
        Console.WriteLine("  --profile <NAME>          Managed Chromium profile folder (default: default)");
        Console.WriteLine("  --poll-ms <N>             Poll interval in milliseconds, 500-1000ms (default: 750)");
        Console.WriteLine("  --headed                  Launch visible browser for login/refresh (default: headless)");
        Console.WriteLine("  --timeout, -t <N>         Watch duration in seconds (default: 0 = continuous until Ctrl+C)");
        Console.WriteLine("  --debug                   Enable verbose OCR diagnostics");
        Console.WriteLine("  --help, -h                Show this help message");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  1. Standard Background Windows Auto-Admit:");
        Console.WriteLine("     dotnet run --project Windows/src/ZoomAutoAdmit.Inspector/ZoomAutoAdmit.Inspector.csproj -- waiting-room-auto-admit");
        Console.WriteLine();
        Console.WriteLine("  2. Web-Engine Auto-Admit:");
        Console.WriteLine("     dotnet run --project Windows/src/ZoomAutoAdmit.Inspector/ZoomAutoAdmit.Inspector.csproj -- waiting-room-auto-admit --engine web --meeting-url \"https://zoom.us/j/91473108490\"");
        Console.WriteLine();
        Console.WriteLine("  3. Using the PowerShell Launcher:");
        Console.WriteLine("     .\\run-auto-admit.ps1 --meeting-url \"https://zoom.us/j/91473108490\" --profile test1");
        Console.WriteLine();
        Console.WriteLine("  4. Complete Meeting Lifecycle:");
        Console.WriteLine("     dotnet run --project Windows/src/ZoomAutoAdmit.Inspector/ZoomAutoAdmit.Inspector.csproj -- meeting-start --account-id teacher-1 --meeting-url \"https://zoom.us/j/123456789\"");
        Console.WriteLine("================================================================================");
    }
}
