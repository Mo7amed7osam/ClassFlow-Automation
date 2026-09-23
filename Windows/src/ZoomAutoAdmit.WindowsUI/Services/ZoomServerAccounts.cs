using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.WebAutomation;
using ZoomAutoAdmit.WindowsRuntime;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>What this app needs from the server to keep a person's Zoom accounts there.</summary>
public interface IZoomAccountsApi
{
    bool IsSignedIn { get; }
    Task<List<CentralZoomAccount>> ZoomAccountsAsync(CancellationToken token);
    Task<CentralZoomSecret> ZoomSecretAsync(string accountId, CancellationToken token);
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
/// The Zoom password travels too, encrypted on the server exactly as an LMS one is, and only for
/// an account whose PC has one saved. It is what lets a browser profile nobody signed in - a new
/// PC, or the second profile of two classes at once - sign itself in; a profile that cannot only
/// joins as a guest, and a guest cannot admit anybody. Zoom asking for a captcha or a one-time
/// code still needs a person, and the app says so rather than pretending otherwise.
/// </summary>
public sealed class ZoomServerAccounts(
    IZoomAccountsApi api,
    Action<string>? log = null,
    IZoomProfileCredentialStore? credentials = null,
    Func<string?, ZoomSignInCredential?>? readLocal = null)
{
    private readonly IZoomProfileCredentialStore _credentials = credentials ?? new ZoomProfileCredentialStore();
    /// <summary>The Zoom sign-in this PC has for an account, by its credential reference.</summary>
    private readonly Func<string?, ZoomSignInCredential?> _readLocal = readLocal ?? ZoomSignInCredential.Read;

    /// <summary>How often the accounts are sent again without anything having changed.</summary>
    public static readonly TimeSpan SendEvery = TimeSpan.FromHours(1);

    private readonly Action<string> _log = log ?? (message => ConsoleLogger.Info($"[ACCOUNTS] {message}"));
    private DateTimeOffset _lastSent = DateTimeOffset.MinValue;
    private string _lastShape = "";
    /// <summary>How many of the last restore still need a person to sign their profile in to Zoom.</summary>
    private int _restoredWithoutPassword;

    /// <summary>
    /// Sends what this PC has, when it is worth sending: signed in, and either the accounts have
    /// changed since last time or an hour has passed. Returns what was sent, or null for nothing.
    /// </summary>
    public async Task<int?> PushAsync(IReadOnlyList<WindowsMeetingAccountMetadata> accounts, CancellationToken token = default) =>
        await PushAsync(accounts, null, token);

    /// <summary>
    /// The same, with a password just typed for one account. It is sent whether or not this PC
    /// managed to keep a copy of it, because the server is where it has to end up: a PC that never
    /// saw it - a cloud one, a replacement - takes it from there.
    /// </summary>
    public async Task<int?> PushAsync(
        IReadOnlyList<WindowsMeetingAccountMetadata> accounts,
        (string AccountId, string Password)? typed,
        CancellationToken token = default)
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
                // Only ever sent when this PC actually has one; an account without it leaves the
                // kept password alone rather than wiping what another PC saved.
                password = typed is { } just && just.AccountId.Equals(account.AccountId, StringComparison.OrdinalIgnoreCase)
                    ? just.Password
                    : (_readLocal(account.CredentialReference)
                       ?? _readLocal(ZoomSignInCredential.StandardReference(account.AccountId)))?.Password,
                active = false,
            })
            .ToArray();

        // An empty list is never sent: on a PC that has not been set up yet it would wipe what the
        // server holds for that person, which is exactly what a new PC is there to take.
        if (rows.Length == 0) return null;

        string shape = string.Join("|", rows.Select(row => $"{row.accountId}:{row.group}:{row.meetingUrl}:{row.preferredEngine}"));
        if (typed == null && shape == _lastShape && DateTimeOffset.Now - _lastSent < SendEvery) return null;

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
            if (restored == 0) return null;
            // Only an account whose password did not come too still needs a person.
            return _restoredWithoutPassword > 0
                ? $"{restored} Zoom account(s) came from your dashboard account; sign in to Zoom as {_restoredWithoutPassword} of them once."
                : $"{restored} Zoom account(s) came from your dashboard account, with their Zoom sign-in.";
        }
        int? sent = await PushAsync(here, token);
        return sent is > 0 ? $"{sent} Zoom account(s) of this PC were saved to your dashboard account." : null;
    }

    /// <summary>
    /// The accounts the server kept for this person, written here. Only ever called for a PC with
    /// none of its own, so nothing can be overwritten. An account whose password was kept has it
    /// written into this PC's own credential store too, so a fresh browser profile signs itself in
    /// instead of joining as a guest.
    /// </summary>
    public async Task<int> RestoreAsync(Func<WindowsMeetingAccountMetadata, CancellationToken, Task> save, CancellationToken token = default)
    {
        var theirs = await api.ZoomAccountsAsync(token);
        int restored = 0, withPassword = 0;
        foreach (var account in theirs)
        {
            if (string.IsNullOrWhiteSpace(account.AccountId)) continue;
            try
            {
                // The Zoom sign-in, when one was kept: into Windows Credential Manager, the same
                // place the Accounts page puts it, so everything that reads it finds it as usual.
                string reference = "";
                if (account.HasPassword)
                {
                    try
                    {
                        var secret = await api.ZoomSecretAsync(account.Id, token);
                        if (!string.IsNullOrEmpty(secret.Password))
                        {
                            _credentials.Save(account.AccountId, secret.Email, secret.Password);
                            reference = _credentials.ReferenceFor(account.AccountId);
                            withPassword++;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // The account is still worth having; its profile just needs signing in once.
                        _log($"{account.AccountId}: its Zoom password could not be read back ({ex.Message}).");
                    }
                }
                await save(new WindowsMeetingAccountMetadata(
                    account.AccountId,
                    string.IsNullOrWhiteSpace(account.Label) ? account.AccountId : account.Label,
                    reference,
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
        _restoredWithoutPassword = restored - withPassword;
        if (restored > 0)
        {
            // What is here now is what the server has, so there is nothing to send back.
            _lastShape = "";
            _log($"{restored} Zoom account(s) came from your dashboard account" +
                 $"{(withPassword > 0 ? $", {withPassword} with their Zoom sign-in" : "")}.");
        }
        return restored;
    }

    /// <summary>The next send goes even if nothing changed (the accounts page was just used).</summary>
    public void Changed() => _lastShape = "";

    private static bool IsLink(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        && url.Trim().Length <= 2048 && !url.Trim().Any(char.IsWhiteSpace);
}
