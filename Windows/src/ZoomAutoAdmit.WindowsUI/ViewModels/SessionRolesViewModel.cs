using System.Collections.ObjectModel;
using System.Windows.Input;
using ZoomAutoAdmit.SessionRoles;
using ZoomAutoAdmit.WindowsUI.Infrastructure;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

/// <summary>
/// Editing surface for the session role profiles: who teaches each session type and who may be
/// made co-host. Nothing here touches a meeting; it only reads and writes the roles file.
/// </summary>
public sealed class SessionRolesViewModel : ObservableObject
{
    private readonly ISessionRoleStore _store;
    private SessionRoleDocument _document = new();
    private SessionRoleProfile? _selected;
    private string _typeName = string.Empty;
    private string _keywords = string.Empty;
    private string _accounts = string.Empty;
    private string _coHosts = string.Empty;
    private string _status = "Pick a session type, then list who may be made co-host in it.";
    private readonly Services.SignedInScope _scope;

    public SessionRolesViewModel(ISessionRoleStore? store = null, Services.SignedInScope? scope = null)
    {
        _store = store ?? new JsonSessionRoleStore();
        // One PC holds everybody's session types; a coordinator sees the ones that name a group of
        // theirs, and the ones that name no group at all because those cover their meetings too.
        _scope = scope ?? new Services.SignedInScope(() => null);
        _scope.Changed += () => { Load(); PublishQuietly(); };
        NewCommand = new RelayCommand(_ => ClearEditor());
        SaveCommand = new RelayCommand(_ => Save());
        DeleteCommand = new RelayCommand(_ => Delete());
        ReloadCommand = new RelayCommand(_ => Load());
        Load();
    }

    /// <summary>
    /// Sends the profiles to the central server, where a cloud worker reads them to make the same
    /// instructor co-host. Set by the shell; called after every save and when the admin signs in.
    /// Only the admin may change shared settings, so nobody else's copy is sent.
    /// </summary>
    public Func<IReadOnlyList<SessionRoleProfile>, Task>? Publish { get; set; }

    private void PublishQuietly()
    {
        if (Publish is not { } publish || !_scope.IsAdmin) return;
        var profiles = _document.Profiles.ToArray();
        _ = Task.Run(async () =>
        {
            // A server that is away for a moment gets the list at the next save or sign-in; the
            // profiles on this PC are what this PC uses either way.
            try { await publish(profiles); }
            catch (Exception ex) { ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Info($"[ROLE] The server did not take the session roles: {ex.Message}"); }
        });
    }

    /// <summary>
    /// A saved profile, so the shell can put it on the desktop: the type and the group it was
    /// saved for. Nothing depends on it being handled.
    /// </summary>
    public event Action<string, string>? ProfileSaved;

    public ObservableCollection<SessionRoleProfile> Profiles { get; } = [];
    /// <summary>What this PC is showing less of, and why. Empty when the whole list is shown.</summary>
    public string ScopeNote { get => _scopeNote; private set => SetProperty(ref _scopeNote, value); }
    private string _scopeNote = string.Empty;
    public ObservableCollection<RoleAssignment> History { get; } = [];
    public SessionRoleProfile? SelectedProfile
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value) || value == null) return;
            TypeName = value.SessionType;
            Keywords = string.Join(", ", value.Keywords);
            Accounts = string.Join(", ", value.Accounts);
            // Everyone the profile knows about goes in the one list: whatever role an older file
            // gave them, they are all people this session may hand co-host to.
            CoHosts = FormatPeople(value.People);
            StatusMessage = $"Editing {value.SessionType}: {value.People.Count} person(s) who may be made co-host.";
        }
    }
    public string TypeName
    {
        get => _typeName;
        set
        {
            string previous = _typeName;
            if (!SetProperty(ref _typeName, value)) return;
            // The type is nearly always the word to look for in the schedule name as well, so it
            // is filled in there too - but never over a keyword list written by hand.
            string picked = (value ?? string.Empty).Trim();
            if (picked.Length > 0 &&
                (Keywords.Trim().Length == 0 || string.Equals(Keywords.Trim(), previous.Trim(), StringComparison.OrdinalIgnoreCase)))
                Keywords = picked;
        }
    }
    public string Keywords { get => _keywords; set => SetProperty(ref _keywords, value); }
    /// <summary>Account/group IDs this profile always applies to, whatever the schedule name says.</summary>
    public string Accounts { get => _accounts; set => SetProperty(ref _accounts, value); }
    /// <summary>The saved accounts, offered in the account box so IDs are never mistyped.</summary>
    public ObservableCollection<string> AvailableAccounts { get; } = [];
    public string CoHosts { get => _coHosts; set => SetProperty(ref _coHosts, value); }

    /// <summary>
    /// The session types on offer: the ones the dashboard runs, plus every type already saved
    /// here. The box stays typeable, so a type nobody listed yet is simply written in.
    /// </summary>
    public static readonly string[] DashboardSessionTypes =
        ["Technical", "English", "Soft Skill", "Freelancing", "Coach"];
    public ObservableCollection<string> SessionTypeOptions { get; } = [];
    public string StatusMessage { get => _status; private set => SetProperty(ref _status, value); }
    public string StorePath => (_store as JsonSessionRoleStore)?.FilePath ?? "session-roles.json";
    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ReloadCommand { get; }

    public void Load()
    {
        try
        {
            _document = _store.Load();
            RefreshLists();
            StatusMessage = Profiles.Count == 0
                ? "No session types yet. Add one — for example Technical — then who may be made co-host in it."
                : $"Loaded {Profiles.Count} session type(s) and {History.Count} remembered assignment(s).";
        }
        catch (Exception ex) { StatusMessage = "Could not read the session roles file: " + ex.Message; }
    }

    public void Save()
    {
        string type = TypeName.Trim();
        if (type.Length == 0) { StatusMessage = "A session type name is required, for example Technical."; return; }
        var people = ParsePeople(CoHosts, SessionRole.CoHost).ToArray();
        if (people.Length == 0) { StatusMessage = "Add at least one person who may be made co-host before saving."; return; }
        try
        {
            var profile = new SessionRoleProfile(type)
            {
                Keywords = Keywords.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                Accounts = Accounts.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                People = people
            };
            var profiles = _document.Profiles.ToList();
            // The profile that was open is replaced wherever it went, so renaming it never leaves a
            // duplicate behind. Anything else only gives way to a profile it cannot be told apart
            // from: same type AND same accounts. One type set up once per group is the normal case.
            profiles.RemoveAll(existing => ReferenceEquals(existing, _selected) || SameProfile(existing, profile));
            profiles.Add(profile);
            _document = JsonSessionRoleStore.Validate(_document with { Profiles = profiles });
            _store.Save(_document);
            PublishQuietly();
            RefreshLists();
            SelectedProfile = Profiles.FirstOrDefault(item => SameProfile(item, profile))
                ?? Profiles.FirstOrDefault(item => string.Equals(item.SessionType, type, StringComparison.OrdinalIgnoreCase));
            string where = profile.Accounts.Count == 0 ? "every meeting" : string.Join(", ", profile.Accounts);
            StatusMessage = $"Saved {type} for {where} — {people.Length} person(s) who may be made co-host.";
            try
            {
                ProfileSaved?.Invoke($"Saved {type}",
                    $"{where} — {people.Length} person(s) who may be made co-host: {string.Join(", ", people.Select(person => person.Name))}");
            }
            catch { }
        }
        catch (Exception ex) { StatusMessage = "Could not save: " + ex.Message; }
    }

    public void Delete()
    {
        if (_selected == null) { StatusMessage = "Select a session type to delete."; return; }
        try
        {
            string removed = $"{_selected.SessionType} for {_selected.Scope}";
            // Only the one that is open: another group's profile may carry the same type name.
            var profiles = _document.Profiles.Where(profile =>
                !ReferenceEquals(profile, _selected) && !SameProfile(profile, _selected)).ToList();
            _document = _document with { Profiles = profiles };
            _store.Save(_document);
            PublishQuietly();
            RefreshLists();
            ClearEditor();
            StatusMessage = $"Deleted {removed}. Remembered assignments for it are kept until it is used again.";
        }
        catch (Exception ex) { StatusMessage = "Could not delete: " + ex.Message; }
    }

    /// <summary>
    /// Two profiles a meeting could not choose between: the same type bound to the same accounts.
    /// The same type for different groups is a different profile, and both are kept.
    /// </summary>
    private static bool SameProfile(SessionRoleProfile left, SessionRoleProfile right) =>
        string.Equals(left.SessionType.Trim(), right.SessionType.Trim(), StringComparison.OrdinalIgnoreCase) &&
        left.Accounts.Select(account => account.Trim()).OrderBy(account => account, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(
                right.Accounts.Select(account => account.Trim()).OrderBy(account => account, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

    private void ClearEditor()
    {
        SelectedProfile = null;
        _selected = null;
        OnPropertyChanged(nameof(SelectedProfile));
        TypeName = string.Empty; Keywords = string.Empty; Accounts = string.Empty;
        CoHosts = string.Empty;
        StatusMessage = "New session type — pick or type it, then list who may be made co-host.";
    }

    private void RefreshLists()
    {
        Profiles.Clear();
        var mine = _document.Profiles.Where(profile => _scope.OwnsAny(profile.Accounts))
            .OrderBy(profile => profile.SessionType, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var profile in mine) Profiles.Add(profile);
        ScopeNote = _scope.Narrowed(mine.Length, _document.Profiles.Count, "session type(s)");
        History.Clear();
        foreach (var entry in _document.History.Take(50)) History.Add(entry);
        RefreshTypeOptions();
    }

    /// <summary>The types offered by the picker: what the dashboard runs, plus what is saved here.</summary>
    private void RefreshTypeOptions()
    {
        var options = DashboardSessionTypes
            .Concat(Profiles.Select(profile => profile.SessionType))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (options.SequenceEqual(SessionTypeOptions, StringComparer.OrdinalIgnoreCase)) return;
        SessionTypeOptions.Clear();
        foreach (var option in options) SessionTypeOptions.Add(option);
    }

    /// <summary>Offers the saved accounts as a picker; called by the shell when accounts change.</summary>
    public void SetAvailableAccounts(IEnumerable<string> accountIds)
    {
        var wanted = accountIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        if (wanted.SequenceEqual(AvailableAccounts, StringComparer.Ordinal)) return;
        AvailableAccounts.Clear();
        foreach (var id in wanted) AvailableAccounts.Add(id);
    }

    /// <summary>One person per line: "Full Name | alias, another alias".</summary>
    public static IReadOnlyList<RolePerson> ParsePeople(string text, SessionRole role) =>
        (text ?? "").Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                var parts = line.Split('|', 2);
                var aliases = parts.Length > 1
                    ? parts[1].Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    : [];
                return new RolePerson(parts[0].Trim(), role) { Aliases = aliases };
            })
            .Where(person => person.Name.Length > 0)
            .ToArray();

    public static string FormatPeople(IEnumerable<RolePerson> people) =>
        string.Join(Environment.NewLine, people.Select(person =>
            person.Aliases.Count == 0 ? person.Name : $"{person.Name} | {string.Join(", ", person.Aliases)}"));
}
