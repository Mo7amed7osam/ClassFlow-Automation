using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ZoomAutoAdmit.WindowsUI.Services;

public interface IZoomProfileCredentialStore
{
    bool HasPassword(string accountId);
    string ReferenceFor(string accountId);
    void Save(string accountId, string email, string password);
    void Delete(string accountId);
}

/// <summary>
/// Keeps each Zoom profile password in Windows Credential Manager. Account metadata contains only
/// the target reference; the secret is never serialized, logged, bound, or returned to the UI.
/// </summary>
public sealed class ZoomProfileCredentialStore : IZoomProfileCredentialStore
{
    private const uint Generic = 1;
    private const int NotFound = 1168;

    public string ReferenceFor(string accountId) => $"wincred:{Target(accountId)}";

    public bool HasPassword(string accountId)
    {
        if (!CredRead(Target(accountId), Generic, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == NotFound) return false;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the Zoom profile credential.");
        }
        CredFree(pointer);
        return true;
    }

    public void Save(string accountId, string email, string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("The Zoom password is empty.");
        var bytes = Encoding.UTF8.GetBytes(password);
        if (bytes.Length > 2048) { Array.Clear(bytes); throw new ArgumentException("The Zoom password is too long."); }
        var pointer = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new Credential
            {
                Type = Generic, Target = Target(accountId), UserName = email.Trim(), BlobSize = (uint)bytes.Length,
                Blob = pointer, Persist = 2, Comment = "Zoom Auto Admit profile"
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot save the Zoom profile credential.");
        }
        finally
        {
            for (int i = 0; i < bytes.Length; i++) Marshal.WriteByte(pointer, i, 0);
            Marshal.FreeCoTaskMem(pointer);
            Array.Clear(bytes);
        }
    }

    public void Delete(string accountId)
    {
        if (!CredDelete(Target(accountId), Generic, 0) && Marshal.GetLastWin32Error() != NotFound)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot remove the Zoom profile credential.");
    }

    private static string Target(string accountId)
    {
        string id = (accountId ?? string.Empty).Trim();
        if (id.Length == 0 || id.Length > 64 || id.Any(char.IsWhiteSpace))
            throw new ArgumentException("Save an Account ID before storing its password.");
        return $"ZoomAutoAdmit/ZoomProfile/{id}";
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
