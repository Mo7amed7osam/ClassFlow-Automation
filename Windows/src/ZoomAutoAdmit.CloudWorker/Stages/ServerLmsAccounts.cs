using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ZoomAutoAdmit.WebAutomation.Lms;

namespace ZoomAutoAdmit.CloudWorker.Stages;

/// <summary>
/// The LMS sign-in for the class a job is a stage of, asked of the server with this device's token.
///
/// The server does not hand out a coordinator's password for the asking: it answers only for a job
/// this device holds and has not finished, only for the account that job's own payload names, and
/// only while that coordinator is turned on. So the credential follows the work rather than sitting
/// on the machine, and a token that leaks opens only what it was already told to do.
///
/// What comes back is kept in memory for the length of the stage and nowhere else. A worker that
/// restarts asks again.
/// </summary>
public sealed class ServerLmsAccounts(
    HttpClient http,
    Func<string?> deviceToken,
    Func<Guid?> currentJobId,
    Action<string>? log = null) : ILmsAccounts
{
    private readonly Action<string> _log = log ?? (_ => { });

    public async Task<ILmsCredentialStore?> ForAsync(Guid lmsAccountId, CancellationToken cancellationToken)
    {
        if (deviceToken() is not { Length: > 0 } token)
        {
            _log("[lms-accounts] this worker has no device token yet, so it cannot ask for a sign-in.");
            return null;
        }
        if (currentJobId() is not { } jobId)
        {
            // The server ties a sign-in to a job. Asking without one would be refused anyway, and
            // saying so here names the real problem instead of reporting a 404 from the server.
            _log("[lms-accounts] no job is being run, and a sign-in is only given for one that is.");
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/agent/jobs/{jobId}/lms-secret");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // The reason, never the body: the body of a successful one holds a password, and
            // logging bodies by habit is how one ends up in a log.
            _log($"[lms-accounts] the server would not give this class's sign-in: {(int)response.StatusCode} " +
                 $"{Reason(response.StatusCode)}");
            return null;
        }

        var answer = await response.Content.ReadFromJsonAsync<Secret>(cancellationToken);
        if (answer is null || string.IsNullOrWhiteSpace(answer.Email) || string.IsNullOrWhiteSpace(answer.Password))
        {
            _log("[lms-accounts] the server's answer held no sign-in.");
            return null;
        }

        // Held for this stage only, in an object of its own: nothing else in the worker can
        // reach it, and it goes when the stage does. The profile is the account's, so two
        // coordinators' stages never share a browser session and sign each other out.
        _log($"[lms-accounts] {answer.Email} for this stage");
        return new StageSignIn(new LmsAccount(answer.Email, answer.Password), $"lms-{lmsAccountId}");
    }

    private static string Reason(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "this worker's device token was not accepted; it may have been revoked",
        HttpStatusCode.NotFound => "no such job for this device",
        HttpStatusCode.Forbidden => "that coordinator is not turned on",
        HttpStatusCode.Conflict => "the job is not one being run, or names an account that is gone",
        _ => "unexpected",
    };

    private sealed record Secret(
        [property: System.Text.Json.Serialization.JsonPropertyName("email")] string Email,
        [property: System.Text.Json.Serialization.JsonPropertyName("password")] string Password);

    /// <summary>
    /// One sign-in, for one stage, in memory and nowhere else. Saving and deleting are refused
    /// rather than quietly doing nothing: the server is where these live, and a worker that
    /// thought it had written one would be wrong.
    /// </summary>
    internal sealed class StageSignIn(LmsAccount account, string profile) : ILmsCredentialStore
    {
        public string Profile { get; } = profile;

        public LmsAccount? Read() => account;

        public void Save(LmsAccount _) =>
            throw new NotSupportedException("An LMS sign-in is kept on the server, not by the worker.");

        public void Delete() =>
            throw new NotSupportedException("An LMS sign-in is removed on the server, not by the worker.");
    }
}
