using ZoomAutoAdmit.AttendanceMatching;

namespace ZoomAutoAdmit.WebAutomation.Lms;

/// <summary>What one student's row on the dashboard is going to be set to.</summary>
public sealed record LmsAttendanceMark(string StudentName, bool Joined);

/// <summary>
/// What an upload is about to do, decided before anything is pressed. It exists so the whole
/// decision can be looked at - and shown to the person - without the dashboard being touched.
/// </summary>
public sealed record LmsAttendancePlan(
    IReadOnlyList<LmsAttendanceMark> Marks,
    IReadOnlyList<string> NotOnTheDashboard)
{
    public int JoinedCount => Marks.Count(mark => mark.Joined);
    public int NotJoinedCount => Marks.Count(mark => !mark.Joined);

    public string Summary =>
        $"{JoinedCount} joined, {NotJoinedCount} not-joined" +
        (NotOnTheDashboard.Count == 0
            ? string.Empty
            : $", {NotOnTheDashboard.Count} the dashboard does not list ({string.Join(", ", NotOnTheDashboard.Take(5))}" +
              $"{(NotOnTheDashboard.Count > 5 ? ", ..." : string.Empty)})");

    /// <summary>
    /// Decides every row from the names the app saw in the meeting. The dashboard's own list is
    /// the one that counts: a student it does not list cannot be marked, and a student it lists
    /// who was never seen is Not-joined rather than left blank, because a blank row is not an
    /// answer. Names are compared the way the matcher compares them, not character by character.
    /// </summary>
    /// <param name="everyone">
    /// Mark every listed student Joined, whatever the names say. Said out loud as its own flag so
    /// that an empty list of names can never be mistaken for "everybody came".
    /// </param>
    public static LmsAttendancePlan Build(
        IReadOnlyList<string> studentsOnTheDashboard,
        IReadOnlyCollection<string> present,
        bool everyone = false)
    {
        ArgumentNullException.ThrowIfNull(studentsOnTheDashboard);
        ArgumentNullException.ThrowIfNull(present);
        if (everyone)
            return new LmsAttendancePlan(
                [.. studentsOnTheDashboard.Select(student => new LmsAttendanceMark(student, true))], []);

        var joined = present
            .Select(NameNormalizer.Normalize)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var marks = new List<LmsAttendanceMark>(studentsOnTheDashboard.Count);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var student in studentsOnTheDashboard)
        {
            string normalized = NameNormalizer.Normalize(student);
            bool wasSeen = joined.Contains(normalized);
            if (wasSeen) claimed.Add(normalized);
            marks.Add(new LmsAttendanceMark(student, wasSeen));
        }

        // Anyone the app marked present who has no row: worth saying out loud rather than losing.
        var missing = present
            .Where(name => !claimed.Contains(NameNormalizer.Normalize(name)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new LmsAttendancePlan(marks, missing);
    }
}
