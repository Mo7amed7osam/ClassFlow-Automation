namespace ZoomAutoAdmit.Core.Sessions;

public sealed record SessionAllocationDecision(
    bool IsAllowed,
    SessionEngineType? EngineType,
    SessionAllocationError Error,
    string? ErrorMessage)
{
    public static SessionAllocationDecision Use(SessionEngineType engineType) =>
        new(true, engineType, SessionAllocationError.None, null);

    public static SessionAllocationDecision Reject(SessionAllocationError error, string message) =>
        new(false, null, error, message);
}

public sealed class SessionAllocationPolicy
{
    /// <summary>Simultaneous Web meetings allowed for one account, each with its own profile.</summary>
    public const int MaxWebInstancesPerAccount = 8;

    public SessionAllocationDecision Decide(
        IReadOnlyCollection<ActiveSession> activeSessions,
        string accountWebProfileName)
    {
        bool desktopOccupied = activeSessions.Any(session =>
            session.OccupiesCapacity && session.EngineType == SessionEngineType.Desktop);
        return desktopOccupied
            ? SessionAllocationDecision.Use(SessionEngineType.Web)
            : SessionAllocationDecision.Use(SessionEngineType.Desktop);
    }

    /// <summary>
    /// First Web profile name for this account that no active session holds. The same account can
    /// run several Web meetings at once; each simply needs its own browser directory.
    /// </summary>
    public static string? NextFreeWebProfile(
        IReadOnlyCollection<ActiveSession> activeSessions,
        string baseProfileName)
    {
        for (int instance = 1; instance <= MaxWebInstancesPerAccount; instance++)
        {
            string candidate = AccountWebProfile.ForProfileInstance(baseProfileName, instance);
            bool taken = activeSessions.Any(session =>
                session.OccupiesCapacity &&
                session.EngineType == SessionEngineType.Web &&
                string.Equals(session.WebProfileName, candidate, StringComparison.OrdinalIgnoreCase));
            if (!taken) return candidate;
        }
        return null;
    }
}
