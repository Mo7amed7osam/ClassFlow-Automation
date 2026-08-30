using FlaUI.Core.AutomationElements;
using Microsoft.Playwright;

namespace ZoomAutoAdmit.WaitingRoomTester;

public static class ActionExecutor
{
    public static bool ClickDesktopElement(AutomationElement element, string actionName)
    {
        try
        {
            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
                TesterLogger.Action($"Clicked {actionName}");
                return true;
            }

            if (element.Patterns.LegacyIAccessible.IsSupported)
            {
                element.Patterns.LegacyIAccessible.Pattern.DoDefaultAction();
                TesterLogger.Action($"Clicked {actionName}");
                return true;
            }

            // Fallback to coordinate bounding box click
            var rect = element.Properties.BoundingRectangle.ValueOrDefault;
            if (rect.Width > 0 && rect.Height > 0)
            {
                element.Click();
                TesterLogger.Action($"Clicked {actionName}");
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
            await locator.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
            TesterLogger.Action($"Clicked {actionName}");
            return true;
        }
        catch (Exception ex)
        {
            TesterLogger.Error($"Failed to click web element '{actionName}': {ex.Message}");
            return false;
        }
    }
}
