using ZoomAutoAdmit.Attendance;
using ZoomAutoAdmit.AttendanceMatching;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Meetings;

namespace ZoomAutoAdmit.SessionRoles;

/// <summary>Supplies the schedule name of a running meeting; only used to recognise the session type.</summary>
public interface ISessionNameSource
{
    string? Describe(string accountId, DateTimeOffset startTime);
}

public enum SessionRoleNoticeKind { CoHostAssigned, NoCoHostFound, AssignmentFailed }

/// <summary>Something the person running the meeting should see straight away, on the desktop.</summary>
public sealed record SessionRoleNotice(SessionRoleNoticeKind Kind, string Title, string Message, Guid SessionId);

/// <summary>
/// Confirms a suspected person for an unfamiliar Zoom display name. The module supplies the
/// candidates; the implementation may only answer about those, never propose anyone else.
/// </summary>
public interface IRoleAiMatcher
{
    /// <param name="isPresenting">
    /// True when this person is sharing their screen right now. The instructor usually is, so it is
    /// worth weighing; it can support a name, never stand in for one.
    /// </param>
    /// <returns>Null when the AI declines, is unsure, or is unavailable.</returns>
    RoleMatch? Confirm(
        string observedName,
        SessionRoleProfile profile,
        IReadOnlyList<RolePerson> suspects,
        CancellationToken token,
        bool isPresenting = false);
}

/// <summary>
/// Watches a running meeting and grants co-host to the people configured for its session type.
/// It observes the existing lifecycle events and reuses the existing participant monitoring; it
/// owns no Zoom engine, changes no admission behaviour, and any failure here stays here.
/// </summary>
public sealed class SessionRoleBridge : IAsyncDisposable
{
    private sealed class Watch
    {
        public required CancellationTokenSource Cancellation { get; init; }
        public required SemaphoreSlim Wake { get; init; }
        public Task Loop { get; set; } = Task.CompletedTask;
    }

    private readonly object _sync = new();
    private readonly Dictionary<Guid, Watch> _watches = [];
    private readonly MeetingLifecycleEvents _events;
    private readonly ISessionRoleStore _store;
    private readonly ISessionNameSource _names;
    private readonly Func<MeetingLaunchContext, IAttendanceParticipantSource> _participants;
    private readonly ICoHostAssigner _assigner;
    private readonly IPresenterSource _presenters;
    private IRoleAiMatcher? _ai;
    private readonly Action<string> _log;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _settle;
    private bool _disposed;

    public SessionRoleBridge(
        MeetingLifecycleEvents events,
        Func<MeetingLaunchContext, IAttendanceParticipantSource> participants,
        ISessionNameSource names,
        ISessionRoleStore? store = null,
        ICoHostAssigner? assigner = null,
        Action<string>? log = null,
        TimeSpan? interval = null,
        IPresenterSource? presenters = null)
    {
        _events = events;
        _participants = participants;
        _names = names;
        _store = store ?? new JsonSessionRoleStore();
        _assigner = assigner ?? new ZoomCoHostAssigner();
        _presenters = presenters ?? new ZoomPresenterReader();
        _log = log ?? ConsoleLogger.Info;
        // Slow by design: an admitted participant wakes the watcher, so polling is only a fallback
        // and never competes with the admission engine for the desktop.
        _interval = interval ?? TimeSpan.FromSeconds(120);
        // Zoom needs a moment to move an admitted person into the joined list; a fast test interval
        // means no settling delay at all.
        _settle = _interval >= TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(2) : TimeSpan.Zero;
        _events.Lifecycle += OnLifecycleAsync;
        _events.AdmissionVerified += OnAdmissionVerified;
    }

    /// <summary>
    /// Optional: lets the AI confirm names the local rules could not settle. Set by the host once
    /// AI credentials exist. Without it the module simply matches less, and assigns nothing extra.
    /// </summary>
    public IRoleAiMatcher? AiMatcher { get => _ai; set => _ai = value; }

    /// <summary>Raised for the desktop notification; never on the UI thread, and never load-bearing.</summary>
    public event Action<SessionRoleNotice>? Notice;

    private Task OnLifecycleAsync(MeetingLifecycleEvent message)
    {
        var id = message.Context.Session.SessionId;
        lock (_sync)
        {
            if (_disposed) return Task.CompletedTask;
            if (message.Kind == MeetingLifecycleEventKind.Ending) { StopLocked(id); return Task.CompletedTask; }
            if (_watches.ContainsKey(id)) return Task.CompletedTask;
            var watch = new Watch { Cancellation = new CancellationTokenSource(), Wake = new SemaphoreSlim(0, 1) };
            _watches[id] = watch;
            watch.Loop = Task.Run(() => RunAsync(message.Context, watch, watch.Cancellation.Token));
        }
        return Task.CompletedTask;
    }

    /// <summary>A newly admitted person is the moment worth looking, so the watcher is woken then.</summary>
    private void OnAdmissionVerified(Guid sessionId)
    {
        lock (_sync)
        {
            if (_disposed || !_watches.TryGetValue(sessionId, out var watch)) return;
            try { if (watch.Wake.CurrentCount == 0) watch.Wake.Release(); } catch (SemaphoreFullException) { }
        }
    }

    private async Task RunAsync(MeetingLaunchContext context, Watch watch, CancellationToken token)
    {
        var session = context.Session;
        try
        {
            var document = _store.Load();
            string? scheduleName = _names.Describe(session.GroupId, session.StartTime);
            var profile = SessionTypeResolver.Resolve(scheduleName, session.GroupId, document.Profiles);
            if (profile == null)
            {
                Log($"[ROLE] No profile applies to this meeting; group={session.GroupId}; name=\"{scheduleName ?? "(unknown)"}\". " +
                    "Add a profile with no keywords and no accounts to cover every meeting.", session.SessionId);
                return;
            }
            bool everyMeeting = ReferenceEquals(profile, SessionTypeResolver.EveryMeetingProfile(document.Profiles));
            Log($"[ROLE] Profile loaded: {profile.SessionType}{(everyMeeting ? " (applies to every meeting)" : string.Empty)}; " +
                $"instructors={profile.Instructors.Count()}; coHostCandidates={profile.CoHosts.Count()}", session.SessionId);

            var source = _participants(context);
            var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // The same failure again is not shown again; after three, that person is left to the host.
            var failures = new Dictionary<string, (string Message, int Count)>(StringComparer.OrdinalIgnoreCase);
            var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool assignedAnyone = false, reportedNobody = false;
            while (!token.IsCancellationRequested)
            {
                // Wait for an admission, or fall back to the heartbeat.
                try { await watch.Wake.WaitAsync(_interval, token); }
                catch (OperationCanceledException) { return; }
                if (token.IsCancellationRequested) return;
                if (_settle > TimeSpan.Zero) await Task.Delay(_settle, token);
                IReadOnlyList<string> observed;
                try { observed = (await source.ReadAsync(token)).Participants.Select(participant => participant.Name).ToArray(); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Log($"[ROLE] Participant read failed; {ex.GetType().Name}: {ex.Message}", session.SessionId); continue; }

                // Re-read the profile each pass so edits made during the meeting are picked up.
                document = _store.Load();
                profile = SessionTypeResolver.Resolve(scheduleName, session.GroupId, document.Profiles) ?? profile;

                foreach (var name in observed)
                {
                    if (token.IsCancellationRequested) return;
                    if (!handled.Add(name)) continue;
                    var match = RoleMatcher.Match(name, profile!, document.History);
                    if (match == null)
                    {
                        // Only worth reading when a name needs help: whoever is presenting is
                        // usually the one teaching, and that is what the AI is told.
                        string? presenter = SafePresenter(token);
                        bool isPresenting = presenter != null &&
                            NameNormalizer.Normalize(presenter) == NameNormalizer.Normalize(name);
                        if (isPresenting)
                            Log($"[ROLE] \"{name}\" is sharing their screen; telling the AI while it checks the name", session.SessionId);
                        match = AskAi(name, profile!, session.SessionId, token, isPresenting);
                    }
                    if (match == null)
                    {
                        rejected.Add(name);
                        handled.Remove(name);   // Unknown now may become known later.
                        continue;
                    }
                    if (match.Person.Role == SessionRole.Instructor)
                        Log($"[ROLE] Instructor detected: {match.Person.Name} (as \"{name}\", {match.Source}, {match.Confidence}%)", session.SessionId);
                    Log($"[COHOST] Participant matched: {match.Person.Name} (as \"{name}\", {match.Source}, {match.Confidence}%)", session.SessionId);
                    Log($"[COHOST] Assignment started for {match.Person.Name}", session.SessionId);
                    CoHostOutcome outcome;
                    try { outcome = _assigner.Assign(name, token); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { outcome = new(false, $"{ex.GetType().Name}: {ex.Message}"); }
                    if (outcome.Success)
                    {
                        assignedAnyone = true;
                        // The end-of-class watch looks for this person: gone for five minutes after the
                        // three hours, the class is over.
                        AssignedCoHosts.Record(session.SessionId, name);
                        Log($"[COHOST] Assignment successful: {outcome.Message}", session.SessionId);
                        Raise(new(SessionRoleNoticeKind.CoHostAssigned, "Co-host assigned",
                            $"{match.Person.Name} — matched \"{name}\" in {profile!.SessionType} ({match.Source}, {match.Confidence}%).", session.SessionId));
                        // Keep the remembered entry in the local document: Remember returns a new
                        // document, so saving from the copy loaded at the top of the pass would drop
                        // whatever earlier participants in this same pass had just recorded.
                        try
                        {
                            document = RoleMatcher.Remember(document, profile!.SessionType, match, name);
                            _store.Save(document);
                        }
                        catch (Exception ex) { Log($"[ROLE] Could not remember the assignment; {ex.GetType().Name}", session.SessionId); }
                    }
                    else
                    {
                        Log($"[COHOST] Assignment failed: {outcome.Message}", session.SessionId);
                        var last = failures.GetValueOrDefault(name);
                        int count = last.Message == outcome.Message ? last.Count + 1 : 1;
                        failures[name] = (outcome.Message, count);
                        if (count == 1)
                            Raise(new(SessionRoleNoticeKind.AssignmentFailed, "Co-host assignment failed", outcome.Message, session.SessionId));
                        if (count >= 3)
                        {
                            Log($"[COHOST] Gave up on {match.Person.Name} after 3 identical failures; make them co-host by hand.", session.SessionId);
                            Raise(new(SessionRoleNoticeKind.AssignmentFailed, "Make co-host by hand",
                                $"{match.Person.Name} could not be made co-host automatically ({outcome.Message}).", session.SessionId));
                        }
                        else handled.Remove(name);   // Try again on the next pass.
                    }
                }
                // Everyone in the meeting was checked — including by the AI — and none of them is on
                // this profile. Say so once, on the desktop, instead of staying silent.
                if (!assignedAnyone && !reportedNobody && failures.Count == 0 && rejected.Count > 0 && observed.Count > 0)
                {
                    reportedNobody = true;
                    Log($"[COHOST] No co-host found among {rejected.Count} participant(s) for {profile!.SessionType}", session.SessionId);
                    Raise(new(SessionRoleNoticeKind.NoCoHostFound, "No co-host found",
                        $"Nobody in this {profile!.SessionType} meeting matches its instructors or co-host candidates. " +
                        $"Checked: {string.Join(", ", rejected.Take(4))}{(rejected.Count > 4 ? "…" : "")}", session.SessionId));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"[ROLE] Watcher stopped; {ex.GetType().Name}: {ex.Message}", session.SessionId); }
    }

    /// <summary>Local rules pick the suspects; the AI only confirms one of them.</summary>
    /// <summary>The presenter is a hint only, so a failure to read it is not worth reporting.</summary>
    private string? SafePresenter(CancellationToken token)
    {
        try { return _presenters.WhoIsPresenting(token); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private RoleMatch? AskAi(
        string observedName,
        SessionRoleProfile profile,
        Guid sessionId,
        CancellationToken token,
        bool isPresenting = false)
    {
        var ai = _ai;
        if (ai == null) return null;
        var suspects = RoleMatcher.Suspects(observedName, profile);
        if (suspects.Count == 0) return null;
        try
        {
            var confirmed = ai.Confirm(observedName, profile, suspects, token, isPresenting);
            if (confirmed == null) return null;
            // Whatever the AI answers, only a person already on this profile can be assigned.
            if (!profile.People.Any(person => string.Equals(person.Name, confirmed.Person.Name, StringComparison.OrdinalIgnoreCase)))
            {
                Log($"[COHOST] AI proposed \"{confirmed.Person.Name}\" who is not on this profile; ignored", sessionId);
                return null;
            }
            if (confirmed.Confidence < AiThreshold)
            {
                Log($"[COHOST] AI suggestion below the threshold: \"{observedName}\" ≈ {confirmed.Person.Name} ({confirmed.Confidence}%); not assigned", sessionId);
                return null;
            }
            Log($"[COHOST] AI confirmed \"{observedName}\" as {confirmed.Person.Name} ({confirmed.Confidence}%)", sessionId);
            return confirmed;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"[COHOST] AI confirmation unavailable; {ex.GetType().Name}: {ex.Message}", sessionId);
            return null;
        }
    }

    public const int AiThreshold = 85;

    private void Raise(SessionRoleNotice notice)
    {
        try { Notice?.Invoke(notice); }
        catch { /* A notification failure never affects the meeting. */ }
    }

    private void StopLocked(Guid sessionId)
    {
        if (!_watches.Remove(sessionId, out var watch)) return;
        watch.Cancellation.Cancel();
    }

    private void Log(string message, Guid sessionId)
    {
        try { _log($"{message}; sessionId={sessionId:D}"); }
        catch { /* Diagnostics never affect the meeting. */ }
    }

    public async ValueTask DisposeAsync()
    {
        Watch[] watches;
        lock (_sync)
        {
            _disposed = true;
            _events.Lifecycle -= OnLifecycleAsync;
            _events.AdmissionVerified -= OnAdmissionVerified;
            watches = _watches.Values.ToArray();
            _watches.Clear();
        }
        foreach (var watch in watches) watch.Cancellation.Cancel();
        foreach (var watch in watches)
        {
            try { await watch.Loop; } catch { }
            watch.Cancellation.Dispose();
            watch.Wake.Dispose();
        }
    }
}
