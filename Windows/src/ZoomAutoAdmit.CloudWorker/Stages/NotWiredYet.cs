using ZoomAutoAdmit.WebAutomation.Lms;

namespace ZoomAutoAdmit.CloudWorker.Stages;

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
