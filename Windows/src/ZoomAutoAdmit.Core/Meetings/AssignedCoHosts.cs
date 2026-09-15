using System.Collections.Concurrent;

namespace ZoomAutoAdmit.Core.Meetings;

/// <summary>
/// Who the app made co-host in each meeting (the instructor, by the name Zoom shows), so the end of
/// the class can tell when that person has left. Kept for the life of the process only.
/// </summary>
public static class AssignedCoHosts
{
    private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> Names = new();

    public static void Record(Guid sessionId, string observedName)
    {
        if (string.IsNullOrWhiteSpace(observedName)) return;
        Names.GetOrAdd(sessionId, _ => new(StringComparer.OrdinalIgnoreCase))[observedName.Trim()] = 0;
    }

    public static IReadOnlyCollection<string> For(Guid sessionId) =>
        Names.TryGetValue(sessionId, out var names) ? [.. names.Keys] : [];
}
