using Microsoft.Playwright;
using ZoomAutoAdmit.Core.Formatting;

namespace ZoomAutoAdmit.WebAutomation.Browser;

public interface IZoomBrowserLauncher
{
    Task<ZoomBrowserSession> LaunchAsync(
        ZoomBrowserLaunchPlan plan,
        CancellationToken cancellationToken = default);
}

public sealed class ZoomBrowserLauncher : IZoomBrowserLauncher
{
    public async Task<ZoomBrowserSession> LaunchAsync(
        ZoomBrowserLaunchPlan plan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IPlaywright playwright = await Playwright.CreateAsync();
        IBrowserContext? context = null;
        try
        {
            if (!File.Exists(playwright.Chromium.ExecutablePath))
            {
                playwright.Dispose();
                ConsoleLogger.Info("WEB_BROWSER_RUNTIME_INSTALLING");
                int exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
                if (exitCode != 0)
                    throw new InvalidOperationException($"Playwright Chromium installation failed with exit code {exitCode}.");
                playwright = await Playwright.CreateAsync();
            }

            cancellationToken.ThrowIfCancellationRequested();
            // A profile another browser still holds is the commonest way a class fails to open:
            // Chromium refuses the directory, every retry asks for the same one, and the class
            // never opens (2026-09-22, S7 at 18:45, eight tries in a row). A lock left behind by a
            // browser that is gone is cleared; one a living browser holds moves this launch to the
            // account's next profile copy, which is seeded from it and signed in just the same.
            var profile = ZoomProfileLock.Free(plan.Profile, out string? moved);
            if (moved != null) ConsoleLogger.Warn($"[WEB_PROFILE] {moved}");
            plan = plan with { Profile = profile };
            context = await playwright.Chromium.LaunchPersistentContextAsync(
                plan.Profile.DirectoryPath,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = plan.Headless,
                    AcceptDownloads = false,
                    // Playwright's default is to pass --no-sandbox for you. These profiles visit
                    // Zoom and the LMS on the open web, so the sandbox is asked for by name; a
                    // machine that cannot build one refuses to start the browser, which is the
                    // right way round.
                    ChromiumSandbox = true,
                    Args = plan.Arguments.Count > 0 ? [.. plan.Arguments] : null
                });
            cancellationToken.ThrowIfCancellationRequested();
            return new ZoomBrowserSession(playwright, context, plan);
        }
        catch
        {
            if (context != null)
            {
                try { await context.CloseAsync(); }
                catch { }
            }
            playwright.Dispose();
            throw;
        }
    }
}
