using System.Text.Json;
using ZoomAutoAdmit.WindowsUI.Services;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

/// <summary>
/// Telling somebody a class needs them. The notice goes to a webhook (n8n), which sends the e-mail;
/// no mailbox password is ever kept here. Nothing about a class depends on the notice arriving.
/// </summary>
public sealed class ClassNotifierTests
{
    private static readonly ClassNotice DidNotOpen =
        new("class-did-not-open", "G1 class did not open", "every try failed: the profile was in use")
        {
            Group = "CAI5_IND1_G1", Coordinator = "Hosam", Date = "2026-09-23", Start = "18:00",
        };

    private static ClassNotifier Notifier(List<string> sent, Func<int, bool>? answers = null, NotifySettings? settings = null)
    {
        int attempt = 0;
        return new ClassNotifier(
            settings: () => settings ?? new NotifySettings { Url = "https://n8n.example.com/webhook/class-notifications", Pc = "THE-PC" },
            post: (url, body, _) =>
            {
                sent.Add(body);
                return Task.FromResult(answers?.Invoke(attempt++) ?? true);
            },
            log: _ => { });
    }

    [Fact]
    public async Task AClassThatDidNotOpenIsSentWithEverythingTheEmailNeeds()
    {
        List<string> sent = [];
        Assert.True(await Notifier(sent).SendAsync(DidNotOpen));

        using var body = JsonDocument.Parse(Assert.Single(sent));
        var it = body.RootElement;
        Assert.Equal("class-did-not-open", it.GetProperty("kind").GetString());
        Assert.Equal("G1 class did not open", it.GetProperty("title").GetString());
        Assert.Equal("CAI5_IND1_G1", it.GetProperty("group").GetString());
        Assert.Equal("Hosam", it.GetProperty("coordinator").GetString());
        Assert.Equal("2026-09-23", it.GetProperty("date").GetString());
        Assert.Equal("18:00", it.GetProperty("start").GetString());
        Assert.Equal("THE-PC", it.GetProperty("pc").GetString());          // which PC, when there are several
        Assert.True(DateTimeOffset.TryParse(it.GetProperty("at").GetString(), out _));
    }

    [Fact]
    public async Task ANoticeWrittenWhileTheNetworkIsDownGoesOutOnceItIsBack()
    {
        var gaps = ClassNotifier.TryAgainAfter;
        ClassNotifier.TryAgainAfter = [TimeSpan.Zero, TimeSpan.Zero];
        try
        {
            List<string> sent = [];
            bool networkUp = false;
            var notifier = new ClassNotifier(
                settings: () => new NotifySettings { Url = "https://n8n.example.com/webhook/class-notifications" },
                post: (_, body, _) => networkUp ? Task.FromResult(Record(sent, body)) : throw new HttpRequestException("No such host is known."),
                log: _ => { });

            Assert.False(await notifier.SendAsync(DidNotOpen));
            Assert.Equal(1, notifier.Waiting);
            Assert.Equal(0, await notifier.FlushAsync());            // still down: kept

            networkUp = true;
            Assert.Equal(1, await notifier.FlushAsync());
            Assert.Equal(0, notifier.Waiting);
            using var body = JsonDocument.Parse(Assert.Single(sent));
            Assert.Equal("G1 class did not open", body.RootElement.GetProperty("title").GetString());
        }
        finally { ClassNotifier.TryAgainAfter = gaps; }
    }

    [Fact]
    public async Task WhatWasKeptGoesWithTheNextNoticeThatGetsThrough()
    {
        var gaps = ClassNotifier.TryAgainAfter;
        ClassNotifier.TryAgainAfter = [TimeSpan.Zero, TimeSpan.Zero];
        try
        {
            List<string> sent = [];
            bool networkUp = false;
            var notifier = new ClassNotifier(
                settings: () => new NotifySettings { Url = "https://n8n.example.com/webhook/class-notifications" },
                post: (_, body, _) => networkUp ? Task.FromResult(Record(sent, body)) : throw new HttpRequestException("No such host is known."),
                log: _ => { });
            await notifier.SendAsync(DidNotOpen);

            networkUp = true;
            Assert.True(await notifier.SendAsync(DidNotOpen with { Title = "the next one" }));
            Assert.Equal(2, sent.Count);
            Assert.Equal(0, notifier.Waiting);
        }
        finally { ClassNotifier.TryAgainAfter = gaps; }
    }

    private static bool Record(List<string> sent, string body) { sent.Add(body); return true; }

    [Fact]
    public async Task AStuckStepCarriesItsStepAndHowManyTriesItHasHad()
    {
        List<string> sent = [];
        await Notifier(sent).SendAsync(new ClassNotice("step-stuck", "CAI5_IND1_G1: TakeAttendance is stuck", "the LMS refused the sign-in")
        {
            Group = "CAI5_IND1_G1", Step = "TakeAttendance", Attempts = 3,
        });

        using var body = JsonDocument.Parse(Assert.Single(sent));
        Assert.Equal("TakeAttendance", body.RootElement.GetProperty("step").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task WithNoWebhookNothingIsSentAndNothingFails()
    {
        List<string> sent = [];
        Assert.False(await Notifier(sent, settings: new NotifySettings()).SendAsync(DidNotOpen));
        Assert.Empty(sent);
    }

    [Fact]
    public async Task NoticesTurnedOffAreNotSent()
    {
        List<string> sent = [];
        var off = new NotifySettings { Url = "https://n8n.example.com/webhook/class-notifications", Enabled = false };
        Assert.False(await Notifier(sent, settings: off).SendAsync(DidNotOpen));
        Assert.Empty(sent);
    }

    [Fact]
    public async Task AWebhookThatIsDownIsTriedAgainAndThenLetGo()
    {
        ClassNotifier.TryAgainAfter = [TimeSpan.Zero, TimeSpan.Zero];
        List<string> sent = [];

        // Every try refused: the class carries on regardless, which is the point.
        Assert.False(await Notifier(sent, answers: _ => false).SendAsync(DidNotOpen));
        Assert.Equal(3, sent.Count);

        sent.Clear();
        // Down once, then up: the notice still arrives.
        Assert.True(await Notifier(sent, answers: attempt => attempt > 0).SendAsync(DidNotOpen));
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task AWebhookThatThrowsIsNoWorseThanOneThatRefuses()
    {
        ClassNotifier.TryAgainAfter = [TimeSpan.Zero];
        var notifier = new ClassNotifier(
            settings: () => new NotifySettings { Url = "https://n8n.example.com/webhook/class-notifications" },
            post: (_, _, _) => throw new HttpRequestException("No such host is known."),
            log: _ => { });

        Assert.False(await notifier.SendAsync(DidNotOpen));
    }

    [Fact]
    public void TheSettingsAreKeptAsThePcLeftThem()
    {
        string path = Path.Combine(Path.GetTempPath(), $"notify-{Guid.NewGuid():N}.json");
        string was = NotifySettingsStore.Path;
        NotifySettingsStore.Path = path;
        try
        {
            Assert.Equal("", NotifySettingsStore.Read().Url);              // nothing set up yet
            NotifySettingsStore.Write(new NotifySettings { Url = "https://n8n.example.com/webhook/class-notifications", Pc = "THE-PC" });

            var read = NotifySettingsStore.Read();
            Assert.Equal("https://n8n.example.com/webhook/class-notifications", read.Url);
            Assert.True(read.Enabled);
            Assert.Equal("THE-PC", read.Pc);
        }
        finally
        {
            NotifySettingsStore.Path = was;
            try { File.Delete(path); } catch { }
        }
    }
}
