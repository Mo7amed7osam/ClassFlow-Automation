using System.Text.Json;
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

        public Task<List<CentralZoomAccount>> ZoomAccountsAsync(CancellationToken token) =>
            Task.FromResult(Kept.ToList());

        public Task<JsonElement> SaveZoomAccountsAsync(IEnumerable<object> accounts, CancellationToken token)
        {
            Sent.Add(JsonSerializer.Serialize(accounts));
            return Task.FromResult(JsonDocument.Parse("""{"accounts":[]}""").RootElement);
        }
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
    public async Task NoZoomSignInIsEverPartOfIt()
    {
        var api = new FakeApi();
        // The credential reference is how this PC finds a saved sign-in; it is not one, and it has
        // no business on the server either.
        await new ZoomServerAccounts(api, _ => { }).PushAsync([
            new WindowsMeetingAccountMetadata("CAI5_AIS4_S7", "S7", "ZoomAutoAdmit/Zoom/Secret")
            { DefaultMeetingUrl = "https://zoom.us/j/91473108490", ZoomEmail = "mona@zoom.example.com" },
        ]);

        string sent = Assert.Single(api.Sent);
        Assert.DoesNotContain("Secret", sent);
        Assert.DoesNotContain("password", sent, StringComparison.OrdinalIgnoreCase);
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
}
