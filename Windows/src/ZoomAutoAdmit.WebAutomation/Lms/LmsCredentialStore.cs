using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ZoomAutoAdmit.WebAutomation.Lms;

// Never a record: a generated ToString must not be able to print the password.
public sealed class LmsAccount(string email, string password)
{
    public string Email { get; } = email;
    public string Password { get; } = password;
    public override string ToString() => $"LMS account {Email} (password redacted)";
}

public interface ILmsCredentialStore
{
    LmsAccount? Read();
    void Save(LmsAccount account);
    void Delete();
}

/// <summary>
/// The LMS sign-in, kept by Windows Credential Manager under the signed-in Windows account.
///
/// It is deliberately not in the app's JSON, not in any log line and not in a file anyone can
/// copy: the password reaches Zoom Auto Admit only to type it into the LMS login form, and
/// nothing else ever reads it.
/// </summary>
public sealed class LmsCredentialStore(string? target = null, string? profile = null) : ILmsCredentialStore
{
    private const int NotFound = 1168;
    private const int MaximumBlobBytes = 2560;

    /// <summary>Without a target, the account chosen in the app (<see cref="LmsAccountDirectory"/>).</summary>
    private string Target => target ?? new LmsAccountDirectory().Active().Target;

    /// <summary>
    /// The browser profile this sign-in uses, so two accounts never share one LMS session. A caller
    /// that already knows the account gives it here; otherwise it is looked up by target.
    /// </summary>
    public string Profile => profile ?? (target == null
        ? new LmsAccountDirectory().Active().Profile
        : new LmsAccountDirectory().List().FirstOrDefault(a => a.Target == target)?.Profile ?? LmsAccountDirectory.LegacyProfile);

    public LmsAccount? Read()
    {
        string target = Target;
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

    public void Save(LmsAccount account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Email);
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Password);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new { email = account.Email.Trim(), password = account.Password });
        if (bytes.Length > MaximumBlobBytes) throw new ArgumentException("The LMS sign-in is too long to store.");
        string target = Target;
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

    public void Delete()
    {
        if (CredDelete(Target, 1, 0)) return;
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
    private static extern void CredFree(IntPtr buffer);
}
