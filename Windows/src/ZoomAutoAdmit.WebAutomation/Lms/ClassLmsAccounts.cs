using System.Text.Json;

namespace ZoomAutoAdmit.WebAutomation.Lms;

/// <summary>Which coordinator a group belongs to, and the LMS sign-in its classes go up under.</summary>
public sealed record ClassLmsAccount
{
    public required string Group { get; init; }
    /// <summary>An id in <see cref="LmsAccountDirectory"/>: whose sign-in this group's steps use.</summary>
    public required string AccountId { get; init; }
    /// <summary>The coordinator's name, for the person reading a schedule or a class card.</summary>
    public string Coordinator { get; init; } = "";
    /// <summary>Their account on the central server, so a change there can be followed here.</summary>
    public string CoordinatorId { get; init; } = "";
    /// <summary>The Zoom account on this PC that opens their meetings (their own, not the admin's).</summary>
    public string? ZoomAccount { get; init; }
}

/// <summary>
/// One PC, several people's classes.
///
/// A class is run under the name of whoever owns it: the LMS shows the session as started by that
/// coordinator, the attendance is written by them, and the recording link goes up as theirs. The
/// app used to have a single LMS account "in use" for everything, which is right when a PC runs
/// its own classes and wrong the moment it runs somebody else's - the 19:00 class of one
/// coordinator and the 19:00 class of another would both go up under whoever happened to be
/// chosen.
///
/// So the sign-in is not a global setting any more: it is a property of the group. This file says
/// which account each group uses; everything that touches the LMS for a class asks here first and
/// falls back to the account in use for a group nobody claimed (this PC's own classes, as before).
///
/// Each account already keeps its own browser profile, and the profile lock is taken per profile,
/// so two coordinators' classes drive the LMS at the same time without waiting for each other or
/// signing each other out.
///
/// File: %LOCALAPPDATA%\ZoomAutoAdmit\Lms\class-accounts.json. No password is here - only which
/// account, whose it is, and which groups; the passwords stay in Windows Credential Manager.
/// </summary>
public sealed class ClassLmsAccounts(string? path = null, LmsAccountDirectory? directory = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly object Gate = new();
    private readonly LmsAccountDirectory _directory = directory ?? new LmsAccountDirectory();

    public string Path { get; } = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Lms", "class-accounts.json");

    private sealed class Document
    {
        public List<ClassLmsAccount> Groups { get; set; } = [];
    }

    public IReadOnlyList<ClassLmsAccount> List() => Load().Groups;

    /// <summary>The group's coordinator and account, or null when the group is this PC's own.</summary>
    public ClassLmsAccount? Find(string? group)
    {
        if (string.IsNullOrWhiteSpace(group)) return null;
        return Load().Groups.FirstOrDefault(g => g.Group.Equals(group.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every group of one coordinator, in the order they were given.</summary>
    public IReadOnlyList<ClassLmsAccount> Of(string coordinatorId) =>
        [.. Load().Groups.Where(g => g.CoordinatorId.Equals(coordinatorId, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// The groups one coordinator's account covers, as a whole set: a group they no longer have
    /// stops being theirs here too, so a class never goes up under somebody who lost it.
    /// </summary>
    public void SetGroups(string coordinatorId, string coordinator, string accountId, IEnumerable<string> groups, string? zoomAccount = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinatorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        lock (Gate)
        {
            var document = Load();
            document.Groups.RemoveAll(g => g.CoordinatorId.Equals(coordinatorId, StringComparison.OrdinalIgnoreCase));
            foreach (var group in groups.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // A group two coordinators both claim belongs to the one set last; it cannot be both.
                document.Groups.RemoveAll(g => g.Group.Equals(group, StringComparison.OrdinalIgnoreCase));
                document.Groups.Add(new ClassLmsAccount
                {
                    Group = group, AccountId = accountId, Coordinator = coordinator, CoordinatorId = coordinatorId, ZoomAccount = zoomAccount,
                });
            }
            Save(document);
        }
    }

    /// <summary>Stops running that coordinator's classes: their groups go back to this PC's own account.</summary>
    public void Forget(string coordinatorId)
    {
        lock (Gate)
        {
            var document = Load();
            if (document.Groups.RemoveAll(g => g.CoordinatorId.Equals(coordinatorId, StringComparison.OrdinalIgnoreCase)) > 0)
                Save(document);
        }
    }

    /// <summary>
    /// The sign-in a class's LMS steps use: its group's coordinator's, or - for a group nobody
    /// claimed - the account chosen in the app, exactly as before this existed.
    /// </summary>
    public LmsCredentialStore StoreFor(string? group)
    {
        if (Find(group) is not { } claim) return new LmsCredentialStore();
        var entry = _directory.List().FirstOrDefault(a => a.Id.Equals(claim.AccountId, StringComparison.OrdinalIgnoreCase));
        // The account was removed from this PC: the class still runs, under the account in use, and
        // says so rather than failing on a credential target that is not there.
        return entry == null ? new LmsCredentialStore() : new LmsCredentialStore(entry.Target, entry.Profile);
    }

    /// <summary>Whose name a class goes up under, for a log line or a column in the list.</summary>
    public string Whose(string? group) => Find(group)?.Coordinator ?? "";

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
            return new Document();
        }
    }

    private void Save(Document document)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, Json));
        File.Move(temporary, Path, overwrite: true);
    }
}
