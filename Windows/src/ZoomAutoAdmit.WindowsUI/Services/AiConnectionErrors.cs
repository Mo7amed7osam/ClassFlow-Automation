using System.IO;
using System.Net.Http;
using System.Text.Json;
using ZoomAutoAdmit.AttendanceMatching;

namespace ZoomAutoAdmit.WindowsUI.Services;

public static class AiConnectionErrors
{
    public static string Describe(Exception ex, AiProvider provider = AiProvider.OpenAI) => ex switch
    {
        OperationCanceledException => "Cancelled or timed out. No success recorded; retry when ready.",
        AiProviderException { Code: "invalid_api_key" } => $"Not valid — {provider} rejected the API key (invalid_api_key). No key was saved.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "Not valid — authentication rejected (HTTP 401). Check or replace the API key. No key was saved.",
        AiProviderException { Code: "insufficient_quota" } => $"Test failed — insufficient API credit/quota (HTTP 429). Check {provider} API billing. This does not mean the key is invalid.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.PaymentRequired } => $"Test failed — insufficient credits (HTTP 402). Check {provider} balance. This does not mean the key is invalid.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "Test failed — access forbidden (HTTP 403). Check project/model permissions or regional restrictions.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } when provider == AiProvider.OpenRouter =>
            "Test failed — model not found, or no provider endpoint accepted this request (HTTP 404). Check the model ID, your OpenRouter account access, and its privacy/data-policy settings.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => "Test failed — model not found or not accessible (HTTP 404). Check the selected model ID and account access.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "Test failed — rate limit or quota exceeded (HTTP 429). Check API billing/limits and retry later.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.BadRequest } => "Test failed — the model rejected the request (HTTP 400). Check the model ID and structured JSON support.",
        HttpRequestException { StatusCode: >= System.Net.HttpStatusCode.InternalServerError } => $"Test failed — {provider} service error (HTTP 5xx). Retry later; this does not mean the key is invalid.",
        HttpRequestException => $"Test failed — no successful response from {provider}. Check network/TLS/proxy. No success recorded.",
        InvalidDataException or JsonException or KeyNotFoundException => "Response not valid — empty, incomplete, refused or malformed model response. Matching remains disabled; no key was saved.",
        _ => "Operation failed. Check Windows Credential Manager and connection settings. No success recorded."
    };
}
