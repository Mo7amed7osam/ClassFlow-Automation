using ZoomAutoAdmit.WebAutomation.Lms;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

/// <summary>Which files a class gets: its kind from the timetable, its number from the date order.</summary>
public class MaterialPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "material-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly TimetableEntry[] Timetable =
    [
        new("CAI5_AIS4_S7", new(2026, 9, 1), new(19, 0), "CAI5_AIS4_S7 • 27 • Technical"),
        new("CAI5_AIS4_S7", new(2026, 9, 6), new(19, 0), "CAI5_AIS4_S7 • 29 • Freelancing Skills"),
        new("CAI5_AIS4_S7", new(2026, 9, 13), new(19, 0), "CAI5_AIS4_S7 • 32 • Freelancing Skills"),
        new("CAI5_AIS4_S7", new(2026, 10, 11), new(19, 0), "CAI5_AIS4_S7 • 44 • Coaching"),
        new("CAI5_AIS4_S8", new(2026, 9, 1), new(17, 0), "CAI5_AIS4_S8 • 23 • English"),
        new("CAI5_AIS4_S8", new(2026, 9, 8), new(17, 0), "CAI5_AIS4_S8 • 28 • English"),
    ];

    private MaterialSettings Settings()
    {
        string freelancing = Directory.CreateDirectory(Path.Combine(_root, "non technical", "Freelancing Skills ( Session 7  to Session 12 )")).FullName;
        foreach (int n in new[] { 1, 2, 12 }) Directory.CreateDirectory(Path.Combine(freelancing, $"Session {n}"));
        File.WriteAllText(Path.Combine(freelancing, "Session 2", "2- Freelancing Platforms DEPI RD5.pdf"), "x");
        File.WriteAllText(Path.Combine(freelancing, "Session 2", "Session 2 - Freelancing Platforms - Assignment.pdf"), "x");
        File.WriteAllText(Path.Combine(freelancing, "Session 2", "notes.docx"), "x");
        string english = Directory.CreateDirectory(Path.Combine(_root, "English material")).FullName;
        File.WriteAllText(Path.Combine(english, "Session 1_Berlitz DEPI_Bio Writing - Student Copy.pdf"), "x");
        File.WriteAllText(Path.Combine(english, "Session 2_Berlitz DEPI_Proposal Writing - Student Copy.pdf"), "x");
        File.WriteAllText(Path.Combine(english, "Session 12_Berlitz DEPI_Later.pdf"), "x");
        var settings = new MaterialSettings();
        settings.Tracks[MaterialPlanner.Freelancing] = freelancing;
        settings.Tracks[MaterialPlanner.English] = english;
        return settings;
    }

    [Theory]
    [InlineData("CAI5_AIS4_S7 • 29 • Freelancing Skills", MaterialPlanner.Freelancing)]
    [InlineData("CAI5_AIS4_S8 • 23 • English", MaterialPlanner.English)]
    [InlineData("CAI5_AIS4_S7 • 12 • Soft skill: presenting", MaterialPlanner.SoftSkills)]
    [InlineData("CAI5_AIS4_S7 • 27 • Technical", MaterialPlanner.Technical)]
    [InlineData("CAI5_AIS4_S7 • 44 • Coaching", "")]
    [InlineData("Soft Skills ( Session 1 to Session 6 )", MaterialPlanner.SoftSkills)]
    [InlineData("non technical", "")]
    public void TheKindOfClassComesFromItsName(string name, string track) => Assert.Equal(track, MaterialPlanner.TrackOf(name));

    [Fact]
    public void AFixedClassGetsItsNumberedFolderAndItsAssignment()
    {
        // S7's second Freelancing class in the timetable is Freelancing session 2.
        var plan = MaterialPlanner.Plan(Timetable, "CAI5_AIS4_S7", new(2026, 9, 13), new(19, 0), Settings());
        Assert.Equal(MaterialPlanner.Freelancing, plan.Track);
        Assert.Equal(2, plan.Number);
        Assert.True(plan.IsFixed);
        Assert.Equal(new[] { "2- Freelancing Platforms DEPI RD5", "Session 2 - Freelancing Platforms - Assignment" }, plan.Files.Select(f => f.Title));
        Assert.Equal("notes.docx", Assert.Single(plan.Skipped));
        Assert.Equal("Session 2 - Freelancing Platforms - Assignment", plan.AssignmentFile?.Title);
        Assert.Equal(new DateTime(2026, 9, 20, 19, 0, 0), MaterialPlanner.DefaultDeadline(plan, new(2026, 9, 13), new(19, 0)));
    }

    [Fact]
    public void EnglishIsOneFilePerSessionAndSession1IsNotSession12()
    {
        var plan = MaterialPlanner.Plan(Timetable, "CAI5_AIS4_S8", new(2026, 9, 1), new(17, 0), Settings());
        Assert.Equal(1, plan.Number);
        Assert.Equal("Session 1_Berlitz DEPI_Bio Writing - Student Copy", Assert.Single(plan.Files).Title);
        Assert.Null(plan.AssignmentFile);
    }

    [Fact]
    public void TechnicalWaitsForItsFolderAndHasNoDefaultDeadline()
    {
        var settings = Settings();
        var plan = MaterialPlanner.Plan(Timetable, "CAI5_AIS4_S7", new(2026, 9, 1), new(19, 0), settings);
        Assert.True(plan.IsTechnical);
        Assert.Empty(plan.Files);
        Assert.Null(MaterialPlanner.DefaultDeadline(plan, new(2026, 9, 1), new(19, 0)));

        string chosen = Directory.CreateDirectory(Path.Combine(_root, "Week 3")).FullName;
        File.WriteAllText(Path.Combine(chosen, "Intro to Python.pptx"), "x");
        settings.Folders[MaterialSettings.KeyOf("cai5_ais4_s7", new(2026, 9, 1), new(19, 0))] = chosen;
        plan = MaterialPlanner.Plan(Timetable, "CAI5_AIS4_S7", new(2026, 9, 1), new(19, 0), settings);
        Assert.Equal("Intro to Python", Assert.Single(plan.Files).Title);
    }

    [Fact]
    public void CoachingHasNoMaterialAndAMissingFolderSaysSo()
    {
        var settings = Settings();
        Assert.Empty(MaterialPlanner.Plan(Timetable, "CAI5_AIS4_S7", new(2026, 10, 11), new(19, 0), settings).Files);
        // Freelancing 1 has an empty folder: nothing to put up, and the note says why.
        var first = MaterialPlanner.Plan(Timetable, "CAI5_AIS4_S7", new(2026, 9, 6), new(19, 0), settings);
        Assert.Empty(first.Files);
        Assert.Contains("no PDF", first.Note);
    }

    [Fact]
    public void FreelancingFollowsTheLmsWeekNotTheTimetable()
    {
        // S7's 20 Sep class is its third Freelancing class in the timetable, but the LMS calls it
        // "Week 10 - Session 3": six Soft Skills weeks, so it is Freelancing 4 (folder Session 7-12).
        var settings = Settings();
        string freelancing = settings.Tracks[MaterialPlanner.Freelancing];
        Directory.CreateDirectory(Path.Combine(freelancing, "Session 4"));
        File.WriteAllText(Path.Combine(freelancing, "Session 4", "4- Negotiation Skills DEPI R5.pdf"), "x");
        var timetable = Timetable.Append(new("CAI5_AIS4_S7", new(2026, 9, 20), new(19, 0), "CAI5_AIS4_S7 • 35 • Freelancing Skills")).ToArray();

        var plan = MaterialPlanner.Plan(timetable, "CAI5_AIS4_S7", new(2026, 9, 20), new(19, 0), settings, "Week 10 - Session 3");
        Assert.Equal(4, plan.Number);
        Assert.Equal("4- Negotiation Skills DEPI R5", Assert.Single(plan.Files).Title);
        Assert.Contains("week 10", plan.Note);
        // Unknown week: the timetable's order is all there is.
        Assert.Equal(3, MaterialPlanner.Plan(timetable, "CAI5_AIS4_S7", new(2026, 9, 20), new(19, 0), settings).Number);
    }

    [Theory]
    [InlineData("Week 10 - Session 3", 10)]
    [InlineData("week 7 - session 3", 7)]
    [InlineData("Session 3", null)]
    public void TheWeekIsReadFromTheLmsTitle(string title, int? week) => Assert.Equal(week, MaterialPlanner.WeekOf(title));

    [Fact]
    public void AnAssignmentAlwaysHasADescriptionAndItsOwnFileGoesUp()
    {
        var settings = Settings();
        var plan = MaterialPlanner.Plan(Timetable, "CAI5_AIS4_S7", new(2026, 9, 1), new(19, 0), settings);   // technical, no folder
        Assert.Null(MaterialPlanner.AssignmentFor(plan, null, new(2026, 9, 1), new(19, 0)));                  // no default deadline

        string sheet = Path.Combine(_root, "Lab 3 - Assignment.pdf");
        File.WriteAllText(sheet, "x");
        var choice = new AssignmentChoice(null, new DateTime(2026, 9, 5, 23, 59, 0), File: sheet);
        var assignment = MaterialPlanner.AssignmentFor(plan, choice, new(2026, 9, 1), new(19, 0));
        Assert.Equal("Lab 3 - Assignment", assignment?.Title);
        Assert.False(string.IsNullOrWhiteSpace(assignment?.Description));
        Assert.Equal("Lab 3 - Assignment", Assert.Single(MaterialPlanner.FilesFor(plan, choice)).Title);
        Assert.Equal(".", MaterialPlanner.AssignmentFor(plan, choice with { Description = "." }, new(2026, 9, 1), new(19, 0))?.Description);
        Assert.Null(MaterialPlanner.AssignmentFor(plan, choice with { None = true }, new(2026, 9, 1), new(19, 0)));
    }

    [Fact]
    public void AClassOrATrackCanTakeASingleFile()
    {
        var settings = Settings();
        string sheet = Path.Combine(_root, "Week 3", "Lab 3.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(sheet)!);
        File.WriteAllText(sheet, "x");
        File.WriteAllText(Path.Combine(_root, "Week 3", "other.pdf"), "x");

        // A file chosen for one class is that file only, not its folder.
        settings.Folders[MaterialSettings.KeyOf("CAI5_AIS4_S7", new(2026, 9, 1), new(19, 0))] = sheet;
        Assert.Equal("Lab 3", Assert.Single(MaterialPlanner.Plan(Timetable, "CAI5_AIS4_S7", new(2026, 9, 1), new(19, 0), settings).Files).Title);

        // A track set to one file puts that file up for each of its sessions.
        settings.Tracks[MaterialPlanner.English] = sheet;
        Assert.Equal("Lab 3", Assert.Single(MaterialPlanner.Plan(Timetable, "CAI5_AIS4_S8", new(2026, 9, 8), new(17, 0), settings).Files).Title);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
