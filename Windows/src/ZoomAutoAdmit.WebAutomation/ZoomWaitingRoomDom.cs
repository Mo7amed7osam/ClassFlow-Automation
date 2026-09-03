using System.Text.RegularExpressions;
using Microsoft.Playwright;
using ZoomAutoAdmit.WebAutomation.Models;

namespace ZoomAutoAdmit.WebAutomation;

public enum AdmitStrategy
{
    NotificationThenParticipantRow,
    ParticipantRowOnly
}

public sealed class ZoomWaitingRoomDom
{
    private const string NotificationLayerSelector = ".notification-message-wrap__layer";
    private const string NotificationTextSelector = ".notification-message-wrap__txt";
    private const string NotificationAdmitSelector = "button.zmu-btn--primary";
    private const string WaitingRoomScopedRowSelector =
        "[role='listbox'][aria-label='Waiting room list'] [role='application']";
    private const string FallbackParticipantRowSelector = "[role='listitem'], [role='row'], li";
    private static readonly TimeSpan AdmitRevealTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex WaitingRoomPattern = new(
        @"^Waiting\s+room(?:\s*\((?<count>\d+)\)|\s+(?<plainCount>\d+))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExactAdmitPattern = new(
        @"^Admit$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExactAdmitAllPattern = new(
        @"^Admit\s+all$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ArrivalToastPattern = new(
        @"^(?<name>.+?)\s+entered\s+the\s+waiting\s+room$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JoinedPattern = new(
        @"^Joined(?:\s*\(\d+\))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<WebWaitingRoomSnapshot> CaptureAsync(ZoomMeetingSurface surface)
    {
        if (surface.Page.IsClosed) return WebWaitingRoomSnapshot.NoMeeting;

        var notificationParticipants = await ReadNotificationParticipantsAsync(surface.Frame);
        if (notificationParticipants.Count > 0)
        {
            var joinedAtNotification = await ReadJoinedParticipantIdentitiesAsync(surface.Frame);
            return new WebWaitingRoomSnapshot(
                true,
                true,
                notificationParticipants.Count,
                false,
                notificationParticipants)
            {
                JoinedParticipantIdentities = joinedAtNotification,
                ArrivalNotificationParticipantIdentities = notificationParticipants
                    .Select(participant => participant.Identity)
                    .ToArray()
            };
        }

        var header = await FindVisibleWaitingRoomHeaderAsync(surface.Frame);
        if (header == null)
        {
            var joinedOnly = await ReadJoinedParticipantIdentitiesAsync(surface.Frame);
            return new WebWaitingRoomSnapshot(
                true,
                false,
                0,
                false,
                Array.Empty<WebWaitingParticipant>())
            {
                JoinedParticipantIdentities = joinedOnly
            };
        }

        string headerText = await header.InnerTextAsync();
        int? declaredCount = ParseWaitingCount(headerText);
        bool admitAll = await HasVisibleExactButtonAsync(surface.Frame, ExactAdmitAllPattern);
        var participants = await ReadActionableParticipantsAsync(surface);
        int count = declaredCount ?? participants.Count;
        var joined = await ReadJoinedParticipantIdentitiesAsync(surface.Frame);
        return new WebWaitingRoomSnapshot(true, true, count, admitAll, participants)
        {
            JoinedParticipantIdentities = joined
        };
    }

    public async Task<bool> ClickAdmitAllAsync(ZoomMeetingSurface surface)
    {
        var buttons = surface.Frame.GetByRole(AriaRole.Button, new() { NameRegex = ExactAdmitAllPattern });
        return await ClickFirstVisibleAsync(buttons);
    }

    public async Task<bool> ClickParticipantAsync(
        ZoomMeetingSurface surface,
        string participantIdentity,
        AdmitStrategy strategy = AdmitStrategy.NotificationThenParticipantRow)
    {
        if (strategy == AdmitStrategy.NotificationThenParticipantRow)
        {
            try
            {
                if (await ClickNotificationParticipantAsync(surface.Frame, participantIdentity)) return true;
            }
            catch (PlaywrightException ex)
            {
                ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Warn(
                    $"NOTIFICATION_ADMIT_CLICK_FAILED: {ex.Message}");
            }
        }

        var rows = await FindWaitingParticipantRowsAsync(surface.Frame);
        foreach (var row in rows)
        {
            string candidateText = await ReadParticipantNameCandidateAsync(row);
            string identity = WebParticipantIdentity.Normalize(WebParticipantIdentity.FromRowText(candidateText));
            if (!identity.Equals(participantIdentity, StringComparison.OrdinalIgnoreCase)) continue;

            await HoverParticipantRowAsync(row);
            var button = await FindVisibleRowAdmitButtonAsync(row);
            if (button == null) continue;
            if (!await ClickThroughOverlaysAsync(button)) continue;
            ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Success("ADMISSION_CLICKED");
            return true;
        }
        return false;
    }

    /// <summary>A visible "... entered the waiting room" notification in this frame.</summary>
    public static async Task<bool> HasVisibleArrivalNotificationAsync(IFrame frame)
    {
        foreach (var layer in await frame.Locator(NotificationLayerSelector).AllAsync())
        {
            if (!await layer.IsVisibleAsync()) continue;
            string text;
            try { text = await layer.Locator(NotificationTextSelector).First.InnerTextAsync(); }
            catch (PlaywrightException) { continue; }
            if (ArrivalToastPattern.IsMatch(text.Trim())) return true;
        }
        return false;
    }

    public static async Task<bool> HasWaitingRoomHeaderAsync(IFrame frame) =>
        await FindVisibleWaitingRoomHeaderAsync(frame) != null;

    public static int? ParseWaitingCount(string text)
    {
        var match = WaitingRoomPattern.Match(text);
        if (!match.Success) return null;
        string value = match.Groups["count"].Success
            ? match.Groups["count"].Value
            : match.Groups["plainCount"].Value;
        return int.TryParse(value, out int count) ? count : null;
    }

    private static async Task<ILocator?> FindVisibleWaitingRoomHeaderAsync(IFrame frame)
    {
        var headers = frame.GetByText(WaitingRoomPattern);
        foreach (var candidate in await headers.AllAsync())
        {
            if (await candidate.IsVisibleAsync()) return candidate;
        }
        return null;
    }

    private async Task<IReadOnlyList<WebWaitingParticipant>> ReadActionableParticipantsAsync(
        ZoomMeetingSurface surface)
    {
        var participants = new List<WebWaitingParticipant>();
        var rows = await FindWaitingParticipantRowsAsync(surface.Frame);
        foreach (var row in rows)
        {
            string candidateText = await ReadParticipantNameCandidateAsync(row);
            string name = WebParticipantIdentity.FromRowText(candidateText);
            string identity = WebParticipantIdentity.Normalize(name);
            if (string.IsNullOrWhiteSpace(identity) ||
                participants.Any(item => item.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase)))
                continue;

            await HoverParticipantRowAsync(row);
            if (await FindVisibleRowAdmitButtonAsync(row) == null) continue;
            participants.Add(new WebWaitingParticipant(name, identity));
            ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Info($"PARTICIPANT_NAME_EXTRACTED: {name}");
        }
        return participants;
    }

    private static async Task<IReadOnlyList<ILocator>> FindWaitingParticipantRowsAsync(IFrame frame)
    {
        var scopedRows = await FilterWaitingParticipantRowsAsync(
            frame.Locator(WaitingRoomScopedRowSelector),
            sectionAlreadyScoped: true);
        if (scopedRows.Count > 0) return scopedRows;

        var fallbackRows = await FilterWaitingParticipantRowsAsync(
            frame.Locator(FallbackParticipantRowSelector));
        if (fallbackRows.Count > 0) return fallbackRows;

        AriaRole[] semanticRowRoles = [AriaRole.Listitem, AriaRole.Row, AriaRole.Treeitem];
        foreach (var role in semanticRowRoles)
        {
            var rows = await FilterWaitingParticipantRowsAsync(frame.GetByRole(role));
            if (rows.Count > 0) return rows;
        }
        return Array.Empty<ILocator>();
    }

    private static async Task<IReadOnlyList<ILocator>> FilterWaitingParticipantRowsAsync(
        ILocator candidates,
        bool sectionAlreadyScoped = false)
    {
        var rows = new List<ILocator>();
        var candidateRows = await candidates.AllAsync();
        foreach (var row in candidateRows)
        {
            bool isWaitingParticipant = await row.EvaluateAsync<bool>("""
                (row, sectionAlreadyScoped) => {
                  const compact = value => (value || '').replace(/\s+/g, ' ').trim();
                  const descendants = [row, ...row.getElementsByTagName('*')];
                  const hasAvatar = descendants.some(element => {
                    const classes = typeof element.className === 'string' ? element.className : '';
                    return element.tagName === 'IMG' || element.getAttribute('role') === 'img' || /avatar/i.test(classes);
                  });
                  if (!hasAvatar || !compact(row.innerText || row.textContent)) return false;
                  if (sectionAlreadyScoped) return true;

                  let lastSection = '';
                  const headings = document.body ? document.body.getElementsByTagName('*') : [];
                  for (const heading of headings) {
                    if (!(heading.compareDocumentPosition(row) & Node.DOCUMENT_POSITION_FOLLOWING)) continue;
                    const tag = heading.tagName || '';
                    const role = heading.getAttribute('role') || '';
                    if (role !== 'heading' && !/^H[1-6]$/.test(tag)) continue;
                    const text = compact(heading.innerText || heading.textContent);
                    if (/^Waiting\s+room(?:\s*\(\d+\))?$/i.test(text)) lastSection = 'waiting';
                    else if (/^Joined(?:\s*\(\d+\))?$/i.test(text)) lastSection = 'joined';
                  }
                  if (lastSection) return lastSection === 'waiting';

                  for (let node = row.parentElement, depth = 0; node && depth < 5; node = node.parentElement, depth++) {
                    const text = compact(node.innerText || node.textContent);
                    if (/^Waiting\s+room(?:\s*\(\d+\))?/i.test(text)) return true;
                    if (/^Joined(?:\s*\(\d+\))?/i.test(text)) return false;
                  }
                  return false;
                }
                """, sectionAlreadyScoped);
            if (!isWaitingParticipant) continue;
            rows.Add(row);
            ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Info("WAITING_ROW_FOUND");
        }
        return rows;
    }

    private static async Task<string> ReadParticipantNameCandidateAsync(ILocator participantRow)
    {
        const string script = """
            row => {
              const actionPattern = /\b(?:Admit\s+all|Admit|View|More|Message|Remove|Mute|Unmute)\b|\.{3}/ig;
              const clean = value => (value || '')
                .replace(/\bWaiting\s+room(?:\s*\(\d+\))?/ig, '')
                .replace(actionPattern, '')
                .replace(/\s+/g, ' ')
                .trim();
              const walker = document.createTreeWalker(row, NodeFilter.SHOW_TEXT);
              const values = [];
              for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                let parent = node.parentElement;
                let isAction = false;
                while (parent && parent !== row) {
                  const role = parent.getAttribute('role') || '';
                  if (parent.tagName === 'BUTTON' || parent.tagName === 'MENU' ||
                      role === 'button' || role === 'menu' || role === 'menuitem' ||
                      parent.getAttribute('aria-hidden') === 'true') {
                    isAction = true;
                    break;
                  }
                  parent = parent.parentElement;
                }
                if (!isAction) values.push(node.nodeValue || '');
              }
              return clean(values.join(' '));
            }
            """;
        return await participantRow.EvaluateAsync<string>(script) ?? string.Empty;
    }

    private static async Task HoverParticipantRowAsync(ILocator participantRow)
    {
        // Hovering only reveals the row's buttons. Zoom often floats a dialog over the panel, and
        // a blocked hover must not end the meeting's monitor: the click path handles overlays.
        try
        {
            await participantRow.HoverAsync(new() { Timeout = 1500 });
            ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Info("PARTICIPANT_HOVERED");
        }
        catch (PlaywrightException)
        {
            try { await participantRow.EvaluateAsync<object?>(HoverScript); }
            catch (PlaywrightException) { }
        }
    }

    private const string HoverScript =
        "row => { const fire = type => row.dispatchEvent(new MouseEvent(type, { bubbles: true }));" +
        " fire('mouseover'); fire('mouseenter'); fire('mousemove'); }";

    /// <summary>
    /// Presses a button that Zoom may have covered with a modal or permission dialog. A real
    /// mouse click is refused by pointer hit-testing in that case, so the DOM click comes first;
    /// it is what the console tool always used, and it reaches the button Zoom is showing.
    /// </summary>
    private static async Task<bool> ClickThroughOverlaysAsync(ILocator button)
    {
        try
        {
            await button.EvaluateAsync<object?>("element => element.click()");
            return true;
        }
        catch (PlaywrightException ex)
        {
            ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Debug($"DOM click failed: {ex.Message}");
        }

        try
        {
            await button.ClickAsync(new() { Timeout = 2000, Force = true });
            return true;
        }
        catch (PlaywrightException ex)
        {
            ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Warn($"WEB_CLICK_BLOCKED: {FirstLine(ex.Message)}");
            return false;
        }
    }

    private static string FirstLine(string message)
    {
        int newline = message.IndexOf('\n');
        return newline < 0 ? message : message[..newline].Trim();
    }

    private static async Task<ILocator?> FindVisibleRowAdmitButtonAsync(ILocator participantRow)
    {
        var buttons = participantRow.GetByRole(
            AriaRole.Button,
            new LocatorGetByRoleOptions { NameRegex = ExactAdmitPattern });
        var button = buttons.First;
        try
        {
            await button.WaitForAsync(new()
            {
                State = WaitForSelectorState.Visible,
                Timeout = (float)AdmitRevealTimeout.TotalMilliseconds
            });
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (PlaywrightException ex) when (
            ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (!await button.IsVisibleAsync()) return null;
        ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Info("HOVER_STATE_ACTIVATED");
        ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Info("ADMIT_BUTTON_VISIBLE");
        return button;
    }

    private static async Task<IReadOnlyList<WebWaitingParticipant>> ReadNotificationParticipantsAsync(IFrame frame)
    {
        var participants = new List<WebWaitingParticipant>();
        foreach (var notification in await frame.Locator(NotificationLayerSelector).AllAsync())
        {
            if (!await notification.IsVisibleAsync()) continue;
            string text = await ReadNotificationTextAsync(notification);
            var match = ArrivalToastPattern.Match(text.Trim());
            if (!match.Success) continue;
            string name = match.Groups["name"].Value.Trim();
            string identity = WebParticipantIdentity.Normalize(name);
            if (string.IsNullOrWhiteSpace(identity)) continue;
            ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Info("WAITING_ROOM_NOTIFICATION_FOUND");
            var button = await FindVisibleNotificationAdmitButtonAsync(notification);
            if (button == null) continue;
            participants.Add(new WebWaitingParticipant(name, identity));
            ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Info("NOTIFICATION_ADMIT_FOUND");
        }
        return participants;
    }

    private static async Task<bool> ClickNotificationParticipantAsync(
        IFrame frame,
        string participantIdentity)
    {
        foreach (var notification in await frame.Locator(NotificationLayerSelector).AllAsync())
        {
            if (!await notification.IsVisibleAsync()) continue;
            string text = await ReadNotificationTextAsync(notification);
            var match = ArrivalToastPattern.Match(text.Trim());
            if (!match.Success) continue;
            string identity = WebParticipantIdentity.Normalize(match.Groups["name"].Value);
            if (!identity.Equals(participantIdentity, StringComparison.OrdinalIgnoreCase)) continue;
            var button = await FindVisibleNotificationAdmitButtonAsync(notification);
            if (button == null) continue;
            if (!await button.IsVisibleAsync()) continue;
            if (!await ClickNotificationAdmitAsync(button)) continue;
            ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Success("NOTIFICATION_ADMIT_CLICKED");
            ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Success("ADMISSION_CLICKED");
            return true;
        }
        return false;
    }

    private static async Task<string> ReadNotificationTextAsync(ILocator notification)
    {
        foreach (var text in await notification.Locator(NotificationTextSelector).AllAsync())
        {
            if (await text.IsVisibleAsync()) return await text.InnerTextAsync();
        }
        return string.Empty;
    }

    private static async Task<ILocator?> FindVisibleNotificationAdmitButtonAsync(ILocator notification)
    {
        var buttons = notification.Locator(NotificationAdmitSelector);
        foreach (var button in await buttons.AllAsync())
        {
            if (!await button.IsVisibleAsync()) continue;
            string text = (await button.InnerTextAsync()).Trim();
            if (ExactAdmitPattern.IsMatch(text)) return button;
        }

        var roleButtons = notification.GetByRole(AriaRole.Button, new() { NameRegex = ExactAdmitPattern });
        foreach (var button in await roleButtons.AllAsync())
        {
            if (await button.IsVisibleAsync()) return button;
        }

        var textButtons = notification.GetByText("Admit", new() { Exact = true });
        foreach (var button in await textButtons.AllAsync())
        {
            if (await button.IsVisibleAsync()) return button;
        }

        return null;
    }

    private static Task<bool> ClickNotificationAdmitAsync(ILocator button) =>
        ClickThroughOverlaysAsync(button);

    private static async Task<IReadOnlyList<string>> ReadJoinedParticipantIdentitiesAsync(IFrame frame)
    {
        var headers = frame.GetByText(JoinedPattern);
        foreach (var header in await headers.AllAsync())
        {
            if (!await header.IsVisibleAsync()) continue;
            string[] names = await header.EvaluateAsync<string[]>("""
                header => {
                  const actionPattern = /\b(?:Admit|View|More|Message|Remove|Mute|Unmute|Ask to unmute)\b|\.{3}/ig;
                  const clean = value => (value || '').replace(actionPattern, '').replace(/\s+/g, ' ').trim();
                  const selectors = [
                    '[data-testid*="participant-name" i]',
                    '[data-name]',
                    '[class*="participant-name" i]',
                    '[class*="display-name" i]',
                    '[class*="user-name" i]'
                  ];
                  for (let node = header.parentElement, depth = 0; node && depth < 6; node = node.parentElement, depth++) {
                    const result = [];
                    const seenCandidates = new Set();
                    for (const selector of selectors) {
                      for (const candidate of node.querySelectorAll(selector)) {
                        if (seenCandidates.has(candidate)) continue;
                        seenCandidates.add(candidate);
                        if (!(header.compareDocumentPosition(candidate) & Node.DOCUMENT_POSITION_FOLLOWING)) continue;
                        if (candidate.closest('button,[role="button"],menu')) continue;
                        const value = clean(candidate.getAttribute('data-name') || candidate.innerText || candidate.textContent);
                        if (value && value.length <= 200 && !/^Joined(?:\s*\(\d+\))?$/i.test(value)) result.push(value);
                      }
                    }
                    if (result.length) return result;
                  }
                  return [];
                }
                """) ?? Array.Empty<string>();
            return names
                .Select(WebParticipantIdentity.FromRowText)
                .Select(WebParticipantIdentity.Normalize)
                .Where(identity => !string.IsNullOrWhiteSpace(identity))
                .ToArray();
        }
        return Array.Empty<string>();
    }

    private static async Task<bool> HasVisibleExactButtonAsync(IFrame frame, Regex name)
    {
        var buttons = frame.GetByRole(AriaRole.Button, new() { NameRegex = name });
        foreach (var button in await buttons.AllAsync())
        {
            if (await button.IsVisibleAsync()) return true;
        }
        return false;
    }

    private static async Task<bool> ClickFirstVisibleAsync(ILocator buttons)
    {
        foreach (var button in await buttons.AllAsync())
        {
            if (!await button.IsVisibleAsync()) continue;
            if (await ClickThroughOverlaysAsync(button)) return true;
        }
        return false;
    }
}
