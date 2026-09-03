using System.Net;
using System.Text.Json;

namespace ZoomAutoAdmit.AttendanceMatching;

// Only allowlisted codes leave the HTTP adapter. Provider messages can echo a secret.
public sealed class AiProviderException(HttpStatusCode status, string code)
    : HttpRequestException($"AI request failed: HTTP {(int)status}; {code}.", null, status)
{
    public string Code { get; } = code;

    public static async Task<AiProviderException> FromResponseAsync(HttpResponseMessage response, CancellationToken token)
    {
        string code = "request_failed";
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var bytes = new byte[8193];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), token);
                if (read == 0) break;
                count += read;
            }
            if (count <= 8192)
            {
                using var doc = JsonDocument.Parse(bytes.AsMemory(0, count));
                if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String)
                    code = value.GetString() switch
                    {
                        "invalid_api_key" => "invalid_api_key",
                        "insufficient_quota" => "insufficient_quota",
                        "model_not_found" => "model_not_found",
                        "rate_limit_exceeded" => "rate_limit_exceeded",
                        "unsupported_parameter" => "unsupported_parameter",
                        _ => "request_failed"
                    };
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return new(response.StatusCode, code);
    }
}
