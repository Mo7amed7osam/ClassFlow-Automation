namespace ZoomAutoAdmit.WebAutomation.Browser;

public sealed record ZoomBrowserProfile(
    string Name,
    string DirectoryPath,
    string ReadyMarkerPath,
    bool HasReusableSession);

public sealed record ZoomBrowserLaunchPlan(
    ZoomBrowserProfile Profile,
    bool Headless)
{
    /// <summary>
    /// Extra Chrome switches for this launch. A site that asks the browser to remember a
    /// password gets a bubble over the page, and that bubble swallows the clicks that follow,
    /// so the automation that signs in anywhere needs to turn those prompts off.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];
}
