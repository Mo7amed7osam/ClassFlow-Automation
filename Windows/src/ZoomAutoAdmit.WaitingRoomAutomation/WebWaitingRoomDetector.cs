using Microsoft.Playwright;
using ZoomAutoAdmit.Core.Sessions;

namespace ZoomAutoAdmit.WaitingRoomAutomation;

public interface IWebWaitingRoomDetector
{
    Task<bool> RunOnceAsync(IPage page);
    Task StartPageWatcherAsync(IPage page, CancellationToken cancellationToken);
}

public sealed class WebWaitingRoomDetector : IWebWaitingRoomDetector
{
    private readonly WaitingRoomSessionLog _log;
    private readonly ActionExecutor _actionExecutor;

    public WebWaitingRoomDetector(Guid sessionId, IWaitingRoomLogger logger)
    {
        _log = new WaitingRoomSessionLog(logger, sessionId, SessionEngineType.Web);
        _actionExecutor = new ActionExecutor(logger, sessionId, SessionEngineType.Web);
    }

    public async Task<bool> RunOnceAsync(IPage page)
    {
        try
        {
            if (page.IsClosed) return false;

            // Auto-click "Join from Your Browser" if on the landing page
            var joinFromBrowser = page.GetByRole(AriaRole.Link, new() { Name = "Join from Your Browser" });
            if (await joinFromBrowser.CountAsync() > 0 && await joinFromBrowser.First.IsVisibleAsync())
            {
                _log.Web("Clicking 'Join from Your Browser' to enter web meeting");
                await _actionExecutor.ClickWebElementAsync(joinFromBrowser.First, "Join from Your Browser");
                await Task.Delay(1000);
                return false;
            }

            // Fast check 1: Instant "Admit all" button
            var admitAllRole = page.Locator("button").Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"^Admit\s+all$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
            if (await admitAllRole.CountAsync() > 0 && await admitAllRole.First.IsVisibleAsync())
            {
                _log.Web("Admit all detected");
                return await _actionExecutor.ClickWebElementAsync(admitAllRole.First, "Admit all");
            }

            // Fast check 2: Instant "Admit" button
            var admitRole = page.Locator("button").Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"^Admit$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
            if (await admitRole.CountAsync() > 0 && await admitRole.First.IsVisibleAsync())
            {
                _log.Web("Admit detected");
                return await _actionExecutor.ClickWebElementAsync(admitRole.First, "Admit");
            }

            // Fast check 3: Any button containing "Admit" (e.g. aria-label or text)
            var anyAdmit = page.Locator("button[aria-label*='Admit'], button:has-text('Admit')");
            if (await anyAdmit.CountAsync() > 0 && await anyAdmit.First.IsVisibleAsync())
            {
                _log.Web("Admit button detected");
                return await _actionExecutor.ClickWebElementAsync(anyAdmit.First, "Admit");
            }

            // Priority 2: Check for Waiting Room notification toast
            var waitingToast = page.Locator("div, section, aside, [role='alert'], [role='dialog']")
                .Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"entered\s+(?:the\s+)?waiting\s+room", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });

            if (await waitingToast.CountAsync() > 0 && await waitingToast.First.IsVisibleAsync())
            {
                var toastAdmit = waitingToast.First.Locator("button:has-text('Admit')");
                if (await toastAdmit.CountAsync() > 0 && await toastAdmit.First.IsVisibleAsync())
                {
                    _log.Web("Toast Admit detected");
                    return await _actionExecutor.ClickWebElementAsync(toastAdmit.First, "Toast Admit");
                }

                var toastView = waitingToast.First.Locator("button:has-text('View')");
                if (await toastView.CountAsync() > 0 && await toastView.First.IsVisibleAsync())
                {
                    _log.Web("Toast View detected");
                    if (await _actionExecutor.ClickWebElementAsync(toastView.First, "Toast View"))
                    {
                        await Task.Delay(300);

                        if (await anyAdmit.CountAsync() > 0 && await anyAdmit.First.IsVisibleAsync())
                        {
                            _log.Web("Admit detected (post-View)");
                            return await _actionExecutor.ClickWebElementAsync(anyAdmit.First, "Admit");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!page.IsClosed)
            {
                _log.Error($"Web detector error: {ex.Message}");
            }
        }

        return false;
    }

    public async Task StartWatcherAsync(IBrowserContext context, CancellationToken cancellationToken)
    {
        await StartMultiContextWatcherAsync(new List<IBrowserContext> { context }, cancellationToken);
    }

    public async Task StartPageWatcherAsync(IPage page, CancellationToken cancellationToken)
    {
        _log.WaitingRoom("Web watcher started across 1 profile session(s).");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (page.IsClosed)
                {
                    await Task.Delay(500, cancellationToken);
                    continue;
                }

                await RunOnceAsync(page);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error($"Web watcher loop error: {ex.Message}");
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

        _log.WaitingRoom("Web watcher stopped.");
    }

    public async Task StartMultiContextWatcherAsync(List<IBrowserContext> contexts, CancellationToken cancellationToken)
    {
        _log.WaitingRoom($"Web watcher started across {contexts.Count} profile session(s).");

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
                _log.Error($"Web watcher loop error: {ex.Message}");
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

        _log.WaitingRoom("Web watcher stopped.");
    }
}
