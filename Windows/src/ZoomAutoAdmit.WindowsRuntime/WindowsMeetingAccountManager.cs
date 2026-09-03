using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Sessions;

namespace ZoomAutoAdmit.WindowsRuntime;

public enum AccountEnginePreference
{
    Auto,
    Desktop,
    Web
}

public sealed record WindowsMeetingAccountMetadata(
    string AccountId,
    string DisplayName,
    string CredentialReference,
    AccountEnginePreference? PreferredEngine = AccountEnginePreference.Auto)
{
    public string? ZoomEmail { get; init; }
    public string? DefaultMeetingUrl { get; init; }
    /// <summary>
    /// Browser profile directory this account signs in with, under the managed Profiles folder.
    /// Empty keeps the historical name derived from the account id. Set it to reuse a profile you
    /// already signed in to (for example one created from the console tester).
    /// </summary>
    public string? WebProfileName { get; init; }
}

public sealed class WindowsAccountWebProfileMapper
{
    private readonly string _profilesRoot;

    public WindowsAccountWebProfileMapper(string? profilesRoot = null)
    {
        _profilesRoot = Path.GetFullPath(profilesRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZoomAutoAdmit",
            "Profiles"));
    }

    public string ResolveDirectory(string accountId) => ResolveDirectory(accountId, null);

    public string ResolveDirectory(string accountId, string? webProfileName)
    {
        string profileName = string.IsNullOrWhiteSpace(webProfileName)
            ? AccountWebProfile.ForAccount(accountId)
            : webProfileName.Trim();
        Directory.CreateDirectory(_profilesRoot);
        string directory = Path.GetFullPath(Path.Combine(_profilesRoot, profileName));
        string rootWithSeparator = _profilesRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Resolved account profile escaped the managed Profiles directory.");
        // Existing account-specific profiles are reused. Unrelated legacy profiles are
        // deliberately left untouched and are never renamed or deleted.
        Directory.CreateDirectory(directory);
        return directory;
    }
}

public interface IWindowsCredentialReferenceResolver
{
    bool CanResolve(string credentialReference);
}

public sealed class WindowsCredentialManagerReferenceResolver : IWindowsCredentialReferenceResolver
{
    private const int CredentialTypeGeneric = 1;

    public bool CanResolve(string credentialReference)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(credentialReference)) return false;
        string target = credentialReference.StartsWith("wincred:", StringComparison.OrdinalIgnoreCase)
            ? credentialReference["wincred:".Length..]
            : credentialReference;
        if (string.IsNullOrWhiteSpace(target)) return false;
        if (!CredRead(target, CredentialTypeGeneric, 0, out var credentialPointer)) return false;
        CredFree(credentialPointer);
        return true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    public static string? TryGetUsername(string credentialReference)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(credentialReference)) return null;
        string target = credentialReference.StartsWith("wincred:", StringComparison.OrdinalIgnoreCase)
            ? credentialReference["wincred:".Length..]
            : credentialReference;
        if (string.IsNullOrWhiteSpace(target)) return null;
        if (!CredRead(target, CredentialTypeGeneric, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            return cred.UserName != IntPtr.Zero ? Marshal.PtrToStringUni(cred.UserName) : null;
        }
        finally
        {
            CredFree(ptr);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        int type,
        int reservedFlag,
        out IntPtr credentialPointer);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr credentialPointer);
}

public sealed class WindowsMeetingAccountManager : IMeetingAccountManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _metadataPath;
    private readonly IWindowsCredentialReferenceResolver _credentialResolver;
    private readonly WindowsAccountWebProfileMapper _profileMapper;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> FileLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _fileLock;

    public WindowsMeetingAccountManager(
        string? metadataPath = null,
        IWindowsCredentialReferenceResolver? credentialResolver = null,
        WindowsAccountWebProfileMapper? profileMapper = null)
    {
        _metadataPath = Path.GetFullPath(metadataPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZoomAutoAdmit",
            "Accounts",
            "accounts.json"));
        _credentialResolver = credentialResolver ?? new WindowsCredentialManagerReferenceResolver();
        _profileMapper = profileMapper ?? new WindowsAccountWebProfileMapper();
        _fileLock = FileLocks.GetOrAdd(_metadataPath, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<MeetingAccount?> LoadAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId) || !File.Exists(_metadataPath)) return null;
        var accounts = await ListConfiguredAsync(cancellationToken);
        var account = accounts.FirstOrDefault(candidate =>
            candidate.AccountId.Equals(accountId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (account == null) return null;
        // Saved Zoom Desktop accounts and persistent Web sessions do not need a password.
        // Legacy records without an explicit identity retain their credential-reference path.
        if (account.ZoomEmail == null && !_credentialResolver.CanResolve(account.CredentialReference)) return null;
        _profileMapper.ResolveDirectory(account.AccountId, account.WebProfileName);

        // Only the secure target reference crosses the orchestration boundary. Credential
        // secret bytes remain owned by Windows Credential Manager and are never serialized.
        var loaded = new MeetingAccount(
            account.AccountId,
            account.DisplayName,
            account.CredentialReference,
            account.PreferredEngine switch
            {
                AccountEnginePreference.Desktop => SessionEngineType.Desktop,
                AccountEnginePreference.Web => SessionEngineType.Web,
                _ => null
            })
        {
            ZoomEmail = account.ZoomEmail,
            WebProfileName = NormalizeWebProfileName(account.WebProfileName)
        };
        ConsoleLogger.Success($"[ACCOUNT] Loaded: {loaded.AccountId}");
        return loaded;
    }

    public async Task<IReadOnlyList<WindowsMeetingAccountMetadata>> ListConfiguredAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_metadataPath)) return [];
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            await using var stream = new FileStream(
                _metadataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            var accounts = await JsonSerializer.DeserializeAsync<List<WindowsMeetingAccountMetadata>>(
                stream,
                JsonOptions,
                cancellationToken) ?? [];
            return ValidateAccounts(accounts);
        }
        finally { _fileLock.Release(); }
    }

    public async Task UpsertAsync(
        WindowsMeetingAccountMetadata account,
        CancellationToken cancellationToken = default)
    {
        account = NormalizeMetadata(account);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var accounts = await ReadUnlockedAsync(cancellationToken);
            int index = accounts.FindIndex(candidate => candidate.AccountId.Equals(
                account.AccountId,
                StringComparison.OrdinalIgnoreCase));
            if (index >= 0) accounts[index] = account;
            else accounts.Add(account);
            accounts = ValidateAccounts(accounts);
            await WriteUnlockedAsync(accounts, cancellationToken);
        }
        finally { _fileLock.Release(); }
    }

    public async Task<bool> DeleteAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return false;
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var accounts = await ReadUnlockedAsync(cancellationToken);
            int removed = accounts.RemoveAll(candidate => candidate.AccountId.Equals(
                accountId.Trim(),
                StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            await WriteUnlockedAsync(accounts, cancellationToken);
            return true;
        }
        finally { _fileLock.Release(); }
    }

    private async Task<List<WindowsMeetingAccountMetadata>> ReadUnlockedAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_metadataPath)) return [];
        await using var stream = File.OpenRead(_metadataPath);
        var accounts = await JsonSerializer.DeserializeAsync<List<WindowsMeetingAccountMetadata>>(
            stream,
            JsonOptions,
            cancellationToken) ?? [];
        return ValidateAccounts(accounts);
    }

    private async Task WriteUnlockedAsync(
        List<WindowsMeetingAccountMetadata> accounts,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(_metadataPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $"accounts.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    accounts.OrderBy(account => account.AccountId).ToList(),
                    JsonOptions,
                    cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_metadataPath))
                File.Replace(temporaryPath, _metadataPath, _metadataPath + ".bak");
            else
                File.Move(temporaryPath, _metadataPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static WindowsMeetingAccountMetadata NormalizeMetadata(WindowsMeetingAccountMetadata account)
    {
        if (string.IsNullOrWhiteSpace(account.AccountId))
            throw new ArgumentException("Account ID is required.", nameof(account));
        _ = AccountWebProfile.ForAccount(account.AccountId);
        if (string.IsNullOrWhiteSpace(account.DisplayName))
            throw new ArgumentException("Display name is required.", nameof(account));
        string? email = NormalizeZoomEmail(account.ZoomEmail);
        if (email == null && string.IsNullOrWhiteSpace(account.CredentialReference))
            throw new ArgumentException("Zoom email is required (legacy records may use a credential reference).", nameof(account));
        return account with { AccountId = account.AccountId.Trim(), DisplayName = account.DisplayName.Trim(),
            CredentialReference = account.CredentialReference?.Trim() ?? string.Empty, ZoomEmail = email,
            DefaultMeetingUrl = NormalizeDefaultMeetingUrl(account.DefaultMeetingUrl),
            WebProfileName = NormalizeWebProfileName(account.WebProfileName) };
    }

    /// <summary>Same rules the browser profile manager enforces for a directory name.</summary>
    public static string? NormalizeWebProfileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string name = value.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$"))
            throw new ArgumentException(
                "Web profile must be 1-64 characters: letters, numbers, dot, underscore or hyphen, starting with a letter or number.");
        return name;
    }

    public static string? NormalizeDefaultMeetingUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string url = value.Trim();
        // Reuse the existing runtime URL rules; preserve the complete link, including its passcode query.
        var parsed = ZoomAutoAdmit.WebAutomation.ZoomWebMeetingController.ValidateMeetingUrl(url);
        if (!string.IsNullOrEmpty(parsed.UserInfo) || url.Any(char.IsWhiteSpace))
            throw new ArgumentException("Meeting URL must not contain login credentials or spaces.");
        return url;
    }

    public static string? NormalizeZoomEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string email = value.Trim();
        if (email.Any(char.IsWhiteSpace) || !System.Net.Mail.MailAddress.TryCreate(email, out var parsed) ||
            !parsed.Address.Equals(email, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Enter the complete Zoom email, without spaces or a display name.");
        return email.ToLowerInvariant(); // Preserve '+' tags; never infer an address from the display name.
    }

    private static List<WindowsMeetingAccountMetadata> ValidateAccounts(List<WindowsMeetingAccountMetadata> accounts)
    {
        var normalized = accounts.Select(NormalizeMetadata).ToList();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in normalized)
        {
            if (!ids.Add(account.AccountId)) throw new InvalidOperationException($"Duplicate account ID: {account.AccountId}.");
            if (account.ZoomEmail != null && !emails.Add(account.ZoomEmail))
                throw new InvalidOperationException($"Zoom email already belongs to another configured account: {account.ZoomEmail}.");
        }
        return normalized;
    }
}
