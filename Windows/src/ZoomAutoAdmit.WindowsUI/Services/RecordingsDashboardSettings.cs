using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>
/// Server: this PC runs the central backend (and its agent) and shows its dashboard.
/// Client: the dashboard of a backend that runs somewhere else, e.g. a coordinator's PC showing the
/// admin's server through its public HTTPS address.
/// </summary>
public enum DashboardMode { Server, Client }

/// <summary>
/// Where the Recordings page gets its dashboard from, and how this PC runs the backend in server
/// mode. Kept in %LOCALAPPDATA%\ZoomAutoAdmit\Central\dashboard.json. Holds no secret: the database
/// password is in Windows Credential Manager (<see cref="DatabasePasswordStore"/>).
/// </summary>
public sealed record RecordingsDashboardSettings
{
    public DashboardMode Mode { get; init; } = DashboardMode.Server;

    /// <summary>Client mode: the backend's base address, e.g. https://server.example.ts.net.</summary>
    public string ServerUrl { get; init; } = "";

    /// <summary>Server mode: start the backend and the agent when the app opens.</summary>
    public bool StartWithApp { get; init; } = true;

    /// <summary>Server mode: the Backend folder (holding central_backend and .venv).</summary>
    public string BackendFolder { get; init; } = "";

    public int Port { get; init; } = 8765;
    public string DatabaseHost { get; init; } = "127.0.0.1";
    public int DatabasePort { get; init; } = 5433;
    public string DatabaseUser { get; init; } = "postgres";
    public string DatabaseName { get; init; } = "postgres";

    /// <summary>The origin the page may show; everything else opens in the normal browser.</summary>
    [JsonIgnore]
    public Uri? BaseUri => Mode == DashboardMode.Server
        ? new Uri($"http://127.0.0.1:{Port}/")
        : Uri.TryCreate(ServerUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) && IsWebAddress(uri) ? uri : null;

    [JsonIgnore]
    public Uri? DashboardUri => BaseUri is { } b ? new Uri(b, "dashboard/") : null;

    /// <summary>Why these settings cannot be used, or null when they can.</summary>
    public string? Problem()
    {
        if (Mode == DashboardMode.Client)
        {
            if (string.IsNullOrWhiteSpace(ServerUrl)) return "Enter the server address, e.g. https://your-server.ts.net.";
            if (BaseUri is null) return "The server address must be a full http(s) address.";
            if (BaseUri.Scheme != Uri.UriSchemeHttps && !BaseUri.IsLoopback) return "Use an https:// address for a server on another computer.";
            return null;
        }
        if (Port is < 1 or > 65535 || DatabasePort is < 1 or > 65535) return "The ports must be between 1 and 65535.";
        if (!BackendExists(BackendFolder)) return "The Backend folder was not found. Set it on this page.";
        return null;
    }

    /// <summary>True for a page inside this dashboard's origin (scheme, host and port).</summary>
    public bool IsInside(Uri target) =>
        BaseUri is { } b && Uri.Compare(target, b, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    public static bool BackendExists(string folder) =>
        !string.IsNullOrWhiteSpace(folder) && Directory.Exists(Path.Combine(folder, "central_backend")) &&
        File.Exists(Path.Combine(folder, ".venv", "Scripts", "python.exe"));

    private static bool IsWebAddress(Uri uri) => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>The repository's Backend folder, found by walking up from the app's own folder.</summary>
    public static string FindBackendFolder(string? start = null)
    {
        var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        for (var d = dir; d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "Backend");
            if (BackendExists(candidate)) return candidate;
        }
        return "";
    }
}

public interface IRecordingsDashboardSettingsStore
{
    RecordingsDashboardSettings Load();
    void Save(RecordingsDashboardSettings settings);
}

public sealed class RecordingsDashboardSettingsStore(string? path = null) : IRecordingsDashboardSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string Path { get; } = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Central", "dashboard.json");

    /// <summary>The saved settings; the first time, a server if this PC has the Backend, else a client.</summary>
    public RecordingsDashboardSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var saved = JsonSerializer.Deserialize<RecordingsDashboardSettings>(File.ReadAllText(Path), Json);
                if (saved != null)
                    return string.IsNullOrWhiteSpace(saved.BackendFolder) ? saved with { BackendFolder = RecordingsDashboardSettings.FindBackendFolder() } : saved;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            ZoomAutoAdmit.WindowsUI.Infrastructure.WindowsUiErrorLog.Write("The dashboard settings could not be read; using defaults.", ex);
        }
        var backend = RecordingsDashboardSettings.FindBackendFolder();
        return new RecordingsDashboardSettings { BackendFolder = backend, Mode = backend.Length > 0 ? DashboardMode.Server : DashboardMode.Client };
    }

    public void Save(RecordingsDashboardSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, Path, overwrite: true);
    }
}
