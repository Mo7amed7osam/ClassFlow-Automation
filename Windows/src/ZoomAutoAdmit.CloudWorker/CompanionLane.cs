using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ZoomAutoAdmit.CentralAgent;

namespace ZoomAutoAdmit.CloudWorker;

/// <summary>
/// The worker's second device: the lane its LMS stages run in.
///
/// A device takes one job at a time, and the meeting lane is busy for the whole of every class it
/// holds. On Windows the LMS steps have their own queue beside the meeting; here they would wait
/// three hours behind it, and Run Session at the start of a class would happen at its end. So the
/// worker enrols a companion for the LMS, with a single-use token its own device token asks the
/// server for. The operator still makes one enrolment token, and never sees this one.
/// </summary>
public static class CompanionLane
{
    private sealed record Grant(
        [property: JsonPropertyName("enrollmentToken")] string EnrollmentToken,
        [property: JsonPropertyName("name")] string Name);

    /// <summary>The companion's identity, enrolled now if it was not yet, or null with the reason logged.</summary>
    public static async Task<DeviceIdentity?> EnsureAsync(
        HttpClient http, Func<string?> parentToken, DeviceIdentityStore identities, IDeviceTokenStore tokens,
        Uri backend, string version, Action<string> log, CancellationToken cancellationToken)
    {
        try
        {
            if (identities.Load() is { IsRegistered: true } known && tokens.Read() is not null)
                return known;
        }
        catch (InvalidDataException problem)
        {
            log($"the LMS lane's identity is unreadable ({problem.Message}); enrolling it again");
        }

        if (parentToken() is not { Length: > 0 } token)
        {
            log("the LMS lane cannot be enrolled before the worker itself is");
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/agent/companion-enrollment");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                log($"the server would not enrol the LMS lane: {(int)response.StatusCode} "
                    + await Stages.ServerAttendanceNames.DetailsAsync(response, cancellationToken));
                return null;
            }
            var grant = await response.Content.ReadFromJsonAsync<Grant>(cancellationToken)
                        ?? throw new InvalidDataException("the server's answer held no token");

            var identity = await new AgentRegistrar(http, identities, tokens, log).RegisterAsync(
                backend, grant.EnrollmentToken, grant.Name, version, ["lms"], cancellationToken);
            log($"the LMS lane is enrolled as {grant.Name} ({identity.DeviceId})");
            return identity;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception problem)
        {
            log($"the LMS lane could not be enrolled: {problem.Message}");
            return null;
        }
    }
}
