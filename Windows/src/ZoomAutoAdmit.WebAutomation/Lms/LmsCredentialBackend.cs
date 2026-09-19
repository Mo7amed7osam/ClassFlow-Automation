using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace ZoomAutoAdmit.WebAutomation.Lms;

/// <summary>
/// Where an LMS password actually rests. On Windows that is Credential Manager, under the signed-in
/// Windows account. On a server there is no such thing, and the password comes from the central
/// backend instead, decrypted for the one run that needs it.
///
/// Everything above this interface - the account directory, the session runner, the class-to-account
/// map - is the same on both, so the automation is written once.
/// </summary>
public interface ILmsCredentialBackend
{
    LmsAccount? Read(string target);
    void Save(string target, LmsAccount account);
    void Delete(string target);
}

/// <summary>
/// The backend in use. A Windows process gets Credential Manager without arranging anything; a
/// process anywhere else sets <see cref="Current"/> once at startup.
///
/// This exists because <see cref="LmsCredentialStore"/> is constructed directly in a dozen places.
/// Rather than thread a dependency through all of them, the choice of store is made once, here,
/// where a process that has not made it gets a sentence explaining what to do instead of a
/// DllNotFoundException from deep inside a P/Invoke.
/// </summary>
public static class LmsCredentialBackend
{
    private static ILmsCredentialBackend? _current;

    public static ILmsCredentialBackend Current
    {
        get
        {
            if (_current != null) return _current;
            if (OperatingSystem.IsWindows()) return _current = new WindowsCredentialManagerBackend();
            throw new PlatformNotSupportedException(
                "LMS passwords are kept in Windows Credential Manager, which this machine does not have. " +
                "A process running off Windows sets LmsCredentialBackend.Current at startup - the cloud " +
                "worker points it at the central backend, which holds them encrypted.");
        }
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Whether a backend can be reached here, without throwing to find out.</summary>
    public static bool IsConfigured => _current != null || OperatingSystem.IsWindows();

    /// <summary>Back to the platform default. For tests, which must not leak a fake into the next one.</summary>
    public static void Reset() => _current = null;
}

/// <summary>
/// Windows Credential Manager, one generic credential per account target. The password reaches this
/// process only to be typed into the LMS login form, and is never written to the app's JSON, a log
/// line, or a file anyone can copy.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialManagerBackend : ILmsCredentialBackend
{
    private const int NotFound = 1168;
    private const int MaximumBlobBytes = 2560;

    public LmsAccount? Read(string target)
    {
        if (!CredRead(target, 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == NotFound) return null;
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Cannot read the LMS sign-in from Windows Credential Manager.");
        }
        try
        {
            var native = Marshal.PtrToStructure<Credential>(pointer);
            if (native.BlobSize is 0 or > MaximumBlobBytes)
                throw new InvalidDataException("The saved LMS sign-in is not valid.");
            var bytes = new byte[native.BlobSize];
            try
            {
                Marshal.Copy(native.Blob, bytes, 0, bytes.Length);
                using var json = JsonDocument.Parse(bytes);
                return new LmsAccount(
                    json.RootElement.GetProperty("email").GetString()!,
                    json.RootElement.GetProperty("password").GetString()!);
            }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public void Save(string target, LmsAccount account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Email);
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Password);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new { email = account.Email.Trim(), password = account.Password });
        if (bytes.Length > MaximumBlobBytes) throw new ArgumentException("The LMS sign-in is too long to store.");
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var credential = new Credential
            {
                Type = 1,                    // Generic
                Persist = 2,                 // Local machine, this Windows account only
                TargetName = target,
                UserName = account.Email.Trim(),
                Blob = handle.AddrOfPinnedObject(),
                BlobSize = bytes.Length
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Cannot save the LMS sign-in to Windows Credential Manager.");
        }
        finally
        {
            handle.Free();
            Array.Clear(bytes);
        }
    }

    public void Delete(string target)
    {
        if (CredDelete(target, 1, 0)) return;
        if (Marshal.GetLastWin32Error() == NotFound) return;
        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            "Cannot remove the LMS sign-in from Windows Credential Manager.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public int BlobSize;
        public IntPtr Blob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr credential);
}

/// <summary>
/// The passwords a server process was given for this run, held in memory only.
///
/// The cloud worker fills one of these from the central backend, which keeps LMS passwords AES-GCM
/// encrypted and hands one over only to an authorised caller, writing every read to its audit log.
/// Nothing here reaches a disk: a worker that restarts asks the server again.
/// </summary>
public sealed class InMemoryLmsCredentialBackend : ILmsCredentialBackend
{
    private readonly Dictionary<string, LmsAccount> _byTarget = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public LmsAccount? Read(string target)
    {
        lock (_gate) return _byTarget.GetValueOrDefault(target);
    }

    public void Save(string target, LmsAccount account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Email);
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Password);
        lock (_gate) _byTarget[target] = account;
    }

    public void Delete(string target)
    {
        lock (_gate) _byTarget.Remove(target);
    }

    /// <summary>Forget every password held. Called when a worker drains, so none outlives its use.</summary>
    public void Clear()
    {
        lock (_gate) _byTarget.Clear();
    }
}
