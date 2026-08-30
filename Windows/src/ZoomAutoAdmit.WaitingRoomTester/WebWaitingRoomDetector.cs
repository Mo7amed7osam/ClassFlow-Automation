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
                await joinFromBrowser.First.ClickAsync();
                await Task.Delay(1000);
                return false;
            }

            // Priority 1: Check for "Admit" or "Admit all" button by role or text
            var admitRole = page.GetByRole(AriaRole.Button, new() { Name = "Admit", Exact = true });
            if (await admitRole.CountAsync() > 0 && await admitRole.First.IsVisibleAsync())
            {
                TesterLogger.Web("Admit detected");
                return await ActionExecutor.ClickWebElementAsync(admitRole.First, "Admit");
            }

            var admitAllRole = page.GetByRole(AriaRole.Button, new() { Name = "Admit all", Exact = true });
            if (await admitAllRole.CountAsync() > 0 && await admitAllRole.First.IsVisibleAsync())
            {
                TesterLogger.Web("Admit all detected");
                return await ActionExecutor.ClickWebElementAsync(admitAllRole.First, "Admit all");
            }

            var admitButtons = page.Locator("button").Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"^Admit(?:\s+all)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
            if (await admitButtons.CountAsync() > 0 && await admitButtons.First.IsVisibleAsync())
            {
                TesterLogger.Web("Admit detected");
                return await ActionExecutor.ClickWebElementAsync(admitButtons.First, "Admit");
            }

            // Priority 2: Check for "View" button ONLY inside a Waiting Room notification toast (not top-right Gallery View)
            var waitingToast = page.Locator("div, section, aside, [role='alert'], [role='dialog']")
                .Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"entered\s+(?:the\s+)?waiting\s+room", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });

            if (await waitingToast.CountAsync() > 0 && await waitingToast.First.IsVisibleAsync())
            {
                // Check if toast has Admit button
                var toastAdmit = waitingToast.First.Locator("button").Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"Admit", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
                if (await toastAdmit.CountAsync() > 0 && await toastAdmit.First.IsVisibleAsync())
                {
                    TesterLogger.Web("Toast Admit detected");
                    return await ActionExecutor.ClickWebElementAsync(toastAdmit.First, "Toast Admit");
                }

                // Check if toast has View button
                var toastView = waitingToast.First.Locator("button").Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"View", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
                if (await toastView.CountAsync() > 0 && await toastView.First.IsVisibleAsync())
                {
                    TesterLogger.Web("Toast View detected");
                    if (await ActionExecutor.ClickWebElementAsync(toastView.First, "Toast View"))
                    {
                        await Task.Delay(500);

                        // Search again for Admit button
                        if (await admitRole.CountAsync() > 0 && await admitRole.First.IsVisibleAsync())
                        {
                            TesterLogger.Web("Admit detected (post-View)");
                            return await ActionExecutor.ClickWebElementAsync(admitRole.First, "Admit");
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
        TesterLogger.WaitingRoom("Web watcher started.");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var pages = context.Pages.Where(p => !p.IsClosed).ToList();
                if (pages.Count == 0)
                {
                    await Task.Delay(1000, cancellationToken);
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
                await Task.Delay(500, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        TesterLogger.WaitingRoom("Web watcher stopped.");
    }
}
