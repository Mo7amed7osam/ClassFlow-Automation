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
                return new ZoomSignInCredential(native.UserName.Trim(), DecodeSecret(bytes));
            }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    /// <summary>
    /// A password as Credential Manager holds it. The app writes UTF-8; a password typed into
    /// Windows' own Credential Manager window is kept as UTF-16, and read as UTF-8 every letter
    /// came out followed by a NUL - Zoom was sent "D\0e\0p\0..." and refused it (2026-09-26, S8).
    /// </summary>
    public static string DecodeSecret(byte[] bytes)
    {
        bool utf16 = bytes.Length >= 2 && bytes.Length % 2 == 0
                     && Enumerable.Range(0, bytes.Length / 2).Count(i => bytes[2 * i + 1] == 0) * 2 >= bytes.Length / 2;
        string text = utf16 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
        return text.TrimEnd('\0');
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

    /// <param name="keepPageForPerson">
    /// In a visible browser: when Zoom wants a person, the page is left open where the typing
    /// stopped, so they carry on from there instead of starting again.
    /// </param>
    public static async Task<ZoomSignInOutcome> EnsureSignedInAsync(IBrowserContext context, ZoomSignInCredential? credential, CancellationToken token,
        bool keepPageForPerson = false)
    {
        var page = await context.NewPageAsync();
        var outcome = ZoomSignInOutcome.Failed;
        try
        {
            outcome = await SignInOnAsync(page, credential, token);
            return outcome;
        }
        finally
        {
            if (!(keepPageForPerson && outcome is ZoomSignInOutcome.NeedsPerson or ZoomSignInOutcome.Failed))
                try { await page.CloseAsync(); } catch (PlaywrightException) { }
        }
    }

    private static async Task<ZoomSignInOutcome> SignInOnAsync(IPage page, ZoomSignInCredential? credential, CancellationToken token)
    {
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
            // Zoom's own field for it has moved: "email" until September 2026, then "account" -
            // "Enter email, Zoom Mail or phone number", with the password on the next step.
            var email = await FirstVisibleAsync(page,
                "input[type=email], input#email, input[name=email], input[name=account], input#account, input[autocomplete=username]", token);
            if (email == null) return await NeedsPersonAsync(page, "no field to type the e-mail into");
            // Typed, not pasted: the newer page enables Next only on the keys it sees typed.
            await email.ClickAsync();
            await email.FillAsync("");
            await email.PressSequentiallyAsync(credential.Email, new() { Delay = 40 });
            var password = await FirstVisibleAsync(page, "input[type=password]", token, TimeSpan.FromSeconds(2));
            if (password == null)
            {
                // Newer sign-in: email first, then the password on the next step.
                bool pressed = false;
                for (int wait = 0; wait < 10 && !pressed; wait++)
                {
                    pressed = await PressAsync(page, NextButton);
                    if (!pressed) await Task.Delay(500, token);
                }
                if (!pressed) await email.PressAsync("Enter");
                password = await FirstVisibleAsync(page, "input[type=password]", token, TimeSpan.FromSeconds(15));
                if (password == null) return await NeedsPersonAsync(page, "no password field after Next");
            }
            await password.FillAsync(credential.Password);
            if (!await PressAsync(page, SignInButton)) await password.PressAsync("Enter");

            var deadline = DateTime.UtcNow.AddSeconds(40);
            int asking = 0;
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(1000, token);
                if (!OnSignInPage(page.Url))
                {
                    ConsoleLogger.Success($"WEB_SIGN_IN: signed in as {credential.Email}.");
                    return ZoomSignInOutcome.SignedIn;
                }
                // A challenge a person must answer does not go away by itself; one seen for a moment
                // while the page moves on is not one. Five seconds of it is.
                asking = await AsksForPersonAsync(page) ? asking + 1 : 0;
                if (asking >= 5) return await NeedsPersonAsync(page, "a challenge or a code is shown");
            }
            ConsoleLogger.Warn($"WEB_SIGN_IN: Zoom did not accept the sign-in in 40 seconds (check the saved Zoom password). {await DescribeAsync(page)}");
            return ZoomSignInOutcome.Failed;
        }
        catch (PlaywrightException ex)
        {
            ConsoleLogger.Warn($"WEB_SIGN_IN: {ex.Message.Split('\n')[0]}");
            return ZoomSignInOutcome.Failed;
        }
    }

    /// <summary>
    /// Opens a zoom.us page on a profile, signing the profile in first when Zoom sends it to its
    /// sign-in page - with the same saved password the meeting uses, looked up by the account's name
    /// and then the profile's. A page that reads recordings or reports needs the account as much as a
    /// meeting does (2026-09-25: the 's8' profile had lost its zoom.us session while S8's password
    /// was saved, and every recording read stopped at "not signed in"). A network that is down is
    /// tried again three times before it counts. Answers false when the page still asks for a sign-in.
    /// </summary>
    /// <param name="waitForPerson">
    /// In a visible browser, how long to leave Zoom's sign-in page up for a person when Zoom asks for a
    /// captcha or a code: they sign in there once, and the profile remembers it.
    /// </param>
    public static async Task<bool> OpenSignedInAsync(IPage page, string url, WaitUntilState waitUntil,
        IEnumerable<string?> accounts, CancellationToken token, TimeSpan? waitForPerson = null)
    {
        await GotoAsync(page, url, waitUntil, token);
        try { await page.WaitForURLAsync(address => OnSignInPage(address), new() { Timeout = 5000 }); }
        catch (TimeoutException) { }
        if (!OnSignInPage(page.Url)) return true;

        var credential = accounts.Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => ZoomSignInCredential.ReadFor(null, name))
            .FirstOrDefault(found => found != null);
        bool person = waitForPerson is { } w && w > TimeSpan.Zero;
        var outcome = await EnsureSignedInAsync(page.Context, credential, token, keepPageForPerson: person);
        if (outcome is not (ZoomSignInOutcome.SignedIn or ZoomSignInOutcome.AlreadySignedIn))
        {
            if (!person) return false;
            // The person finishes in the window: the tab where the e-mail and password were typed
            // is still open. Signed in shows as any tab of the profile leaving Zoom's sign-in page.
            ConsoleLogger.Info($"WEB_SIGN_IN: finish signing in to Zoom in the open window (captcha or code); waiting up to {waitForPerson!.Value.TotalMinutes:0} minutes.");
            var until = DateTime.UtcNow + waitForPerson.Value;
            bool done = false;
            while (!done && DateTime.UtcNow < until)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(2000, token);
                var tabs = page.Context.Pages;
                if (tabs.Count == 0) return false;                       // the window was closed
                done = tabs.Any(tab => tab.Url.Contains("zoom.us", StringComparison.OrdinalIgnoreCase) && !OnSignInPage(tab.Url)
                                       && !tab.Url.Contains("/signup", StringComparison.OrdinalIgnoreCase));
            }
            if (!done) return false;
            ConsoleLogger.Success("WEB_SIGN_IN: signed in by hand; the profile remembers it.");
        }
        await GotoAsync(page, url, waitUntil, token);
        return !OnSignInPage(page.Url);
    }

    /// <summary>Goes to a page, trying again after 5 and 20 seconds when the network is what failed.</summary>
    public static async Task GotoAsync(IPage page, string url, WaitUntilState waitUntil, CancellationToken token)
    {
        TimeSpan[] waits = [TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20)];
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await page.GotoAsync(url, new() { WaitUntil = waitUntil });
                return;
            }
            catch (PlaywrightException ex) when (attempt + 1 < waits.Length
                                                 && ex.Message.Contains("net::ERR_", StringComparison.OrdinalIgnoreCase)
                                                 && !ex.Message.Contains("ERR_ABORTED", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleLogger.Warn($"WEB: {url} could not be reached ({ex.Message.Split((char)10)[0].Trim()}); trying again.");
                await Task.Delay(waits[attempt + 1], token);
            }
        }
    }

    private static bool OnSignInPage(string url) =>
        url.Contains("/signin", StringComparison.OrdinalIgnoreCase) || url.Contains("/login", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> AsksForPersonAsync(IPage page)
    {
        try
        {
            // Zoom's sign-in page always carries an invisible reCAPTCHA frame, so a captcha frame
            // being there says nothing (2026-09-26: every sign-in of 's8' gave up on it). Only a
            // challenge shown on the page - reCAPTCHA's picture grid, hCaptcha - is one.
            var challenge = page.Locator("iframe[src*='bframe'], iframe[src*='hcaptcha.com'], iframe[title*='challenge' i]");
            foreach (var frame in await challenge.AllAsync())
                if (await frame.IsVisibleAsync() && await frame.BoundingBoxAsync() is { Height: > 60 }) return true;
            var code = page.Locator("input[autocomplete='one-time-code'], input[name*='code' i]:not([name*='country' i]), input[id*='code' i]:not([id*='country' i])");
            foreach (var input in await code.AllAsync()) if (await input.IsVisibleAsync()) return true;
        }
        catch (PlaywrightException) { }
        return false;
    }

    private static async Task<ZoomSignInOutcome> NeedsPersonAsync(IPage page, string why)
    {
        ConsoleLogger.Warn($"WEB_SIGN_IN: Zoom asks for a person ({why}). Sign this profile in by hand once; it is remembered. " +
                           await DescribeAsync(page));
        return ZoomSignInOutcome.NeedsPerson;
    }

    /// <summary>
    /// What the sign-in page shows when it stops: its address without the query, the fields a person
    /// could type into, and its first words - so "asks for a person" can be told from a page that
    /// only looked like it. Never the values in the fields.
    /// </summary>
    private static async Task<string> DescribeAsync(IPage page)
    {
        try
        {
            string address = page.Url.Split('?')[0];
            var fields = new List<string>();
            foreach (var input in await page.Locator("input").AllAsync())
            {
                try
                {
                    if (await input.IsVisibleAsync())
                        fields.Add($"{await input.GetAttributeAsync("type") ?? "text"}:{await input.GetAttributeAsync("name") ?? await input.GetAttributeAsync("id") ?? "?"}");
                }
                catch (PlaywrightException) { }
            }
            string text = System.Text.RegularExpressions.Regex.Replace(await page.Locator("body").InnerTextAsync(new() { Timeout = 3000 }), @"\s+", " ").Trim();
            return $"[page {address}; fields {string.Join(", ", fields)}; reads \"{(text.Length > 240 ? text[..240] + "..." : text)}\"]";
        }
        catch (Exception) { return ""; }
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
            await button.ClickAsync(new() { Timeout = 5000 });
            return true;
        }
        return false;
    }
}
