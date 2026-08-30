using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace ZoomAutoAdmit.WaitingRoomTester;

public sealed class DesktopWaitingRoomDetector
{
    private static readonly string[] ZoomProcessNames = ["Zoom", "CptHost"];

    public void RunOnce(UIA3Automation automation)
    {
        var zoomPids = Process.GetProcesses()
            .Where(p => ZoomProcessNames.Any(name => p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Id)
            .ToHashSet();

        if (zoomPids.Count == 0) return;

        var desktop = automation.GetDesktop();
        var allWindows = desktop.FindAllChildren();

        foreach (var window in allWindows)
        {
            try
            {
                int pid = window.Properties.ProcessId.ValueOrDefault;
                if (!zoomPids.Contains(pid)) continue;
                if (window.Properties.IsOffscreen.ValueOrDefault) continue;

                // Priority 1: Search for visible "Admit" button
                var admitButton = window.FindFirstDescendant(cf => 
                    cf.ByControlType(ControlType.Button)
                      .And(cf.ByName("Admit")));

                if (admitButton != null && !admitButton.Properties.IsOffscreen.ValueOrDefault && admitButton.Properties.IsEnabled.ValueOrDefault)
                {
                    TesterLogger.Desktop("Admit detected");
                    ActionExecutor.ClickDesktopElement(admitButton, "Admit");
                    return;
                }

                // Also check if any button contains "Admit" (e.g. "Admit", "Admit all")
                var allButtons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
                var admitAny = allButtons.FirstOrDefault(b => 
                    !b.Properties.IsOffscreen.ValueOrDefault &&
                    b.Properties.IsEnabled.ValueOrDefault &&
                    (b.Properties.Name.ValueOrDefault?.Trim().Equals("Admit", StringComparison.OrdinalIgnoreCase) == true ||
                     b.Properties.Name.ValueOrDefault?.Trim().Equals("Admit all", StringComparison.OrdinalIgnoreCase) == true));

                if (admitAny != null)
                {
                    TesterLogger.Desktop("Admit detected");
                    ActionExecutor.ClickDesktopElement(admitAny, "Admit");
                    return;
                }

                // Priority 2: If Admit is not visible, find "View" button
                var viewButton = window.FindFirstDescendant(cf => 
                    cf.ByControlType(ControlType.Button)
                      .And(cf.ByName("View"))) ??
                    allButtons.FirstOrDefault(b => 
                        !b.Properties.IsOffscreen.ValueOrDefault &&
                        b.Properties.IsEnabled.ValueOrDefault &&
                        b.Properties.Name.ValueOrDefault?.Trim().Equals("View", StringComparison.OrdinalIgnoreCase) == true);

                if (viewButton != null)
                {
                    TesterLogger.Desktop("View detected");
                    if (ActionExecutor.ClickDesktopElement(viewButton, "View"))
                    {
                        Thread.Sleep(500);

                        // Search again for Admit button in the opened waiting room
                        var retryAdmit = window.FindFirstDescendant(cf => 
                            cf.ByControlType(ControlType.Button)
                              .And(cf.ByName("Admit"))) ??
                            window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                                  .FirstOrDefault(b => !b.Properties.IsOffscreen.ValueOrDefault &&
                                                       b.Properties.Name.ValueOrDefault?.IndexOf("Admit", StringComparison.OrdinalIgnoreCase) >= 0);

                        if (retryAdmit != null)
                        {
                            TesterLogger.Desktop("Admit detected (post-View)");
                            ActionExecutor.ClickDesktopElement(retryAdmit, "Admit");
                            return;
                        }
                    }
                }
            }
            catch { }
        }
    }

    public async Task StartWatcherAsync(CancellationToken cancellationToken)
    {
        TesterLogger.WaitingRoom("Desktop watcher started.");
        using var automation = new UIA3Automation();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                RunOnce(automation);
            }
            catch (Exception ex)
            {
                TesterLogger.Error($"Desktop watcher error: {ex.Message}");
            }

            await Task.Delay(500, cancellationToken);
        }

        TesterLogger.WaitingRoom("Desktop watcher stopped.");
    }
}
