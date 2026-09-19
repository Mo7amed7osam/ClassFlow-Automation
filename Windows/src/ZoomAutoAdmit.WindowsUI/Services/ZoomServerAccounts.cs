using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.WindowsRuntime;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>What this app needs from the server to keep a person's Zoom accounts there.</summary>
public interface IZoomAccountsApi
{
    bool IsSignedIn { get; }
    Task<List<CentralZoomAccount>> ZoomAccountsAsync(CancellationToken token);
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
/// It goes both ways. A PC that already has accounts is the one that knows: it sends them. A PC
/// that has none - a new one, a cloud one - takes what the server kept instead, so signing in is
/// all it takes to find the accounts and the links there. Neither direction can quietly throw the
/// other away, because a PC only takes when it has nothing of its own to lose.
///
/// No Zoom sign-in is sent either way. The password (or the saved Desktop account, or the browser
/// profile) stays on the PC exactly as it was; what travels is the account's name, its e-mail, its
/// group and its link. Signing in to Zoom itself is still done once on each PC.
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

        // An empty list is never sent: on a PC that has not been set up yet it would wipe what the
        // server holds for that person, which is exactly what a new PC is there to take.
        if (rows.Length == 0) return null;

        string shape = string.Join("|", rows.Select(row => $"{row.accountId}:{row.group}:{row.meetingUrl}:{row.preferredEngine}"));
        if (shape == _lastShape && DateTimeOffset.Now - _lastSent < SendEvery) return null;

        await api.SaveZoomAccountsAsync(rows, token);
        _lastShape = shape;
        _lastSent = DateTimeOffset.Now;
        _log($"{rows.Length} Zoom account(s) of this PC were saved to your dashboard account.");
        return rows.Length;
    }

    /// <summary>
    /// Both directions, whichever this PC needs: nothing here and something there means the
    /// accounts come down; otherwise what is here goes up. Returns what to tell the person, or null
    /// when there was nothing to do.
    /// </summary>
    public async Task<string?> SyncAsync(
        IReadOnlyList<WindowsMeetingAccountMetadata> here,
        Func<WindowsMeetingAccountMetadata, CancellationToken, Task> save,
        CancellationToken token = default)
    {
        if (!api.IsSignedIn) return null;
        if (here.Count == 0)
        {
            // Take, and stop there. Sending an empty list afterwards would delete on the server the
            // very accounts this PC has just failed to take. What is here goes up on the next pass.
            int restored = await RestoreAsync(save, token);
            return restored > 0
                ? $"{restored} Zoom account(s) came from your dashboard account; sign in to Zoom as each of them once."
                : null;
        }
        int? sent = await PushAsync(here, token);
        return sent is > 0 ? $"{sent} Zoom account(s) of this PC were saved to your dashboard account." : null;
    }

    /// <summary>
    /// The accounts the server kept for this person, written here. Only ever called for a PC with
    /// none of its own, so nothing can be overwritten. The Zoom sign-in is not among them: each
    /// account still has to be signed in to Zoom on this PC once.
    /// </summary>
    public async Task<int> RestoreAsync(Func<WindowsMeetingAccountMetadata, CancellationToken, Task> save, CancellationToken token = default)
    {
        var theirs = await api.ZoomAccountsAsync(token);
        int restored = 0;
        foreach (var account in theirs)
        {
            if (string.IsNullOrWhiteSpace(account.AccountId)) continue;
            try
            {
                await save(new WindowsMeetingAccountMetadata(
                    account.AccountId,
                    string.IsNullOrWhiteSpace(account.Label) ? account.AccountId : account.Label,
                    "",
                    account.PreferredEngine switch
                    {
                        "desktop" => AccountEnginePreference.Desktop,
                        "web" => AccountEnginePreference.Web,
                        _ => AccountEnginePreference.Auto,
                    })
                {
                    ZoomEmail = account.ZoomEmail,
                    GroupName = account.Group,
                    DefaultMeetingUrl = IsLink(account.MeetingUrl) ? account.MeetingUrl!.Trim() : null,
                }, token);
                restored++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"{account.AccountId} could not be added here: {ex.Message}");
            }
        }
        if (restored > 0)
        {
            // What is here now is what the server has, so there is nothing to send back.
            _lastShape = "";
            _log($"{restored} Zoom account(s) came from your dashboard account.");
        }
        return restored;
    }

    /// <summary>The next send goes even if nothing changed (the accounts page was just used).</summary>
    public void Changed() => _lastShape = "";

    private static bool IsLink(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        && url.Trim().Length <= 2048 && !url.Trim().Any(char.IsWhiteSpace);
}
