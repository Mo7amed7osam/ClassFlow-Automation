using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ZoomAutoAdmit.Core.Sessions;

public enum SessionEngineType
{
    Desktop,
    Web
}

public enum SessionStatus
{
    Allocated,
    Starting,
    Active,
    Stopping,
    Completed,
    Failed
}

public sealed record ActiveSession(
    Guid SessionId,
    string AccountId,
    SessionEngineType EngineType,
    DateTimeOffset StartTime,
    SessionStatus Status,
    string? WebProfileName)
{
    public bool OccupiesCapacity => Status is not SessionStatus.Completed and not SessionStatus.Failed;
}

public enum SessionAllocationError
{
    None,
    InvalidAccountId,
    DuplicateSessionId,
    DesktopOccupied,
    WebProfileLocked
}

public sealed record SessionAllocationResult(
    bool IsSuccess,
    ActiveSession? Session,
    SessionAllocationError Error,
    string? ErrorMessage)
{
    public static SessionAllocationResult Success(ActiveSession session) =>
        new(true, session, SessionAllocationError.None, null);

    public static SessionAllocationResult Failure(SessionAllocationError error, string message) =>
        new(false, null, error, message);
}

public static class AccountWebProfile
{
    private static readonly Regex SafeDirectoryName = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string ForAccount(string accountId)
    {
        string normalized = accountId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Account ID is required.", nameof(accountId));

        if (SafeDirectoryName.IsMatch(normalized)) return normalized;

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"account-{Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    /// <summary>
    /// Profile for the n-th simultaneous Web meeting of one account. Instance 1 is the account's
    /// own profile; later instances get their own directory, seeded from it, because Chromium
    /// refuses to open the same user-data directory twice.
    /// </summary>
    public static string ForAccountInstance(string accountId, int instance) =>
        ForProfileInstance(ForAccount(accountId), instance);

    /// <summary>Same numbering, for an account that names its browser profile explicitly.</summary>
    public static string ForProfileInstance(string baseProfileName, int instance)
    {
        if (instance < 1) throw new ArgumentOutOfRangeException(nameof(instance));
        string baseName = baseProfileName?.Trim() ?? string.Empty;
        if (baseName.Length == 0) throw new ArgumentException("Profile name is required.", nameof(baseProfileName));
        if (instance == 1) return baseName;

        string suffix = $"-{instance}";
        // Directory names stay within the 64-character limit the profile manager enforces.
        if (baseName.Length + suffix.Length > 64)
            baseName = baseName[..(64 - suffix.Length)];
        return baseName + suffix;
    }

    /// <summary>The account profile a per-session profile was seeded from, or null for a base one.</summary>
    public static string? BaseProfileOf(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return null;
        int separator = profileName.LastIndexOf('-');
        if (separator <= 0 || separator == profileName.Length - 1) return null;
        string tail = profileName[(separator + 1)..];
        return tail.Length <= 3 && tail.All(char.IsAsciiDigit) && tail != "0" && tail != "1"
            ? profileName[..separator]
            : null;
    }
}
