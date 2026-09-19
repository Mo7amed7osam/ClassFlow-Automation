namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>
/// What the person signed in to this copy of the app is allowed to see on its pages.
///
/// One PC is shared: the admin signs in, a coordinator signs in after them, and everything the app
/// keeps - Zoom accounts, rosters, session roles, classes, attendance - lives in one place under
/// %LOCALAPPDATA%. Left alone, a coordinator opens Accounts and finds the admin's Zoom accounts,
/// and Groups &amp; Students and finds groups that were never theirs.
///
/// Splitting those files per account would be the wrong fix: a scheduled class opens in its own
/// process with nobody signed in, and it has to find every class this PC runs, whoever owns it. So
/// the files stay as they are and the pages narrow instead. Everything here is keyed by group -
/// a Zoom account hosts one, a roster is one, a session role names them, a class belongs to one -
/// and the server already knows which groups are whose.
///
/// The rule, in one place:
///   nobody signed in  -> everything, exactly as a PC that only ever ran its own classes behaved
///   an admin          -> everything
///   a coordinator     -> the groups they were given, and nothing else
/// </summary>
public sealed class SignedInScope(Func<CentralMe?> signedIn)
{
    private readonly Func<CentralMe?> _signedIn = signedIn;

    /// <summary>Raised when the signed-in person changes, so the pages narrow again.</summary>
    public event Action? Changed;

    public CentralMe? Me => _signedIn();
    public bool IsSignedIn => Me != null;
    public bool IsAdmin => Me?.IsAdmin == true;

    /// <summary>
    /// True when this person sees every group: nobody is signed in (this PC's own work), or the
    /// admin is, or a coordinator whose account says so.
    /// </summary>
    public bool SeesEverything => Me is not { } me || me.IsAdmin || me.AllGroups;

    /// <summary>The groups they were given, by name. Empty when they see everything.</summary>
    public IReadOnlyCollection<string> Groups =>
        SeesEverything
            ? []
            : (Me?.Groups ?? []).Where(group => !group.Archived).Select(group => group.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a group - or an account or roster named after one - is this person's.</summary>
    public bool Owns(string? group)
    {
        if (SeesEverything) return true;
        if (string.IsNullOrWhiteSpace(group)) return false;
        return Groups.Contains(group.Trim());
    }

    /// <summary>
    /// Whether any of several names is theirs. A thing that names no group at all - a session role
    /// profile that covers every meeting, say - applies to theirs too, so it is shown.
    /// </summary>
    public bool OwnsAny(IEnumerable<string>? groups)
    {
        if (SeesEverything) return true;
        var named = (groups ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        return named.Length == 0 || named.Any(Owns);
    }

    /// <summary>What a page says when it is showing less than this PC holds.</summary>
    public string Narrowed(int shown, int all, string what) =>
        SeesEverything || shown == all
            ? ""
            : $"Showing the {shown} {what} of your groups; {all - shown} on this PC belong to somebody else.";

    /// <summary>The signed-in person changed: every page that narrows should look again.</summary>
    public void Refresh() => Changed?.Invoke();
}
