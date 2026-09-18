using ZoomAutoAdmit.WindowsUI.Services;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

public sealed class AppAttendanceMatcherTests
{
    private static readonly DateTime Class = new(2026, 9, 16, 19, 0, 0);

    [Theory]
    [InlineData(0, 14, -1)]      // the class has only just opened
    [InlineData(0, 15, 0)]       // the first match, while the class runs
    [InlineData(0, 44, 0)]
    [InlineData(0, 45, 1)]       // then every half hour, counted from the class's time
    [InlineData(1, 15, 2)]
    [InlineData(2, 45, 5)]
    [InlineData(4, 31, -1)]      // a class long over is left alone
    public void TheNamesAreMatchedFromTheStartOfTheClassAndOnAgain(int hours, int minutes, int slot)
    {
        Assert.Equal(slot, AppAttendanceMatcher.SlotAt(Class, Class.AddHours(hours).AddMinutes(minutes)));
    }
}
