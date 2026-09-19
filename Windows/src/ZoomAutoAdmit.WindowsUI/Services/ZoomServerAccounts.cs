using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.WindowsRuntime;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>What this app needs from the server to keep a person's Zoom accounts there.</summary>
public interface IZoomAccountsApi
{
    bool IsSignedIn { get; }
    Task<System.Text.Json.JsonElement> SaveZoomAccountsAsync(IEnumerable<object> accounts, CancellationToken token);
}

/// <summary>
/// The Zoom accounts on this PC, kept against the signed-in person's dashboard account.
///
/// A coordinator sets their accounts up once in their own copy of the app - which group each one
/// hosts and the link its classes open. When the admin's PC runs their classes it needs exactly
/// that: which Zoom account opens a class, and with what link. Sending it here means nobody types
/// a meeting link a second time, and the admin chooses from what that person actually has rather
/// than from a box they could spell wrong.
///
/// No Zoom sign-in is sent. The password (or the saved Desktop account, or the browser profile)
/// stays on the PC exactly as it was; what goes to the server is the account's name, its e-mail,
/// its group and its link.
/// </summary>
public sealed class ZoomServerAccounts(IZoomAccountsApi api, Action<string>? log = null)
{
    /// <summary>How often the accounts are sent again without anything having changed.</summary>
    public static readonly TimeSpan SendEvery = TimeSpan.FromHours(1);

    private readonly Action<string> _log = log ?? (message => ConsoleLogger.Info($"[ACCOUNTS] {message}"));
    private DateTimeOffset _lastSent = DateTimeOffset.MinValue;
    private string _lastShape = "";

    /// <summary>
    /// Sends what this PC has, when it is worth sending: signed in, and either the accounts have
    /// changed since last time or an hour has passed. Returns what was sent, or null for nothing.
    /// </summary>
    public async Task<int?> PushAsync(IReadOnlyList<WindowsMeetingAccountMetadata> accounts, CancellationToken token = default)
    {
        if (!api.IsSignedIn) return null;
        var rows = accounts
            .Where(account => !string.IsNullOrWhiteSpace(account.AccountId))
            .Select(account => new
            {
                accountId = account.AccountId.Trim(),
                label = string.IsNullOrWhiteSpace(account.DisplayName) ? account.AccountId.Trim() : account.DisplayName.Trim(),
                zoomEmail = string.IsNullOrWhiteSpace(account.ZoomEmail) ? null : account.ZoomEmail.Trim(),
                group = string.IsNullOrWhiteSpace(account.GroupName) ? null : account.GroupName.Trim(),
                // Only a link the server will take; a half-typed one is left behind rather than
                // failing the whole send.
                meetingUrl = IsLink(account.DefaultMeetingUrl) ? account.DefaultMeetingUrl!.Trim() : null,
                preferredEngine = account.PreferredEngine switch
                {
                    AccountEnginePreference.Desktop => "desktop",
                    AccountEnginePreference.Web => "web",
                    _ => (string?)null,
                },
                active = false,
            })
            .ToArray();

        string shape = string.Join("|", rows.Select(row => $"{row.accountId}:{row.group}:{row.meetingUrl}:{row.preferredEngine}"));
        if (shape == _lastShape && DateTimeOffset.Now - _lastSent < SendEvery) return null;

        await api.SaveZoomAccountsAsync(rows, token);
        _lastShape = shape;
        _lastSent = DateTimeOffset.Now;
        _log($"{rows.Length} Zoom account(s) of this PC were saved to your dashboard account.");
        return rows.Length;
    }

    /// <summary>The next send goes even if nothing changed (the accounts page was just used).</summary>
    public void Changed() => _lastShape = "";

    private static bool IsLink(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        && url.Trim().Length <= 2048 && !url.Trim().Any(char.IsWhiteSpace);
}
