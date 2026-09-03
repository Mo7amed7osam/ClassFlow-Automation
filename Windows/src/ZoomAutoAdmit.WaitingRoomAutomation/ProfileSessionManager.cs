using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace ZoomAutoAdmit.WaitingRoomAutomation;

public interface IProfileSessionManager : IAsyncDisposable
{
    Task<IWebProfileSession> OpenMeetingAsync(
        Guid sessionId,
        string profileName,
        Uri meetingUrl,
        CancellationToken cancellationToken = default);

    Task<IWebProfileSession> OpenSetupAsync(
        Guid sessionId,
        string profileName,
        CancellationToken cancellationToken = default);

    Task CloseAsync(Guid sessionId);
    Task CloseAllAsync();
}

public interface IWebProfileSession : IAsyncDisposable
{
    Guid SessionId { get; }
    string ProfileName { get; }
    string ProfilePath { get; }
    IBrowserContext Context { get; }
    IPage MeetingPage { get; }
}

public sealed class WebProfileSession : IWebProfileSession
{
    private readonly IPlaywright _playwright;
    private readonly Func<ValueTask> _onDisposed;
    private int _disposed;

    internal WebProfileSession(
        Guid sessionId,
        string profileName,
        string profilePath,
        IPlaywright playwright,
        IBrowserContext context,
        IPage meetingPage,
        Func<ValueTask> onDisposed)
    {
        SessionId = sessionId;
        ProfileName = profileName;
        ProfilePath = profilePath;
        _playwright = playwright;
        Context = context;
        MeetingPage = meetingPage;
        _onDisposed = onDisposed;
    }

    public Guid SessionId { get; }
    public string ProfileName { get; }
    public string ProfilePath { get; }
    public IBrowserContext Context { get; }
    public IPage MeetingPage { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            try { await Context.CloseAsync(); }
            catch { }
            _playwright.Dispose();
        }
        finally
        {
            await _onDisposed();
        }
    }
}

public sealed class ProfileSessionManager : IProfileSessionManager
{
    private readonly string _profilesRoot;
    private readonly ConcurrentDictionary<Guid, IWebProfileSession> _sessions = new();
    private readonly ConcurrentDictionary<string, Guid> _profileOwners =
        new(StringComparer.OrdinalIgnoreCase);

    public ProfileSessionManager(string? profilesRoot = null)
    {
        _profilesRoot = Path.GetFullPath(profilesRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZoomAutoAdmit",
            "Profiles"));
    }

    public Task<IWebProfileSession> OpenMeetingAsync(
        Guid sessionId,
        string profileName,
        Uri meetingUrl,
        CancellationToken cancellationToken = default) =>
        OpenAsync(sessionId, profileName, NormalizeMeetingUrl(meetingUrl), cancellationToken);

    public Task<IWebProfileSession> OpenSetupAsync(
        Guid sessionId,
        string profileName,
        CancellationToken cancellationToken = default) =>
        OpenAsync(sessionId, profileName, new Uri("https://zoom.us/signin"), cancellationToken);

    public async Task CloseAsync(Guid sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            await session.DisposeAsync();
    }

    public async Task CloseAllAsync()
    {
        foreach (var session in _sessions.Values.ToArray())
            await session.DisposeAsync();
    }

    public async ValueTask DisposeAsync() => await CloseAllAsync();

    private async Task<IWebProfileSession> OpenAsync(
        Guid sessionId,
        string profileName,
        Uri destination,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A session ID is required.", nameof(sessionId));
        string normalizedProfile = NormalizeProfileName(profileName);
        if (!_profileOwners.TryAdd(normalizedProfile, sessionId))
            throw new InvalidOperationException($"Web profile '{normalizedProfile}' is already in use.");

        IPlaywright? playwright = null;
        IBrowserContext? context = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string profilePath = GetProfilePath(normalizedProfile);
            playwright = await Playwright.CreateAsync();
            context = await playwright.Chromium.LaunchPersistentContextAsync(
                profilePath,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = false,
                    ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
                    Args = ["--disable-blink-features=AutomationControlled"]
                });

            cancellationToken.ThrowIfCancellationRequested();
            IPage page = context.Pages.Count > 0
                ? context.Pages[0]
                : await context.NewPageAsync();
            await page.GotoAsync(destination.AbsoluteUri);

            var session = new WebProfileSession(
                sessionId,
                normalizedProfile,
                profilePath,
                playwright,
                context,
                page,
                () =>
                {
                    _sessions.TryRemove(sessionId, out _);
                    _profileOwners.TryRemove(
                        new KeyValuePair<string, Guid>(normalizedProfile, sessionId));
                    return ValueTask.CompletedTask;
                });

            if (!_sessions.TryAdd(sessionId, session))
            {
                await session.DisposeAsync();
                throw new InvalidOperationException($"Session '{sessionId}' already exists.");
            }

            return session;
        }
        catch
        {
            if (context != null)
            {
                try { await context.CloseAsync(); }
                catch { }
            }
            playwright?.Dispose();
            _profileOwners.TryRemove(
                new KeyValuePair<string, Guid>(normalizedProfile, sessionId));
            throw;
        }
    }

    private string GetProfilePath(string profileName)
    {
        Directory.CreateDirectory(_profilesRoot);
        string path = Path.GetFullPath(Path.Combine(_profilesRoot, profileName));
        string rootWithSeparator = _profilesRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Resolved browser profile escaped the Profiles directory.");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string NormalizeProfileName(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("A Web profile name is required.", nameof(profileName));
        return string.Join("_", profileName.Trim().Split(Path.GetInvalidFileNameChars()));
    }

    private static Uri NormalizeMeetingUrl(Uri meetingUrl)
    {
        ArgumentNullException.ThrowIfNull(meetingUrl);
        string url = meetingUrl.AbsoluteUri;
        if (url.Contains("zoom.us/j/", StringComparison.OrdinalIgnoreCase))
            url = url.Replace("zoom.us/j/", "zoom.us/wc/join/", StringComparison.OrdinalIgnoreCase);
        return new Uri(url);
    }
}
