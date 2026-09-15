using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZoomAutoAdmit.WebAutomation.Lms;

/// <summary>One file of a class's material: the title it gets on the LMS (its name) and where it is.</summary>
public sealed record LmsMaterialFile(string Title, string Path);

/// <summary>An assignment to create on a session, due at Deadline (local time).</summary>
public sealed record LmsAssignment(string Title, string Description, DateTime Deadline);

/// <summary>What putting a class's material on the LMS did.</summary>
public sealed record LmsMaterialResult(
    bool IsSuccess, string Message, IReadOnlyList<string> Added, IReadOnlyList<string> AlreadyThere,
    bool AssignmentCreated, bool AssignmentAlreadyThere)
{
    public static LmsMaterialResult Failed(string message) => new(false, message, [], [], false, false);
}

/// <summary>One class of the timetable: its group, day, time and name ("CAI5_AIS4_S7 • 29 • Freelancing Skills").</summary>
public sealed record TimetableEntry(string Group, DateOnly Date, TimeOnly Start, string Name);

/// <summary>What was put on a class's session, so it is never put there twice.</summary>
public sealed record MaterialRecord(DateTimeOffset At, string[] Files, string? Assignment, DateTime? Deadline);

/// <summary>
/// A class's assignment as chosen on the Sessions page: its title, description and deadline, and
/// its own file when it has one (a technical class's assignment sheet) - or that it has none.
/// </summary>
public sealed record AssignmentChoice(string? Title, DateTime? Deadline, bool None = false, string? Description = null, string? File = null);

/// <summary>
/// Where each kind of session's material is, and what was done per class. The fixed material
/// (Freelancing, Soft Skills, English) has one folder per track; a technical class gets the folder
/// chosen for it. Kept in %LOCALAPPDATA%\ZoomAutoAdmit\Lms\materials.json.
/// </summary>
public sealed class MaterialSettings
{
    /// <summary>Track (Freelancing, Soft Skills, English) -> its folder.</summary>
    public Dictionary<string, string> Tracks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Class key -> the folder chosen for that class (a technical class, or any class by hand).</summary>
    public Dictionary<string, string> Folders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AssignmentChoice> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MaterialRecord> Done { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Class key -> why the last try did not finish.</summary>
    public Dictionary<string, string> Errors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _found;
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Lms", "materials.json");

    public static string KeyOf(string group, DateOnly date, TimeOnly start) => $"{group.Trim().ToUpperInvariant()}|{date:yyyy-MM-dd}|{start:HH\\:mm}";

    public static MaterialSettings Load(string? path = null)
    {
        lock (Gate)
        {
            MaterialSettings settings;
            try
            {
                string file = path ?? DefaultPath;
                settings = File.Exists(file) ? JsonSerializer.Deserialize<MaterialSettings>(File.ReadAllText(file), Json) ?? new() : new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { settings = new(); }
            settings.Tracks = new(settings.Tracks ?? [], StringComparer.OrdinalIgnoreCase);
            settings.Folders = new(settings.Folders ?? [], StringComparer.OrdinalIgnoreCase);
            settings.Assignments = new(settings.Assignments ?? [], StringComparer.OrdinalIgnoreCase);
            settings.Done = new(settings.Done ?? [], StringComparer.OrdinalIgnoreCase);
            settings.Errors = new(settings.Errors ?? [], StringComparer.OrdinalIgnoreCase);
            if (path == null) settings.FindTrackFolders();
            return settings;
        }
    }

    public void Save(string? path = null)
    {
        lock (Gate)
        {
            string file = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(this, Json));
        }
    }

    /// <summary>A track with no folder yet takes the one a "Depi" folder on this PC holds for it.</summary>
    private void FindTrackFolders()
    {
        if (MaterialPlanner.FixedTracks.All(t => Tracks.ContainsKey(t))) return;
        if (_found != null) { foreach (var (track, folder) in _found) Tracks.TryAdd(track, folder); return; }
        _found = new(StringComparer.OrdinalIgnoreCase);             // looked for once per run of the app
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            string depi = Path.Combine(drive.RootDirectory.FullName, "Depi");
            if (!Directory.Exists(depi)) continue;
            try
            {
                var folders = Directory.EnumerateDirectories(depi, "*", SearchOption.AllDirectories).Take(4000).ToArray();
                foreach (string track in MaterialPlanner.FixedTracks.Where(t => !Tracks.ContainsKey(t)))
                    if (folders.Where(f => MaterialPlanner.TrackOf(Path.GetFileName(f)) == track).OrderBy(f => f.Length).FirstOrDefault() is { } found)
                        Tracks[track] = _found[track] = found;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>A class's material: its track, its number in that track, and the files to put on its session.</summary>
public sealed record MaterialPlan(
    string Track, int? Number, string? Folder, IReadOnlyList<LmsMaterialFile> Files, IReadOnlyList<string> Skipped,
    LmsMaterialFile? AssignmentFile, string Note)
{
    public bool IsTechnical => Track == MaterialPlanner.Technical;
    /// <summary>Fixed material goes up by itself at class time; a technical class waits for its folder.</summary>
    public bool IsFixed => MaterialPlanner.FixedTracks.Contains(Track);
    public string Label => Track.Length == 0 ? "" : Number is { } n ? $"{Track} {n}" : Track;
}

/// <summary>
/// Which material belongs to a class. The timetable names each class's kind ("… • Freelancing
/// Skills"), and its number is its place among its group's classes of that kind, in date order:
/// the group's first Freelancing class is Freelancing session 1. A track's folder holds a
/// "Session N" folder per session (Freelancing, Soft Skills) or files named "Session N_…" (English).
/// </summary>
public static class MaterialPlanner
{
    public const string Freelancing = "Freelancing", SoftSkills = "Soft Skills", English = "English", Technical = "Technical";
    public static readonly string[] FixedTracks = [Freelancing, SoftSkills, English];

    /// <summary>The kind of class a timetable name (or a folder name) is about; "" for coaching, discussion and the rest.</summary>
    public static string TrackOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        string kind = name.Split('•', '|').Last().Trim();
        if (kind.Contains("freelanc", StringComparison.OrdinalIgnoreCase)) return Freelancing;
        if (kind.Contains("soft", StringComparison.OrdinalIgnoreCase)) return SoftSkills;
        if (kind.Contains("english", StringComparison.OrdinalIgnoreCase)) return English;
        if (kind.Contains("technical", StringComparison.OrdinalIgnoreCase) && !kind.Contains("non", StringComparison.OrdinalIgnoreCase)) return Technical;
        return "";
    }

    /// <summary>The class's place among its group's classes of the same kind, counting from 1.</summary>
    public static int? NumberOf(IEnumerable<TimetableEntry> timetable, string group, DateOnly date, TimeOnly start, string track)
    {
        int number = 0;
        foreach (var entry in timetable.Where(e => e.Group.Equals(group, StringComparison.OrdinalIgnoreCase) && TrackOf(e.Name) == track)
                     .OrderBy(e => e.Date).ThenBy(e => e.Start))
        {
            number++;
            if (entry.Date == date && entry.Start == start) return number;
        }
        return null;
    }

    /// <summary>The week in an LMS session title: "Week 10 - Session 3" is week 10.</summary>
    public static int? WeekOf(string? lmsTitle) =>
        Regex.Match(lmsTitle ?? "", @"\bWeek\s*(\d{1,2})\b", RegexOptions.IgnoreCase) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    /// <summary>The non-technical sessions a track's folder covers: "Freelancing Skills ( Session 7 to Session 12 )" is 7-12.</summary>
    public static (int First, int Last)? RangeOf(string? folder)
    {
        var m = Regex.Match(Path.GetFileName(folder ?? ""), @"Session\s*(\d+)\s*(?:to|-|–)\s*Session\s*(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }

    /// <summary>
    /// A class's number in its track. Freelancing and Soft Skills follow the LMS week: a group has
    /// one non-technical class a week, Soft Skills are its first six and Freelancing the next six,
    /// so "Week 10" is non-technical session 10 - Freelancing 4 in a folder of sessions 7 to 12. The
    /// timetable alone cannot say this (it starts part-way through), so it is only the fallback,
    /// along with English, which runs on its own.
    /// </summary>
    public static int? TrackNumber(IReadOnlyList<TimetableEntry> timetable, string group, DateOnly date, TimeOnly start, string track,
        string? lmsTitle, MaterialSettings settings, out string how)
    {
        how = "";
        if (track is Freelancing or SoftSkills && WeekOf(lmsTitle) is { } week &&
            settings.Tracks.TryGetValue(track, out var folder) && RangeOf(folder) is { } range &&
            week >= range.First && week <= range.Last)
        {
            how = $"week {week} on the LMS";
            return week - range.First + 1;
        }
        int? ordinal = NumberOf(timetable, group, date, start, track);
        if (ordinal != null) how = "its place in the timetable";
        return ordinal;
    }

    public static MaterialPlan Plan(IReadOnlyList<TimetableEntry> timetable, string group, DateOnly date, TimeOnly start, MaterialSettings settings, string? lmsTitle = null)
    {
        string key = MaterialSettings.KeyOf(group, date, start);
        var entry = timetable.FirstOrDefault(e => e.Group.Equals(group, StringComparison.OrdinalIgnoreCase) && e.Date == date && e.Start == start);
        string track = TrackOf(entry?.Name);
        string how = "";
        int? number = track.Length > 0 && track != Technical ? TrackNumber(timetable, group, date, start, track, lmsTitle, settings, out how) : null;

        if (settings.Folders.TryGetValue(key, out var chosen) && Directory.Exists(chosen))
            return FromFolder(track, number, chosen, null, $"The folder chosen for this class: {Path.GetFileName(chosen)}.");
        if (track.Length == 0) return new(track, null, null, [], [], null, "No material for this kind of session.");
        if (track == Technical) return new(track, null, null, [], [], null, "Choose this session's folder to upload its material.");
        if (number == null) return new(track, null, null, [], [], null, $"Not in the timetable, so its {track} number is unknown.");
        if (!settings.Tracks.TryGetValue(track, out var root) || !Directory.Exists(root))
            return new(track, number, null, [], [], null, $"The {track} folder is not set (Sessions settings).");

        var pattern = new Regex($@"^\s*Session\s*0*{number}(?!\d)", RegexOptions.IgnoreCase);
        string? sessionFolder = Directory.EnumerateDirectories(root).FirstOrDefault(d => pattern.IsMatch(Path.GetFileName(d)));
        string numbered = $"{track} · Session {number} (from {how}).";
        if (sessionFolder != null) return FromFolder(track, number, sessionFolder, null, numbered);
        // English keeps one file per session in the track's folder: "Session 1_…".
        var flat = Directory.EnumerateFiles(root).Where(f => pattern.IsMatch(Path.GetFileName(f))).ToArray();
        return flat.Length > 0
            ? FromFolder(track, number, root, flat, numbered)
            : new(track, number, root, [], [], null, $"No material for {track} session {number} (nothing named Session {number} in its folder).");
    }

    private static MaterialPlan FromFolder(string track, int? number, string folder, string[]? only, string note)
    {
        var all = (only ?? Directory.EnumerateFiles(folder).ToArray()).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToArray();
        var files = all.Where(f => LmsSessionRunner.AttachableExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => new LmsMaterialFile(Path.GetFileNameWithoutExtension(f).Trim(), f)).ToArray();
        var skipped = all.Where(f => !LmsSessionRunner.AttachableExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(Path.GetFileName).OfType<string>().ToArray();
        var assignment = files.FirstOrDefault(f => f.Title.Contains("assignment", StringComparison.OrdinalIgnoreCase));
        return new(track, number, folder, files, skipped, assignment, files.Length == 0 ? $"{note} The folder has no PDF, ZIP or PowerPoint file." : note);
    }

    /// <summary>
    /// The assignment to create for a class: the one chosen on its card, else its folder's
    /// assignment file with the default deadline. The LMS wants a description, so there always is one.
    /// </summary>
    public static LmsAssignment? AssignmentFor(MaterialPlan plan, AssignmentChoice? choice, DateOnly date, TimeOnly start)
    {
        if (choice?.None == true) return null;
        string? title = choice?.Title
                        ?? (choice?.File is { Length: > 0 } file ? Path.GetFileNameWithoutExtension(file).Trim() : null)
                        ?? plan.AssignmentFile?.Title;
        DateTime? deadline = choice?.Deadline ?? DefaultDeadline(plan, date, start);
        if (string.IsNullOrWhiteSpace(title) || deadline == null) return null;
        bool hasSheet = choice?.File is { Length: > 0 } || plan.AssignmentFile != null;
        string description = !string.IsNullOrWhiteSpace(choice?.Description) ? choice!.Description!.Trim()
            : hasSheet ? "The assignment is in the attached file of this session."
            : "See this session's attachments.";
        return new(title.Trim(), description, deadline.Value);
    }

    /// <summary>The files that go up: the class's material, and the assignment's own file when it is not among them.</summary>
    public static IReadOnlyList<LmsMaterialFile> FilesFor(MaterialPlan plan, AssignmentChoice? choice)
    {
        if (choice?.None == true || choice?.File is not { Length: > 0 } sheet || !File.Exists(sheet)) return plan.Files;
        if (plan.Files.Any(f => string.Equals(f.Path, sheet, StringComparison.OrdinalIgnoreCase))) return plan.Files;
        return [.. plan.Files, new LmsMaterialFile(Path.GetFileNameWithoutExtension(sheet).Trim(), sheet)];
    }

    /// <summary>The deadline a class's assignment gets unless one was chosen: a week after the class, except technical ones.</summary>
    public static DateTime? DefaultDeadline(MaterialPlan plan, DateOnly date, TimeOnly start) =>
        plan.IsTechnical ? null : date.ToDateTime(start).AddDays(7);
}
