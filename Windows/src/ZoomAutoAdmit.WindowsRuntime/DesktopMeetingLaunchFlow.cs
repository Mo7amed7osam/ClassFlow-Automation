using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.WebAutomation;

namespace ZoomAutoAdmit.WindowsRuntime;

internal enum DesktopLaunchState { Home, Progress, Unknown }

internal interface IDesktopMeetingLaunchActions
{
    DesktopLaunchState ReadState();
    void OpenLink(Uri url);
    void JoinById(string meetingId, CancellationToken cancellation);
    void Wait(CancellationToken cancellation);
}

/// <summary>Only an idle home screen permits a second launch attempt. Any dialog stops retries.</summary>
internal sealed class DesktopMeetingLaunchFlow(IDesktopMeetingLaunchActions actions, int observationAttempts = 20)
{
    public MeetingOperationResult Run(Uri url, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string id = ExtractMeetingId(url);
        if (actions.ReadState() != DesktopLaunchState.Home)
            return MeetingOperationResult.Failure("Zoom is not idle on its home screen. Close existing join/preview dialogs before starting another meeting.");
        try
        {
            ConsoleLogger.Info("[MEETING_LINK] Opening saved meeting link");
            actions.OpenLink(url);
            for (int i = 0; i < observationAttempts; i++)
            {
                actions.Wait(cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (actions.ReadState() == DesktopLaunchState.Progress)
                {
                    ConsoleLogger.Info("[MEETING_LINK] Zoom responded; Join-by-ID fallback suppressed");
                    return MeetingOperationResult.Success();
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Do not include the URL/passcode in diagnostics.
            ConsoleLogger.Warn($"[MEETING_LINK] Launch observation failed ({ex.GetType().Name}); checking whether fallback is safe");
        }
        cancellation.ThrowIfCancellationRequested();
        var state = actions.ReadState();
        if (state == DesktopLaunchState.Progress) return MeetingOperationResult.Success();
        if (state != DesktopLaunchState.Home)
            return MeetingOperationResult.Failure("Zoom state is uncertain; Join-by-ID was not attempted to avoid a duplicate launch.");

        ConsoleLogger.Info($"[MEETING_JOIN_FALLBACK] Trying Join with meeting ID {id}");
        try
        {
            actions.JoinById(id, cancellation); // Exactly one submission; never retry Enter/Join.
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // The fallback refuses to run once Zoom has left its home screen. That refusal means
            // the opened link is taking effect after all, so the launch succeeded and no second
            // one is needed. Reporting failure here abandoned a meeting that was already opening.
            if (actions.ReadState() != DesktopLaunchState.Home)
            {
                ConsoleLogger.Info("[MEETING_JOIN_FALLBACK] Zoom is already opening the meeting; the fallback was not needed");
                return MeetingOperationResult.Success();
            }
            return MeetingOperationResult.Failure(ex.Message);
        }
        ConsoleLogger.Info("[MEETING_JOIN_FALLBACK] Join request sent; waiting for normal join verification");
        return MeetingOperationResult.Success();
    }

    internal static string ExtractMeetingId(Uri url)
    {
        ZoomWebMeetingController.ValidateMeetingUrl(url.AbsoluteUri);
        var ids = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length is >= 9 and <= 11 && p.All(c => c is >= '0' and <= '9')).ToArray();
        if (ids.Length != 1 || !string.IsNullOrEmpty(url.UserInfo))
            throw new ArgumentException("The Zoom URL must contain one numeric meeting ID (9-11 digits).");
        return ids[0];
    }
}
