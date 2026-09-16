using ZoomAutoAdmit.WindowsUI.Services;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

public sealed class AppUpdaterClassBrowserTests
{
    private const string Root = @"C:\Users\x\AppData\Local\ZoomAutoAdmit\Profiles\";

    [Theory]
    [InlineData("chrome.exe --user-data-dir=" + Root + "s8 --remote-debugging-pipe about:blank", true)]         // a web class
    [InlineData("chrome.exe --user-data-dir=" + Root + "lms-dashboard-view --remote-debugging-pipe", false)]   // the LMS list
    [InlineData("chrome.exe --user-data-dir=" + Root + "lms-mohabmando488-material", false)]                   // material upload
    [InlineData("chrome.exe --headless --user-data-dir=" + Root + "s8", false)]                                // Zoom report / recordings
    [InlineData(@"chrome.exe --user-data-dir=C:\Other\Profile", false)]                                      // someone's own Chrome
    public void OnlyAClassBrowserHoldsAnUpdateBack(string commandLine, bool blocks) =>
        Assert.Equal(blocks, AppUpdater.IsClassBrowser(commandLine));
}
