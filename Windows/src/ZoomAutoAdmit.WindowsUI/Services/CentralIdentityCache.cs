using System.IO;
using System.Text.Json;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>
/// Who signed in here last, kept on this PC so the app still knows them while the central server is
/// away - my PC turned off, no network, a funnel down. Without it a coordinator was treated as
/// signed out the moment the server stopped answering, and was shown the admin's pages.
///
/// Nothing secret: the name, the role and the groups the server already sent, plus when that
/// session runs out - past that the identity is not used. The session cookie and any password stay
/// in Windows Credential Manager, as before.
/// </summary>
public sealed class CentralIdentityCache(string? path = null)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Central", "last-signed-in.json");

    // Keys are the lower-cased user name, as everywhere else on this PC: a dictionary read back from
    // JSON has the plain comparer again, so the name is folded here rather than by the dictionary.
    private sealed class Document { public Dictionary<string, CentralMe> Accounts { get; set; } = []; }

    private static string Key(string username) => username.Trim().ToLowerInvariant();

    private Document Load()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), Options) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { return new(); }
    }

    public void Save(CentralMe me)
    {
        try
        {
            var document = Load();
            document.Accounts[Key(me.Username)] = me;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The account as the server last described it, while that session has not run out.</summary>
    public CentralMe? Read(string? username) =>
        username is { Length: > 0 } && Load().Accounts.TryGetValue(Key(username), out var me) && me.ExpiresAt > DateTimeOffset.UtcNow
            ? me
            : null;

    public void Forget(string username)
    {
        var document = Load();
        if (!document.Accounts.Remove(Key(username))) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(document, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
