using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>
/// The attendance page's AI calls, made with the app's own key: one key, saved once on the AI
/// Engine page, for the whole app. The page sends an OpenRouter-style chat completion; this adds the
/// key from Windows Credential Manager and sends it to the provider and model the app is set to.
/// The key never reaches the page.
/// </summary>
public static class AiProxy
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(150) };
    private const int MaximumBody = 512 * 1024;

    public static async Task<(int Status, string Body)> ChatAsync(string requestJson, IAiCredentialStore? store = null, CancellationToken token = default)
    {
        var settings = (store ?? new AiCredentialStore()).Read();
        if (settings == null || string.IsNullOrWhiteSpace(settings.ApiKey))
            return (401, Error("Save the AI key on the app's AI Engine page; the attendance page uses that key."));
        if (requestJson.Length > MaximumBody) return (413, Error("The request is too large."));
        if (JsonNode.Parse(requestJson) is not JsonObject body) return (400, Error("The request is not JSON."));

        body["model"] = settings.Model;
        string url;
        if (settings.Provider == AiProvider.OpenRouter) url = "https://openrouter.ai/api/v1/chat/completions";
        else
        {
            url = "https://api.openai.com/v1/chat/completions";
            // OpenAI's current models take max_completion_tokens and only their own temperature.
            if (body.Remove("max_tokens", out var max)) body["max_completion_tokens"] = max;
            body.Remove("temperature");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        if (settings.Provider == AiProvider.OpenRouter) request.Headers.Add("X-Title", "Zoom Auto Admit Attendance");
        try
        {
            using var response = await Http.SendAsync(request, token);
            string text = await response.Content.ReadAsStringAsync(token);
            if (text.Length > MaximumBody) return (502, Error("The AI answer was too large."));
            return ((int)response.StatusCode, text);
        }
        catch (HttpRequestException ex) { return (502, Error($"The AI provider could not be reached ({ex.Message}).")); }
        catch (TaskCanceledException) when (!token.IsCancellationRequested) { return (504, Error("The AI provider did not answer in time.")); }
    }

    private static string Error(string message) => new JsonObject { ["error"] = new JsonObject { ["message"] = message } }.ToJsonString();
}
