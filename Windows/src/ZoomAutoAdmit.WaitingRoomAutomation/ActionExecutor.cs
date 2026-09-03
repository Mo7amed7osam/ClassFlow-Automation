using FlaUI.Core.AutomationElements;
using Microsoft.Playwright;
using ZoomAutoAdmit.Core.Sessions;

namespace ZoomAutoAdmit.WaitingRoomAutomation;

public sealed class ActionExecutor
{
    private readonly IWaitingRoomLogger _logger;
    private readonly Guid _sessionId;
    private readonly SessionEngineType _engineType;

    public ActionExecutor(IWaitingRoomLogger logger, Guid sessionId, SessionEngineType engineType)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionId = sessionId;
        _engineType = engineType;
    }

    public bool InvokeDesktopElementUiaOnly(AutomationElement element, string actionName)
    {
        try
        {
            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
                Log("ACTION", $"Invoked {actionName} (UIA InvokePattern)");
                return true;
            }

            if (element.Patterns.LegacyIAccessible.IsSupported)
            {
                element.Patterns.LegacyIAccessible.Pattern.DoDefaultAction();
                Log("ACTION", $"Invoked {actionName} (UIA LegacyIAccessible)");
                return true;
            }

            Log("ERROR", $"Element '{actionName}' has no UIA invocation pattern.");
            return false;
        }
        catch (Exception ex)
        {
            Log("ERROR", $"Failed to invoke desktop element '{actionName}' through UIA: {ex.Message}");
            return false;
        }
    }

    public bool ClickDesktopElement(AutomationElement element, string actionName)
    {
        try
        {
            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
                Log("ACTION", $"Clicked {actionName} (via Invoke)");
                return true;
            }

            if (element.Patterns.LegacyIAccessible.IsSupported)
            {
                element.Patterns.LegacyIAccessible.Pattern.DoDefaultAction();
                Log("ACTION", $"Clicked {actionName} (via LegacyIAccessible)");
                return true;
            }

            // Fallback to coordinate bounding box click
            var rect = element.Properties.BoundingRectangle.ValueOrDefault;
            if (rect.Width > 0 && rect.Height > 0)
            {
                element.Click();
                Log("ACTION", $"Clicked {actionName} (via Click)");
                return true;
            }

            Log("ERROR", $"Element '{actionName}' does not support Invoke or Click patterns.");
            return false;
        }
        catch (Exception ex)
        {
            Log("ERROR", $"Failed to click desktop element '{actionName}': {ex.Message}");
            return false;
        }
    }

    public async Task<bool> ClickWebElementAsync(ILocator locator, string actionName)
    {
        try
        {
            // 1. Try instant JS click first to avoid pointer-event interception from overlay headers
            await locator.EvaluateAsync("el => el.click()");
            Log("ACTION", $"Clicked {actionName} (Instant JS Click)");
            return true;
        }
        catch
        {
            // 2. Fallback to forced Playwright click
            try
            {
                await locator.ClickAsync(new LocatorClickOptions { Timeout = 1500, Force = true });
                Log("ACTION", $"Clicked {actionName} (Forced Click)");
                return true;
            }
            catch (Exception ex)
            {
                Log("ERROR", $"Failed to click web element '{actionName}': {ex.Message}");
                return false;
            }
        }
    }

    private void Log(string category, string message) =>
        _logger.Log(_sessionId, _engineType, category, message);
}
