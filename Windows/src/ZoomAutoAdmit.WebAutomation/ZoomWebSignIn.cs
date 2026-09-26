using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using ZoomAutoAdmit.Core.Formatting;

namespace ZoomAutoAdmit.WebAutomation;

// Never a record: a generated ToString must not be able to print the password.
public sealed class ZoomSignInCredential(string email, string password)
{
    public string Email { get; } = email;
    public string Password { get; } = password;
    public override string ToString() => $"Zoom sign-in {Email} (password redacted)";

    /// <summary>
    /// Where a Zoom password comes from when the reference is not a "wincred:" one. A server has no
    /// Credential Manager, so the cloud worker sets this to ask the central backend instead; it is
    /// given the whole reference and answers null for one it does not recognise.
    /// </summary>
    public static Func<string, ZoomSignInCredential?>? Resolver { get; set; }

    /// <summary>
    /// The account's Zoom email and password. On a PC that is Windows Credential Manager
    /// ("wincred:ZoomAutoAdmit/ZoomProfile/&lt;account&gt;": the email as its user name, the
    /// password as its secret); anywhere else it is whatever <see cref="Resolver"/> was set to.
    /// Null when no password was saved for the account.
    /// </summary>
    /// <summary>Where the Accounts page saves an account's Zoom password on this PC.</summary>
    public static string StandardReference(string accountId) => $"wincred:ZoomAutoAdmit/ZoomProfile/{accountId.Trim()}";

    /// <summary>
    /// The account's sign-in: by its reference, or - when that names nothing, as a hand-typed
    /// reference like "CAI5_IND1_G1" does - from where the Accounts page always saves it. A password
    /// that is there must not be missed because of what was typed into a free-text field
    /// (2026-09-21: G1 waited for a person at 17:45 with the reference set to its own name).
    /// </summary>
    public static ZoomSignInCredential? ReadFor(string? reference, string? accountId)
    {
        var found = Read(reference);
        if (found != null || string.IsNullOrWhiteSpace(accountId)) return found;
        string standard = StandardReference(accountId);
        return string.Equals(reference?.Trim(), standard, StringComparison.OrdinalIgnoreCase) ? null : Read(standard);
    }

    /// <summary>The reference that actually holds the account's sign-in, or null when none does.</summary>
    public static string? ResolveReference(string? reference, string? accountId)
    {
        if (Read(reference) != null) return reference!.Trim();
        if (string.IsNullOrWhiteSpace(accountId)) return null;
        string standard = StandardReference(accountId);
        return Read(standard) != null ? standard : null;
    }

    public static ZoomSignInCredential? Read(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        reference = reference.Trim();

        const string prefix = "wincred:";
        if (!reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return Resolver?.Invoke(reference);

        // A wincred reference off Windows is not an error to shout about: it is a PC's reference
        // reaching a server, and the resolver is the one that may know what to do with it.
        if (!OperatingSystem.IsWindows()) return Resolver?.Invoke(reference);

        string target = reference[prefix.Length..].Trim();
        if (target.Length == 0 || !CredRead(target, 1, 0, out var pointer)) return null;
        try
        {
            var native = Marshal.PtrToStructure<Credential>(pointer);
            if (native.BlobSize is 0 or > 2048 || string.IsNullOrWhiteSpace(native.UserName)) return null;
            var bytes = new byte[native.BlobSize];
            try
            {
                Marshal.Copy(native.Blob, bytes, 0, bytes.Length);
                return new ZoomSignInCredential(native.UserName.Trim(), Encoding.UTF8.GetString(bytes));
            }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? Target, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
}

public enum ZoomSignInOutcome { AlreadySignedIn, SignedIn, NoPassword, NeedsPerson, Failed }

/// <summary>
/// Makes a Web profile the meeting's host before it joins: a profile nobody signed in joins as a
/// guest, and a guest cannot admit anyone. When zoom.us shows its sign-in page, the account's own
/// saved email and password are entered - the same thing a person does once by hand. A captcha or a
/// one-time code needs a person; that is said, and the visible sign-in is left to them.
/// </summary>
public static class ZoomWebSignIn
{
    private static readonly Regex SignInButton = new(@"^\s*sign\s*in\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NextButton = new(@"^\s*(next|continue)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<ZoomSignInOutcome> EnsureSignedInAsync(IBrowserContext context, ZoomSignInCredential? credential, CancellationToken token)
    {
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync("https://zoom.us/profile", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
            // Signed out, zoom.us sends the profile page on to its sign-in page from a script, a
            // moment after the document arrives. Deciding at once read "still on /profile" as
            // signed in, and the meeting then waited for a person.
            try { await page.WaitForURLAsync(url => OnSignInPage(url), new() { Timeout = 8000 }); }
            catch (TimeoutException) { }
            if (!OnSignInPage(page.Url))
            {
                ConsoleLogger.Info("WEB_SIGN_IN: this profile is already signed in to Zoom.");
                return ZoomSignInOutcome.AlreadySignedIn;
            }
            if (credential == null)
            {
                ConsoleLogger.Warn("WEB_SIGN_IN: this Zoom profile is not signed in and no Zoom password is saved for the account (Accounts page).");
                return ZoomSignInOutcome.NoPassword;
            }

            ConsoleLogger.Info($"WEB_SIGN_IN: signing the profile in as {credential.Email}.");
            var email = await FirstVisibleAsync(page, "input[type=email], input#email, input[name=email]", token);
            if (email == null) return await NeedsPersonAsync(page);
            await email.FillAsync(credential.Email);
            var password = await FirstVisibleAsync(page, "input[type=password]", token, TimeSpan.FromSeconds(2));
            if (password == null)
            {
                // Newer sign-in: email first, then the password on the next step.
                await PressAsync(page, NextButton);
                password = await FirstVisibleAsync(page, "input[type=password]", token, TimeSpan.FromSeconds(10));
                if (password == null) return await NeedsPersonAsync(page);
            }
            await password.FillAsync(credential.Password);
            if (!await PressAsync(page, SignInButton)) await password.PressAsync("Enter");

            var deadline = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(1000, token);
                if (!OnSignInPage(page.Url))
                {
                    ConsoleLogger.Success($"WEB_SIGN_IN: signed in as {credential.Email}.");
                    return ZoomSignInOutcome.SignedIn;
                }
                if (await AsksForPersonAsync(page)) return await NeedsPersonAsync(page);
            }
            ConsoleLogger.Warn("WEB_SIGN_IN: Zoom did not accept the sign-in in 40 seconds (check the saved Zoom password).");
            return ZoomSignInOutcome.Failed;
        }
        catch (PlaywrightException ex)
        {
            ConsoleLogger.Warn($"WEB_SIGN_IN: {ex.Message.Split('\n')[0]}");
            return ZoomSignInOutcome.Failed;
        }
        finally
        {
            try { await page.CloseAsync(); } catch (PlaywrightException) { }
        }
    }

    private static bool OnSignInPage(string url) =>
        url.Contains("/signin", StringComparison.OrdinalIgnoreCase) || url.Contains("/login", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> AsksForPersonAsync(IPage page)
    {
        try
        {
            if (page.Frames.Any(f => f.Url.Contains("captcha", StringComparison.OrdinalIgnoreCase))) return true;
            var code = page.Locator("input[autocomplete='one-time-code'], input[name*='code' i], input[id*='code' i]");
            foreach (var input in await code.AllAsync()) if (await input.IsVisibleAsync()) return true;
        }
        catch (PlaywrightException) { }
        return false;
    }

    private static Task<ZoomSignInOutcome> NeedsPersonAsync(IPage page)
    {
        ConsoleLogger.Warn("WEB_SIGN_IN: Zoom asks for a person (captcha or a one-time code). Sign this profile in by hand once; it is remembered.");
        return Task.FromResult(ZoomSignInOutcome.NeedsPerson);
    }

    private static async Task<ILocator?> FirstVisibleAsync(IPage page, string selector, CancellationToken token, TimeSpan? wait = null)
    {
        var until = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            foreach (var element in await page.Locator(selector).AllAsync())
                if (await element.IsVisibleAsync()) return element;
            if (DateTime.UtcNow >= until) return null;
            await Task.Delay(400, token);
        }
    }

    private static async Task<bool> PressAsync(IPage page, Regex name)
    {
        foreach (var button in await page.GetByRole(AriaRole.Button, new() { NameRegex = name }).AllAsync())
        {
            if (!await button.IsVisibleAsync() || !await button.IsEnabledAsync()) continue;
            // Zoom's account page can keep the enabled Continue button in motion during its
            // responsive-layout transition. A five-second click timeout turned that transient
            // movement into a permanent class failure even though the button was present.
            await button.ClickAsync(new() { Timeout = 20000 });
            return true;
        }
        return false;
    }
}
