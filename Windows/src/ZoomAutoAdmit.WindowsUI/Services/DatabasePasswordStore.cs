using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ZoomAutoAdmit.WindowsUI.Services;

public interface IDatabasePasswordStore
{
    bool HasPassword { get; }
    string? Read();
    void Save(string password);
    void Delete();
}

/// <summary>
/// The central database's password, for server mode. Kept in Windows Credential Manager for this
/// Windows account, like the LMS sign-in and the AI key; never in a file, a log or the settings JSON.
/// </summary>
public sealed class DatabasePasswordStore(string target = "ZoomAutoAdmit/Central/DatabasePassword") : IDatabasePasswordStore
{
    private const uint Generic = 1;
    private const int NotFound = 1168;

    public bool HasPassword => Read() is not null;

    public string? Read()
    {
        if (!CredRead(target, Generic, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == NotFound) return null;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the database password from Windows Credential Manager.");
        }
        try
        {
            var native = Marshal.PtrToStructure<Credential>(pointer);
            if (native.BlobSize is 0 or > 2048) return null;
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

    public void Save(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("The password is empty.");
        var bytes = Encoding.UTF8.GetBytes(password);
        if (bytes.Length > 2048) { Array.Clear(bytes); throw new ArgumentException("The password is too long."); }
        var pointer = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new Credential { Type = Generic, Target = target, UserName = "central database", BlobSize = (uint)bytes.Length, Blob = pointer, Persist = 2 };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot save the database password in Windows Credential Manager.");
        }
        finally
        {
            for (int i = 0; i < bytes.Length; i++) Marshal.WriteByte(pointer, i, 0);
            Marshal.FreeCoTaskMem(pointer);
            Array.Clear(bytes);
        }
    }

    public void Delete()
    {
        if (!CredDelete(target, Generic, 0) && Marshal.GetLastWin32Error() != NotFound)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot remove the saved database password.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? Target, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
}
