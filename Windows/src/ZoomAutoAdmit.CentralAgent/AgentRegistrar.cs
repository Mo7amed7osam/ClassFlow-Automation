using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ZoomAutoAdmit.CentralAgent;

public sealed class AgentRegistrationException(string message) : Exception(message);

/// <summary>
/// Registers this installation once: spends an operator's single-use enrollment token and keeps
/// the device token the backend issues. Registering again (with a new enrollment token) keeps the
/// installation id - and so the device - and replaces the token.
/// </summary>
public sealed class AgentRegistrar(HttpClient http, DeviceIdentityStore identities, IDeviceTokenStore tokens, Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });

    public async Task<DeviceIdentity> RegisterAsync(Uri backendUrl, string enrollmentToken, string name, string version,
        IReadOnlyList<string> capabilities, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentToken);
        var backend = CentralAgentSettings.ParseBackendUrl(backendUrl.ToString());
        var settings = new CentralAgentSettings { BackendUrl = backend, Version = version };
        var identity = identities.LoadOrCreate();
        string deviceName = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();

        using var response = await http.PostAsJsonAsync(settings.RegisterUri, new
        {
            enrollmentToken = enrollmentToken.Trim(),
            installationId = identity.InstallationId,
            name = deviceName.Length > 100 ? deviceName[..100] : deviceName,
            version,
            capabilities,
        }, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new AgentRegistrationException("The backend refused the enrollment token: it is unknown, already used or expired. Ask for a new one.");
        if (response.StatusCode != HttpStatusCode.Created)
            throw new AgentRegistrationException($"The backend answered {(int)response.StatusCode} to the registration.");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!body.RootElement.TryGetProperty("deviceId", out var idElement) || !Guid.TryParse(idElement.GetString(), out var deviceId) ||
            !body.RootElement.TryGetProperty("deviceToken", out var tokenElement) || tokenElement.GetString() is not { } deviceToken ||
            !deviceToken.StartsWith($"zaad_{deviceId}.", StringComparison.Ordinal))
            throw new AgentRegistrationException("The backend's registration answer was not understood.");

        tokens.Save(deviceToken);
        var registered = identity with
        {
            DeviceId = deviceId,
            BackendUrl = backend.ToString(),
            Name = deviceName,
            RegisteredAt = DateTimeOffset.UtcNow,
        };
        identities.Save(registered);
        _log(AgentLog.Line("registered", ("deviceId", deviceId), ("name", deviceName), ("backend", backend.Host)));
        return registered;
    }
}
