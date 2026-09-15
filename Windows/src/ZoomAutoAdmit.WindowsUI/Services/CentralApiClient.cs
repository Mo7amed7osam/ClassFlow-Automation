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

public sealed class CentralApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
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
public sealed class CentralApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly Func<Uri?> _baseUri;
    private readonly CentralLoginStore _logins;
    // "Keep me signed in": the session lives in the server's database (30 days, ended there when the
    // account is disabled or its password changes); this PC keeps only that session's cookie, one per
    // account that signed in here, so the Dashboard can offer them to pick from.
    private static readonly DatabasePasswordStore Legacy = new("ZoomAutoAdmit/Central/Session");
    private static DatabasePasswordStore SessionOf(string username) => new($"ZoomAutoAdmit/Central/Session/{username.ToLowerInvariant()}");
    private readonly CentralKnownAccounts _known = new();
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
    public bool HasSavedLogin => (_session is { } s && SafeRead(s) != null) || _logins.Read() != null;

    /// <summary>The accounts that signed in on this PC, newest first; HasSession = can continue without a password.</summary>
    public IReadOnlyList<CentralKnownAccount> KnownAccounts =>
        [.. _known.List().Select(a => a with { HasSession = SafeRead(SessionOf(a.Username)) != null })];

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

    /// <summary>Continue as another account that signed in here. Null when its session has ended (sign in again).</summary>
    public async Task<CentralMe?> SwitchToAsync(string username, CancellationToken token = default)
    {
        _known.SetCurrent(username);
        Me = null; _http?.Dispose(); _http = null;
        try { Me = await GetDirectAsync<CentralMe>("api/v1/auth/me", token); _known.Touch(Me); return Me; }
        catch (CentralApiException ex) when (ex.Status == HttpStatusCode.Unauthorized) { try { SessionOf(username).Delete(); } catch { } return null; }
    }

    /// <summary>Removes an account from this PC's list (and ends its kept session on the server when it is the current one).</summary>
    public void ForgetAccount(string username)
    {
        if (string.Equals(_known.Current, username, StringComparison.OrdinalIgnoreCase)) Forget();
        try { SessionOf(username).Delete(); } catch { }
        _known.Remove(username);
    }

    public async Task<CentralMe> SignInAsync(string username, string password, bool remember, CancellationToken token = default)
    {
        // The server wants X-Dashboard-Request on every POST, the sign-in included.
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login")
        {
            Content = JsonContent.Create(new { username = username.Trim(), password, remember }, options: Json),
        };
        request.Headers.Add("X-Dashboard-Request", "1");
        using var response = await Http().SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, token);
        // No password is kept on this PC any more: a remembered sign-in is a server session.
        try { _logins.Delete(); } catch { }
        string who = username.Trim().ToLowerInvariant();
        _known.SetCurrent(who);
        if (remember) KeepSession(who); else try { SessionOf(who).Delete(); } catch { }
        Me = await GetDirectAsync<CentralMe>("api/v1/auth/me", token);
        _known.Touch(Me);
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
            return Me;
        }
        catch (CentralApiException ex) when (ex.Status == HttpStatusCode.Unauthorized) { try { _session?.Delete(); } catch { } }
        var saved = _logins.Read();
        if (saved == null) return null;
        return await SignInAsync(saved.Value.Username, saved.Value.Password, remember: true, token);
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
        catch (CentralApiException ex) when (ex.Status == HttpStatusCode.Unauthorized && _logins.Read() is { } saved)
        {
            await SignInAsync(saved.Username, saved.Password, remember: true, token);
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

    /// <summary>The account's email and password, for this app to sign in to the LMS with. Never logged.</summary>
    public Task<CentralLmsSecret> LmsSecretAsync(string id, CancellationToken token = default) =>
        SendAsync<CentralLmsSecret>(HttpMethod.Post, $"api/v1/me/lms-accounts/{id}/secret", null, token);

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
public sealed class CentralLmsSecret
{
    public string Id { get; init; } = "";
    public string Email { get; init; } = "";
    public string Password { get; init; } = "";
    public override string ToString() => "LMS sign-in (secret redacted)";
}
