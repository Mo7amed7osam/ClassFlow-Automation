using System.IO;
using ZoomAutoAdmit.Attendance;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.SessionRoles;
using ZoomAutoAdmit.WindowsUI.ViewModels;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

public sealed class SessionRolesTests
{
    private sealed class MemoryStore : ISessionRoleStore
    {
        public SessionRoleDocument Value = new();
        public SessionRoleDocument Load() => Value;
        public void Save(SessionRoleDocument document) => Value = JsonSessionRoleStore.Validate(document);
    }

    private static SessionRoleProfile Technical() => new("Technical")
    {
        Keywords = ["technical", "python"],
        People =
        [
            new("Ahmed Mohamed", SessionRole.Instructor) { Aliases = ["Ahmed M"] },
            new("Assistant One", SessionRole.CoHost),
            new("Assistant Two", SessionRole.CoHost) { Aliases = ["Assistant 2"] }
        ]
    };

    [Fact]
    public void EditorSavesAProfileAndReadsItBack()
    {
        var store = new MemoryStore();
        var vm = new SessionRolesViewModel(store)
        {
            TypeName = " Technical ",
            Keywords = "technical, python",
            CoHosts = "Ahmed Mohamed | Ahmed M\nAssistant One\nAssistant Two | Assistant 2"
        };

        vm.Save();

        var profile = Assert.Single(store.Value.Profiles);
        Assert.Equal("Technical", profile.SessionType);
        Assert.Equal(new[] { "technical", "python" }, profile.Keywords);
        // One list now: everyone written down is someone this session may hand co-host to.
        Assert.Empty(profile.Instructors);
        Assert.Equal(new[] { "Ahmed Mohamed", "Assistant One", "Assistant Two" }, profile.CoHosts.Select(person => person.Name));
        Assert.Equal(new[] { "Ahmed M" }, profile.CoHosts.First().Aliases);
        Assert.Contains("Saved Technical", vm.StatusMessage);

        // A second view model over the same store shows the saved profile.
        var reopened = new SessionRolesViewModel(store);
        Assert.Equal("Technical", Assert.Single(reopened.Profiles).SessionType);
    }

    [Fact]
    public void SavingWithoutPeopleOrNameIsRefused()
    {
        var store = new MemoryStore();
        var vm = new SessionRolesViewModel(store) { TypeName = "", CoHosts = "Someone" };
        vm.Save();
        Assert.Empty(store.Value.Profiles);
        Assert.Contains("session type name is required", vm.StatusMessage);

        vm.TypeName = "English";
        vm.CoHosts = "";
        vm.Save();
        Assert.Empty(store.Value.Profiles);
        Assert.Contains("at least one person who may be made co-host", vm.StatusMessage);
    }

    [Fact]
    public void OneTypeCanBeSetUpOncePerGroupAndBothAreKept()
    {
        var store = new MemoryStore();
        var vm = new SessionRolesViewModel(store);

        vm.TypeName = "Technical";
        vm.Accounts = "CAI5_AIS4_S7";
        vm.CoHosts = "Mostafa Badr";
        vm.Save();

        // A second group, same type: this is a different profile, not a rewrite of the first.
        vm.NewCommand.Execute(null);
        vm.TypeName = "Technical";
        vm.Accounts = "CAI5_AIS4_S8";
        vm.CoHosts = "Sara Ahmed";
        vm.Save();

        Assert.Equal(2, store.Value.Profiles.Count);
        var s7 = store.Value.Profiles.Single(profile => profile.Accounts.Contains("CAI5_AIS4_S7"));
        var s8 = store.Value.Profiles.Single(profile => profile.Accounts.Contains("CAI5_AIS4_S8"));
        Assert.Equal("Mostafa Badr", Assert.Single(s7.People).Name);
        Assert.Equal("Sara Ahmed", Assert.Single(s8.People).Name);

        // Each meeting still gets exactly the one meant for its own group.
        Assert.Same(s7, SessionTypeResolver.Resolve("CAI5_AIS4_S7 • 27 • Technical", "CAI5_AIS4_S7", store.Value.Profiles));
        Assert.Same(s8, SessionTypeResolver.Resolve("CAI5_AIS4_S8 • 25 • Technical", "CAI5_AIS4_S8", store.Value.Profiles));

        // Saving the same type against the same group again replaces it rather than duplicating.
        vm.NewCommand.Execute(null);
        vm.TypeName = "Technical";
        vm.Accounts = "CAI5_AIS4_S8";
        vm.CoHosts = "Someone Else";
        vm.Save();
        Assert.Equal(2, store.Value.Profiles.Count);
        Assert.Equal("Someone Else",
            Assert.Single(store.Value.Profiles.Single(profile => profile.Accounts.Contains("CAI5_AIS4_S8")).People).Name);
    }

    [Fact]
    public void SavingAnnouncesTheTypeAndItsGroupAndDeletingTakesOnlyTheOpenProfile()
    {
        var store = new MemoryStore();
        var vm = new SessionRolesViewModel(store);
        List<(string Title, string Message)> announced = [];
        vm.ProfileSaved += (title, message) => announced.Add((title, message));

        vm.TypeName = "Technical";
        vm.Accounts = "CAI5_AIS4_S7";
        vm.CoHosts = "Mostafa Badr";
        vm.Save();
        vm.NewCommand.Execute(null);
        vm.TypeName = "Technical";
        vm.Accounts = "CAI5_AIS4_S8";
        vm.CoHosts = "Sara Ahmed";
        vm.Save();

        Assert.Equal(2, announced.Count);
        Assert.Equal("Saved Technical", announced[1].Title);
        Assert.Contains("CAI5_AIS4_S8", announced[1].Message);
        Assert.Contains("Sara Ahmed", announced[1].Message);

        // Deleting one group's profile leaves the other group's, despite the shared type name.
        vm.SelectedProfile = vm.Profiles.Single(profile => profile.Accounts.Contains("CAI5_AIS4_S8"));
        vm.Delete();
        var left = Assert.Single(store.Value.Profiles);
        Assert.Equal("CAI5_AIS4_S7", Assert.Single(left.Accounts));
    }

    [Fact]
    public void PickingATypeAlsoWritesItAsTheWordToLookForButNeverOverwritesTypedKeywords()
    {
        var vm = new SessionRolesViewModel(new MemoryStore());

        vm.TypeName = "Technical";
        Assert.Equal("Technical", vm.Keywords);

        // Changing the pick moves the keyword with it, while it is still just the type.
        vm.TypeName = "English";
        Assert.Equal("English", vm.Keywords);

        // Once the keywords are the person's own, a later pick leaves them alone.
        vm.Keywords = "english, speaking, ielts";
        vm.TypeName = "Soft Skill";
        Assert.Equal("english, speaking, ielts", vm.Keywords);
    }

    [Fact]
    public void TheSessionTypePickerOffersWhatRunsAndWhatIsAlreadySaved()
    {
        var store = new MemoryStore();
        var vm = new SessionRolesViewModel(store);

        // The types the dashboard runs are offered before anything has been saved at all.
        Assert.Equal(SessionRolesViewModel.DashboardSessionTypes.OrderBy(name => name), vm.SessionTypeOptions);

        vm.TypeName = "Graduation Project";
        vm.CoHosts = "Assistant One";
        vm.Save();

        // A type that was typed rather than picked joins the list, and is not duplicated.
        Assert.Contains("Graduation Project", vm.SessionTypeOptions);
        Assert.Equal(vm.SessionTypeOptions.Count, vm.SessionTypeOptions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("Technical", vm.SessionTypeOptions);
    }

    [Theory]
    [InlineData("CAI5_AIS4_S7 • 27 • Intro to Python", "Technical")]
    [InlineData("CAI5_AIS4_S7 • 12 • Soft skill: presenting", "Soft Skill")]
    [InlineData("CAI5_AIS4_S7 • 3 • Revision", null)]
    public void SessionTypeComesFromTheScheduleName(string scheduleName, string? expected)
    {
        SessionRoleProfile[] profiles = [Technical(), new("Soft Skill") { Keywords = ["soft skill"], People = [new("Sara Ahmed", SessionRole.Instructor)] }];
        Assert.Equal(expected, SessionTypeResolver.Resolve(scheduleName, accountId: null, profiles)?.SessionType);
    }

    [Fact]
    public void AccountBindingBeatsKeywordsAndWorksWithoutAScheduleName()
    {
        SessionRoleProfile[] profiles =
        [
            Technical() with { Accounts = ["CAI5_AIS4_S8"] },
            new("English") { Keywords = ["english"], People = [new("Mohab", SessionRole.Instructor)] }
        ];

        // The account decides even when the name says otherwise, and even with no name at all.
        Assert.Equal("Technical", SessionTypeResolver.Resolve("CAI5_AIS4_S8 • 4 • English practice", "CAI5_AIS4_S8", profiles)?.SessionType);
        Assert.Equal("Technical", SessionTypeResolver.Resolve(null, "CAI5_AIS4_S8", profiles)?.SessionType);
        // An unbound account still falls back to the schedule name.
        Assert.Equal("English", SessionTypeResolver.Resolve("CAI5_AIS4_S7 • 4 • English practice", "CAI5_AIS4_S7", profiles)?.SessionType);
        Assert.Null(SessionTypeResolver.Resolve("CAI5_AIS4_S7 • 4 • Revision", "CAI5_AIS4_S7", profiles));
    }

    [Fact]
    public void ZoomNamesWithExtraWordsStillMatchTheConfiguredPerson()
    {
        var profile = new SessionRoleProfile("English")
        {
            People = [new("Mohab Mohamed", SessionRole.CoHost), new("Sara", SessionRole.Instructor)]
        };

        // "Mohab Mohamed __Coordinator" is the display name Zoom shows for a configured "Mohab Mohamed".
        var match = RoleMatcher.Match("Mohab Mohamed __Coordinator", profile, []);
        Assert.Equal("Mohab Mohamed", match!.Person.Name);
        Assert.Equal(RoleMatchSource.NameRule, match.Source);
        Assert.True(match.Confidence >= 90);

        // A single configured first name is never enough to hand out co-host.
        Assert.Null(RoleMatcher.Match("Sara Ahmed Ali", profile, []));
        Assert.Null(RoleMatcher.Match("Mohamed Mohab", profile, []));
    }

    [Fact]
    public void ShortenedAndRewrittenNamesMatchButADifferentSecondNameDoesNot()
    {
        var profile = new SessionRoleProfile("English")
        {
            People = [new("Mohab Mohamed", SessionRole.Instructor)]
        };

        // Typing less of your own name is still you.
        Assert.Equal("Mohab Mohamed", RoleMatcher.Match("Mohab", profile, [])!.Person.Name);
        // Chat-alphabet spelling of the same word.
        Assert.Equal("Mohab Mohamed", RoleMatcher.Match("mo7ab", profile, [])!.Person.Name);
        // A second name that is not part of the configured one is somebody else: left to the AI.
        Assert.Null(RoleMatcher.Match("Mohab Ahmed", profile, []));
    }

    [Fact]
    public void AShortenedNameTwoPeopleCouldOwnIsLeftToTheAi()
    {
        var profile = new SessionRoleProfile("English")
        {
            People =
            [
                new("Mohab Mohamed", SessionRole.Instructor),
                new("Mohab Ahmed", SessionRole.Instructor)
            ]
        };

        // "Mohab" fits both, so nobody is handed co-host on a guess.
        Assert.Null(RoleMatcher.Match("Mohab", profile, []));
        // The full name still resolves to exactly one of them.
        Assert.Equal("Mohab Ahmed", RoleMatcher.Match("Mohab Ahmed", profile, [])!.Person.Name);
    }

    [Fact]
    public void MatchingPrefersConfiguredNameThenAliasThenMemory()
    {
        var profile = Technical();
        var document = new SessionRoleDocument { Profiles = [profile] };

        var exact = RoleMatcher.Match("assistant one", profile, document.History);
        Assert.Equal(RoleMatchSource.ConfiguredName, exact!.Source);

        var alias = RoleMatcher.Match("Assistant 2", profile, document.History);
        Assert.Equal("Assistant Two", alias!.Person.Name);
        Assert.Equal(RoleMatchSource.Alias, alias.Source);

        Assert.Null(RoleMatcher.Match("Mo7ab Mohamed", profile, document.History));

        // Remembering one successful assignment makes the same Zoom name match without any AI call.
        var remembered = RoleMatcher.Remember(document, "Technical",
            new RoleMatch(profile.CoHosts.First(), RoleMatchSource.ConfiguredName, 96), "Mo7ab Mohamed");
        var learned = RoleMatcher.Match("Mo7ab Mohamed", profile, remembered.History);
        Assert.Equal(RoleMatchSource.PreviousAssignment, learned!.Source);
        Assert.Equal("Assistant One", learned.Person.Name);

        // Memory never introduces someone who is no longer on the profile.
        var trimmed = profile with { People = profile.People.Where(person => person.Name != "Assistant One").ToArray() };
        Assert.Null(RoleMatcher.Match("Mo7ab Mohamed", trimmed, remembered.History));
    }

    [Fact]
    public async Task BridgeAssignsCoHostToConfiguredPeopleOnlyAndRemembersTheZoomName()
    {
        var store = new MemoryStore { Value = new SessionRoleDocument { Profiles = [Technical()] } };
        var assigner = new FakeAssigner();
        var participants = new FakeParticipants(["Assistant One", "Someone Random", "Ahmed Mohamed"]);
        var events = new MeetingLifecycleEvents();
        await using var bridge = new SessionRoleBridge(events, _ => participants, new FakeNames("CAI5_AIS4_S7 • 12 • Advanced Python"),
            store, assigner, log: _ => { }, interval: TimeSpan.FromMilliseconds(20), presenters: NoPresenterSource.Instance);

        var context = Context("CAI5_AIS4_S7");
        await events.PublishAsync(context, MeetingLifecycleEventKind.Active);
        await WaitFor(() => assigner.Assigned.Count >= 2);

        // Only the two configured people are touched; the unknown participant is never assigned.
        Assert.Equal(new[] { "Assistant One", "Ahmed Mohamed" }, assigner.Assigned.OrderBy(name => name == "Ahmed Mohamed").ToArray());
        Assert.DoesNotContain("Someone Random", assigner.Assigned);
        Assert.Contains(store.Value.History, entry => entry.PersonName == "Assistant One" && entry.ObservedName == "Assistant One" && entry.Approved);

        await events.PublishAsync(context, MeetingLifecycleEventKind.Ending);
        int afterStop = assigner.Assigned.Count;
        participants.Names = ["Assistant Two"];
        await Task.Delay(120);
        Assert.Equal(afterStop, assigner.Assigned.Count);   // The watcher stops with the meeting.
    }

    [Fact]
    public async Task AnInstructorBackWithoutCoHostIsMadeCoHostAgain()
    {
        var store = new MemoryStore { Value = new SessionRoleDocument { Profiles = [Technical()] } };
        var assigner = new FakeAssigner();
        // Zoom's own row text: the tags say who is co-host.
        var room = new FakeRows([("Ahmed Mohamed", "Ahmed Mohamed,(Guest), Computer audio muted,Video off")]);
        assigner.OnAssign = name => room.Rows = [(name, name + ",(Co-host), Computer audio muted,Video off")];
        var events = new MeetingLifecycleEvents();
        await using var bridge = new SessionRoleBridge(events, _ => room, new FakeNames("CAI5_AIS4_S7 • 12 • Advanced Python"),
            store, assigner, log: _ => { }, interval: TimeSpan.FromMilliseconds(20), presenters: NoPresenterSource.Instance,
            restoreEvery: TimeSpan.Zero);

        await events.PublishAsync(Context("CAI5_AIS4_S7"), MeetingLifecycleEventKind.Active);
        await WaitFor(() => assigner.Assigned.Count == 1);

        // Now a co-host: nothing more is done.
        await Task.Delay(120);
        Assert.Single(assigner.Assigned);

        // Their internet dropped: back in the meeting as a plain guest - made co-host again.
        room.Rows = [("Ahmed Mohamed", "Ahmed Mohamed,(Guest), Computer audio muted,Video off")];
        await WaitFor(() => assigner.Assigned.Count >= 2);
        Assert.All(assigner.Assigned, name => Assert.Equal("Ahmed Mohamed", name));
    }

    [Fact]
    public async Task AWebClassIsMadeCoHostThroughTheWebAssigner()
    {
        var store = new MemoryStore { Value = new SessionRoleDocument { Profiles = [Technical()] } };
        var desktop = new FakeAssigner();
        var web = new FakeAssigner();
        // The web client's own row text (recorded live): the role sits in brackets after the name.
        var room = new FakeRows([("Ahmed Mohamed", "Ahmed Mohamed (Guest),computer audio muted,video off")]);
        web.OnAssign = name => room.Rows = [(name, name + " (Co-host, guest),computer audio muted,video off")];
        var events = new MeetingLifecycleEvents();
        await using var bridge = new SessionRoleBridge(events, _ => room, new FakeNames("CAI5_AIS4_S7 • 12 • Advanced Python"),
            store, desktop, log: _ => { }, interval: TimeSpan.FromMilliseconds(20), presenters: NoPresenterSource.Instance,
            restoreEvery: TimeSpan.Zero,
            assignerFor: context => context.EngineType == SessionEngineType.Web ? web : desktop);

        await events.PublishAsync(Context("CAI5_AIS4_S7", SessionEngineType.Web), MeetingLifecycleEventKind.Active);
        await WaitFor(() => web.Assigned.Count == 1);

        // Dropped and back as a plain guest on the web: made co-host again, still through the web.
        room.Rows = [("Ahmed Mohamed", "Ahmed Mohamed (Guest),computer audio muted,video off")];
        await WaitFor(() => web.Assigned.Count >= 2);
        Assert.Empty(desktop.Assigned);
    }

    [Fact]
    public async Task NobodyIsMadeCoHostAgainFromABareNameOrWhileTheyAreHost()
    {
        var store = new MemoryStore { Value = new SessionRoleDocument { Profiles = [Technical()] } };
        var assigner = new FakeAssigner();
        var room = new FakeRows([("Ahmed Mohamed", "Ahmed Mohamed,(Guest), Computer audio muted")]);
        assigner.OnAssign = name => room.Rows = [(name, name + ",(Co-host), Computer audio muted,Video off")];
        var events = new MeetingLifecycleEvents();
        await using var bridge = new SessionRoleBridge(events, _ => room, new FakeNames("CAI5_AIS4_S7 • 12 • Advanced Python"),
            store, assigner, log: _ => { }, interval: TimeSpan.FromMilliseconds(20), presenters: NoPresenterSource.Instance,
            restoreEvery: TimeSpan.Zero);

        await events.PublishAsync(Context("CAI5_AIS4_S7"), MeetingLifecycleEventKind.Active);
        await WaitFor(() => assigner.Assigned.Count == 1);

        // A read that gives only the name cannot say they lost co-host.
        room.Rows = [("Ahmed Mohamed", "Ahmed Mohamed")];
        await Task.Delay(120);
        // Zoom handed them the host role (the host left): never "demoted" to co-host.
        room.Rows = [("Ahmed Mohamed", "Ahmed Mohamed,(Host), Computer audio unmuted")];
        await Task.Delay(120);
        Assert.Single(assigner.Assigned);
    }

    [Fact]
    public async Task WithAutoCoHostOffNobodyIsMadeCoHostUntilItIsBackOn()
    {
        var store = new MemoryStore { Value = new SessionRoleDocument { Profiles = [Technical()] } };
        var assigner = new FakeAssigner();
        bool on = false;
        var room = new FakeRows([("Ahmed Mohamed", "Ahmed Mohamed,(Guest), Computer audio muted")]);
        assigner.OnAssign = name => room.Rows = [(name, name + ",(Co-host), Computer audio muted,Video off")];
        var events = new MeetingLifecycleEvents();
        await using var bridge = new SessionRoleBridge(events, _ => room, new FakeNames("CAI5_AIS4_S7 • 12 • Advanced Python"),
            store, assigner, log: _ => { }, interval: TimeSpan.FromMilliseconds(20), presenters: NoPresenterSource.Instance,
            restoreEvery: TimeSpan.Zero, autoCoHostOn: () => on);

        await events.PublishAsync(Context("CAI5_AIS4_S7"), MeetingLifecycleEventKind.Active);
        await Task.Delay(150);
        Assert.Empty(assigner.Assigned);            // switched off: the instructor is left alone

        on = true;
        await WaitFor(() => assigner.Assigned.Count == 1);

        // Taken off on purpose with the switch off: not made co-host again.
        on = false;
        room.Rows = [("Ahmed Mohamed", "Ahmed Mohamed,(Guest), Computer audio muted")];
        await Task.Delay(150);
        Assert.Single(assigner.Assigned);
    }

    private sealed class FakeRows(IReadOnlyList<(string Name, string Label)> rows) : IAttendanceParticipantSource
    {
        public IReadOnlyList<(string Name, string Label)> Rows = rows;
        public AttendanceSource Source => AttendanceSource.Desktop;
        public Task<ParticipantReadResult> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ParticipantReadResult(Rows.Select(r => new ParticipantPresence(r.Name) { RowLabel = r.Label }).ToArray()));
    }

    [Fact]
    public async Task BridgeAssignsNothingWhenTheScheduleNameMatchesNoSessionType()
    {
        var store = new MemoryStore { Value = new SessionRoleDocument { Profiles = [Technical()] } };
        var assigner = new FakeAssigner();
        var events = new MeetingLifecycleEvents();
        await using var bridge = new SessionRoleBridge(events, _ => new FakeParticipants(["Assistant One"]),
            new FakeNames("CAI5_AIS4_S7 • 3 • Revision"), store, assigner, log: _ => { }, interval: TimeSpan.FromMilliseconds(20), presenters: NoPresenterSource.Instance);

        await events.PublishAsync(Context("CAI5_AIS4_S7"), MeetingLifecycleEventKind.Active);
        await Task.Delay(150);

        Assert.Empty(assigner.Assigned);
        Assert.Empty(store.Value.History);
    }

    [Fact]
    public async Task AiConfirmsASuspectButCanNeverIntroduceSomeoneElse()
    {
        var store = new MemoryStore { Value = new SessionRoleDocument { Profiles = [Technical()] } };
        var assigner = new FakeAssigner();
        var ai = new FakeAi();
        var events = new MeetingLifecycleEvents();
        await using var bridge = new SessionRoleBridge(events, _ => new FakeParticipants(["Mo7ab __TA", "Ghareeb Person"]),
            new FakeNames("Advanced Python"), store, assigner, log: _ => { }, interval: TimeSpan.FromMilliseconds(20), presenters: NoPresenterSource.Instance)
        { AiMatcher = ai };

        await events.PublishAsync(Context("CAI5_AIS4_S7"), MeetingLifecycleEventKind.Active);
        await WaitFor(() => assigner.Assigned.Count >= 1);
        await Task.Delay(80);

        // Only the name the AI confirmed with high confidence is assigned.
        Assert.Equal(new[] { "Mo7ab __TA" }, assigner.Assigned);
        Assert.Contains(store.Value.History, entry => entry.PersonName == "Assistant One" && entry.ObservedName == "Mo7ab __TA");
        // The suspects offered to the AI never include anyone outside the profile.
        Assert.All(ai.Offered, suspects => Assert.All(suspects, person =>
            Assert.Contains(person.Name, Technical().People.Select(configured => configured.Name))));
    }

    [Fact]
    public async Task NobodyMatchingRaisesOneDesktopNoticeForTheWholeSession()
    {
        var store = new MemoryStore { Value = new SessionRoleDocument { Profiles = [Technical()] } };
        var assigner = new FakeAssigner();
        var events = new MeetingLifecycleEvents();
        List<SessionRoleNotice> notices = [];
        await using var bridge = new SessionRoleBridge(events, _ => new FakeParticipants(["Random Guest", "Another Guest"]),
            new FakeNames("Advanced Python"), store, assigner, log: _ => { }, interval: TimeSpan.FromMilliseconds(20), presenters: NoPresenterSource.Instance);
        bridge.Notice += notice => { lock (notices) notices.Add(notice); };

        await events.PublishAsync(Context("CAI5_AIS4_S7"), MeetingLifecycleEventKind.Active);
        await WaitFor(() => notices.Count > 0);
        await Task.Delay(120);   // Several more passes must not repeat it.

        var notice = Assert.Single(notices);
        Assert.Equal(SessionRoleNoticeKind.NoCoHostFound, notice.Kind);
        Assert.Contains("Random Guest", notice.Message);
        Assert.Empty(assigner.Assigned);
    }

    [Fact]
    public async Task ASuccessfulAssignmentRaisesItsOwnNotice()
    {
        var store = new MemoryStore { Value = new SessionRoleDocument { Profiles = [Technical()] } };
        var events = new MeetingLifecycleEvents();
        List<SessionRoleNotice> notices = [];
        await using var bridge = new SessionRoleBridge(events, _ => new FakeParticipants(["Assistant One"]),
            new FakeNames("Advanced Python"), store, new FakeAssigner(), log: _ => { }, interval: TimeSpan.FromMilliseconds(20), presenters: NoPresenterSource.Instance);
        bridge.Notice += notice => { lock (notices) notices.Add(notice); };

        await events.PublishAsync(Context("CAI5_AIS4_S7"), MeetingLifecycleEventKind.Active);
        await WaitFor(() => notices.Count > 0);

        Assert.Equal(SessionRoleNoticeKind.CoHostAssigned, notices[0].Kind);
        Assert.Contains("Assistant One", notices[0].Message);
        // A meeting where somebody was assigned never reports "no co-host found".
        await Task.Delay(100);
        Assert.DoesNotContain(notices, item => item.Kind == SessionRoleNoticeKind.NoCoHostFound);
    }

    private sealed class FakeAi : IRoleAiMatcher
    {
        public List<IReadOnlyList<RolePerson>> Offered { get; } = [];
        public bool SawPresenter { get; private set; }
        public RoleMatch? Confirm(
            string observedName,
            SessionRoleProfile profile,
            IReadOnlyList<RolePerson> suspects,
            CancellationToken token,
            bool isPresenting = false)
        {
            if (isPresenting) SawPresenter = true;
            lock (Offered) Offered.Add(suspects);
            if (observedName == "Mo7ab __TA")
                return new RoleMatch(profile.People.First(person => person.Name == "Assistant One"), RoleMatchSource.NameRule, 96);
            // An answer about somebody who is not configured must be ignored by the bridge.
            return new RoleMatch(new RolePerson("Complete Stranger", SessionRole.CoHost), RoleMatchSource.NameRule, 99);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(20);
        Assert.True(condition(), "The bridge did not reach the expected state in time.");
    }

    private static MeetingLaunchContext Context(string accountId, SessionEngineType engine = SessionEngineType.Desktop)
    {
        var scheduled = new ScheduledMeeting(new("https://zoom.us/j/12345678901"), accountId, DateTimeOffset.Now);
        return new(new(Guid.NewGuid(), scheduled, DateTimeOffset.Now), new(accountId, accountId, "reference"), engine, accountId);
    }

    private sealed class FakeNames(string? name) : ISessionNameSource
    {
        public string? Describe(string accountId, DateTimeOffset startTime) => name;
    }

    private sealed class FakeParticipants(IReadOnlyList<string> names) : IAttendanceParticipantSource
    {
        public IReadOnlyList<string> Names = names;
        public AttendanceSource Source => AttendanceSource.Desktop;
        public Task<ParticipantReadResult> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ParticipantReadResult(Names.Select(name => new ParticipantPresence(name)).ToArray()));
    }

    private sealed class FakeAssigner : ICoHostAssigner
    {
        public List<string> Assigned { get; } = [];
        /// <summary>What Zoom does once someone is made co-host (a test updates the rows it reads).</summary>
        public Action<string>? OnAssign;
        public CoHostOutcome Assign(string observedDisplayName, CancellationToken token = default)
        {
            lock (Assigned) Assigned.Add(observedDisplayName);
            OnAssign?.Invoke(observedDisplayName);
            return new(true, observedDisplayName + " is now a co-host.");
        }
    }

    [Fact]
    public void FileStoreRoundTripsAndRejectsDuplicateTypes()
    {
        string path = Path.Combine(Path.GetTempPath(), "zoom-roles-tests", Guid.NewGuid().ToString("N"), "session-roles.json");
        try
        {
            var store = new JsonSessionRoleStore(path);
            Assert.Empty(store.Load().Profiles);
            store.Save(new SessionRoleDocument { Profiles = [Technical()] });
            var loaded = new JsonSessionRoleStore(path).Load();
            Assert.Equal("Ahmed Mohamed", Assert.Single(loaded.Profiles).Instructors.Single().Name);

            Assert.Throws<InvalidDataException>(() =>
                store.Save(new SessionRoleDocument { Profiles = [Technical(), Technical()] }));
        }
        finally
        {
            string directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
