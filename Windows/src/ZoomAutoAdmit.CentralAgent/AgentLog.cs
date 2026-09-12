using System.Text;

namespace ZoomAutoAdmit.CentralAgent;

/// <summary>
/// Structured log lines: "[AGENT] event=job_started jobId=... attempt=1". Values are ids, counts,
/// codes and names; each is reduced to characters that cannot break the line or look like another
/// field. Tokens and links are never passed in.
/// </summary>
public static class AgentLog
{
    public static string Line(string @event, params (string Key, object? Value)[] fields)
    {
        var line = new StringBuilder("[AGENT] event=").Append(@event);
        foreach (var (key, value) in fields)
        {
            if (value is null) continue;
            line.Append(' ').Append(key).Append('=').Append(Clean(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));
        }
        return line.ToString();
    }

    private static string Clean(string value)
    {
        var cleaned = new StringBuilder(Math.Min(value.Length, 120));
        foreach (char c in value)
        {
            if (cleaned.Length >= 120) { cleaned.Append('…'); break; }
            cleaned.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '/' ? c : '_');
        }
        return cleaned.ToString();
    }
}
