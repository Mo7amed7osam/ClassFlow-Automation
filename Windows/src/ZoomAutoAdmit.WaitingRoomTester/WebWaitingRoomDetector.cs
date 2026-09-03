using Microsoft.Playwright;

namespace ZoomAutoAdmit.WaitingRoomTester;

public sealed class WebWaitingRoomDetector
{
    public async Task<bool> RunOnceAsync(IPage page)
    {
        try
        {
            if (page.IsClosed) return false;

            // Auto-click "Join from Your Browser" if on the landing page
            var joinFromBrowser = page.GetByRole(AriaRole.Link, new() { Name = "Join from Your Browser" });
            if (await joinFromBrowser.CountAsync() > 0 && await joinFromBrowser.First.IsVisibleAsync())
            {
                TesterLogger.Web("Clicking 'Join from Your Browser' to enter web meeting");
                await ActionExecutor.ClickWebElementAsync(joinFromBrowser.First, "Join from Your Browser");
                await Task.Delay(1000);
                return false;
            }

            // Fast check 1: Instant "Admit all" button
            var admitAllRole = page.Locator("button").Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"^Admit\s+all$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
            if (await admitAllRole.CountAsync() > 0 && await admitAllRole.First.IsVisibleAsync())
            {
                TesterLogger.Web("Admit all detected");
                return await ActionExecutor.ClickWebElementAsync(admitAllRole.First, "Admit all");
            }

            // Fast check 2: Instant "Admit" button
            var admitRole = page.Locator("button").Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"^Admit$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
            if (await admitRole.CountAsync() > 0 && await admitRole.First.IsVisibleAsync())
            {
                TesterLogger.Web("Admit detected");
                return await ActionExecutor.ClickWebElementAsync(admitRole.First, "Admit");
            }

            // Fast check 3: Any button containing "Admit" (e.g. aria-label or text)
            var anyAdmit = page.Locator("button[aria-label*='Admit'], button:has-text('Admit')");
            if (await anyAdmit.CountAsync() > 0 && await anyAdmit.First.IsVisibleAsync())
            {
                TesterLogger.Web("Admit button detected");
                return await ActionExecutor.ClickWebElementAsync(anyAdmit.First, "Admit");
            }

            // Priority 2: Check for Waiting Room notification toast
            var waitingToast = page.Locator("div, section, aside, [role='alert'], [role='dialog']")
                .Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"entered\s+(?:the\s+)?waiting\s+room", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });

            if (await waitingToast.CountAsync() > 0 && await waitingToast.First.IsVisibleAsync())
            {
                var toastAdmit = waitingToast.First.Locator("button:has-text('Admit')");
                if (await toastAdmit.CountAsync() > 0 && await toastAdmit.First.IsVisibleAsync())
                {
                    TesterLogger.Web("Toast Admit detected");
                    return await ActionExecutor.ClickWebElementAsync(toastAdmit.First, "Toast Admit");
                }

                var toastView = waitingToast.First.Locator("button:has-text('View')");
                if (await toastView.CountAsync() > 0 && await toastView.First.IsVisibleAsync())
                {
                    TesterLogger.Web("Toast View detected");
                    if (await ActionExecutor.ClickWebElementAsync(toastView.First, "Toast View"))
                    {
                        await Task.Delay(300);

                        if (await anyAdmit.CountAsync() > 0 && await anyAdmit.First.IsVisibleAsync())
                        {
                            TesterLogger.Web("Admit detected (post-View)");
                            return await ActionExecutor.ClickWebElementAsync(anyAdmit.First, "Admit");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!page.IsClosed)
            {
                TesterLogger.Error($"Web detector error: {ex.Message}");
            }
        }

        return false;
    }

    public async Task StartWatcherAsync(IBrowserContext context, CancellationToken cancellationToken)
    {
        await StartMultiContextWatcherAsync(new List<IBrowserContext> { context }, cancellationToken);
    }

    public async Task StartMultiContextWatcherAsync(List<IBrowserContext> contexts, CancellationToken cancellationToken)
    {
        TesterLogger.WaitingRoom($"Web watcher started across {contexts.Count} profile session(s).");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var pages = contexts.SelectMany(c => c.Pages.Where(p => !p.IsClosed)).ToList();
                if (pages.Count == 0)
                {
                    await Task.Delay(500, cancellationToken);
                    continue;
                }

                foreach (var page in pages)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    await RunOnceAsync(page);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                TesterLogger.Error($"Web watcher loop error: {ex.Message}");
            }

            try
            {
                await Task.Delay(250, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        TesterLogger.WaitingRoom("Web watcher stopped.");
    }
}
