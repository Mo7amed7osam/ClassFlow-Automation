using System.Text.RegularExpressions;

namespace ZoomAutoAdmit.Attendance;

/// <summary>
/// A participant row's label as Zoom writes it, cut back to the person's name.
///
/// Both readers need this and neither owns it: the desktop panel and the web client label a row
/// the same way, and the name has to match a roster either way. It lived in the file that chooses
/// between the two readers, which also pulls in FlaUI - so a Linux build could read the web list
/// and then had no way to turn a row into a name.
/// </summary>
public static class ParticipantNames
{
    /// <summary>Where a row's role tail begins. Only Zoom's own role words, never a name's brackets.</summary>
    private static readonly Regex RoleSuffix = new(
        @"\s*\((host|co-?host|guest|me)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Clean(string label)
    {
        int marker = label.IndexOf(",(", StringComparison.Ordinal);
        if (marker > 0) return label[..marker].Trim();
        // The web client writes the role without that comma - "eyouth coordinator (Host, me),computer
        // audio muted…" - so cutting at the first comma would keep "(Host" in the name.
        if (RoleSuffix.Match(label) is { Success: true } role) return label[..role.Index].Trim();
        int comma = label.IndexOf(',');
        return (comma > 0 ? label[..comma] : label).Trim();
    }
}
