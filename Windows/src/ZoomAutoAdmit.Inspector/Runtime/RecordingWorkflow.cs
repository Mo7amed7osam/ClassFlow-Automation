using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.WebAutomation.Lms;
using ZoomAutoAdmit.WebAutomation.Recordings;
using ZoomAutoAdmit.WebAutomation.Zoom;
using ZoomAutoAdmit.WindowsRuntime;

namespace ZoomAutoAdmit.Inspector.Runtime;

/// <summary>
/// Builds the real recording workflow - Zoom's My Recordings, the dashboard, the profile locks and the
/// accounts' own browser profiles - once, for every entry point. The terminal command and the HTTP
/// API get exactly the same object, so they cannot drift apart.
/// </summary>
public static class RecordingWorkflow
{
    public static RecordingLinkProcessor Create(Action<string> log, TimeSpan? lockWait = null)
    {
        var zoomZone = ZoomRecordingLinkReader.ConfiguredZoomTimeZone();
        string? zoneSetting = Environment.GetEnvironmentVariable(ZoomRecordingLinkReader.ZoomTimeZoneVariable);
        if (zoomZone != null)
            log($"[RECORDINGS] Zoom's recording times are read as {zoomZone.Id} and converted to this computer's time.");
        else if (!string.IsNullOrWhiteSpace(zoneSetting))
            log($"[RECORDINGS] {ZoomRecordingLinkReader.ZoomTimeZoneVariable} names a time zone Windows does not know; Zoom's times are read as they are.");

        var accounts = new WindowsMeetingAccountManager();
        return new RecordingLinkProcessor(
            new ZoomRecordingSource(new ZoomRecordingLinkReader(zoomDisplayTimeZone: zoomZone)),
            new LmsRecordingTarget(new LmsSessionRunner(new LmsCredentialStore())),
            new ProfileOperationLock(),
            accountProfile: async (group, token) =>
            {
                // Read on every request: an account's profile can be changed in the app at any time.
                var configured = await accounts.ListConfiguredAsync(token);
                return configured.FirstOrDefault(account =>
                    string.Equals(account.AccountId, group, StringComparison.OrdinalIgnoreCase))?.WebProfileName;
            },
            log: log,
            lockWait: lockWait);
    }

    /// <summary>The log the terminal already uses; the Zoom and dashboard steps write to it too.</summary>
    public static void ToConsole(string line)
    {
        if (line.Contains("failed", StringComparison.OrdinalIgnoreCase) || line.Contains("busy", StringComparison.OrdinalIgnoreCase))
            ConsoleLogger.Warn(line);
        else
            ConsoleLogger.Info(line);
    }
}
