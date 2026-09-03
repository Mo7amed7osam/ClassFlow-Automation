using ZoomAutoAdmit.AttendanceMatching;
using ZoomAutoAdmit.Roster;

namespace ZoomAutoAdmit.SessionRoles;

/// <summary>
/// Recognises a Zoom display name as one of the people configured for this session type.
/// Order: configured name, then approved alias, then a previous successful assignment.
/// AI is not consulted here — it may only propose a candidate for a human to approve, and it can
/// never introduce someone who is not already on the profile.
/// </summary>
public static class RoleMatcher
{
    public static RoleMatch? Match(string observedName, SessionRoleProfile profile, IEnumerable<RoleAssignment> history)
    {
        string observed = NameNormalizer.Normalize(observedName ?? "");
        if (observed.Length == 0) return null;

        foreach (var person in profile.People)
            if (NameNormalizer.Normalize(person.Name) == observed)
                return new(person, RoleMatchSource.ConfiguredName, 100);

        foreach (var person in profile.People)
            foreach (var alias in person.Aliases)
                if (NameNormalizer.Normalize(alias) == observed)
                    return new(person, RoleMatchSource.Alias, 98);

        foreach (var entry in history)
        {
            if (!entry.Approved) continue;
            if (!string.Equals(entry.SessionType, profile.SessionType, StringComparison.OrdinalIgnoreCase)) continue;
            if (NameNormalizer.Normalize(entry.ObservedName) != observed) continue;
            var person = profile.People.FirstOrDefault(candidate =>
                NameNormalizer.Normalize(candidate.Name) == NameNormalizer.Normalize(entry.PersonName));
            // A remembered assignment only counts while that person is still on the profile.
            if (person != null) return new(person, RoleMatchSource.PreviousAssignment, entry.Confidence);
        }

        // Then the shortened-or-decorated forms of the same name.
        var contained = ByContainedName(observedName, profile);
        if (contained != null) return contained;

        // Last: the Zoom display name often carries the configured name plus extra words
        // ("Mohab Mohamed __Coordinator"). The existing rule matcher scores that case; only a
        // near-certain score counts, and a single first name never qualifies.
        return ByNameRule(observedName, profile);
    }

    public const int RuleThreshold = 90;

    /// <summary>
    /// Accepts a Zoom name that is the configured name with parts left out or extra words added,
    /// and only that.
    ///
    /// "Mohab Mohamed" configured:
    ///   "Mohab"                      -> yes, every word given belongs to the configured name
    ///   "mo7ab"                      -> yes, the same word once written out (7 reads as h)
    ///   "Mohab Mohamed __Coordinator"-> yes, the configured name is there in full
    ///   "Mohab Ahmed"                -> no. "Ahmed" is not part of the configured name, so this
    ///                                   is somebody else and the question goes to the AI.
    ///
    /// A shortened name is only accepted when exactly one configured person can own it: with both
    /// "Mohab Mohamed" and "Mohab Ahmed" on the profile, a bare "Mohab" is ambiguous and is left
    /// to the AI rather than guessed.
    /// </summary>
    private static RoleMatch? ByContainedName(string observedName, SessionRoleProfile profile)
    {
        var observed = NameNormalizer.Tokens(observedName ?? string.Empty);
        if (observed.Length == 0) return null;

        var candidates = new List<(RolePerson Person, int Confidence)>();
        foreach (var person in profile.People)
        {
            var configured = NameNormalizer.Tokens(person.Name);
            if (configured.Length == 0) continue;

            // The configured name in full, plus whatever Zoom decorates it with. Two words at
            // least: a lone configured first name inside a longer Zoom name is not an identity.
            if (configured.Length >= 2 && IsOrderedSubsequence(configured, observed))
            {
                candidates.Add((person, 96));
                continue;
            }
            // A shortened form: every word given is part of the configured name, in that order.
            if (IsOrderedSubsequence(observed, configured))
                candidates.Add((person, observed.Length >= 2 ? 94 : 88));
        }

        if (candidates.Count != 1) return null;
        return new RoleMatch(candidates[0].Person, RoleMatchSource.NameRule, candidates[0].Confidence);
    }

    /// <summary>
    /// Every word of <paramref name="inner"/> appears in <paramref name="outer"/>, in that order.
    /// Order matters because a family name and a given name swap places between people:
    /// "Mohamed Mohab" is not "Mohab Mohamed".
    /// </summary>
    private static bool IsOrderedSubsequence(string[] inner, string[] outer)
    {
        if (inner.Length == 0 || inner.Length > outer.Length) return false;
        int index = 0;
        foreach (string word in outer)
        {
            if (index < inner.Length && string.Equals(word, inner[index], StringComparison.Ordinal)) index++;
        }
        return index == inner.Length;
    }

    /// <summary>
    /// The people worth asking the AI about: those whose name already overlaps the Zoom name.
    /// The AI only ever confirms or rejects one of these — it can never introduce someone else.
    /// </summary>
    public static IReadOnlyList<RolePerson> Suspects(string observedName, SessionRoleProfile profile, int limit = 5)
    {
        var scored = profile.People
            .Select(person => (person, score: RuleBasedNameMatcher.Evaluate(
                new GroupStudent("role", profile.SessionType, 0, observedName, []), person.Name).Confidence))
            .Where(pair => pair.score >= 25)
            .OrderByDescending(pair => pair.score)
            .Select(pair => pair.person)
            .Take(limit)
            .ToArray();
        // No overlap at all: still offer the configured people, but never anybody outside the profile.
        return scored.Length > 0 ? scored : profile.People.Take(limit).ToArray();
    }

    private static RoleMatch? ByNameRule(string observedName, SessionRoleProfile profile)
    {
        RoleMatch? best = null;
        foreach (var person in profile.People)
        {
            if (NameNormalizer.Tokens(person.Name).Length < 2) continue;   // "mohab" alone is not an identity.
            // The observed Zoom name is the fuller side, so it plays the roster role here.
            var evaluation = RuleBasedNameMatcher.Evaluate(
                new GroupStudent("role", profile.SessionType, 0, observedName, []), person.Name);
            if (evaluation.Confidence < RuleThreshold) continue;
            if (best == null || evaluation.Confidence > best.Confidence)
                best = new RoleMatch(person, RoleMatchSource.NameRule, evaluation.Confidence);
        }
        return best;
    }

    /// <summary>Records what was assigned so the next session matches without asking anything.</summary>
    public static SessionRoleDocument Remember(SessionRoleDocument document, string sessionType, RoleMatch match, string observedName)
    {
        var history = document.History.ToList();
        history.RemoveAll(entry =>
            string.Equals(entry.SessionType, sessionType, StringComparison.OrdinalIgnoreCase) &&
            NameNormalizer.Normalize(entry.ObservedName) == NameNormalizer.Normalize(observedName));
        history.Insert(0, new RoleAssignment(sessionType, match.Person.Name, observedName.Trim(), match.Person.Role,
            DateTimeOffset.Now, match.Confidence, Approved: true));
        return document with { History = history };
    }
}
