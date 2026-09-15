using System.IO;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.WindowsUI.ViewModels;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

public sealed class RecordingsDashboardTests : IDisposable
{
    // Made-up values, for these tests only.
    private const string HashA = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string HashB = "0000000000000000000000000000000000000000000000000000000000000002";
    private readonly string _temp = Directory.CreateTempSubdirectory("recordings-dashboard-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }

    private string FakeBackend()
    {
        var backend = Path.Combine(_temp, "repo", "Backend");
        Directory.CreateDirectory(Path.Combine(backend, "central_backend"));
        Directory.CreateDirectory(Path.Combine(backend, ".venv", "Scripts"));
        File.WriteAllText(Path.Combine(backend, ".venv", "Scripts", "python.exe"), "");
        return backend;
    }

    // ------------------------------------------------------------------ api.env

    [Fact]
    public void OnlyLiteralCentralAssignmentsAreReadFromApiEnv()
    {
        var lines = new[]
        {
            "# notes",
            "API key (give it to n8n; it is not stored anywhere):",
            "  zaak_not-a-real-key-just-a-line-of-output",
            "$env:CENTRAL_DATABASE_URL = \"postgresql+asyncpg://postgres@127.0.0.1:5439/postgres\"",
            "$env:CENTRAL_ENVIRONMENT = \"development\"",
            $"$env:CENTRAL_CLIENT_API_KEY_HASHES = \"{HashA}\"",
            "Remove-Item Env:\\CENTRAL_ADMIN_USERS",
            "$env:CENTRAL_ADMIN_USERS = \"$(Get-Secret)\"",                  // an expression: never read, never run
            "$env:OTHER_THING = \"x\"",
            $"$env:CENTRAL_CLIENT_API_KEY_HASHES = '{HashB}, {HashA}'",
        };
        var values = BackendEnvironment.ReadApiEnv(lines);
        Assert.Equal(new[] { "CENTRAL_CLIENT_API_KEY_HASHES", "CENTRAL_DATABASE_URL", "CENTRAL_ENVIRONMENT" }, values.Keys.Order());
        Assert.Equal($"{HashA},{HashB}", values["CENTRAL_CLIENT_API_KEY_HASHES"]);   // every hash ever set, once
        Assert.Equal("development", values["CENTRAL_ENVIRONMENT"]);
    }

    [Fact]
    public void TheBackendGetsTheConfiguredDatabaseWithAnEscapedPassword()
    {
        var settings = new RecordingsDashboardSettings { DatabaseHost = "127.0.0.1", DatabasePort = 5433, DatabaseUser = "postgres", DatabaseName = "postgres" };
        var env = BackendEnvironment.Build(settings, "p@ss:w/rd #1", new[]
        {
            "$env:CENTRAL_DATABASE_URL = \"postgresql+asyncpg://postgres@127.0.0.1:5439/postgres\"",
            $"$env:CENTRAL_CLIENT_API_KEY_HASHES = \"{HashA}\"",
            "$env:CENTRAL_ADMIN_USERS = \"admin:scrypt$old\"",
        });
        Assert.Equal("postgresql+asyncpg://postgres:p%40ss%3Aw%2Frd%20%231@127.0.0.1:5433/postgres", env["CENTRAL_DATABASE_URL"]);
        Assert.False(env.ContainsKey("CENTRAL_ADMIN_USERS"));
        Assert.Equal("development", env["CENTRAL_ENVIRONMENT"]);
        Assert.Throws<InvalidOperationException>(() => BackendEnvironment.Build(settings, "x", new[] { "# no hashes" }));
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public void ServerAndClientAddresses()
    {
        var server = new RecordingsDashboardSettings { Mode = DashboardMode.Server, Port = 8765 };
        Assert.Equal("http://127.0.0.1:8765/dashboard/", server.DashboardUri!.AbsoluteUri);
        Assert.True(server.IsInside(new Uri("http://127.0.0.1:8765/api/v1/auth/me")));
        Assert.False(server.IsInside(new Uri("http://127.0.0.1:8080/dashboard/")));
        Assert.False(server.IsInside(new Uri("https://drive.google.com/file/d/x/view")));

        var client = new RecordingsDashboardSettings { Mode = DashboardMode.Client, ServerUrl = " https://server.example.ts.net/ " };
        Assert.Equal("https://server.example.ts.net/dashboard/", client.DashboardUri!.AbsoluteUri);
        Assert.True(client.IsInside(new Uri("https://SERVER.example.ts.net/dashboard/recordings")));
        Assert.False(client.IsInside(new Uri("http://server.example.ts.net/dashboard/")));      // another scheme
        Assert.Null(client.Problem());
    }

    [Theory]
    [InlineData("", "Enter the server address")]
    [InlineData("not an address", "full http(s) address")]
    [InlineData("ftp://server.example", "full http(s) address")]
    [InlineData("http://server.example.ts.net", "https://")]
    public void AClientNeedsAFullHttpsAddress(string url, string problem)
    {
        var client = new RecordingsDashboardSettings { Mode = DashboardMode.Client, ServerUrl = url };
        Assert.Contains(problem, client.Problem());
    }

    [Fact]
    public void AServerNeedsItsBackendFolder()
    {
        Assert.Contains("Backend folder", new RecordingsDashboardSettings { BackendFolder = _temp }.Problem());
        Assert.Null(new RecordingsDashboardSettings { BackendFolder = FakeBackend() }.Problem());
    }

    [Fact]
    public void SettingsAreSavedAndTheBackendIsFoundByWalkingUp()
    {
        var backend = FakeBackend();
        var deep = Directory.CreateDirectory(Path.Combine(_temp, "repo", "Windows", "src", "App", "bin")).FullName;
        Assert.Equal(backend, RecordingsDashboardSettings.FindBackendFolder(deep));

        var store = new RecordingsDashboardSettingsStore(Path.Combine(_temp, "Central", "dashboard.json"));
        var saved = new RecordingsDashboardSettings { Mode = DashboardMode.Client, ServerUrl = "https://server.example.ts.net", StartWithApp = false, BackendFolder = backend };
        store.Save(saved);
        Assert.Equal(saved, store.Load());
        Assert.DoesNotContain("password", File.ReadAllText(store.Path), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ the page's model

    private sealed class MemoryStore(RecordingsDashboardSettings settings) : IRecordingsDashboardSettingsStore
    {
        public RecordingsDashboardSettings Saved { get; private set; } = settings;
        public RecordingsDashboardSettings Load() => Saved;
        public void Save(RecordingsDashboardSettings s) => Saved = s;
    }

    private sealed class MemoryPasswords : IDatabasePasswordStore
    {
        public string? Value { get; private set; }
        public bool HasPassword => Value != null;
        public string? Read() => Value;
        public void Save(string password) => Value = password;
        public void Delete() => Value = null;
    }

    private sealed class FakeHost : ICentralServerHost
    {
        public int Starts, Stops;
        public ServerStatus Status { get; private set; } = ServerStatus.Initial;
        public event Action<ServerStatus>? StatusChanged;
        public Task StartAsync(RecordingsDashboardSettings settings, CancellationToken cancellationToken = default)
        {
            Starts++;
            Set(new ServerStatus(ServerState.Running, AgentState.Running, "The backend is running."));
            return Task.CompletedTask;
        }
        public void Stop() { Stops++; Set(new ServerStatus(ServerState.Stopped, AgentState.Stopped, "Stopped.")); }
        public void Set(ServerStatus status) { Status = status; StatusChanged?.Invoke(status); }
        public void Dispose() { }
    }

    [Fact]
    public async Task AServerStartsWithTheAppOnlyOnceItsPasswordIsSaved()
    {
        var host = new FakeHost();
        var passwords = new MemoryPasswords();
        var store = new MemoryStore(new RecordingsDashboardSettings { Mode = DashboardMode.Server, BackendFolder = FakeBackend(), StartWithApp = true });
        using var model = new RecordingsDashboardViewModel(store, passwords, host);
        Assert.True(model.ShowConnection);                                 // nothing to run with yet: show the settings
        Assert.False(model.CanShowDashboard);

        await model.StartIfConfiguredAsync();
        Assert.Equal(0, host.Starts);

        model.SavePassword("made-up db password");
        Assert.Equal("made-up db password", passwords.Value);
        Assert.True(model.HasPassword);
        var reloads = 0;
        model.ReloadRequested += () => reloads++;
        await model.StartIfConfiguredAsync();
        Assert.Equal(1, host.Starts);
        Assert.True(model.CanShowDashboard);
        Assert.Equal(1, reloads);                                          // the page loads as soon as the server answers
        Assert.Equal("good", model.StatusTone);
        Assert.Equal("Server running on this PC", model.StatusText);
    }

    [Fact]
    public void SwitchingToAClientStopsTheServerAndShowsTheOtherDashboard()
    {
        var host = new FakeHost();
        var store = new MemoryStore(new RecordingsDashboardSettings { Mode = DashboardMode.Server, BackendFolder = FakeBackend() });
        using var model = new RecordingsDashboardViewModel(store, new MemoryPasswords(), host);
        model.IsClientMode = true;
        model.ServerUrl = "http://server.example.ts.net";
        Assert.False(model.Save());                                        // plain http to another PC is refused
        Assert.Contains("https://", model.Notice);
        Assert.Equal(DashboardMode.Server, store.Saved.Mode);

        model.ServerUrl = "https://server.example.ts.net";
        Assert.True(model.Save());
        Assert.Equal(DashboardMode.Client, store.Saved.Mode);
        Assert.Equal(1, host.Stops);
        Assert.True(model.CanShowDashboard);
        Assert.Equal("https://server.example.ts.net/dashboard/", model.DashboardUri!.AbsoluteUri);
        Assert.Equal("Connected to server.example.ts.net", model.StatusText);
    }

    [Fact]
    public void AnEmptyPasswordIsNotSaved()
    {
        var passwords = new MemoryPasswords();
        using var model = new RecordingsDashboardViewModel(new MemoryStore(new RecordingsDashboardSettings { BackendFolder = FakeBackend() }), passwords, new FakeHost());
        model.SavePassword("");
        Assert.Null(passwords.Value);
        Assert.Contains("Type the database password", model.Notice);
    }
}
