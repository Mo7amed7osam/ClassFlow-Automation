namespace ZoomAutoAdmit.SessionRoles;

// What the bridge needs from whoever presses the buttons. Kept apart from the Zoom-app
// implementations so they build on every platform: the web client's assigner and the bridge
// itself run on a Linux worker too.

public sealed record CoHostOutcome(bool Success, string Message, bool AlreadyCoHost = false);

public interface ICoHostAssigner
{
    CoHostOutcome Assign(string observedDisplayName, CancellationToken token = default);
}

/// <summary>Who is sharing their screen in the running meeting, if anybody.</summary>
public interface IPresenterSource
{
    /// <summary>The Zoom display name of the person presenting, or null when nobody is.</summary>
    string? WhoIsPresenting(CancellationToken cancellationToken);
}

/// <summary>Nobody is presenting. Used when the meeting is not a Zoom Desktop one.</summary>
public sealed class NoPresenterSource : IPresenterSource
{
    public static NoPresenterSource Instance { get; } = new();
    public string? WhoIsPresenting(CancellationToken cancellationToken) => null;
}
