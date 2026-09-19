using Microsoft.Playwright;

namespace ZoomAutoAdmit.CloudWorker;

public sealed record Check(string Name, bool Passed, string Detail);

/// <summary>
/// What the machine has, checked before the worker offers to do any work.
///
/// A worker that connects and then fails every class because tzdata is missing or Chromium was
/// never installed is worse than one that refuses to start and says which. Each check names the
/// thing to do, not just the thing that is wrong.
/// </summary>
public static class Preflight
{
    public static async Task<IReadOnlyList<Check>> RunAsync(WorkerSettings settings, CancellationToken token = default)
    {
        var checks = new List<Check>
        {
            Platform(),
            Clock(settings),
            Writable("State directory", settings.StateDirectory),
            Writable("Browser profiles", settings.BrowserProfilesDirectory),
            SharedMemory(),
        };
        checks.Add(await BrowserAsync(settings, token));
        return checks;
    }

    private static Check Platform() =>
        new("Platform", !OperatingSystem.IsWindows(),
            OperatingSystem.IsWindows()
                ? "This is the cloud worker and it is running on Windows. That works, but the Windows application is the "
                  + "better tool there: this one has no desktop Zoom automation at all."
                : $"{Environment.OSVersion.VersionString}, {Environment.ProcessorCount} processors");

    private static Check Clock(WorkerSettings settings)
    {
        try
        {
            var zone = settings.ResolveTimeZone();
            var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
            return new Check("Time zone", true, $"{zone.Id}, now {now:yyyy-MM-dd HH:mm zzz}");
        }
        catch (Exception problem)
        {
            return new Check("Time zone", false, problem.Message);
        }
    }

    private static Check Writable(string name, string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            string probe = Path.Combine(path, $".writable-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return new Check(name, true, path);
        }
        catch (Exception problem)
        {
            return new Check(name, false,
                $"{path} cannot be written to ({problem.Message}). It must be a volume the container's user owns; "
                + "state kept in the image itself is lost on every deployment.");
        }
    }

    /// <summary>
    /// Chromium in a container dies with no useful message when /dev/shm is Docker's default 64 MB.
    /// It is the single most common reason a browser container "just crashes", so it is a check and
    /// not a comment in a document nobody reads.
    /// </summary>
    private static Check SharedMemory()
    {
        if (OperatingSystem.IsWindows()) return new Check("Shared memory", true, "not applicable on Windows");
        try
        {
            var shm = new DriveInfo("/dev/shm");
            long megabytes = shm.TotalSize / 1024 / 1024;
            const long needed = 512;
            return new Check("Shared memory", megabytes >= needed,
                megabytes >= needed
                    ? $"/dev/shm is {megabytes} MB"
                    : $"/dev/shm is only {megabytes} MB. Chromium needs about {needed} MB and crashes without a clear "
                      + "message below it. Give the service shm_size: 1gb in the compose file.");
        }
        catch (Exception problem)
        {
            return new Check("Shared memory", false, $"/dev/shm could not be read: {problem.Message}");
        }
    }

    private static async Task<Check> BrowserAsync(WorkerSettings settings, CancellationToken token)
    {
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = settings.Headless,
                Args = ["--no-sandbox=false"],     // the sandbox stays on; see COOLIFY_DEPLOYMENT.md
            });
            var page = await browser.NewPageAsync();
            await page.SetContentAsync("<title>preflight</title><h1>ok</h1>");
            string title = await page.TitleAsync();
            token.ThrowIfCancellationRequested();
            return new Check("Browser", title == "preflight",
                $"Chromium {browser.Version}, {(settings.Headless ? "headless" : "on a display")}");
        }
        catch (Exception problem)
        {
            return new Check("Browser", false,
                $"Chromium would not start: {problem.Message}. The image installs it with "
                + "`playwright install --with-deps chromium`; check that step ran and that the browsers path is readable.");
        }
    }

    /// <summary>The report, for the log and for the Server page. Never a secret: nothing here holds one.</summary>
    public static string Describe(IReadOnlyList<Check> checks)
    {
        var lines = checks.Select(c => $"  [{(c.Passed ? "ok" : "FAILED")}] {c.Name}: {c.Detail}");
        return string.Join(Environment.NewLine, lines);
    }
}
