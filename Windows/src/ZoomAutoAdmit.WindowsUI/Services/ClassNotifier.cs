using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ZoomAutoAdmit.Core.Formatting;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>Where notices go, and whether they go at all.</summary>
public sealed record NotifySettings
{
    /// <summary>The n8n webhook that turns a notice into an e-mail. Empty: nothing is sent.</summary>
    public string Url { get; init; } = "";
    public bool Enabled { get; init; } = true;
    /// <summary>What this PC calls itself in a notice, so several PCs are told apart.</summary>
    public string Pc { get; init; } = "";
}

/// <summary>
/// One thing worth telling somebody about: a class that did not open, a step that keeps failing,
/// the day's summary. It is what the webhook receives, and what an e-mail is written from.
/// </summary>
public sealed record ClassNotice(string Kind, string Title, string Message)
{
    public string? Group { get; init; }
    public string? Coordinator { get; init; }
    /// <summary>The class's day, yyyy-MM-dd.</summary>
    public string? Date { get; init; }
    /// <summary>The class's time, HH:mm.</summary>
    public string? Start { get; init; }
    public string? Step { get; init; }
    public int? Attempts { get; init; }
}

/// <summary>
/// Says out loud what a person would otherwise have to watch the app to notice: a class whose
/// meeting never opened, a step that has failed several times. The notice is posted to an n8n
/// webhook, and n8n sends the e-mail - so no mailbox password is ever kept here.
///
/// Nothing depends on it: a webhook that is down, slow or not set up at all changes nothing about
/// the class. It is tried three times, a little further apart each time, and then written off with
/// a line in the log.
/// </summary>
public sealed class ClassNotifier
{
    /// <summary>The one the app uses, so a notice can be sent from wherever something goes wrong.</summary>
    public static ClassNotifier? Current { get; set; }

    public static TimeSpan[] TryAgainAfter { get; set; } =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20)];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly Func<NotifySettings> _settings;
    private readonly Func<string, string, CancellationToken, Task<bool>> _post;
    private readonly Action<string> _log;

    public ClassNotifier(
        Func<NotifySettings>? settings = null,
        Func<string, string, CancellationToken, Task<bool>>? post = null,
        Action<string>? log = null)
    {
        _settings = settings ?? NotifySettingsStore.Read;
        _post = post ?? PostAsync;
        _log = log ?? (message => ConsoleLogger.Info($"[NOTIFY] {message}"));
    }

    /// <summary>
    /// Sends a notice. True when it was taken; false when there is nowhere to send it, notices are
    /// turned off, or every try failed. Never throws.
    /// </summary>
    public async Task<bool> SendAsync(ClassNotice notice, CancellationToken token = default)
    {
        NotifySettings settings;
        try { settings = _settings(); }
        catch (Exception ex) { _log($"the notification settings could not be read: {ex.Message}"); return false; }

        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Url)) return false;

        string body = JsonSerializer.Serialize(new
        {
            kind = notice.Kind,
            title = notice.Title,
            message = notice.Message,
            group = notice.Group,
            coordinator = notice.Coordinator,
            date = notice.Date,
            start = notice.Start,
            step = notice.Step,
            attempts = notice.Attempts,
            pc = string.IsNullOrWhiteSpace(settings.Pc) ? Environment.MachineName : settings.Pc,
            at = DateTimeOffset.Now.ToString("o"),
        }, Json);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (await _post(settings.Url, body, token))
                {
                    _log($"{notice.Kind}: {notice.Title} - sent.");
                    return true;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
            catch (Exception ex) { _log($"{notice.Kind}: {ex.Message}"); }

            if (attempt >= TryAgainAfter.Length)
            {
                _log($"{notice.Kind}: \"{notice.Title}\" could not be sent after {attempt + 1} tries. The class is unaffected.");
                return false;
            }
            try { await Task.Delay(TryAgainAfter[attempt], token); }
            catch (OperationCanceledException) { return false; }
        }
    }

    /// <summary>Sends without waiting and without ever failing the caller: for a handler that must not block.</summary>
    public void Send(ClassNotice notice) => _ = Task.Run(async () =>
    {
        try { await SendAsync(notice); } catch { }
    });

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static async Task<bool> PostAsync(string url, string body, CancellationToken token)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var answer = await Http.PostAsync(url, content, token);
        if (answer.IsSuccessStatusCode) return true;
        // 404 from n8n means the workflow is not listening: its test URL only answers while
        // "Listen for test event" is on, and its production URL only once it is activated.
        throw new InvalidOperationException($"the webhook answered {(int)answer.StatusCode}.");
    }
}

/// <summary>Where this PC keeps the notification settings.</summary>
public static class NotifySettingsStore
{
    public static string Path { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "notify.json");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static NotifySettings Read()
    {
        try
        {
            if (!File.Exists(Path)) return new NotifySettings();
            return JsonSerializer.Deserialize<NotifySettings>(File.ReadAllText(Path), Json) ?? new NotifySettings();
        }
        catch { return new NotifySettings(); }
    }

    public static void Write(NotifySettings settings)
    {
        string? folder = System.IO.Path.GetDirectoryName(Path);
        if (folder is { Length: > 0 }) Directory.CreateDirectory(folder);
        File.WriteAllText(Path, JsonSerializer.Serialize(settings, Json));
    }
}
