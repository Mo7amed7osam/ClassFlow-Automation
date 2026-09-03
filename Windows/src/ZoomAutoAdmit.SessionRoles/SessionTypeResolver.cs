using ZoomAutoAdmit.AttendanceMatching;

namespace ZoomAutoAdmit.SessionRoles;

/// <summary>
/// Recognises the session type from the schedule name (imported names look like
/// "CAI5_AIS4_S7 • 27 • Intro to Python"). Longest keyword wins.
///
/// Session types are optional. A profile that names no keywords and no accounts is the one to use
/// for every meeting, which is all most people need: list the instructors once and they are made
/// co-host wherever they appear. Types only matter when different sessions need different people.
/// </summary>
public static class SessionTypeResolver
{
    /// <summary>Account/group binding wins over keywords: it is explicit and cannot be misread.</summary>
    public static SessionRoleProfile? Resolve(string? scheduleName, string? accountId, IEnumerable<SessionRoleProfile> profiles)
    {
        var all = profiles as IReadOnlyList<SessionRoleProfile> ?? profiles.ToArray();
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            var bound = all.Where(profile => profile.Accounts.Any(account =>
                string.Equals(account.Trim(), accountId.Trim(), StringComparison.OrdinalIgnoreCase))).ToArray();
            if (bound.Length == 1) return bound[0];
            // Several profiles claim this account: the schedule name decides between them.
            if (bound.Length > 1) return Resolve(scheduleName, bound) ?? null;
        }
        return Resolve(scheduleName, all) ?? EveryMeetingProfile(all);
    }

    /// <summary>The profile that applies whenever nothing more specific does.</summary>
    public static SessionRoleProfile? EveryMeetingProfile(IEnumerable<SessionRoleProfile> profiles) =>
        profiles.FirstOrDefault(profile =>
            profile.Keywords.All(string.IsNullOrWhiteSpace) &&
            profile.Accounts.All(string.IsNullOrWhiteSpace) &&
            profile.People.Count > 0);

    public static SessionRoleProfile? Resolve(string? scheduleName, IEnumerable<SessionRoleProfile> profiles)
    {
        if (string.IsNullOrWhiteSpace(scheduleName)) return null;
        string haystack = NameNormalizer.Normalize(scheduleName);
        if (haystack.Length == 0) return null;
        SessionRoleProfile? best = null;
        int bestLength = 0;
        foreach (var profile in profiles)
        {
            foreach (var keyword in profile.Keywords)
            {
                string needle = NameNormalizer.Normalize(keyword);
                if (needle.Length == 0 || !haystack.Contains(needle, StringComparison.Ordinal)) continue;
                if (needle.Length <= bestLength) continue;
                best = profile;
                bestLength = needle.Length;
            }
        }
        return best;
    }
}
