using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using ZoomAutoAdmit.UIAutomation.Discovery;
using ZoomAutoAdmit.UIAutomation.Window;

namespace ZoomAutoAdmit.SessionRoles;

/// <summary>Who is sharing their screen in the running meeting, if anybody.</summary>
public interface IPresenterSource
{
    /// <summary>The Zoom display name of the person presenting, or null when nobody is.</summary>
    string? WhoIsPresenting(CancellationToken cancellationToken);
}

/// <summary>Nobody is presenting. Used when the meeting is not a Zoom Desktop one.</summary>
public sealed class NoPresenterSource : IPresenterSource
{
    public static NoPresenterSource Instance { get; } = new();
    public string? WhoIsPresenting(CancellationToken cancellationToken) => null;
}

/// <summary>
/// Reads the name Zoom shows while somebody shares their screen ("Ahmed's screen",
/// "Ahmed is sharing screen"), through the accessibility tree.
///
/// This is a hint about who is teaching, not a decision: it is passed to the AI as context when a
/// Zoom display name does not match anybody by name. Presenting alone never grants co-host, since
/// a student can share a screen too.
/// </summary>
public sealed class ZoomPresenterReader : IPresenterSource
{
    private const uint UiaTimeoutMs = 6000;

    // "Ahmed Ali's screen", "You are viewing Ahmed Ali's screen", "Ahmed Ali is sharing screen".
    private static readonly Regex[] Patterns =
    [
        new(@"^(?:you are viewing\s+)?(?<name>.+?)'s screen$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^(?<name>.+?)\s+is sharing(?:\s+screen)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^(?<name>.+?)\s+started sharing", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    ];

    public string? WhoIsPresenting(CancellationToken cancellationToken)
    {
        string? presenter = null;
        try
        {
            DesktopThread.RunOnInteractiveDesktop(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntPtr meeting = ZoomWindowManager.FindMainZoomMeetingWindow();
                if (meeting == IntPtr.Zero) return;
                using var automation = new UIA3Automation();
                var root = automation.FromHandle(meeting);
                if (root == null) return;

                foreach (var element in Descendants(root))
                {
                    string name = NameOf(element);
                    if (name.Length is 0 or > 120) continue;
                    foreach (var pattern in Patterns)
                    {
                        var match = pattern.Match(name);
                        if (!match.Success) continue;
                        string candidate = match.Groups["name"].Value.Trim();
                        if (candidate.Length is > 0 and <= 80)
                        {
                            presenter = candidate;
                            return;
                        }
                    }
                }
            }, UiaTimeoutMs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { /* A hint that cannot be read is simply absent. */ }
        return presenter;
    }

    private static string NameOf(AutomationElement element)
    {
        try { return (element.Name ?? string.Empty).Trim(); }
        catch { return string.Empty; }
    }

    private static IEnumerable<AutomationElement> Descendants(AutomationElement root)
    {
        var stack = new Stack<AutomationElement>();
        stack.Push(root);
        int visited = 0;
        while (stack.Count > 0)
        {
            // The banner sits near the top of the tree; the cap keeps a huge meeting tree cheap.
            if (visited++ > 600) yield break;
            var element = stack.Pop();
            yield return element;
            AutomationElement[] children;
            try { children = element.FindAllChildren(); }
            catch { continue; }
            foreach (var child in children) stack.Push(child);
        }
    }
}
