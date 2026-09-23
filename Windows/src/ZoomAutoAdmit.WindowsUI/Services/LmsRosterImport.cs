using ZoomAutoAdmit.AttendanceMatching;
using ZoomAutoAdmit.Roster;
using ZoomAutoAdmit.WebAutomation.Lms;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>What bringing a group's students from the LMS did.</summary>
public sealed record LmsRosterImportResult(bool Ok, string Message, string Group, IReadOnlyList<string> Names, int Added = 0);

/// <summary>
/// A group's roster from the LMS: its students are read from one of its sessions (this month's
/// first) and saved as that group's roster. Students already on the roster keep their place and
/// their spelling; only new names are added, in the LMS's order, and nobody is removed.
///
/// It signs in as whoever the group belongs to - the coordinator whose class it is, or this PC's
/// own account for its own groups. A PC that runs several people's classes therefore fetches each
/// group's students from the account that can actually see them, instead of asking one account for
/// a group it was never given.
/// </summary>
public sealed class LmsRosterImport(IGroupRosterService? rosters = null, Func<string?, LmsSessionRunner>? runner = null)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly IGroupRosterService _rosters = rosters ?? new GroupRosterStore(log: ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Info);
    private readonly ClassLmsAccounts _classes = new();
    private readonly Func<string?, LmsSessionRunner> _runner =
        runner ?? (group => new LmsSessionRunner(new ClassLmsAccounts().StoreFor(group)));

    /// <summary>A group's roster changed here; pages that show rosters read them again.</summary>
    public static event Action<string>? Changed;

    public static bool IsBusy => Gate.CurrentCount == 0;

    /// <summary>
    /// The groups that are this person's: a dashboard coordinator's own groups; when the LMS account
    /// in use is a coordinator's, the groups of its timetable and of what the LMS showed that account;
    /// otherwise (the admin) every group the app knows - rosters, the timetable, the server's list.
    /// A roster read earlier with another account is therefore not shown as this person's.
    /// </summary>
    public static IReadOnlyList<string> GroupsFor(ViewModels.MainViewModel? main)
    {
        var names = new List<string>();
        if (main != null)
        {
            var me = main.Central.IsSignedIn ? main.Central.Api.Me : null;
            var lms = main.LmsSessions.SelectedAccount;
            if (me != null && !me.AllGroups)
                names.AddRange((me.Groups ?? []).Where(g => !g.Archived).Select(g => g.Name));
            else if (lms is { Role: "coordinator" })
            {
                names.AddRange(main.LmsSessions.Rows.Where(r => r.Zoom != "—").Select(r => r.Group));     // this PC's timetable
                try
                {
                    names.AddRange(new LmsSessionCache().Read()
                        .Where(e => e.Account.Equals(lms.Email, StringComparison.OrdinalIgnoreCase)).Select(e => e.Session.Group));
                }
                catch { }
            }
            else
            {
                names.AddRange(main.Roster.Groups.Select(g => g.GroupId));
                names.AddRange(main.LmsSessions.Rows.Select(r => r.Group));
                if (main.Central.IsAdmin) names.AddRange(main.Central.Groups.Where(g => !g.Archived).Select(g => g.Group));
            }
            // The groups of the coordinators whose classes this PC runs. Each is read with that
            // coordinator's own sign-in, so they belong on the list even though they are not this
            // account's own groups.
            try { names.AddRange(new ClassLmsAccounts().List().Select(c => c.Group)); } catch { }
        }
        return [.. names.Where(n => !string.IsNullOrWhiteSpace(n) && !n.Contains(" | ")).Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<LmsRosterImportResult> ImportAsync(string group, CancellationToken token = default)
    {
        group = (group ?? "").Trim();
        if (group.Length == 0) return new(false, "Choose a group first.", group, []);
        if (!await Gate.WaitAsync(0, token))
            return new(false, "A roster is already being read from the LMS; wait for it to finish.", group, []);
        try
        {
            string whose = _classes.Whose(group);
            if (whose.Length > 0)
                ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Info($"[LMS] {group}: reading its students as {whose}, who the group belongs to.");
            var read = await _runner(group).ReadRosterAsync(group, cancellationToken: token);
            if (!read.IsSuccess && whose.Length > 0)
            {
                // Reading students changes nothing on the LMS, so when the owner's sign-in cannot
                // do it (a password that stopped working, say) this PC's own account - normally the
                // admin, who sees every group - is asked instead, rather than taking attendance
                // against no roster at all.
                ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Warn(
                    $"[LMS] {group}: {whose}'s account could not read its students ({read.Message}); trying this PC's own account.");
                var own = await _runner(null).ReadRosterAsync(group, cancellationToken: token);
                if (own.IsSuccess)
                    return await SaveAsync(group, own.Names, $"{own.Message} (read with this PC's own account: {read.Message})", token);
            }
            if (!read.IsSuccess) return new(false, read.Message, group, []);
            return await SaveAsync(group, read.Names, read.Message, token);
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Adds the names the group's roster does not have yet. A name counts as already there when it
    /// is the same person the way attendance matching decides it (the same name, or three or more
    /// of its first names), or differs from a roster name by a small typo in one word; the LMS
    /// spelling is then kept as one of that student's other names, which helps matching later.
    /// </summary>
    public async Task<LmsRosterImportResult> SaveAsync(string group, IReadOnlyList<string> names, string readMessage = "", CancellationToken token = default)
    {
        var existing = (await _rosters.ListAsync(token)).FirstOrDefault(g => g.GroupId.Equals(group, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            await _rosters.CreateAsync(group, group, token);
            existing = (await _rosters.ListAsync(token)).First(g => g.GroupId.Equals(group, StringComparison.OrdinalIgnoreCase));
        }

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var spellings = new Dictionary<string, string>(StringComparer.Ordinal);      // student id -> LMS spelling
        int order = existing.Students.Select(s => s.Order).DefaultIfEmpty(0).Max();
        var added = new List<GroupStudent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in names)
        {
            string name = raw.Trim();
            string key = NameNormalizer.Normalize(name);
            if (key.Length == 0 || !seen.Add(key)) continue;
            var same = existing.Students.Where(s => !claimed.Contains(s.StudentId)).FirstOrDefault(s => IsSameStudent(s, name))
                       ?? added.FirstOrDefault(s => IsSameStudent(s, name));
            if (same != null)
            {
                claimed.Add(same.StudentId);
                bool spelledAlike = same.Aliases.Append(same.FullName).Any(n => NameNormalizer.Normalize(n) == key);
                if (!spelledAlike && existing.Students.Contains(same)) spellings[same.StudentId] = name;
                continue;
            }
            added.Add(new GroupStudent(Guid.NewGuid().ToString("D"), existing.GroupId, ++order, name, []));
        }
        if (added.Count > 0) await _rosters.AddStudentsAsync(existing, added, token);
        foreach (var (id, spelling) in spellings)
        {
            var current = (await _rosters.ListAsync(token)).First(g => g.GroupId == existing.GroupId);
            var student = current.Students.First(s => s.StudentId == id);
            await _rosters.UpdateStudentAsync(current, student with { Aliases = [.. student.Aliases, spelling] }, token);
        }

        int notOnLms = existing.Students.Count(s => !claimed.Contains(s.StudentId));
        string message = (readMessage.Length > 0 ? readMessage + " " : "") +
                         (added.Count == 0 ? "The roster already had all of them." : $"{added.Count} added to the {existing.GroupId} roster.") +
                         (spellings.Count > 0 ? $" {spellings.Count} spelled differently on the LMS; that spelling was kept as their other name." : "") +
                         (notOnLms > 0 ? $" {notOnLms} on the roster are not on the LMS list; they were kept." : "");
        try { Changed?.Invoke(existing.GroupId); } catch { }
        return new(true, message, existing.GroupId, names, added.Count);
    }

    /// <summary>The same person: matched as attendance matches (full name, or a 3+ name prefix either way), or one small typo apart.</summary>
    public static bool IsSameStudent(GroupStudent student, string name)
    {
        foreach (string known in student.Aliases.Append(student.FullName))
        {
            if (string.IsNullOrWhiteSpace(known)) continue;
            if (RuleBasedNameMatcher.Evaluate(student with { FullName = known }, name).Confidence >= 98) return true;
            if (RuleBasedNameMatcher.Evaluate(student with { FullName = name }, known).Confidence >= 98) return true;
            if (OneTypoApart(NameNormalizer.Tokens(known), NameNormalizer.Tokens(name))) return true;
        }
        return false;
    }

    /// <summary>Three or more words, all equal but one, and that one at most two letters different.</summary>
    private static bool OneTypoApart(string[] a, string[] b)
    {
        if (a.Length != b.Length || a.Length < 3) return false;
        int different = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            if (++different > 1 || Distance(a[i], b[i]) > 2 || Math.Min(a[i].Length, b[i].Length) < 4) return false;
        }
        return different == 1;
    }

    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int previous = row[0]; row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int kept = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = kept;
            }
        }
        return row[b.Length];
    }
}
