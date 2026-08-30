using Microsoft.Playwright;

namespace ZoomAutoAdmit.WaitingRoomTester;

internal static class Program
{
    private static CancellationTokenSource? _activeCts;
    private static Task? _activeTask;
    private static IPlaywright? _playwright;
    private static IBrowserContext? _browserContext;

    private static string GetProfilesBaseDir()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZoomAutoAdmit",
            "Profiles");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return dir;
    }

    private static string GetProfilePath(string profileName)
    {
        string clean = string.Join("_", profileName.Split(Path.GetInvalidFileNameChars()));
        string path = Path.Combine(GetProfilesBaseDir(), clean);
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
        return path;
    }

    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TesterLogger.WaitingRoom("Started");

        while (true)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("==================================================");
            Console.WriteLine("       Zoom Waiting Room Action Tester (Standalone)       ");
            Console.WriteLine("==================================================");
            Console.ResetColor();
            Console.WriteLine("1. Start Desktop watcher");
            Console.WriteLine("2. Start Web watcher (Persistent Profile)");
            Console.WriteLine("3. Setup / Login to a Web Profile (Manual Login)");
            Console.WriteLine("4. Stop watcher");
            Console.WriteLine("5. Show logs");
            Console.WriteLine("6. Exit");
            Console.Write("\nSelect an option (1-6): ");

            string? input = Console.ReadLine()?.Trim();
            Console.WriteLine();

            switch (input)
            {
                case "1":
                    StartDesktopWatcher();
                    break;
                case "2":
                    await StartWebWatcherAsync();
                    break;
                case "3":
                    await SetupWebProfileAsync();
                    break;
                case "4":
                    StopWatcher();
                    break;
                case "5":
                    ShowLogs();
                    break;
                case "6":
                    StopWatcher();
                    await CleanupPlaywrightAsync();
                    Console.WriteLine("Exiting tester.");
                    return;
                default:
                    Console.WriteLine("Invalid option. Please choose 1 to 6.");
                    break;
            }
        }
    }

    private static void StartDesktopWatcher()
    {
        StopWatcher();
        _activeCts = new CancellationTokenSource();
        var token = _activeCts.Token;

        var detector = new DesktopWaitingRoomDetector();
        _activeTask = Task.Run(() => detector.StartWatcherAsync(token), token);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n[ACTIVE] Desktop Watcher is RUNNING and monitoring Zoom Desktop in real-time.");
        Console.WriteLine("Incoming detections (Admit / View / Actions) will be printed live below:");
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("==> Press [ENTER] at any time to stop watcher and return to main menu <==\n");
        Console.ResetColor();

        Console.ReadLine();
        StopWatcher();
    }

    private static async Task StartWebWatcherAsync()
    {
        StopWatcher();
        await CleanupPlaywrightAsync();

        Console.Write("Enter Profile Name (e.g. s7, depi21, depi20, depi42): ");
        string? profile = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(profile))
        {
            profile = "default";
        }

        Console.Write("Enter Zoom Web Meeting URL: ");
        string? url = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.WriteLine("Meeting URL cannot be empty.");
            return;
        }

        // Normalize zoom /j/ URL to web client /wc/join/ URL for direct web client loading
        if (url.Contains("zoom.us/j/", StringComparison.OrdinalIgnoreCase))
        {
            url = url.Replace("zoom.us/j/", "zoom.us/wc/join/", StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            string profileDir = GetProfilePath(profile);
            Console.WriteLine($"[INFO] Using profile directory: {profileDir}");

            _playwright ??= await Playwright.CreateAsync();
            _browserContext = await _playwright.Chromium.LaunchPersistentContextAsync(
                profileDir,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = false,
                    ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
                    Args = new[] { "--disable-blink-features=AutomationControlled" }
                });

            var page = _browserContext.Pages.Count > 0 
                ? _browserContext.Pages[0] 
                : await _browserContext.NewPageAsync();

            Console.WriteLine($"[INFO] Navigating directly to web client URL: {url}");
            await page.GotoAsync(url);

            _activeCts = new CancellationTokenSource();
            var token = _activeCts.Token;

            var detector = new WebWaitingRoomDetector();
            _activeTask = Task.Run(() => detector.StartWatcherAsync(_browserContext, token), token);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n[ACTIVE] Web Watcher is RUNNING and monitoring the Waiting Room in real-time.");
            Console.WriteLine("Incoming detections (Admit / View / Actions) will be printed live below:");
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("==> Press [ENTER] at any time to stop watcher and return to main menu <==\n");
            Console.ResetColor();

            Console.ReadLine();
            StopWatcher();
            await CleanupPlaywrightAsync();
        }
        catch (Exception ex)
        {
            TesterLogger.Error($"Failed to launch web watcher: {ex.Message}");
        }
    }

    private static async Task SetupWebProfileAsync()
    {
        StopWatcher();
        await CleanupPlaywrightAsync();

        Console.Write("Enter Profile Name to setup (e.g. depi21, depi20, depi42): ");
        string? profile = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(profile))
        {
            Console.WriteLine("Profile name cannot be empty.");
            return;
        }

        try
        {
            string profileDir = GetProfilePath(profile);
            Console.WriteLine($"[INFO] Opening Zoom Sign-In for profile '{profile}'...");
            Console.WriteLine($"[INFO] Storage path: {profileDir}");

            _playwright ??= await Playwright.CreateAsync();
            _browserContext = await _playwright.Chromium.LaunchPersistentContextAsync(
                profileDir,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = false,
                    ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
                    Args = new[] { "--disable-blink-features=AutomationControlled" }
                });

            var page = _browserContext.Pages.Count > 0 
                ? _browserContext.Pages[0] 
                : await _browserContext.NewPageAsync();

            await page.GotoAsync("https://zoom.us/signin");

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n[ACTION REQUIRED] Please complete manual Zoom login in the opened browser window.");
            Console.WriteLine("When done logging in, press [ENTER] here to save profile and return to menu...");
            Console.ResetColor();

            Console.ReadLine();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SUCCESS] Profile '{profile}' is saved! Future meeting runs will use this session automatically without asking for login.\n");
            Console.ResetColor();

            await CleanupPlaywrightAsync();
        }
        catch (Exception ex)
        {
            TesterLogger.Error($"Failed to setup web profile: {ex.Message}");
        }
    }

    private static void StopWatcher()
    {
        if (_activeCts != null)
        {
            _activeCts.Cancel();
            _activeCts.Dispose();
            _activeCts = null;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[INFO] Watcher stopped.");
            Console.ResetColor();
        }
        _activeTask = null;
    }

    private static void ShowLogs()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("--- Log Output (%LOCALAPPDATA%\\ZoomAutoAdmit\\Logs\\waiting-room-tester.log) ---");
        Console.ResetColor();
        Console.WriteLine(TesterLogger.ReadAllLogs());
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("--- End of Log Output ---");
        Console.ResetColor();
    }

    private static async Task CleanupPlaywrightAsync()
    {
        if (_browserContext != null)
        {
            try
            {
                await _browserContext.CloseAsync();
            }
            catch { }
            _browserContext = null;
        }
        if (_playwright != null)
        {
            try
            {
                _playwright.Dispose();
            }
            catch { }
            _playwright = null;
        }
    }
}
