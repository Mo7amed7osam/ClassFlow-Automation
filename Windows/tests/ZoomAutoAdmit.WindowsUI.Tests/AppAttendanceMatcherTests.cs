using ZoomAutoAdmit.WindowsUI.Services;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

public sealed class AppAttendanceMatcherTests
{
    private static readonly DateTime Class = new(2026, 9, 16, 19, 0, 0);

    [Theory]
    [InlineData(0, 54, -1)]      // before the first match
    [InlineData(0, 55, 0)]       // the match just before the 1 h upload
    [InlineData(1, 54, 0)]
    [InlineData(1, 55, 1)]       // then every hour, counted from the class's time
    [InlineData(2, 55, 2)]
    [InlineData(3, 55, 3)]
    [InlineData(4, 31, -1)]      // a class long over is left alone
    public void TheNamesAreMatchedHourlyFromTheClassTime(int hours, int minutes, int slot)
    {
        Assert.Equal(slot, AppAttendanceMatcher.SlotAt(Class, Class.AddHours(hours).AddMinutes(minutes)));
    }
}
