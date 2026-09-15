namespace ZoomAutoAdmit.Roster;

public sealed record GroupStudent(string StudentId, string GroupId, int Order, string FullName,
    IReadOnlyList<string> Aliases, string? Email = null);

public sealed record RosterGroup(string GroupId, string DisplayName, DateTimeOffset CreatedAt,
    IReadOnlyList<GroupStudent> Students, long Revision = 0);

public interface IGroupRosterService
{
    Task<IReadOnlyList<RosterGroup>> ListAsync(CancellationToken token = default);
    Task CreateAsync(string groupId, string displayName, CancellationToken token = default);
    Task RenameAsync(RosterGroup expected, string displayName, CancellationToken token = default);
    Task DeleteAsync(RosterGroup expected, CancellationToken token = default);
    Task AddStudentAsync(RosterGroup expected, GroupStudent student, CancellationToken token = default);
    /// <summary>Several students in one save (a roster read from the LMS).</summary>
    Task AddStudentsAsync(RosterGroup expected, IReadOnlyList<GroupStudent> students, CancellationToken token = default);
    Task UpdateStudentAsync(RosterGroup expected, GroupStudent student, CancellationToken token = default);
    Task DeleteStudentAsync(RosterGroup expected, string studentId, CancellationToken token = default);
    Task ReorderAsync(RosterGroup expected, IReadOnlyList<string> orderedStudentIds, CancellationToken token = default);
    Task<int> ImportAsync(RosterGroup expected, string path, CancellationToken token = default);
}
