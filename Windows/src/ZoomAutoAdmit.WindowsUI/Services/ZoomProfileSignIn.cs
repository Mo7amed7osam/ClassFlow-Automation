using Microsoft.Playwright;
using ZoomAutoAdmit.WebAutomation;
using ZoomAutoAdmit.WebAutomation.Browser;
using ZoomAutoAdmit.WebAutomation.Recordings;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>
/// Signs an account's own browser profile in to Zoom, once, when the account is saved - so every
/// later meeting, recording and report on that profile starts signed in instead of stopping at
/// Zoom's sign-in page.
///
/// It looks without a window first: a profile that is still signed in is left alone, and one that
/// Zoom lets sign in with the saved password is done there too. Zoom holds a browser it does not
/// know at the e-mail step, though (2026-09-26), so otherwise a visible window opens on the
/// profile with the e-mail and password typed in, and waits for the person to finish - a captcha
/// or a code - after which the profile remembers the sign-in.
/// </summary>
public sealed class ZoomProfileSignIn(ZoomProfileManager? profiles = null, IProfileLock? locks = null)
{
    public const string ProfileUrl = "https://zoom.us/profile";
    public static readonly TimeSpan WaitForPerson = TimeSpan.FromMinutes(5);

    private readonly ZoomProfileManager _profiles = profiles ?? new ZoomProfileManager();
    private readonly IProfileLock _locks = locks ?? new ProfileOperationLock();

    /// <summary>Answers what happened, in a sentence for the Accounts page.</summary>
    public async Task<string> SignInAsync(string accountId, string profileName, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(profileName)) profileName = accountId;
        // A meeting running on the profile has it open; a second browser cannot share it.
        await using var held = await _locks.TryAcquireAsync(profileName, TimeSpan.FromSeconds(10), token);
        if (held == null)
            return $"The '{profileName}' browser profile is in use (a meeting or a recording read), so it was not signed in now; save again once it is free.";

        var profile = _profiles.GetOrCreate(profileName);
        if (await TryAsync(profile, accountId, headless: true, token))
        {
            _profiles.MarkSessionReady(profile);
            return $"The '{profileName}' profile is signed in to Zoom.";
        }
        if (await TryAsync(profile, accountId, headless: false, token))
        {
            _profiles.MarkSessionReady(profile);
            return $"The '{profileName}' profile is signed in to Zoom and remembers it.";
        }
        return $"The '{profileName}' profile is not signed in to Zoom: the window was closed or {WaitForPerson.TotalMinutes:0} minutes passed. Save again to try once more.";
    }

    private static async Task<bool> TryAsync(ZoomBrowserProfile profile, string accountId, bool headless, CancellationToken token)
    {
        await using var session = await new ZoomBrowserLauncher().LaunchAsync(new ZoomBrowserLaunchPlan(profile, Headless: headless), token);
        var page = session.Context.Pages.Count > 0 ? session.Context.Pages[0] : await session.Context.NewPageAsync();
        page.SetDefaultTimeout(30000);
        try
        {
            return await ZoomWebSignIn.OpenSignedInAsync(page, ProfileUrl, WaitUntilState.DOMContentLoaded, [accountId, profile.Name], token,
                waitForPerson: headless ? null : WaitForPerson);
        }
        catch (PlaywrightException) { return false; }
    }
}
