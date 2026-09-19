using System.Text.Json;
using Xunit;
using ZoomAutoAdmit.CloudWorker.Stages;

namespace ZoomAutoAdmit.CloudWorker.Tests;

public sealed class ClassStageTests
{
    private const string Plan = "11111111-2222-3333-4444-555555555555";
    private const string Coordinator = "66666666-7777-8888-9999-000000000000";

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement Complete(string extra = "") => Payload($$"""
        {
          "classPlanId": "{{Plan}}",
          "group": "CAI5_AIS4_S7",
          "date": "2026-09-20",
          "startTime": "19:00",
          "coordinatorId": "{{Coordinator}}"
          {{extra}}
        }
        """);

    [Fact]
    public void A_whole_stage_is_read_back_as_it_was_sent()
    {
        Assert.True(ClassStage.TryParse(
            Complete(""", "meetingUrl": "https://zoom.us/j/123", "dryRun": true """), out var stage, out _));

        Assert.Equal(Guid.Parse(Plan), stage!.ClassPlanId);
        Assert.Equal("CAI5_AIS4_S7", stage.Group);
        Assert.Equal(new DateOnly(2026, 9, 20), stage.Date);
        Assert.Equal(new TimeOnly(19, 0), stage.StartTime);
        Assert.Equal(Guid.Parse(Coordinator), stage.CoordinatorId);
        Assert.Equal("https://zoom.us/j/123", stage.MeetingUrl!.ToString());
        Assert.True(stage.DryRun);
    }

    [Fact]
    public void A_class_with_no_time_of_its_own_is_still_a_class()
    {
        string json = $$"""
            {"classPlanId":"{{Plan}}","group":"G","date":"2026-09-20","coordinatorId":"{{Coordinator}}"}
            """;
        Assert.True(ClassStage.TryParse(Payload(json), out var stage, out _));
        Assert.Null(stage!.StartTime);
        Assert.Null(stage.MeetingUrl);
        Assert.False(stage.DryRun);
    }

    [Theory]
    [InlineData("""{"group":"G","date":"2026-09-20","coordinatorId":"66666666-7777-8888-9999-000000000000"}""", "classPlanId")]
    [InlineData("""{"classPlanId":"11111111-2222-3333-4444-555555555555","group":"G","date":"2026-09-20"}""", "coordinatorId")]
    [InlineData("""{"classPlanId":"nope","group":"G","date":"2026-09-20","coordinatorId":"66666666-7777-8888-9999-000000000000"}""", "UUID")]
    [InlineData("""{"classPlanId":"11111111-2222-3333-4444-555555555555","date":"2026-09-20","coordinatorId":"66666666-7777-8888-9999-000000000000"}""", "group")]
    [InlineData("""{"classPlanId":"11111111-2222-3333-4444-555555555555","group":"G","date":"20-09-2026","coordinatorId":"66666666-7777-8888-9999-000000000000"}""", "yyyy-MM-dd")]
    public void A_payload_that_cannot_be_acted_on_is_named_not_guessed_at(string json, string fragment)
    {
        Assert.False(ClassStage.TryParse(Payload(json), out _, out string error));
        Assert.Contains(fragment, error);
    }

    [Fact]
    public void A_meeting_link_that_is_not_https_is_refused()
    {
        Assert.False(ClassStage.TryParse(
            Complete(""", "meetingUrl": "http://zoom.us/j/123" """), out _, out string error));
        Assert.Contains("https", error);
    }

    [Fact]
    public void What_a_class_is_called_in_a_log_line_carries_no_account()
    {
        ClassStage.TryParse(Complete(""", "lmsAccountId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" """),
                            out var stage, out _);
        string described = stage!.Describe();
        Assert.Equal("CAI5_AIS4_S7 on 2026-09-20 at 19:00", described);
        Assert.DoesNotContain("aaaaaaaa", described);
    }
}
