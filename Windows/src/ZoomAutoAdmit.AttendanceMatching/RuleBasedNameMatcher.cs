using ZoomAutoAdmit.Roster;

namespace ZoomAutoAdmit.AttendanceMatching;

public sealed record RuleMatch(int Confidence, string Reason);

public static class RuleBasedNameMatcher
{
    public static RuleMatch Evaluate(GroupStudent student, string observedName)
    {
        var roster = NameNormalizer.Tokens(student.FullName);
        var observed = NameNormalizer.Tokens(observedName);
        if (roster.Length == 0 || observed.Length == 0) return new(0, "No comparable names.");
        bool first = roster[0] == observed[0];
        bool family = roster[^1] == observed[^1];
        if (observed.Length == 1)
            return first ? new(55, "First name only; identity is incomplete.") :
                family ? new(35, "Family name only; insufficient evidence.") :
                roster.Contains(observed[0]) ? new(25, "One middle name only.") : new(0, "No name overlap.");
        if (NameNormalizer.HasMultipleNames(observedName))
        {
            if (roster.SequenceEqual(observed)) return new(100, "Exact normalized full name.");
            if (roster.Length >= observed.Length && roster.Take(observed.Length).SequenceEqual(observed))
                return new(observed.Length >= 3 ? 98 : 95, "Complete observed first two/three-name prefix.");
        }
        if (first && observed.Length == 2 && family) return new(88, "First and family names; middle names omitted.");
        if (first && roster.Length > 1 && observed[1] == roster[1])
            return new(75, "First and second names agree but other tokens conflict.");
        if (first) return new(60, "First name agrees; other names need review.");
        if (roster.Length > 1 && observed.Contains(roster[1])) return new(45, "Second-name evidence without matching first name.");
        return family ? new(35, "Family name only; insufficient evidence.") :
            observed.Any(roster.Contains) ? new(25, "Middle-name overlap only.") : new(0, "No name overlap.");
    }
}
