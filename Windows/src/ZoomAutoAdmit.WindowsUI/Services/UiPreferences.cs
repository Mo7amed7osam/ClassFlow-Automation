using System.IO;
using System.Text.Json;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>The app has one design (Studio); the only choice is night or day.</summary>
public sealed record UiPreference(bool Dark)
{
    /// <summary>Resource path of the dictionary for this mode.</summary>
    public string ThemePath => $"/ZoomAutoAdmit.WindowsUI;component/Themes/{(Dark ? "Dark" : "Light")}.xaml";
}

/// <summary>
/// Remembers night or day between runs. It is a display preference only: nothing here
/// touches accounts, schedules, meetings or attendance.
/// </summary>
public static class UiPreferences
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZoomAutoAdmit", "ui-preference.json");

    // Older files also carry the design they picked; that part is ignored now.
    private sealed record Stored(bool Dark);

    public static UiPreference Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new(false);
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath));
            return new(stored?.Dark ?? false);
        }
        catch
        {
            // An unreadable preference must never stop the window from opening.
            return new(false);
        }
    }

    public static void Save(UiPreference preference)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Stored(preference.Dark)));
        }
        catch { }
    }
}
