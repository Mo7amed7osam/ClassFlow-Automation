using System.Text;
using System.Text.RegularExpressions;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.UIAutomation.Inspection;

namespace ZoomAutoAdmit.Inspector.Commands;

/// <summary>
/// READ-ONLY probe for the Session Role Management module: reports whether the Windows UIA tree
/// exposes the Participants button, the Joined list, participant rows, each row's More menu,
/// a Make Co-host action and a co-host confirmation. It never clicks, hovers, moves the cursor
/// or changes any Zoom state; it only reads the tree the existing inspector already produces.
/// </summary>
public static class RolesProbeCommand
{
    // Live Windows names: "Participants, open panel, 1 participants, Alt+U" and
    // "Participant list, use arrow key to navigate…" — both are prefixes, not exact labels.
    private static readonly Regex ParticipantsButton = new(@"^participants\b|manage\s+participants", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ListContainer = new(@"participants?\b|joined|waiting\s*room", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MoreMenu = new(@"\bmore\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CoHost = new(@"co-?host", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] RowTypes = ["ListItem", "TreeItem", "DataItem", "Custom", "Group"];
    private static readonly string[] ContainerTypes = ["List", "Tree", "Table", "DataGrid", "Group", "Pane", "Custom"];

    public static int Execute(CliOptions options)
    {
        ConsoleLogger.Info("[ROLE-PROBE] Read-only UIA probe for session role management. Nothing is clicked or changed.");
        ConsoleLogger.Info("[ROLE-PROBE] Run this with the meeting open and the Participants panel visible.");

        var inspection = options.ToInspectionOptions() with
        {
            MaxDepth = Math.Max(options.MaxDepth, 25),
            MaxElements = Math.Max(options.MaxElements, 20000),
            IncludeAllDetails = true
        };
        using var inspector = new ZoomTreeInspector();
        var (roots, summary) = inspector.Inspect(inspection);
        if (roots.Count == 0)
        {
            ConsoleLogger.Warn("[ROLE-PROBE] No UI Automation elements found. Is a Zoom meeting running?");
            return 1;
        }

        List<(InspectElementInfo Element, string Path)> all = [];
        foreach (var root in roots) Collect(root, root.Name is { Length: > 0 } ? root.Name : "(window)", all);

        var participantsButtons = all.Where(item =>
            ParticipantsButton.IsMatch(item.Element.Name) &&
            item.Element.ControlType is "Button" or "SplitButton" or "MenuItem" or "TabItem" or "CheckBox").ToArray();
        var containers = all.Where(item =>
            ContainerTypes.Contains(item.Element.ControlType) &&
            (ListContainer.IsMatch(item.Element.Name) || ListContainer.IsMatch(item.Element.AutomationId))).ToArray();
        var rows = all.Where(item => RowTypes.Contains(item.Element.ControlType) &&
            (ListContainer.IsMatch(item.Path) || ListContainer.IsMatch(item.Element.Name))).ToArray();
        var moreMenus = all.Where(item => MoreMenu.IsMatch(item.Element.Name) || MoreMenu.IsMatch(item.Element.AutomationId)).ToArray();
        var coHost = all.Where(item => CoHost.IsMatch(item.Element.Name) || CoHost.IsMatch(item.Element.AutomationId) ||
            CoHost.IsMatch(item.Element.Value ?? "") || CoHost.IsMatch(item.Element.LegacyName ?? "")).ToArray();
        var menuItems = all.Where(item => item.Element.ControlType is "MenuItem" or "Menu").ToArray();
        var dialogs = all.Where(item => item.Element.ControlType is "Window" or "Pane" &&
            (item.Element.Name.Contains("co-host", StringComparison.OrdinalIgnoreCase) ||
             item.Element.Name.Contains("host", StringComparison.OrdinalIgnoreCase))).ToArray();

        var report = new StringBuilder();
        report.AppendLine("Zoom Session Roles — UIA probe (read-only)");
        report.AppendLine($"Captured: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"Windows: {roots.Count}; elements visited: {summary.TotalElementsVisited}; " +
                          $"depth truncated: {summary.DepthTruncated}; count truncated: {summary.ElementCountTruncated}");
        report.AppendLine();

        Section(report, "1. Participants button candidates", participantsButtons);
        Section(report, "2. Participants / Joined / Waiting-room containers", containers);
        Section(report, "3. Participant rows (first 25)", rows.Take(25).ToArray());
        Section(report, "4. 'More' menu candidates", moreMenus);
        Section(report, "5. Co-host wording (menu item, row label or dialog)", coHost);
        Section(report, "6. Open menus and menu items", menuItems.Take(60).ToArray());
        Section(report, "7. Windows/panes mentioning host", dialogs);

        if (participantsButtons.Any(item => item.Element.Name.Contains("open panel", StringComparison.OrdinalIgnoreCase)))
        {
            report.AppendLine("NOTE");
            report.AppendLine("----");
            report.AppendLine("  The Participants panel is CLOSED — its toolbar button still reads \"open panel\".");
            report.AppendLine("  The Joined list only exists while the panel is open. Open it, then run this probe again.");
            report.AppendLine();
        }

        report.AppendLine("VERDICT");
        report.AppendLine("-------");
        Verdict(report, "Participants button", participantsButtons.Length);
        Verdict(report, "Participants/Joined list container", containers.Length);
        Verdict(report, "Participant rows", rows.Length);
        Verdict(report, "Per-row More menu", moreMenus.Length);
        Verdict(report, "Make Co-host action", coHost.Count(item => item.Element.ControlType is "MenuItem" or "Button"));
        Verdict(report, "Co-host confirmation / label", dialogs.Length + coHost.Count(item => item.Element.ControlType is "Text" or "Window"));
        report.AppendLine();
        report.AppendLine("Repeat this probe with a participant's More menu open by hand to capture sections 4-6 fully.");

        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Logs");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"roles-probe-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, report.ToString());

        Console.WriteLine();
        Console.WriteLine(report.ToString());
        ConsoleLogger.Success($"[ROLE-PROBE] Report written to {path}");
        return 0;
    }

    private static void Collect(InspectElementInfo element, string path, List<(InspectElementInfo, string)> sink)
    {
        sink.Add((element, path));
        foreach (var child in element.Children)
        {
            string name = string.IsNullOrWhiteSpace(child.Name) ? child.ControlType : child.Name;
            Collect(child, path + " > " + Trim(name, 40), sink);
        }
    }

    private static void Section(StringBuilder report, string title, IReadOnlyList<(InspectElementInfo Element, string Path)> items)
    {
        report.AppendLine(title);
        report.AppendLine(new string('-', title.Length));
        if (items.Count == 0) report.AppendLine("  (none found)");
        foreach (var (element, path) in items)
        {
            var patterns = element.Patterns.GetSupportedPatternNames();
            report.AppendLine($"  [{element.ControlType}] Name=\"{Trim(element.Name, 70)}\" AutomationId=\"{Trim(element.AutomationId, 40)}\" " +
                              $"Class=\"{Trim(element.ClassName, 30)}\" Enabled={element.IsEnabled} Offscreen={element.IsOffscreen}");
            report.AppendLine($"      Patterns: {(patterns.Count == 0 ? "(none)" : string.Join(", ", patterns))}");
            report.AppendLine($"      Bounds: {element.BoundingRectangle?.ToString() ?? "(none)"} | Children: {element.Children.Count} | Depth: {element.Depth}");
            report.AppendLine($"      Path: {Trim(path, 200)}");
        }
        report.AppendLine();
    }

    private static void Verdict(StringBuilder report, string label, int count) =>
        report.AppendLine($"  {(count > 0 ? "FOUND  " : "MISSING")} {label} ({count} candidate{(count == 1 ? "" : "s")})");

    private static string Trim(string? value, int length)
    {
        value ??= string.Empty;
        return value.Length <= length ? value : value[..length] + "…";
    }
}
