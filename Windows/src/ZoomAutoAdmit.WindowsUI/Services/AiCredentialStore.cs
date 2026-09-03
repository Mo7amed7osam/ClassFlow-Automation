using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ZoomAutoAdmit.WindowsUI.Services;

// Never a record: generated ToString must not expose the secret.
public enum AiProvider { OpenAI, OpenRouter }

public sealed class AiConnectionSettings(string model, string apiKey, AiProvider provider = AiProvider.OpenAI)
{
    public string Model { get; } = model;
    public string ApiKey { get; } = apiKey;
    public AiProvider Provider { get; } = provider;
    public override string ToString() => "AI connection (secret redacted)";
}

public interface IAiCredentialStore
{
    AiConnectionSettings? Read();
    void Save(AiConnectionSettings settings);
    void Delete();
}

/// <summary>User-owned BYOK secret, protected by Windows, not saved in app JSON or logs.</summary>
public sealed class AiCredentialStore(string target = "ZoomAutoAdmit/AI/OpenAI") : IAiCredentialStore
{
    public AiConnectionSettings? Read()
    {
        if (!CredRead(target, 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return null;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the AI credential from Windows Credential Manager.");
        }
        try
        {
            var native = Marshal.PtrToStructure<Credential>(pointer);
            if (native.BlobSize is 0 or > 2560) throw new InvalidDataException("Invalid saved AI credential.");
            var bytes = new byte[native.BlobSize];
            try
            {
                Marshal.Copy(native.Blob, bytes, 0, bytes.Length);
                using var json = JsonDocument.Parse(bytes);
                var provider = AiProvider.OpenAI; // Credentials saved by previous versions had no provider.
                if (json.RootElement.TryGetProperty("provider", out var savedProvider) &&
                    (!Enum.TryParse(savedProvider.GetString(), out provider) || !Enum.IsDefined(provider)))
                    throw new InvalidDataException("Invalid saved AI provider.");
                return new(json.RootElement.GetProperty("model").GetString()!, json.RootElement.GetProperty("key").GetString()!, provider);
            }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public void Save(AiConnectionSettings settings)
    {
        if (!Enum.IsDefined(settings.Provider)) throw new ArgumentException("Invalid AI provider.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { model = settings.Model, key = settings.ApiKey, provider = settings.Provider.ToString() });
        if (bytes.Length > 2560) { Array.Clear(bytes); throw new ArgumentException("API key is too long."); }
        var pointer = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new Credential { Type = 1, Target = target, UserName = settings.Provider + " API", BlobSize = (uint)bytes.Length, Blob = pointer, Persist = 2 };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot save the AI credential in Windows Credential Manager.");
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
        if (!CredDelete(target, 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot remove the saved AI credential.");
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
