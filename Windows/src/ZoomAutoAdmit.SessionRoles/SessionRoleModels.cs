using System.Text.Json.Serialization;

namespace ZoomAutoAdmit.SessionRoles;

public enum SessionRole { Instructor, CoHost }

/// <summary>One configured person for a session type. Aliases are the Zoom display names already seen.</summary>
public sealed record RolePerson(string Name, SessionRole Role)
{
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

/// <summary>Reusable profile per session type: who teaches it and who may be made co-host.</summary>
public sealed record SessionRoleProfile(string SessionType)
{
    /// <summary>Words matched against the schedule name to recognise this session type.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];
    /// <summary>Account/group IDs that always mean this session type, whatever the schedule name says.</summary>
    public IReadOnlyList<string> Accounts { get; init; } = [];
    public IReadOnlyList<RolePerson> People { get; init; } = [];
    [JsonIgnore] public IEnumerable<RolePerson> Instructors => People.Where(person => person.Role == SessionRole.Instructor);
    [JsonIgnore] public IEnumerable<RolePerson> CoHosts => People.Where(person => person.Role == SessionRole.CoHost);
    /// <summary>Which meetings this profile covers, for a list that may hold one type per group.</summary>
    [JsonIgnore] public string Scope => Accounts.Count == 0 ? "every meeting" : string.Join(", ", Accounts);
}

/// <summary>A remembered successful assignment: the bridge between a Zoom display name and a configured person.</summary>
public sealed record RoleAssignment(
    string SessionType,
    string PersonName,
    string ObservedName,
    SessionRole Role,
    DateTimeOffset AssignedAt,
    int Confidence,
    bool Approved);

public sealed record SessionRoleDocument
{
    public int SchemaVersion { get; init; } = 1;
    public List<SessionRoleProfile> Profiles { get; init; } = [];
    public List<RoleAssignment> History { get; init; } = [];
}

/// <summary>How a Zoom display name was recognised. AI is deliberately absent here: it can only propose.</summary>
public enum RoleMatchSource { ConfiguredName, Alias, PreviousAssignment, NameRule }

public sealed record RoleMatch(RolePerson Person, RoleMatchSource Source, int Confidence);
