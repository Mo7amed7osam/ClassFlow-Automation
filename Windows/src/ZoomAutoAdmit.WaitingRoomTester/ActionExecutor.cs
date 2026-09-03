using FlaUI.Core.AutomationElements;
using Microsoft.Playwright;

namespace ZoomAutoAdmit.WaitingRoomTester;

public static class ActionExecutor
{
    /// <summary>
    /// Presses a Waiting Room control through UI Automation only: no coordinates, no mouse and no
    /// focus change. The row admitter calls this; it was added to the runtime copy of this class
    /// but never to the tester's, which left this project unable to build.
    /// </summary>
    public static bool InvokeDesktopElementUiaOnly(AutomationElement element, string actionName)
    {
        try
        {
            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
                TesterLogger.Action($"Invoked {actionName} (UIA InvokePattern)");
                return true;
            }

            if (element.Patterns.LegacyIAccessible.IsSupported)
            {
                element.Patterns.LegacyIAccessible.Pattern.DoDefaultAction();
                TesterLogger.Action($"Invoked {actionName} (UIA LegacyIAccessible)");
                return true;
            }

            TesterLogger.Error($"Element '{actionName}' has no UIA invocation pattern.");
            return false;
        }
        catch (Exception ex)
        {
            TesterLogger.Error($"Failed to invoke desktop element '{actionName}' through UIA: {ex.Message}");
            return false;
        }
    }

    public static bool ClickDesktopElement(AutomationElement element, string actionName)
    {
        try
        {
            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
                TesterLogger.Action($"Clicked {actionName} (via Invoke)");
                return true;
            }

            if (element.Patterns.LegacyIAccessible.IsSupported)
            {
                element.Patterns.LegacyIAccessible.Pattern.DoDefaultAction();
                TesterLogger.Action($"Clicked {actionName} (via LegacyIAccessible)");
                return true;
            }

            // Fallback to coordinate bounding box click
            var rect = element.Properties.BoundingRectangle.ValueOrDefault;
            if (rect.Width > 0 && rect.Height > 0)
            {
                element.Click();
                TesterLogger.Action($"Clicked {actionName} (via Click)");
                return true;
            }

            TesterLogger.Error($"Element '{actionName}' does not support Invoke or Click patterns.");
            return false;
        }
        catch (Exception ex)
        {
            TesterLogger.Error($"Failed to click desktop element '{actionName}': {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> ClickWebElementAsync(ILocator locator, string actionName)
    {
        try
        {
            // 1. Try instant JS click first to avoid pointer-event interception from overlay headers
            await locator.EvaluateAsync("el => el.click()");
            TesterLogger.Action($"Clicked {actionName} (Instant JS Click)");
            return true;
        }
        catch
        {
            // 2. Fallback to forced Playwright click
            try
            {
                await locator.ClickAsync(new LocatorClickOptions { Timeout = 1500, Force = true });
                TesterLogger.Action($"Clicked {actionName} (Forced Click)");
                return true;
            }
            catch (Exception ex)
            {
                TesterLogger.Error($"Failed to click web element '{actionName}': {ex.Message}");
                return false;
            }
        }
    }
}
