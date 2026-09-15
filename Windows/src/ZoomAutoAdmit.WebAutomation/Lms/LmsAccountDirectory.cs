using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZoomAutoAdmit.WebAutomation.Lms;

/// <summary>One LMS sign-in the app knows: a name for people, the email, and where its password is.</summary>
public sealed record LmsAccountEntry
{
    public required string Id { get; init; }
    /// <summary>What it is for, e.g. "Mohab - coordinator".</summary>
    public required string Label { get; init; }
    public required string Email { get; init; }
    /// <summary>coordinator or admin: the role this sign-in has on the LMS.</summary>
    public string Role { get; init; } = "coordinator";
    /// <summary>Windows Credential Manager target holding the email and password.</summary>
    public required string Target { get; init; }
    /// <summary>The browser profile it signs in with: each account keeps its own LMS sign-in.</summary>
    public required string Profile { get; init; }
}

/// <summary>
/// The LMS accounts (a coordinator's and an admin's, say) and which one the app uses now. Only
/// names, emails and where the passwords are: the passwords themselves stay in Windows Credential
/// Manager, one target per account. The sign-in saved before accounts existed becomes the first
/// account, keeping its target and its browser profile, so nothing has to be typed again.
/// File: %LOCALAPPDATA%\ZoomAutoAdmit\Lms\accounts.json.
/// </summary>
public sealed class LmsAccountDirectory(string? path = null)
{
    public const string LegacyTarget = "ZoomAutoAdmit/LMS/Dashboard";
    public const string LegacyProfile = "lms-dashboard";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly object Gate = new();

    public string Path { get; } = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Lms", "accounts.json");

    private sealed class Document
    {
        public string? Active { get; set; }
        public List<LmsAccountEntry> Accounts { get; set; } = [];
    }

    public IReadOnlyList<LmsAccountEntry> List() => Load().Accounts;

    /// <summary>The account in use; the legacy sign-in when nothing was chosen yet.</summary>
    public LmsAccountEntry Active()
    {
        var doc = Load();
        return doc.Accounts.FirstOrDefault(a => a.Id == doc.Active) ?? doc.Accounts.FirstOrDefault() ?? Legacy(email: "");
    }

    public void SetActive(string id)
    {
        lock (Gate)
        {
            var doc = Load();
            if (doc.Accounts.All(a => a.Id != id)) throw new ArgumentException("No such LMS account.");
            doc.Active = id;
            Save(doc);
        }
    }

    /// <summary>Adds (or updates, by email) an account; its password goes to its own credential target.</summary>
    public LmsAccountEntry Upsert(string label, string email, string password, string role = "coordinator", bool makeActive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        lock (Gate)
        {
            var doc = Load();
            var existing = doc.Accounts.FirstOrDefault(a => a.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase));
            string id = existing?.Id ?? NewId(email, doc.Accounts);
            var entry = new LmsAccountEntry
            {
                Id = id,
                Label = string.IsNullOrWhiteSpace(label) ? email.Trim() : label.Trim(),
                Email = email.Trim(),
                Role = role is "admin" ? "admin" : "coordinator",
                Target = existing?.Target ?? $"ZoomAutoAdmit/LMS/Account/{id}",
                Profile = existing?.Profile ?? $"lms-{id}",
            };
            new LmsCredentialStore(entry.Target).Save(new LmsAccount(entry.Email, password));
            doc.Accounts.RemoveAll(a => a.Id == id);
            doc.Accounts.Add(entry);
            if (makeActive || doc.Active == null) doc.Active = id;
            Save(doc);
            return entry;
        }
    }

    public void Remove(string id)
    {
        lock (Gate)
        {
            var doc = Load();
            var entry = doc.Accounts.FirstOrDefault(a => a.Id == id);
            if (entry == null) return;
            new LmsCredentialStore(entry.Target).Delete();
            doc.Accounts.Remove(entry);
            if (doc.Active == id) doc.Active = doc.Accounts.FirstOrDefault()?.Id;
            Save(doc);
        }
    }

    private Document Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(Path))
                    return JsonSerializer.Deserialize<Document>(File.ReadAllText(Path), Json) ?? new Document();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }

            // First time: the sign-in saved before accounts existed, if there is one.
            var doc = new Document();
            LmsAccount? legacy = null;
            try { legacy = new LmsCredentialStore(LegacyTarget).Read(); } catch { }
            if (legacy != null)
            {
                doc.Accounts.Add(Legacy(legacy.Email));
                doc.Active = "main";
                try { Save(doc); } catch { }
            }
            return doc;
        }
    }

    private static LmsAccountEntry Legacy(string email) => new()
    {
        Id = "main", Label = "Coordinator", Email = email, Role = "coordinator", Target = LegacyTarget, Profile = LegacyProfile,
    };

    private void Save(Document doc)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(doc, Json));
        File.Move(temporary, Path, overwrite: true);
    }

    private static string NewId(string email, IEnumerable<LmsAccountEntry> taken)
    {
        string stem = Regex.Replace(email.Split('@')[0].ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (stem.Length == 0) stem = "account";
        if (stem.Length > 24) stem = stem[..24];
        string id = stem;
        for (int n = 2; taken.Any(a => a.Id == id) || id == "main"; n++) id = $"{stem}-{n}";
        return id;
    }
}
