using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ZoomAutoAdmit.CentralAgent;

/// <summary>Where the device token lives between runs.</summary>
public interface IDeviceTokenStore
{
    string? Read();
    void Save(string token);
    void Delete();
}

/// <summary>
/// The device token, kept by Windows Credential Manager for the signed-in Windows account - like the
/// LMS sign-in - so it is in no file, no log and no command line. It is not the local recording
/// API's key and is never used as one.
/// </summary>
public sealed class CredentialManagerDeviceTokenStore(string target = CredentialManagerDeviceTokenStore.DefaultTarget) : IDeviceTokenStore
{
    public const string DefaultTarget = "ZoomAutoAdmit/Central/DeviceToken";
    private const int NotFound = 1168;
    private const int MaximumBlobBytes = 2560;

    public string? Read()
    {
        if (!CredRead(target, 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == NotFound) return null;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the device token from Windows Credential Manager.");
        }
        try
        {
            var native = Marshal.PtrToStructure<Credential>(pointer);
            if (native.BlobSize is 0 or > MaximumBlobBytes) return null;
            var bytes = new byte[native.BlobSize];
            try
            {
                Marshal.Copy(native.Blob, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public void Save(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var bytes = Encoding.UTF8.GetBytes(token.Trim());
        if (bytes.Length > MaximumBlobBytes) throw new ArgumentException("The device token is too long to store.");
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var credential = new Credential
            {
                Type = 1,                    // Generic
                Persist = 2,                 // Local machine, this Windows account only
                TargetName = target,
                UserName = "central-agent",
                Blob = handle.AddrOfPinnedObject(),
                BlobSize = bytes.Length,
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot save the device token to Windows Credential Manager.");
        }
        finally
        {
            handle.Free();
            Array.Clear(bytes);
        }
    }

    public void Delete()
    {
        if (CredDelete(target, 1, 0)) return;
        if (Marshal.GetLastWin32Error() == NotFound) return;
        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot remove the device token from Windows Credential Manager.");
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
