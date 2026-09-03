namespace ZoomAutoAdmit.Core.Sessions;

public sealed class SessionCoordinator
{
    private readonly object _allocationSync = new();
    private readonly ActiveSessionRegistry _registry;
    private readonly SessionAllocationPolicy _policy;

    public SessionCoordinator(
        ActiveSessionRegistry? registry = null,
        SessionAllocationPolicy? policy = null)
    {
        _registry = registry ?? new ActiveSessionRegistry();
        _policy = policy ?? new SessionAllocationPolicy();
    }

    public IReadOnlyList<ActiveSession> ActiveSessions => _registry.GetActive();

    /// <param name="webProfileName">
    /// The account's own browser profile. Null uses the name derived from the account id, which
    /// is what every caller did before accounts could name their profile.
    /// </param>
    public SessionAllocationResult Allocate(
        string accountId,
        DateTimeOffset? startTime = null,
        Guid? sessionId = null,
        string? webProfileName = null)
    {
        try
        {
            webProfileName = string.IsNullOrWhiteSpace(webProfileName)
                ? AccountWebProfile.ForAccount(accountId)
                : webProfileName.Trim();
        }
        catch (ArgumentException ex)
        {
            return SessionAllocationResult.Failure(
                SessionAllocationError.InvalidAccountId,
                ex.Message);
        }

        Guid id = sessionId ?? Guid.NewGuid();
        DateTimeOffset startedAt = startTime ?? DateTimeOffset.UtcNow;
        lock (_allocationSync)
        {
            if (_registry.TryGet(id, out _))
            {
                return SessionAllocationResult.Failure(
                    SessionAllocationError.DuplicateSessionId,
                    $"Session '{id}' is already registered.");
            }

            // Another caller can reserve Desktop, or the chosen Web profile, between the decision
            // and the registration. Re-evaluate a few times so such a race just moves to the next
            // free engine/profile instead of failing the meeting.
            for (int attempt = 0; attempt < SessionAllocationPolicy.MaxWebInstancesPerAccount + 1; attempt++)
            {
                var active = _registry.GetActive();
                var decision = _policy.Decide(active, webProfileName);
                if (!decision.IsAllowed || decision.EngineType == null)
                {
                    return SessionAllocationResult.Failure(
                        decision.Error,
                        decision.ErrorMessage ?? "The session could not be allocated.");
                }

                string? profile = null;
                if (decision.EngineType == SessionEngineType.Web)
                {
                    profile = SessionAllocationPolicy.NextFreeWebProfile(active, webProfileName);
                    if (profile == null)
                        return SessionAllocationResult.Failure(
                            SessionAllocationError.WebProfileLocked,
                            $"Account '{accountId.Trim()}' already runs {SessionAllocationPolicy.MaxWebInstancesPerAccount} simultaneous Web meetings.");
                }

                var session = new ActiveSession(
                    id,
                    accountId.Trim(),
                    decision.EngineType.Value,
                    startedAt,
                    SessionStatus.Allocated,
                    profile);
                if (_registry.TryAdd(session, out var error, out var errorMessage))
                    return SessionAllocationResult.Success(session);

                if (error is not SessionAllocationError.DesktopOccupied and not SessionAllocationError.WebProfileLocked)
                    return SessionAllocationResult.Failure(
                        error,
                        errorMessage ?? "The session reservation failed.");
            }

            return SessionAllocationResult.Failure(
                SessionAllocationError.DesktopOccupied,
                "The engines stayed occupied while the session was being allocated.");
        }
    }

    public SessionAllocationResult AllocateWeb(
        string accountId,
        DateTimeOffset? startTime = null,
        Guid? sessionId = null,
        string? webProfileName = null)
    {
        try
        {
            webProfileName = string.IsNullOrWhiteSpace(webProfileName)
                ? AccountWebProfile.ForAccount(accountId)
                : webProfileName.Trim();
        }
        catch (ArgumentException ex)
        {
            return SessionAllocationResult.Failure(
                SessionAllocationError.InvalidAccountId,
                ex.Message);
        }

        Guid id = sessionId ?? Guid.NewGuid();
        DateTimeOffset webStartedAt = startTime ?? DateTimeOffset.UtcNow;
        lock (_allocationSync)
        {
            for (int attempt = 0; attempt < SessionAllocationPolicy.MaxWebInstancesPerAccount + 1; attempt++)
            {
                string? profile = SessionAllocationPolicy.NextFreeWebProfile(_registry.GetActive(), webProfileName);
                if (profile == null)
                    return SessionAllocationResult.Failure(
                        SessionAllocationError.WebProfileLocked,
                        $"Account '{accountId.Trim()}' already runs {SessionAllocationPolicy.MaxWebInstancesPerAccount} simultaneous Web meetings.");

                var session = new ActiveSession(
                    id,
                    accountId.Trim(),
                    SessionEngineType.Web,
                    webStartedAt,
                    SessionStatus.Allocated,
                    profile);
                if (_registry.TryAdd(session, out var error, out var errorMessage))
                    return SessionAllocationResult.Success(session);
                if (error != SessionAllocationError.WebProfileLocked)
                    return SessionAllocationResult.Failure(
                        error,
                        errorMessage ?? "The Web session reservation failed.");
            }

            return SessionAllocationResult.Failure(
                SessionAllocationError.WebProfileLocked,
                $"No free Web profile could be reserved for account '{accountId.Trim()}'.");
        }
    }

    public bool TryUpdateStatus(Guid sessionId, SessionStatus status, out ActiveSession? updated) =>
        _registry.TryUpdateStatus(sessionId, status, out updated);

    public bool Release(Guid sessionId) => _registry.Remove(sessionId);
}
