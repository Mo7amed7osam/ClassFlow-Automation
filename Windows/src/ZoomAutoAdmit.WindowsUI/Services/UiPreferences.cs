using System.IO;
using System.Text.Json;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>The four palettes the window can wear. Each has a dark and a light file.</summary>
public enum UiDesign { Midnight, Ice, Teal, Slate }

public sealed record UiPreference(UiDesign Design, bool Dark)
{
    /// <summary>Resource path of the dictionary for this design and mode.</summary>
    public string ThemePath => Design == UiDesign.Midnight
        ? $"/ZoomAutoAdmit.WindowsUI;component/Themes/{(Dark ? "Dark" : "Light")}.xaml"
        : $"/ZoomAutoAdmit.WindowsUI;component/Themes/{Design}.{(Dark ? "Dark" : "Light")}.xaml";
}

/// <summary>
/// Remembers the chosen look between runs. It is a display preference only: nothing here
/// touches accounts, schedules, meetings or attendance.
/// </summary>
public static class UiPreferences
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZoomAutoAdmit", "ui-preference.json");

    private sealed record Stored(string Design, bool Dark);

    public static UiPreference Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new(UiDesign.Midnight, true);
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath));
            if (stored == null || !Enum.TryParse<UiDesign>(stored.Design, out var design))
                return new(UiDesign.Midnight, true);
            return new(design, stored.Dark);
        }
        catch
        {
            // An unreadable preference must never stop the window from opening.
            return new(UiDesign.Midnight, true);
        }
    }

    public static void Save(UiPreference preference)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(
                new Stored(preference.Design.ToString(), preference.Dark)));
        }
        catch { }
    }
}
