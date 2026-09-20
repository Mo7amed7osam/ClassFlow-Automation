using System.Text.Json;
using Xunit;
using ZoomAutoAdmit.CloudWorker.Stages;
using ZoomAutoAdmit.WebAutomation;

namespace ZoomAutoAdmit.CloudWorker.Tests;

/// <summary>
/// The rules class.run keeps before a browser is opened. What happens after that needs a real Zoom
/// meeting and is not pretended at here; these are the refusals, which are the part that decides
/// whether a wrong meeting can be opened at all.
/// </summary>
public sealed class ClassRunStageTests : IDisposable
{
    private const string Plan = "11111111-2222-3333-4444-555555555555";
    private const string Coordinator = "66666666-7777-8888-9999-000000000000";
    private const string ZoomAccount = "cccccccc-dddd-eeee-ffff-aaaaaaaaaaaa";

    public void Dispose() => ZoomSignInCredential.Resolver = null;

    private sealed class Accounts(ZoomSignInCredential? answer = null) : IZoomAccounts
    {
        public readonly List<Guid> Asked = [];

        public Task<ZoomSignInCredential?> ForAsync(Guid zoomAccountId, CancellationToken cancellationToken)
        {
            Asked.Add(zoomAccountId);
            return Task.FromResult(answer);
        }
    }

    private static JsonElement Payload(string? zoomAccountId = ZoomAccount, string? url = "https://zoom.us/j/91473108490",
                                       bool dryRun = false, int? minutes = null) =>
        JsonDocument.Parse($$"""
            {
              "classPlanId": "{{Plan}}",
              "group": "CAI5_AIS4_S7",
              "date": "2026-09-20",
              "startTime": "19:00",
              "coordinatorId": "{{Coordinator}}"
              {{(zoomAccountId is null ? "" : $", \"zoomAccountId\": \"{zoomAccountId}\"")}}
              {{(url is null ? "" : $", \"meetingUrl\": \"{url}\"")}}
              {{(minutes is null ? "" : $", \"durationMinutes\": {minutes}")}}
              , "dryRun": {{(dryRun ? "true" : "false")}}
            }
            """).RootElement;

    [Fact]
    public async Task A_class_with_no_link_never_opens_a_browser()
    {
        var accounts = new Accounts();
        var outcome = await new ClassRunStage(accounts, headless: true).ExecuteAsync(Payload(url: null), default);

        Assert.False(outcome.Succeeded);
        Assert.Equal("invalidPayload", outcome.Error!.Code);
        Assert.Contains("meetingUrl", outcome.Error.Message);
        Assert.Empty(accounts.Asked);          // it did not go asking for a password first
    }

    [Fact]
    public async Task A_meeting_is_opened_by_a_named_account_or_not_at_all()
    {
        // Opening by whichever profile the machine happens to have is the wrong person's meeting,
        // and the students are in it before anyone notices.
        var accounts = new Accounts();
        var outcome = await new ClassRunStage(accounts, headless: true).ExecuteAsync(Payload(zoomAccountId: null), default);

        Assert.Equal("invalidPayload", outcome.Error!.Code);
        Assert.Contains("zoomAccountId", outcome.Error.Message);
        Assert.Empty(accounts.Asked);
    }

    [Fact]
    public async Task The_account_asked_for_is_the_one_the_class_names()
    {
        var accounts = new Accounts();
        await new ClassRunStage(accounts, headless: true).ExecuteAsync(Payload(), default);
        Assert.Equal([Guid.Parse(ZoomAccount)], accounts.Asked);
    }

    [Fact]
    public async Task A_sign_in_the_server_will_not_give_stops_the_class_there()
    {
        var outcome = await new ClassRunStage(new Accounts(answer: null), headless: true)
            .ExecuteAsync(Payload(), default);

        Assert.False(outcome.Succeeded);
        Assert.Equal("noZoomSignIn", outcome.Error!.Code);
        // Not retryable: a coordinator who is turned off, or an account with no password saved,
        // will still be that way in a minute.
        Assert.False(outcome.Error.Retryable);
    }

    [Fact]
    public async Task A_dry_run_checks_everything_and_opens_nothing()
    {
        var credential = new ZoomSignInCredential("mona@zoom.example.com", "her password");
        var outcome = await new ClassRunStage(new Accounts(credential), headless: true)
            .ExecuteAsync(Payload(dryRun: true), default);

        Assert.True(outcome.Succeeded);
        Assert.Equal("would have opened the meeting and held it", outcome.Result!["did"]!.GetValue<string>());
        Assert.True(outcome.Result["dryRun"]!.GetValue<bool>());
        // And nothing of the password is in what it reports.
        Assert.DoesNotContain("her password", outcome.Result.ToJsonString());
    }

    [Fact]
    public async Task How_long_a_meeting_is_held_comes_from_the_class_or_has_a_limit()
    {
        var credential = new ZoomSignInCredential("mona@zoom.example.com", "pw");

        var given = await new ClassRunStage(new Accounts(credential), headless: true)
            .ExecuteAsync(Payload(dryRun: true, minutes: 90), default);
        Assert.Equal(90, given.Result!["holdsFor"]!.GetValue<int>());

        // A class that named no length is still bounded: a worker must not sit in an empty meeting
        // for ever, holding the only slot it has.
        var defaulted = await new ClassRunStage(new Accounts(credential), headless: true)
            .ExecuteAsync(Payload(dryRun: true), default);
        Assert.Equal((int)ClassRunStage.DefaultLength.TotalMinutes, defaulted.Result!["holdsFor"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_password_is_readable_only_through_the_reference_this_class_was_given()
    {
        // One class's sign-in must not be readable while another's is running. The resolver
        // answers for this stage's reference and nothing else.
        var credential = new ZoomSignInCredential("mona@zoom.example.com", "her password");
        await new ClassRunStage(new Accounts(credential), headless: true).ExecuteAsync(Payload(dryRun: true), default);

        // A dry run sets nothing up, so nothing is left readable afterwards either.
        Assert.Null(ZoomSignInCredential.Read($"server:zoom/{ZoomAccount}"));
        Assert.Null(ZoomSignInCredential.Read("server:zoom/somebody-else"));
    }
}
