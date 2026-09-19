using ZoomAutoAdmit.WebAutomation.Lms;

namespace ZoomAutoAdmit.CloudWorker.Stages;

/// <summary>
/// The account source before it has a route to the server.
///
/// The server already holds what this needs and already hands it over to an authorised caller:
/// `POST /api/v1/admin/users/{id}/lms-accounts/{aid}/secret` answers with a coordinator's sign-in,
/// refuses it unless that coordinator is turned on, and writes every read to admin_audit_log. What
/// is missing is the worker's side of that call, which needs the device token to be accepted where
/// today only a dashboard session is.
///
/// Until then this answers null, and every LMS stage stops with "the server would not give this
/// class's LMS sign-in" instead of running under whatever account the machine last used. That
/// wrong answer - a class written up under somebody else's name - is the one worth making
/// impossible, so the gap is a null here rather than a fallback.
/// </summary>
public sealed class ServerLmsAccounts : ILmsAccounts
{
    public Task<ILmsCredentialStore?> ForAsync(Guid lmsAccountId, CancellationToken cancellationToken) =>
        Task.FromResult<ILmsCredentialStore?>(null);
}

/// <summary>
/// Who was in a class, before anything collects it.
///
/// On Windows this comes from the attendance snapshots taken while the meeting is live, and then
/// from Zoom's own participants report once it has ended. Both are Zoom stages, and this worker
/// does not have them yet.
///
/// It answers nobody, and <see cref="AttendanceStage"/> refuses to write an empty list rather than
/// marking a whole class absent. An attendance that is missing can be put right; one that was
/// written wrong has to be noticed first.
/// </summary>
public sealed class NoAttendanceCollected : IAttendanceNames
{
    public Task<IReadOnlyCollection<string>> PresentAsync(ClassStage stage, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<string>>([]);
}
