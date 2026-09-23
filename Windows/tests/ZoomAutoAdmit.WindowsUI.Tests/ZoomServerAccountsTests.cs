using System.Text.Json;
using ZoomAutoAdmit.WebAutomation;
using ZoomAutoAdmit.WindowsRuntime;
using ZoomAutoAdmit.WindowsUI.Services;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

/// <summary>
/// This PC's Zoom accounts, kept against the signed-in person's dashboard account. It is what lets
/// whoever runs their classes pick the account and the meeting link from what they already have,
/// instead of being told either of them a second time. No Zoom sign-in ever leaves this PC.
/// </summary>
public sealed class ZoomServerAccountsTests
{
    private sealed class FakeApi : IZoomAccountsApi
    {
        public bool IsSignedIn { get; set; } = true;
        public List<string> Sent { get; } = [];
        /// <summary>What the server already holds for this person, for a PC that has none.</summary>
        public List<CentralZoomAccount> Kept { get; } = [];

        /// <summary>The Zoom sign-in the server kept, by account id.</summary>
        public Dictionary<string, (string Email, string Password)> Secrets { get; } = [];
        public List<string> SecretsRead { get; } = [];

        public Task<List<CentralZoomAccount>> ZoomAccountsAsync(CancellationToken token) =>
            Task.FromResult(Kept.ToList());

        public Task<CentralZoomSecret> ZoomSecretAsync(string accountId, CancellationToken token)
        {
            SecretsRead.Add(accountId);
            if (!Secrets.TryGetValue(accountId, out var found)) throw new CentralApiException(System.Net.HttpStatusCode.NotFound, "No password");
            return Task.FromResult(new CentralZoomSecret { Id = accountId, Email = found.Email, Password = found.Password });
        }

        public Task<JsonElement> SaveZoomAccountsAsync(IEnumerable<object> accounts, CancellationToken token)
        {
            Sent.Add(JsonSerializer.Serialize(accounts));
            return Task.FromResult(JsonDocument.Parse("""{"accounts":[]}""").RootElement);
        }
    }

    private sealed class FakeCredentials : IZoomProfileCredentialStore
    {
        public Dictionary<string, (string Email, string Password)> Saved { get; } = [];
        public bool HasPassword(string accountId) => Saved.ContainsKey(accountId);
        public string ReferenceFor(string accountId) => $"wincred:ZoomAutoAdmit/ZoomProfile/{accountId}";
        public void Save(string accountId, string email, string password) => Saved[accountId] = (email, password);
        public void Delete(string accountId) => Saved.Remove(accountId);
    }

    private static WindowsMeetingAccountMetadata Account(string id, string? url = "https://zoom.us/j/91473108490",
        string? group = null, string? email = null, AccountEnginePreference? engine = null) =>
        new(id, id, "", engine) { DefaultMeetingUrl = url, GroupName = group ?? id, ZoomEmail = email };

    [Fact]
    public async Task NothingIsSentBeforeSomebodyIsSignedIn()
    {
        var api = new FakeApi { IsSignedIn = false };
        Assert.Null(await new ZoomServerAccounts(api, _ => { }).PushAsync([Account("CAI5_AIS4_S7")]));
        Assert.Empty(api.Sent);
    }

    [Fact]
    public async Task EachAccountGoesWithItsGroupItsEmailAndTheLinkItsClassesOpen()
    {
        var api = new FakeApi();
        await new ZoomServerAccounts(api, _ => { }).PushAsync([
            Account("CAI5_AIS4_S7", group: "CAI5_AIS4_S7", email: "mona@zoom.example.com", engine: AccountEnginePreference.Web),
        ]);

        string sent = Assert.Single(api.Sent);
        Assert.Contains("\"accountId\":\"CAI5_AIS4_S7\"", sent);
        Assert.Contains("\"group\":\"CAI5_AIS4_S7\"", sent);
        Assert.Contains("\"zoomEmail\":\"mona@zoom.example.com\"", sent);
        Assert.Contains("\"meetingUrl\":\"https://zoom.us/j/91473108490\"", sent);
        Assert.Contains("\"preferredEngine\":\"web\"", sent);
    }

    [Fact]
    public async Task AHalfTypedLinkIsLeftBehindRatherThanFailingTheWholeSend()
    {
        var api = new FakeApi();
        await new ZoomServerAccounts(api, _ => { }).PushAsync([
            Account("GOOD"), Account("BAD", url: "zoom.us/j/1"), Account("EMPTY", url: null),
        ]);

        string sent = Assert.Single(api.Sent);
        Assert.Contains("\"accountId\":\"GOOD\"", sent);
        Assert.Contains("\"accountId\":\"BAD\"", sent);            // the account is still theirs
        Assert.DoesNotContain("zoom.us/j/1\"", sent);              // but that is not a link
    }

    [Fact]
    public async Task SendingTheSameAccountsAgainDoesNotTalkToTheServerForNothing()
    {
        var api = new FakeApi();
        var accounts = new ZoomServerAccounts(api, _ => { });
        Assert.Equal(1, await accounts.PushAsync([Account("CAI5_AIS4_S7")]));
        Assert.Null(await accounts.PushAsync([Account("CAI5_AIS4_S7")]));
        Assert.Single(api.Sent);
    }

    [Fact]
    public async Task AChangedAccountGoesAtOnce()
    {
        var api = new FakeApi();
        var accounts = new ZoomServerAccounts(api, _ => { });
        await accounts.PushAsync([Account("CAI5_AIS4_S7")]);
        await accounts.PushAsync([Account("CAI5_AIS4_S7", url: "https://zoom.us/j/999999999")]);

        Assert.Equal(2, api.Sent.Count);
        Assert.Contains("999999999", api.Sent[1]);
    }

    [Fact]
    public async Task UsingTheAccountsPageSendsThemAgainEvenIfNothingLooksDifferent()
    {
        var api = new FakeApi();
        var accounts = new ZoomServerAccounts(api, _ => { });
        await accounts.PushAsync([Account("CAI5_AIS4_S7")]);
        accounts.Changed();
        await accounts.PushAsync([Account("CAI5_AIS4_S7")]);

        Assert.Equal(2, api.Sent.Count);
    }

    [Fact]
    public async Task WhereThisPcKeepsTheSignInIsItsOwnBusinessAndNeverGoesUp()
    {
        var api = new FakeApi();
        // The credential reference says where on THIS PC the sign-in lives. The password itself
        // travels; the place it is kept does not, because it means nothing anywhere else.
        await new ZoomServerAccounts(api, _ => { }, new FakeCredentials(),
            readLocal: _ => new ZoomSignInCredential("mona@zoom.example.com", "made-up Zoom password")).PushAsync([
            new WindowsMeetingAccountMetadata("CAI5_AIS4_S7", "S7", "wincred:ZoomAutoAdmit/ZoomProfile/CAI5_AIS4_S7")
            { DefaultMeetingUrl = "https://zoom.us/j/91473108490", ZoomEmail = "mona@zoom.example.com" },
        ]);

        string sent = Assert.Single(api.Sent);
        Assert.DoesNotContain("wincred", sent);
        Assert.DoesNotContain("ZoomProfile", sent);
        Assert.Contains("made-up Zoom password", sent);
    }

    [Fact]
    public async Task APasswordSavedWhereTheAccountsPageSavesItGoesUpEvenUnderAHandTypedReference()
    {
        // 2026-09-21: G1's reference was typed as "CAI5_IND1_G1", which names nothing.
        var api = new FakeApi();
        await new ZoomServerAccounts(api, _ => { }, new FakeCredentials(),
            readLocal: reference => reference == "wincred:ZoomAutoAdmit/ZoomProfile/CAI5_IND1_G1"
                ? new ZoomSignInCredential("depi+10@zoom.example.com", "made-up Zoom password") : null).PushAsync([
            new WindowsMeetingAccountMetadata("CAI5_IND1_G1", "G1", "CAI5_IND1_G1") { ZoomEmail = "depi+10@zoom.example.com" },
        ]);

        Assert.Contains("made-up Zoom password", Assert.Single(api.Sent));
    }

    [Fact]
    public async Task APcWithNoSignInForAnAccountSendsNoPasswordForIt()
    {
        var api = new FakeApi();
        await new ZoomServerAccounts(api, _ => { }, new FakeCredentials(), readLocal: _ => null)
            .PushAsync([Account("CAI5_AIS4_S7")]);

        // null, not "": the server leaves whatever it has alone rather than clearing it.
        Assert.Contains("\"password\":null", Assert.Single(api.Sent));
    }

    // ------------------------------------------------------------------ a PC that has none yet

    [Fact]
    public async Task ANewPcTakesTheAccountsAndTheirLinksFromTheDashboardAccount()
    {
        var api = new FakeApi();
        api.Kept.Add(new CentralZoomAccount("z1", "CAI5_AIS4_S7", "S7", "mona@zoom.example.com", "CAI5_AIS4_S7",
            "https://zoom.us/j/91473108490", "web", true));
        var here = new List<WindowsMeetingAccountMetadata>();

        string? said = await new ZoomServerAccounts(api, _ => { })
            .SyncAsync([], (account, _) => { here.Add(account); return Task.CompletedTask; });

        var restored = Assert.Single(here);
        Assert.Equal("CAI5_AIS4_S7", restored.AccountId);
        Assert.Equal("mona@zoom.example.com", restored.ZoomEmail);
        Assert.Equal("https://zoom.us/j/91473108490", restored.DefaultMeetingUrl);
        Assert.Equal("CAI5_AIS4_S7", restored.GroupName);
        Assert.Equal(AccountEnginePreference.Web, restored.PreferredEngine);
        Assert.Contains("came from your dashboard account", said);
        Assert.Contains("sign in to Zoom", said);          // the one thing that cannot travel
    }

    [Fact]
    public async Task APcWithNoAccountsNeverEmptiesWhatTheServerKept()
    {
        var api = new FakeApi();
        api.Kept.Add(new CentralZoomAccount("z1", "CAI5_AIS4_S7", "S7", null, null, null, null, true));

        await new ZoomServerAccounts(api, _ => { }).SyncAsync([], (_, _) => Task.CompletedTask);

        Assert.Empty(api.Sent);                            // nothing was sent, least of all an empty list
    }

    [Fact]
    public async Task APcWithNothingAnywhereSaysNothingHappened()
    {
        var api = new FakeApi();
        Assert.Null(await new ZoomServerAccounts(api, _ => { }).SyncAsync([], (_, _) => Task.CompletedTask));
        Assert.Empty(api.Sent);
    }

    [Fact]
    public async Task APcThatHasItsOwnAccountsSendsThemAndTakesNothing()
    {
        var api = new FakeApi();
        api.Kept.Add(new CentralZoomAccount("z1", "SOMETHING_ELSE", "Else", null, null, null, null, true));
        var took = new List<WindowsMeetingAccountMetadata>();

        string? said = await new ZoomServerAccounts(api, _ => { })
            .SyncAsync([Account("CAI5_AIS4_S7")], (account, _) => { took.Add(account); return Task.CompletedTask; });

        Assert.Empty(took);                                // its own accounts are the ones that count
        Assert.Contains("CAI5_AIS4_S7", Assert.Single(api.Sent));
        Assert.Contains("were saved to your dashboard account", said);
    }

    [Fact]
    public async Task OneAccountThatCannotBeWrittenDoesNotStopTheRest()
    {
        var api = new FakeApi();
        api.Kept.Add(new CentralZoomAccount("z1", "BAD", "Bad", null, null, null, null, false));
        api.Kept.Add(new CentralZoomAccount("z2", "GOOD", "Good", null, null, null, null, true));
        var here = new List<WindowsMeetingAccountMetadata>();

        int restored = await new ZoomServerAccounts(api, _ => { }).RestoreAsync((account, _) =>
        {
            if (account.AccountId == "BAD") throw new InvalidOperationException("that name is taken");
            here.Add(account);
            return Task.CompletedTask;
        });

        Assert.Equal(1, restored);
        Assert.Equal("GOOD", Assert.Single(here).AccountId);
    }

    // ------------------------------------------------------------------ the Zoom sign-in itself

    [Fact]
    public async Task TheZoomPasswordThisPcHasGoesUpWithItsAccount()
    {
        var api = new FakeApi();
        var accounts = new ZoomServerAccounts(api, _ => { }, new FakeCredentials(),
            readLocal: reference => reference == "wincred:S7" ? new ZoomSignInCredential("s7@zoom.example.com", "made-up Zoom password") : null);

        await accounts.PushAsync([
            new WindowsMeetingAccountMetadata("CAI5_AIS4_S7", "S7", "wincred:S7") { DefaultMeetingUrl = "https://zoom.us/j/1" },
            new WindowsMeetingAccountMetadata("CAI5_AIS4_S8", "S8", "") { DefaultMeetingUrl = "https://zoom.us/j/2" },
        ]);

        string sent = Assert.Single(api.Sent);
        Assert.Contains("\"password\":\"made-up Zoom password\"", sent);
        // The account with no password here sends none, so the kept one is left alone.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(sent, "\"password\":\""));
    }

    [Fact]
    public async Task ANewPcTakesTheZoomSignInTooAndKeepsItWhereEverythingLooks()
    {
        var api = new FakeApi();
        api.Kept.Add(new CentralZoomAccount("z1", "CAI5_AIS4_S7", "S7", "s7@zoom.example.com", "CAI5_AIS4_S7",
            "https://zoom.us/j/1", null, true) { HasPassword = true });
        api.Secrets["z1"] = ("s7@zoom.example.com", "made-up Zoom password");
        var credentials = new FakeCredentials();
        var here = new List<WindowsMeetingAccountMetadata>();

        string? said = await new ZoomServerAccounts(api, _ => { }, credentials, _ => null)
            .SyncAsync([], (a, _) => { here.Add(a); return Task.CompletedTask; });

        Assert.Equal(("s7@zoom.example.com", "made-up Zoom password"), credentials.Saved["CAI5_AIS4_S7"]);
        // The account points at it, so ZoomWebSignIn finds it exactly as if it had been typed here.
        Assert.Equal("wincred:ZoomAutoAdmit/ZoomProfile/CAI5_AIS4_S7", Assert.Single(here).CredentialReference);
        Assert.Contains("with their Zoom sign-in", said);
        Assert.DoesNotContain("sign in to Zoom", said);
    }

    [Fact]
    public async Task AnAccountWithNoKeptPasswordStillComesAndSaysWhoNeedsSigningIn()
    {
        var api = new FakeApi();
        api.Kept.Add(new CentralZoomAccount("z1", "WITH", "With", null, null, null, null, true) { HasPassword = true });
        api.Kept.Add(new CentralZoomAccount("z2", "WITHOUT", "Without", null, null, null, null, false));
        api.Secrets["z1"] = ("with@zoom.example.com", "made-up Zoom password");
        var credentials = new FakeCredentials();
        var here = new List<WindowsMeetingAccountMetadata>();

        string? said = await new ZoomServerAccounts(api, _ => { }, credentials, _ => null)
            .SyncAsync([], (a, _) => { here.Add(a); return Task.CompletedTask; });

        Assert.Equal(2, here.Count);
        Assert.Single(credentials.Saved);
        Assert.Contains("sign in to Zoom as 1 of them once", said);
        Assert.Equal(["z1"], api.SecretsRead);          // only the one that has a password is asked for
    }

    [Fact]
    public async Task APasswordTheServerWillNotGiveBackDoesNotCostTheAccount()
    {
        var api = new FakeApi();
        api.Kept.Add(new CentralZoomAccount("z1", "CAI5_AIS4_S7", "S7", null, null, null, null, true) { HasPassword = true });
        // No entry in Secrets: the server refuses it.
        var credentials = new FakeCredentials();
        var here = new List<WindowsMeetingAccountMetadata>();

        await new ZoomServerAccounts(api, _ => { }, credentials, _ => null)
            .SyncAsync([], (a, _) => { here.Add(a); return Task.CompletedTask; });

        Assert.Single(here);                            // the account is still here
        Assert.Empty(credentials.Saved);                // its profile just needs a person once
        Assert.Equal("", here[0].CredentialReference);
    }
}
