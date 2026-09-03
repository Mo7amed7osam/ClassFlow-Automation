using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZoomAutoAdmit.SessionRoles;

public interface ISessionRoleStore
{
    SessionRoleDocument Load();
    void Save(SessionRoleDocument document);
}

/// <summary>
/// Independent storage for session roles: %LOCALAPPDATA%\ZoomAutoAdmit\Roles\session-roles.json.
/// It never reads or writes the roster, attendance or schedule stores.
/// </summary>
public sealed class JsonSessionRoleStore : ISessionRoleStore
{
    public const int MaxProfiles = 50;
    public const int MaxPeoplePerProfile = 100;
    public const int MaxHistory = 2000;
    private const long MaxFileBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string FilePath { get; }

    public JsonSessionRoleStore(string? filePath = null) => FilePath = Path.GetFullPath(filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Roles", "session-roles.json"));

    public SessionRoleDocument Load()
    {
        if (!File.Exists(FilePath)) return new SessionRoleDocument();
        if (new FileInfo(FilePath).Length > MaxFileBytes) throw new InvalidDataException("The session roles file exceeds its size limit.");
        using var stream = File.OpenRead(FilePath);
        var document = JsonSerializer.Deserialize<SessionRoleDocument>(stream, Json);
        if (document == null || document.SchemaVersion != 1 || document.Profiles == null || document.History == null)
            throw new InvalidDataException("Invalid session roles file. The original is preserved.");
        return Validate(document);
    }

    public void Save(SessionRoleDocument document)
    {
        var validated = Validate(document);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(validated, Json);
            if (payload.Length > MaxFileBytes) throw new InvalidDataException("The session roles file exceeds its size limit.");
            File.WriteAllBytes(temporary, payload);
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Trims and de-duplicates, and refuses shapes that would make matching ambiguous.</summary>
    public static SessionRoleDocument Validate(SessionRoleDocument document)
    {
        var profiles = new List<SessionRoleProfile>();
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in document.Profiles.Take(MaxProfiles))
        {
            string type = (profile.SessionType ?? "").Trim();
            if (type.Length is 0 or > 60) throw new InvalidDataException("A session type name is required, up to 60 characters.");
            var accounts = (profile.Accounts ?? []).Select(account => account.Trim())
                .Where(account => account.Length is > 0 and <= 80)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray();
            // The same type may be set up once per group - Technical for S7 and Technical for S8 -
            // because the account decides which one a meeting gets. Two of a type bound to the same
            // accounts is the shape that cannot be told apart, and that is what is refused.
            string identity = type + "\u0000" + string.Join(",", accounts.OrderBy(account => account, StringComparer.OrdinalIgnoreCase));
            if (!types.Add(identity)) throw new InvalidDataException(accounts.Length == 0
                ? $"Duplicate session type: {type}."
                : $"Duplicate session type for the same account: {type}.");
            var people = new List<RolePerson>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var person in (profile.People ?? []).Take(MaxPeoplePerProfile))
            {
                string name = (person.Name ?? "").Trim();
                if (name.Length is 0 or > 120) continue;
                if (!names.Add(name)) continue;
                people.Add(person with
                {
                    Name = name,
                    Aliases = (person.Aliases ?? []).Select(alias => alias.Trim())
                        .Where(alias => alias.Length is > 0 and <= 120)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray()
                });
            }
            profiles.Add(profile with
            {
                SessionType = type,
                Keywords = (profile.Keywords ?? []).Select(keyword => keyword.Trim())
                    .Where(keyword => keyword.Length is > 0 and <= 60)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray(),
                Accounts = accounts,
                People = people
            });
        }
        var history = (document.History ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.SessionType) && !string.IsNullOrWhiteSpace(entry.PersonName) &&
                            !string.IsNullOrWhiteSpace(entry.ObservedName) && entry.Confidence is >= 0 and <= 100)
            .OrderByDescending(entry => entry.AssignedAt)
            .Take(MaxHistory)
            .ToList();
        return new SessionRoleDocument { SchemaVersion = 1, Profiles = profiles, History = history };
    }
}
