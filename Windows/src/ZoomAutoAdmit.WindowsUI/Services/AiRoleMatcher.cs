using ZoomAutoAdmit.AttendanceMatching;
using ZoomAutoAdmit.Roster;
using ZoomAutoAdmit.SessionRoles;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>
/// Asks the configured AI model to confirm which of the suspected people a Zoom display name is.
/// It reuses the existing name matcher and the securely stored key; it asks one yes/no question per
/// suspect and takes the strongest confident answer. It can never introduce a person who is not
/// already configured for that session type.
/// </summary>
public sealed class AiRoleMatcher(IAiCredentialStore credentials, IAiMatchingService service) : IRoleAiMatcher
{
    public RoleMatch? Confirm(
        string observedName,
        SessionRoleProfile profile,
        IReadOnlyList<RolePerson> suspects,
        CancellationToken token,
        bool isPresenting = false)
    {
        AiConnectionSettings? settings;
        try { settings = credentials.Read(); }
        catch { return null; }
        if (settings == null || suspects.Count == 0) return null;

        RoleMatch? best = null;
        foreach (var suspect in suspects)
        {
            token.ThrowIfCancellationRequested();
            AiNameMatch answer;
            try
            {
                // One comparison per suspect: "is this Zoom name this person?".
                answer = service.CompareAsync(
                    settings,
                    new GroupStudent(suspect.Name, profile.SessionType, 0, suspect.Name, [.. suspect.Aliases]),
                    observedName,
                    token,
                    // The person teaching is usually the one sharing their screen, so this is worth
                    // telling the model. It only ever supports a name match, never replaces one.
                    isPresenting ? "This person is sharing their screen and presenting to the meeting." : null)
                    .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { throw; }
            catch { return best; }
            if (!answer.Match || answer.NeedsReview) continue;
            if (best == null || answer.Confidence > best.Confidence)
                best = new RoleMatch(suspect, RoleMatchSource.NameRule, answer.Confidence);
            if (answer.Confidence >= 98) break;   // Certain enough; no reason to spend more calls.
        }
        return best;
    }
}
