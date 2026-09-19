using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZoomAutoAdmit.WindowsUI.Services;

// ---------------------------------------------------------------------- what the server answers

public sealed record CentralGroupRef(string Id, string Name, string? DisplayName, bool Archived);
public sealed record CentralMe(string Id, string Username, string DisplayName, string Role, bool AllGroups, List<CentralGroupRef>? Groups, DateTimeOffset ExpiresAt)
{
    public bool IsAdmin => Role == "admin";
}
public sealed record CentralLinkStatus(string Link, string Lms, string Label);
public sealed record CentralJob(string JobId, string Type, string Status, string? Group, string? Date, int Attempts, DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt, string? ErrorCode, bool? AlreadyExists, bool? DryRun, bool? ReplaceExisting);
public sealed record CentralRecording(string Id, string Group, string Date, string? StartTime, string? FileName, string? Type, string? DriveLink,
    string? ZoomLink, string Source, string LmsStatus, DateTimeOffset? LmsUpdatedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    CentralLinkStatus LinkStatus, CentralJob? LastJob)
{
    public string When => $"{Date}{(StartTime is { Length: > 0 } t ? " · " + t : "")}";
    public string SourceText => LinkStatus.Link switch { "drive" => "Drive", "zoom" => "Zoom", _ => "Missing" };
    public bool JobOpen => LastJob?.Status is "queued" or "assigned" or "running";
    public string LmsText => JobOpen ? "Processing" : LmsStatus switch { "attached" => "On the LMS", "failed" => "Failed", _ => "Pending" };
    public string? Link => DriveLink ?? ZoomLink;
}
public sealed record CentralRecordingPage(List<CentralRecording> Items, int Total, int Page, int PageSize);
public sealed record CentralGroup(string Id, string Group, string? DisplayName, bool Archived, int Recordings, string? LastSessionDate,
    int Pending, int OnLms, int MissingLink, List<CentralUserBrief>? Coordinators);
public sealed record CentralUserBrief(string Id, string Username, string DisplayName, string Status);
public sealed record CentralUser(string Id, string Username, string DisplayName, string Role, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt, List<CentralGroupRef> Groups)
{
    public string GroupsText => Groups.Count == 0 ? "—" : string.Join(", ", Groups.Select(g => g.Name));
}
public sealed record CentralUserList(List<CentralUser> Users, int Count);

// ---------------------------------------------------------------------- other people's classes

/// <summary>How many classes of one coordinator are waiting, done, or still need a meeting link.</summary>
public sealed record CentralDelegationClasses(int Planned, int Done, int Skipped, int NeedsLink);

/// <summary>
/// One Zoom account a person opens classes with, kept against their dashboard account. No sign-in:
/// that lives in the Zoom app's saved accounts or a browser profile on their PC. What is here is
/// which account, the group it hosts and the link its classes open.
/// </summary>
public sealed record CentralZoomAccount(string Id, string AccountId, string Label, string? ZoomEmail, string? Group,
    string? MeetingUrl, string? PreferredEngine, bool Active);

/// <summary>A coordinator, and whether this PC runs their classes under their own two accounts.</summary>
public sealed record CentralDelegation(string CoordinatorId, string Username, string DisplayName, string Status, bool Enabled,
    List<CentralGroupRef>? Groups, CentralLmsAccount? LmsAccount, List<CentralLmsAccount>? LmsAccounts,
    string? ZoomAccountId, string? ZoomAccount, List<CentralZoomAccount>? ZoomAccounts,
    CentralDelegationClasses? Classes, DateTimeOffset? UpdatedAt)
{
    public IReadOnlyList<CentralZoomAccount> ZoomAccountList => ZoomAccounts ?? [];
    /// <summary>The Zoom account their classes open with, as the running PC knows it by name.</summary>
    public CentralZoomAccount? Zoom =>
        ZoomAccountList.FirstOrDefault(a => a.Id == ZoomAccountId)
        ?? ZoomAccountList.FirstOrDefault(a => a.AccountId.Equals(ZoomAccount, StringComparison.OrdinalIgnoreCase))
        ?? ZoomAccountList.FirstOrDefault(a => a.Active);
    public IReadOnlyList<CentralGroupRef> GroupList => Groups ?? [];
    public string GroupsText => GroupList.Count == 0 ? "no groups" : string.Join(", ", GroupList.Select(g => g.Name));
    /// <summary>Everything needed to actually run their classes is there.</summary>
    public bool IsReady => Enabled && LmsAccount != null && Zoom != null;
}

public sealed record CentralDelegationList(List<CentralDelegation> Delegations);

/// <summary>One class of a coordinator as the server holds it, before this PC turns it into a schedule.</summary>
public sealed record CentralClassPlan(string Id, string CoordinatorId, string Group, string Date, string? StartTime, string? Title,
    string? MeetingUrl, string? ZoomAccount, string? PreferredEngine, string Source, string Status, string? Note, bool NeedsLink,
    DateTimeOffset? ImportedAt, DateTimeOffset UpdatedAt)
{
    public DateOnly? Day => DateOnly.TryParse(Date, out var day) ? day : null;
    public TimeOnly? Start => TimeOnly.TryParse(StartTime, out var start) ? start : null;
}

public sealed record CentralRunCoordinator(string CoordinatorId, string DisplayName, string Username, bool Enabled,
    string? ZoomAccount, List<CentralZoomAccount>? ZoomAccounts, CentralLmsAccount? LmsAccount);

public sealed record CentralRunPlan(List<CentralClassPlan>? Classes, List<CentralRunCoordinator>? Coordinators)
{
    public IReadOnlyList<CentralClassPlan> ClassList => Classes ?? [];
    public IReadOnlyList<CentralRunCoordinator> CoordinatorList => Coordinators ?? [];
}

/// <summary>
/// The little of the server that running other people's classes needs. The app's own client is
/// what implements it; naming it separately is what lets that part be exercised without a server,
/// a signed-in session or this PC's saved accounts.
/// </summary>
public interface IDelegatedRunsApi
{
    /// <summary>Only an admin has other people's classes to run.</summary>
    bool IsAdmin { get; }
    Task<CentralDelegationList> DelegationsAsync(CancellationToken token);
    Task<CentralCoordinatorSecret> CoordinatorLmsSecretAsync(string coordinatorId, string accountId, CancellationToken token);
    Task<JsonElement> ImportRunPlanAsync(string coordinatorId, IEnumerable<object> classes, CancellationToken token);
    Task<CentralRunPlan> RunPlanAsync(DateOnly from, DateOnly to, IEnumerable<string>? coordinators, CancellationToken token);
}

public sealed class CentralApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>
    /// What to tell a person when a call to the server failed. The central server runs on the
    /// admin's PC, so "it cannot be reached" is ordinary - the PC is asleep or off - and must not
    /// read like a broken certificate: with the PC asleep, Tailscale still answers the connection
    /// but nobody completes the TLS handshake, which .NET reports as "The SSL connection could not
    /// be established, see inner exception" (seen by a coordinator, 2026-09-16).
    /// </summary>
    public static string Explain(Exception error)
    {
        for (var e = error; e != null; e = e.InnerException)
        {
            if (e is CentralApiException api) return api.Message;
            if (e is HttpRequestException or System.Security.Authentication.AuthenticationException or System.Net.Sockets.SocketException or TaskCanceledException or TimeoutException or System.IO.IOException)
                return "The server can't be reached right now - the admin's PC is probably asleep or off. "
                     + "Classes still run on this PC as usual; try again in a few minutes.";
        }
        return error.Message;
    }
}

/// <summary>The sign-in to the central server, kept by Windows (like the LMS one), never in a file.</summary>
public sealed class CentralLoginStore(string target = "ZoomAutoAdmit/Central/DashboardLogin")
{
    public (string Username, string Password)? Read()
    {
        if (!CredRead(target, 1, 0, out var pointer)) return null;
        try
        {
            var native = Marshal.PtrToStructure<Credential>(pointer);
            if (native.BlobSize is 0 or > 2560) return null;
            var bytes = new byte[native.BlobSize];
            try
            {
                Marshal.Copy(native.Blob, bytes, 0, bytes.Length);
                using var json = JsonDocument.Parse(bytes);
                return (json.RootElement.GetProperty("u").GetString()!, json.RootElement.GetProperty("p").GetString()!);
            }
            finally { Array.Clear(bytes); }
        }
        catch (JsonException) { return null; }
        finally { CredFree(pointer); }
    }

    public void Save(string username, string password)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { u = username, p = password });
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var c = new Credential { Type = 1, Persist = 2, TargetName = target, UserName = username, Blob = handle.AddrOfPinnedObject(), BlobSize = bytes.Length };
            if (!CredWrite(ref c, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot save the sign-in in Windows Credential Manager.");
        }
        finally { handle.Free(); Array.Clear(bytes); }
    }

    public void Delete() => CredDelete(target, 1, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags; public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten; public int BlobSize; public IntPtr Blob; public int Persist; public int AttributeCount; public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredWrite(ref Credential credential, int flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll", EntryPoint = "CredFree")] private static extern void CredFree(IntPtr buffer);
}

/// <summary>
/// The app's own connection to the central server - the same API, roles and group scoping the web
/// page used, with none of the web page. Signs in with the dashboard account (admin or
/// coordinator), keeps the session cookie in memory only, and signs in again by itself from the
/// saved sign-in when the session ends. Every change carries X-Dashboard-Request: 1.
/// </summary>
public sealed class CentralApiClient : IDelegatedRunsApi, IZoomAccountsApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly Func<Uri?> _baseUri;
    private readonly CentralLoginStore _logins;
    // "Keep me signed in": the session lives in the server's database (120 days, ended there when the
    // account is disabled or its password changes); this PC keeps only that session's cookie, one per
    // account that signed in here, so the Dashboard can offer them to pick from.
    private static readonly DatabasePasswordStore Legacy = new("ZoomAutoAdmit/Central/Session");
    private static DatabasePasswordStore SessionOf(string username) => new($"ZoomAutoAdmit/Central/Session/{username.ToLowerInvariant()}");
    // "Remember the password": kept by Windows (Credential Manager, this Windows user only), one per
    // account, so Continue signs in again by itself once the 120-day session has ended.
    private static CentralLoginStore PasswordOf(string username) => new($"ZoomAutoAdmit/Central/Password/{username.ToLowerInvariant()}");
    private static (string Username, string Password)? SavedPassword(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        try { return PasswordOf(username).Read(); } catch { return null; }
    }
    private readonly CentralKnownAccounts _known = new();
    // Who this PC is signed in as, kept here so it still knows while the server cannot be reached.
    private readonly CentralIdentityCache _identity = new();
    private DatabasePasswordStore? _session => _known.Current is { } user ? SessionOf(user) : null;
    private HttpClient? _http;
    private CookieContainer? _cookies;
    private Uri? _httpFor;

    public CentralApiClient(Func<Uri?>? baseUri = null, CentralLoginStore? logins = null)
    {
        _baseUri = baseUri ?? (() => new RecordingsDashboardSettingsStore().Load().BaseUri);
        _logins = logins ?? new CentralLoginStore();
    }

    public CentralMe? Me { get; private set; }
    public bool IsAdmin => Me?.IsAdmin == true;
    public bool IsSignedIn => Me != null;
    public bool HasSavedLogin => (_session is { } s && SafeRead(s) != null) || _logins.Read() != null || SavedPassword(_known.Current) != null;

    /// <summary>The accounts that signed in on this PC, newest first; each can continue without typing when it has a session or a saved password.</summary>
    public IReadOnlyList<CentralKnownAccount> KnownAccounts =>
        [.. _known.List().Select(a => a with { HasSession = SafeRead(SessionOf(a.Username)) != null, HasPassword = SavedPassword(a.Username) != null })];

    private static string? SafeRead(DatabasePasswordStore? store) { try { return store?.Read(); } catch { return null; } }

    private HttpClient Http()
    {
        var uri = _baseUri() ?? throw new CentralApiException(0, "The central server is not set up on the Recordings page.");
        if (_http == null || _httpFor != uri)
        {
            _http?.Dispose();
            _cookies = new CookieContainer();
            // A kept session goes back in, so the app is signed in again without a password.
            // (the one kept by the previous version, before accounts were kept one by one, is used once)
            if ((SafeRead(_session) ?? SafeRead(Legacy)) is { } kept && kept.Split('\n') is [var name, var value])
                try { _cookies.Add(uri, new Cookie(name, value, "/")); } catch (CookieException) { }
            _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies, UseCookies = true })
            { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
            _httpFor = uri;
            Me = null;
        }
        return _http;
    }

    private void KeepSession(string username)
    {
        if (_cookies == null || _httpFor == null) return;
        var cookie = _cookies.GetCookies(_httpFor).FirstOrDefault(c => c.Name.EndsWith("zaa_session", StringComparison.Ordinal));
        if (cookie != null) SessionOf(username).Save(cookie.Name + "\n" + cookie.Value);
    }

    /// <summary>
    /// Continue as another account that signed in here: its kept session, or - when that has ended -
    /// its saved password. Null when it has neither (or the password no longer works): sign in again.
    /// </summary>
    public async Task<CentralMe?> SwitchToAsync(string username, CancellationToken token = default)
    {
        _known.SetCurrent(username);
        Me = null; _http?.Dispose(); _http = null;
        try { Me = await GetDirectAsync<CentralMe>("api/v1/auth/me", token); _known.Touch(Me); _identity.Save(Me); return Me; }
        catch (CentralApiException ex) when (ex.Status == HttpStatusCode.Unauthorized) { try { SessionOf(username).Delete(); } catch { } }
        return await SignInWithSavedPasswordAsync(username, token);
    }

    /// <summary>A fresh 120-day session from the account's saved password; a password the server refuses is removed.</summary>
    private async Task<CentralMe?> SignInWithSavedPasswordAsync(string? username, CancellationToken token)
    {
        if (SavedPassword(username) is not { } saved) return null;
        try { return await SignInAsync(saved.Username, saved.Password, remember: true, savePassword: true, token: token); }
        catch (CentralApiException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            try { PasswordOf(saved.Username).Delete(); } catch { }             // changed or disabled: type it again
            return null;
        }
    }

    /// <summary>Removes an account from this PC's list - its kept session and saved password too.</summary>
    public void ForgetAccount(string username)
    {
        if (string.Equals(_known.Current, username, StringComparison.OrdinalIgnoreCase)) Forget();
        try { SessionOf(username).Delete(); } catch { }
        try { PasswordOf(username).Delete(); } catch { }
        _identity.Forget(username);
        _known.Remove(username);
    }

    /// <param name="savePassword">Keep the password in Windows Credential Manager for this account; false removes a saved one.</param>
    public async Task<CentralMe> SignInAsync(string username, string password, bool remember, bool savePassword = false, CancellationToken token = default)
    {
        // The server wants X-Dashboard-Request on every POST, the sign-in included.
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login")
        {
            Content = JsonContent.Create(new { username = username.Trim(), password, remember }, options: Json),
        };
        request.Headers.Add("X-Dashboard-Request", "1");
        using var response = await Http().SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, token);
        // The single sign-in an older version kept is replaced by the account's own entry.
        try { _logins.Delete(); } catch { }
        string who = username.Trim().ToLowerInvariant();
        _known.SetCurrent(who);
        if (remember) KeepSession(who); else try { SessionOf(who).Delete(); } catch { }
        if (savePassword) PasswordOf(who).Save(who, password); else try { PasswordOf(who).Delete(); } catch { }
        Me = await GetDirectAsync<CentralMe>("api/v1/auth/me", token);
        _known.Touch(Me);
        _identity.Save(Me);
        return Me;
    }

    /// <summary>Signs out here and ends the session on the server.</summary>
    public void Forget()
    {
        try
        {
            if (_http != null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/logout");
                request.Headers.Add("X-Dashboard-Request", "1");
                _http.Send(request);
            }
        }
        catch { }
        try { _logins.Delete(); } catch { }
        try { _session?.Delete(); } catch { }
        if (_known.Current is { } signedOut) _identity.Forget(signedOut);
        _known.SetCurrent(null);
        Me = null; _http?.Dispose(); _http = null;
    }

    /// <summary>
    /// Signed in from the kept session when there is one. A password saved by an older version is
    /// used once, to turn it into a kept session, and then removed.
    /// </summary>
    public async Task<CentralMe?> EnsureSignedInAsync(CancellationToken token = default)
    {
        if (Me != null && Me.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return Me;
        try
        {
            Me = await GetDirectAsync<CentralMe>("api/v1/auth/me", token);
            if (SafeRead(Legacy) is { } old) { SessionOf(Me.Username).Save(old); try { Legacy.Delete(); } catch { } }
            _known.Touch(Me);
            _identity.Save(Me);
            return Me;
        }
        catch (CentralApiException ex) when (ex.Status == HttpStatusCode.Unauthorized) { try { _session?.Delete(); } catch { } }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The server cannot be reached; this PC does not stop working and the person does not
            // become someone else. Who they are is what the server last said, until that session
            // would have run out - it is asked again as soon as the server answers.
            if (_identity.Read(_known.Current) is { } remembered) { Me = remembered; return Me; }
            throw;
        }
        if (await SignInWithSavedPasswordAsync(_known.Current, token) is { } me) return me;
        var saved = _logins.Read();
        if (saved == null) return null;
        return await SignInAsync(saved.Value.Username, saved.Value.Password, remember: true, token: token);
    }

    public Task<T> GetAsync<T>(string path, CancellationToken token = default) => WithSignInAsync(() => GetDirectAsync<T>(path, token), token);

    public Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null, CancellationToken token = default) =>
        WithSignInAsync(async () =>
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Dashboard-Request", "1");
            if (body != null) request.Content = JsonContent.Create(body, options: Json);
            using var response = await Http().SendAsync(request, token);
            if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, token);
            return (await response.Content.ReadFromJsonAsync<T>(Json, token))!;
        }, token);

    private async Task<T> WithSignInAsync<T>(Func<Task<T>> call, CancellationToken token)
    {
        if (Me == null) await EnsureSignedInAsync(token);
        try { return await call(); }
        catch (CentralApiException ex) when (ex.Status == HttpStatusCode.Unauthorized && (SavedPassword(_known.Current) ?? _logins.Read()) is { } saved)
        {
            // The session ended mid-use: a fresh one from the saved password, and the call again.
            await SignInAsync(saved.Username, saved.Password, remember: true, savePassword: SavedPassword(saved.Username) != null, token: token);
            return await call();
        }
    }

    private async Task<T> GetDirectAsync<T>(string path, CancellationToken token)
    {
        using var response = await Http().GetAsync(path, token);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, token);
        return (await response.Content.ReadFromJsonAsync<T>(Json, token))!;
    }

    private static async Task<CentralApiException> ErrorAsync(HttpResponseMessage response, CancellationToken token)
    {
        string text = "";
        try { text = await response.Content.ReadAsStringAsync(token); } catch { }
        string message = $"The server answered {(int)response.StatusCode}.";
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string? error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            string? details = root.TryGetProperty("details", out var d)
                ? d.ValueKind == JsonValueKind.String ? d.GetString() : d.ToString() : null;
            message = string.Join(": ", new[] { error, details }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        catch (JsonException) { }
        return new CentralApiException(response.StatusCode, message.Length > 0 ? message : $"The server answered {(int)response.StatusCode}.");
    }

    // ------------------------------------------------------------------ the calls the pages make

    public Task<CentralRecordingPage> RecordingsAsync(string? group, string? status, string? link, int page = 1, CancellationToken token = default) =>
        GetAsync<CentralRecordingPage>($"api/v1/dashboard/recordings?page={page}&pageSize=100&sort=session" +
            (string.IsNullOrEmpty(group) ? "" : $"&group={Uri.EscapeDataString(group)}") +
            (string.IsNullOrEmpty(status) ? "" : $"&status={status}") + (string.IsNullOrEmpty(link) ? "" : $"&link={link}"), token);

    public Task<JsonElement> AttachAsync(string recordingId, bool replaceExisting, bool dryRun, CancellationToken token = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, $"api/v1/dashboard/recordings/{recordingId}/attach", new { replaceExisting, dryRun }, token);

    public Task<JsonElement> CancelJobAsync(string recordingId, CancellationToken token = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, $"api/v1/dashboard/recordings/{recordingId}/cancel", null, token);

    public Task<JsonElement> EditLinkAsync(string recordingId, string link, CancellationToken token = default) =>
        SendAsync<JsonElement>(HttpMethod.Patch, $"api/v1/dashboard/recordings/{recordingId}", new { link }, token);

    public async Task<List<CentralGroup>> GroupsAsync(bool admin, CancellationToken token = default) =>
        (await GetAsync<JsonElement>(admin ? "api/v1/admin/groups" : "api/v1/dashboard/groups", token)).GetProperty("groups").Deserialize<List<CentralGroup>>(Json) ?? [];

    public Task<JsonElement> CreateGroupAsync(string name, string? displayName, CancellationToken token = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, "api/v1/admin/groups", new { name, displayName }, token);

    public Task<JsonElement> ArchiveGroupAsync(string id, bool archived, CancellationToken token = default) =>
        SendAsync<JsonElement>(HttpMethod.Patch, $"api/v1/admin/groups/{id}", new { archived }, token);

    public Task<CentralUserList> UsersAsync(CancellationToken token = default) => GetAsync<CentralUserList>("api/v1/admin/users", token);

    public Task<JsonElement> ApproveAsync(string id, bool approve, CancellationToken token = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, $"api/v1/admin/users/{id}/{(approve ? "approve" : "reject")}", null, token);

    public Task<JsonElement> SetUserStatusAsync(string id, string status, CancellationToken token = default) =>
        SendAsync<JsonElement>(HttpMethod.Patch, $"api/v1/admin/users/{id}", new { status }, token);

    public Task<JsonElement> CreateUserAsync(string username, string displayName, string password, IEnumerable<string> groupIds, CancellationToken token = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, "api/v1/admin/users", new { username, displayName, password, groupIds = groupIds.ToArray() }, token);

    public Task<JsonElement> SetUserGroupsAsync(string id, IEnumerable<string> groupIds, CancellationToken token = default) =>
        SendAsync<JsonElement>(HttpMethod.Put, $"api/v1/admin/users/{id}/groups", new { groupIds = groupIds.ToArray() }, token);

    public Task<JsonElement> ResetPasswordAsync(string id, string password, CancellationToken token = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, $"api/v1/admin/users/{id}/password", new { password }, token);

    // ------------------------------------------------------------------ what the user keeps on the server

    public Task<CentralLmsAccounts> LmsAccountsAsync(CancellationToken token = default) => GetAsync<CentralLmsAccounts>("api/v1/me/lms-accounts", token);

    public Task<CentralLmsAccount> SaveLmsAccountAsync(string label, string email, string role, string password, bool active, CancellationToken token = default) =>
        SendAsync<CentralLmsAccount>(HttpMethod.Post, "api/v1/me/lms-accounts", new { label, email, role, password, active }, token);

    public Task<CentralLmsAccount> UseLmsAccountAsync(string id, CancellationToken token = default) =>
        SendAsync<CentralLmsAccount>(HttpMethod.Post, $"api/v1/me/lms-accounts/{id}/use", null, token);

    public Task<JsonElement> DeleteLmsAccountAsync(string id, CancellationToken token = default) =>
        SendAsync<JsonElement>(HttpMethod.Delete, $"api/v1/me/lms-accounts/{id}", null, token);

    /// <summary>The Zoom accounts this person keeps on the server (never a Zoom sign-in).</summary>
    public async Task<List<CentralZoomAccount>> ZoomAccountsAsync(CancellationToken token = default) =>
        (await GetAsync<JsonElement>("api/v1/me/zoom-accounts", token)).GetProperty("accounts").Deserialize<List<CentralZoomAccount>>(Json) ?? [];

    /// <summary>
    /// The Zoom accounts this PC has, as a whole set. What a person's own app knows is what is kept,
    /// so whoever runs their classes picks the account and the link from there instead of typing it.
    /// </summary>
    public Task<JsonElement> SaveZoomAccountsAsync(IEnumerable<object> accounts, CancellationToken token = default) =>
        SendAsync<JsonElement>(HttpMethod.Put, "api/v1/me/zoom-accounts", new { accounts = accounts.ToArray() }, token);

    /// <summary>A coordinator's Zoom accounts, for the admin's app that runs their classes.</summary>
    public async Task<List<CentralZoomAccount>> CoordinatorZoomAccountsAsync(string coordinatorId, CancellationToken token = default) =>
        (await GetAsync<JsonElement>($"api/v1/admin/users/{coordinatorId}/zoom-accounts", token))
            .GetProperty("accounts").Deserialize<List<CentralZoomAccount>>(Json) ?? [];

    /// <summary>The account's email and password, for this app to sign in to the LMS with. Never logged.</summary>
    public Task<CentralLmsSecret> LmsSecretAsync(string id, CancellationToken token = default) =>
        SendAsync<CentralLmsSecret>(HttpMethod.Post, $"api/v1/me/lms-accounts/{id}/secret", null, token);

    /// <summary>
    /// A large file from the server (the app's own update) straight to disk, with the session this
    /// app is signed in with. Its own client with no overall timeout: 170 MB over a home connection
    /// takes longer than the 30 seconds an ordinary call is given.
    /// </summary>
    public async Task DownloadAsync(string path, System.IO.Stream destination, IProgress<long>? received = null, CancellationToken token = default)
    {
        if (Me == null) await EnsureSignedInAsync(token);
        var http = Http();                                  // makes sure the cookies are loaded
        using var download = new HttpClient(new HttpClientHandler { CookieContainer = _cookies!, UseCookies = true }, disposeHandler: true)
        { BaseAddress = http.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        using var response = await download.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, token);
        await using var body = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, token)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
            total += read;
            received?.Report(total);
        }
    }

    /// <summary>
    /// A single-use token for this PC to register itself as a device, so that what it does here -
    /// the attendance above all - reaches the server by itself. Spent at once by the agent.
    /// </summary>
    public async Task<string> EnrollThisPcAsync(string name, CancellationToken token = default) =>
        (await SendAsync<JsonElement>(HttpMethod.Post, "api/v1/me/devices/enroll", new { name }, token))
            .GetProperty("enrollmentToken").GetString() ?? throw new CentralApiException(0, "The server sent no enrollment token.");

    // ------------------------------------------------------------------ other people's classes (admin)

    /// <summary>Every coordinator, their groups and sign-ins, and whether this PC runs their classes.</summary>
    public Task<CentralDelegationList> DelegationsAsync(CancellationToken token = default) =>
        GetAsync<CentralDelegationList>("api/v1/admin/delegations", token);

    /// <summary>Run this coordinator's classes (or stop), under the LMS and Zoom account named.</summary>
    public Task<JsonElement> SetDelegationAsync(string coordinatorId, bool enabled, string? lmsAccountId = null,
        string? zoomAccountId = null, CancellationToken token = default) =>
        SendAsync<JsonElement>(HttpMethod.Put, $"api/v1/admin/delegations/{coordinatorId}",
            new { enabled, lmsAccountId, zoomAccountId }, token);

    /// <summary>That coordinator's LMS sign-in, so their classes go up as theirs. Never logged.</summary>
    public Task<CentralCoordinatorSecret> CoordinatorLmsSecretAsync(string coordinatorId, string accountId, CancellationToken token = default) =>
        SendAsync<CentralCoordinatorSecret>(HttpMethod.Post, $"api/v1/admin/users/{coordinatorId}/lms-accounts/{accountId}/secret", null, token);

    /// <summary>The classes to run between two days, for everyone turned on or only the ones asked for.</summary>
    public Task<CentralRunPlan> RunPlanAsync(DateOnly from, DateOnly to, IEnumerable<string>? coordinators = null,
        CancellationToken token = default)
    {
        string who = string.Concat((coordinators ?? []).Select(id => $"&coordinator={Uri.EscapeDataString(id)}"));
        return GetAsync<CentralRunPlan>($"api/v1/admin/run-plan?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}{who}", token);
    }

    /// <summary>What that coordinator's own LMS session list showed. Re-sending it changes nothing else.</summary>
    public Task<JsonElement> ImportRunPlanAsync(string coordinatorId, IEnumerable<object> classes, CancellationToken token = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, "api/v1/admin/run-plan/import",
            new { coordinatorId, classes = classes.ToArray() }, token);

    /// <summary>One class's meeting link, Zoom account, what it opens with, or skipping it.</summary>
    public Task<CentralClassPlan> UpdateClassPlanAsync(string planId, object body, CancellationToken token = default) =>
        SendAsync<CentralClassPlan>(HttpMethod.Patch, $"api/v1/admin/run-plan/{planId}", body, token);

    public async Task<JsonElement?> SettingAsync(string key, CancellationToken token = default)
    {
        var answer = await GetAsync<JsonElement>($"api/v1/settings/{key}", token);
        return answer.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object ? value : null;
    }

    public Task<JsonElement> SaveSettingAsync(string key, object value, CancellationToken token = default) =>
        SendAsync<JsonElement>(HttpMethod.Put, $"api/v1/settings/{key}", new { value }, token);
}

public sealed record CentralLmsAccount(string Id, string Label, string Email, string Role, bool Active);

public sealed record CentralKnownAccount(string Username, string DisplayName, string Role, DateTimeOffset LastUsed)
{
    public bool HasSession { get; init; }
    /// <summary>Its password is saved in Windows Credential Manager on this PC.</summary>
    public bool HasPassword { get; init; }
}

/// <summary>
/// The dashboard accounts that signed in on this PC and which one is current - names only, never a
/// password (%LOCALAPPDATA%\ZoomAutoAdmit\Central\known-accounts.json).
/// </summary>
public sealed class CentralKnownAccounts(string? path = null)
{
    private sealed class Document { public string? Current { get; set; } public List<CentralKnownAccount> Accounts { get; set; } = []; }
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Central", "known-accounts.json");

    private Document Load()
    {
        try { return System.IO.File.Exists(_path) ? JsonSerializer.Deserialize<Document>(System.IO.File.ReadAllText(_path), Options) ?? new() : new(); }
        catch (Exception ex) when (ex is System.IO.IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    private void Save(Document document)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        System.IO.File.WriteAllText(_path, JsonSerializer.Serialize(document, Options));
    }

    public string? Current => Load().Current;
    public IReadOnlyList<CentralKnownAccount> List() => [.. Load().Accounts.OrderByDescending(a => a.LastUsed)];

    public void SetCurrent(string? username)
    {
        var d = Load();
        d.Current = username?.ToLowerInvariant();
        Save(d);
    }

    public void Touch(CentralMe me)
    {
        var d = Load();
        d.Accounts.RemoveAll(a => a.Username.Equals(me.Username, StringComparison.OrdinalIgnoreCase));
        d.Accounts.Add(new CentralKnownAccount(me.Username, me.DisplayName, me.Role, DateTimeOffset.Now));
        d.Current = me.Username.ToLowerInvariant();
        Save(d);
    }

    public void Remove(string username)
    {
        var d = Load();
        d.Accounts.RemoveAll(a => a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(d.Current, username, StringComparison.OrdinalIgnoreCase)) d.Current = null;
        Save(d);
    }
}
public sealed record CentralLmsAccounts(List<CentralLmsAccount> Accounts, bool CanKeepPasswords);
// Never a plain record ToString in a log: the password is in it.
public sealed class CentralCoordinatorSecret
{
    public string Id { get; init; } = "";
    public string CoordinatorId { get; init; } = "";
    public string Email { get; init; } = "";
    public string Role { get; init; } = "coordinator";
    public string Label { get; init; } = "";
    public string Password { get; init; } = "";
    public override string ToString() => "A coordinator's LMS sign-in (secret redacted)";
}

// Never a plain record ToString in a log: the password is in it.
public sealed class CentralLmsSecret
{
    public string Id { get; init; } = "";
    public string Email { get; init; } = "";
    public string Password { get; init; } = "";
    public override string ToString() => "LMS sign-in (secret redacted)";
}
