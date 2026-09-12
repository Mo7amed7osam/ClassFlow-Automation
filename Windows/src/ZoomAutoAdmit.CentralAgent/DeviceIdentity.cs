using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZoomAutoAdmit.CentralAgent;

/// <summary>
/// Who this installation is to the backend. Nothing secret: the device token is kept apart, in
/// Windows Credential Manager (<see cref="CredentialManagerDeviceTokenStore"/>).
/// </summary>
public sealed record DeviceIdentity
{
    /// <summary>Made once, the first time, and never again: it is how the backend recognises this PC.</summary>
    public required Guid InstallationId { get; init; }
    public Guid? DeviceId { get; init; }
    public string? BackendUrl { get; init; }
    public string? Name { get; init; }
    public DateTimeOffset? RegisteredAt { get; init; }

    [JsonIgnore]
    public bool IsRegistered => DeviceId is not null && !string.IsNullOrWhiteSpace(BackendUrl);
}

/// <summary>%LOCALAPPDATA%\ZoomAutoAdmit\Central\device.json, written atomically.</summary>
public sealed class DeviceIdentityStore(string? path = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();

    public string Path { get; } = path ?? System.IO.Path.Combine(CentralAgentPaths.Folder, "device.json");

    public DeviceIdentity? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(Path)) return null;
            try { return JsonSerializer.Deserialize<DeviceIdentity>(File.ReadAllText(Path), Json); }
            catch (JsonException) { throw new InvalidDataException($"{Path} is not a valid device identity file."); }
        }
    }

    /// <summary>The identity, creating (and saving) the installation id the first time only.</summary>
    public DeviceIdentity LoadOrCreate()
    {
        lock (_gate)
        {
            var existing = Load();
            if (existing != null && existing.InstallationId != Guid.Empty) return existing;
            var created = new DeviceIdentity { InstallationId = Guid.NewGuid() };
            Save(created);
            return created;
        }
    }

    public void Save(DeviceIdentity identity)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            string temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(identity, Json));
            File.Move(temporary, Path, overwrite: true);
        }
    }
}

public static class CentralAgentPaths
{
    public static string Folder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Central");

    public static string LogFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Logs", "central-agent.log");
}
