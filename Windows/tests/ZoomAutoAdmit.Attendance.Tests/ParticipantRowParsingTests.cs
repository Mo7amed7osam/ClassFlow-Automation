using Xunit;

namespace ZoomAutoAdmit.Attendance.Tests;

/// <summary>
/// The Windows participant list exposes each row as one label carrying the person's name plus
/// their role and device status. Only the leading name is a participant identity.
/// </summary>
public class ParticipantRowParsingTests
{
    [Theory]
    [InlineData("Mohab Mohamed __Coordinator,(Guest), Computer audio muted,Video off, Participant", "Mohab Mohamed __Coordinator")]
    [InlineData("eyouth coordinator,(Host, me), Computer audio muted,Video off,Recording to the cloud", "eyouth coordinator")]
    [InlineData("Sara Ahmed,(Co-host, guest), Video on", "Sara Ahmed")]
    [InlineData("Ahmed, Mohamed, Computer audio muted", "Ahmed")]
    [InlineData("Plain Name", "Plain Name")]
    [InlineData("  Padded Name ,(Guest)", "Padded Name")]
    public void RowLabelYieldsOnlyTheDisplayName(string label, string expected) =>
        Assert.Equal(expected, RuntimeAttendanceSources.CleanParticipantName(label));
}
