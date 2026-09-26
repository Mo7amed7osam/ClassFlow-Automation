using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.WebAutomation.Lms;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>
/// The signed-in dashboard user's LMS accounts, kept on the server (the password encrypted there).
/// The one in use is also copied into this PC's LMS sign-in, because classes open by themselves
/// (scheduled tasks) when nobody is signed in and the server may be off: that copy is only ever the
/// account the server says is in use, and it follows every change made here.
/// </summary>
public sealed class LmsServerAccounts(CentralApiClient api, LmsAccountDirectory? local = null)
{
    private readonly LmsAccountDirectory _local = local ?? new LmsAccountDirectory();

    public IReadOnlyList<CentralLmsAccount> Accounts { get; private set; } = [];
    /// <summary>The server's list has been read at least once; until then there is nothing to show from it.</summary>
    public bool Loaded { get; private set; }
    public bool CanKeepPasswords { get; private set; } = true;

    /// <summary>
    /// Reads the user's accounts. The first time the admin signs in (nothing on the server yet) the
    /// accounts this PC already has are uploaded, so nothing has to be typed again; a coordinator
    /// never gets this PC's accounts - they add their own. Then the one in use is copied here.
    /// </summary>
    public async Task<string?> SyncAsync(CancellationToken token = default)
    {
        var answer = await api.LmsAccountsAsync(token);
        CanKeepPasswords = answer.CanKeepPasswords;
        if (answer.Accounts.Count == 0 && answer.CanKeepPasswords && api.Me?.IsAdmin == true)
        {
            var active = _local.Active();
            int uploaded = 0;
            foreach (var entry in _local.List())
            {
                var saved = new LmsCredentialStore(entry.Target).Read();
                if (saved == null) continue;
                await api.SaveLmsAccountAsync(entry.Label, saved.Email, entry.Role, saved.Password, entry.Id == active.Id, token);
                uploaded++;
            }
            if (uploaded > 0) answer = await api.LmsAccountsAsync(token);
            if (uploaded > 0) ConsoleLogger.Info($"[LMS] {uploaded} LMS account(s) of this PC were saved to your dashboard account.");
        }
        Accounts = answer.Accounts;
        Loaded = true;
        return await CopyActiveHereAsync(token);
    }

    public async Task<string?> UseAsync(string id, CancellationToken token = default)
    {
        await api.UseLmsAccountAsync(id, token);
        return await SyncAsync(token);
    }

    public async Task<string?> SaveAsync(string label, string email, string role, string password, bool active, CancellationToken token = default)
    {
        await api.SaveLmsAccountAsync(label, email, role, password, active, token);
        return await SyncAsync(token);
    }

    public async Task<string?> RemoveAsync(string id, CancellationToken token = default)
    {
        var gone = Accounts.FirstOrDefault(a => a.Id == id);
        await api.DeleteLmsAccountAsync(id, token);
        if (gone != null && _local.List().FirstOrDefault(e => e.Email.Equals(gone.Email, StringComparison.OrdinalIgnoreCase)) is { } copy && copy.Id != _local.Active().Id)
            _local.Remove(copy.Id);
        return await SyncAsync(token);
    }

    /// <summary>The account in use on the server becomes this PC's LMS sign-in. Returns what changed.</summary>
    private async Task<string?> CopyActiveHereAsync(CancellationToken token)
    {
        var active = Accounts.FirstOrDefault(a => a.Active);
        if (active == null) return null;
        var secret = await api.LmsSecretAsync(active.Id, token);
        var here = _local.Upsert(active.Label, secret.Email, secret.Password, active.Role, makeActive: true);
        return $"The app signs in to the LMS as {here.Label} ({here.Email}).";
    }
}
