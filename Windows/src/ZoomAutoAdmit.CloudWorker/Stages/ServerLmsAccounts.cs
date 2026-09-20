using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ZoomAutoAdmit.WebAutomation;
using ZoomAutoAdmit.WebAutomation.Lms;

namespace ZoomAutoAdmit.CloudWorker.Stages;

/// <summary>What the server answers with when a worker asks for the account a job names.</summary>
public sealed record AccountSecret(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password);

/// <summary>
/// Asking the server for the sign-in a job needs. One place, because the two kinds of account
/// follow the same rules and two copies would eventually stop agreeing about what a refusal means.
///
/// The server ties the answer to the work: only a job this device holds and has not finished, only
/// the account that job's payload names, and only while that coordinator is turned on. So a device
/// never given a class never gets a password, and a leaked token opens only what it was told to do.
/// </summary>
internal static class ServerAccountSecret
{
    public static async Task<AccountSecret?> AskAsync(
        HttpClient http, string? deviceToken, Guid? jobId, string path,
        Action<string> log, string area, CancellationToken cancellationToken)
    {
        if (deviceToken is not { Length: > 0 } token)
        {
            log($"[{area}] this worker has no device token yet, so it cannot ask for a sign-in.");
            return null;
        }
        if (jobId is not { } job)
        {
            log($"[{area}] no job is being run, and a sign-in is only given for one that is.");
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/agent/jobs/{job}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // The reason, never the body: a successful body holds a password, and logging bodies
            // by habit is how one ends up in a log.
            log($"[{area}] the server would not give this class's sign-in: {(int)response.StatusCode} {Reason(response.StatusCode)}");
            return null;
        }

        var answer = await response.Content.ReadFromJsonAsync<AccountSecret>(cancellationToken);
        if (answer is null || string.IsNullOrWhiteSpace(answer.Email) || string.IsNullOrWhiteSpace(answer.Password))
        {
            log($"[{area}] the server's answer held no sign-in.");
            return null;
        }
        log($"[{area}] {answer.Email} for this stage");
        return answer;
    }

    private static string Reason(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "this worker's device token was not accepted; it may have been revoked",
        HttpStatusCode.NotFound => "no such job for this device",
        HttpStatusCode.Forbidden => "that coordinator is not turned on",
        HttpStatusCode.Conflict => "the job is not one being run, names no account, or no password is saved",
        _ => "unexpected",
    };
}

/// <summary>
/// The Zoom sign-in for the class a job is opening, asked of the server with this device's token.
/// The same endpoint shape and the same rules as the LMS one beside it.
/// </summary>
public sealed class ServerZoomAccounts(
    HttpClient http,
    Func<string?> deviceToken,
    Func<Guid?> currentJobId,
    Action<string>? log = null) : IZoomAccounts
{
    private readonly Action<string> _log = log ?? (_ => { });

    public async Task<ZoomSignInCredential?> ForAsync(Guid zoomAccountId, CancellationToken cancellationToken)
    {
        var answer = await ServerAccountSecret.AskAsync(
            http, deviceToken(), currentJobId(), "zoom-secret", _log, "zoom-accounts", cancellationToken);
        return answer is null ? null : new ZoomSignInCredential(answer.Email, answer.Password);
    }
}

/// <summary>
/// The LMS sign-in for the class a job is a stage of. Held in memory for the one stage and nowhere
/// else; a worker that restarts asks again.
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
        var answer = await ServerAccountSecret.AskAsync(
            http, deviceToken(), currentJobId(), "lms-secret", _log, "lms-accounts", cancellationToken);
        // The profile is the account's, so two coordinators' stages never share a browser session
        // and sign each other out.
        return answer is null ? null
            : new StageSignIn(new LmsAccount(answer.Email, answer.Password), $"lms-{lmsAccountId}");
    }

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
