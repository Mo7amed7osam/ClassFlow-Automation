using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ZoomAutoAdmit.AttendanceMatching.Tests;

public sealed class OpenAiNameMatcherTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private const string Valid = """
        {"match":true,"confidence":96,"studentId":"S1","reason":"First and family names agree","needsReview":false}
        """;
    private static string Envelope(string answer, string status = "completed", string type = "output_text") => JsonSerializer.Serialize(new
    {
        status, output = new[] { new { type = "message", content = new[] { new { type, text = answer } } } }
    });
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task SendsOnlyPairWithStrictSchemaAndParsesResult()
    {
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var body = json.RootElement;
            Assert.False(body.GetProperty("store").GetBoolean());
            var format = body.GetProperty("text").GetProperty("format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            Assert.True(format.GetProperty("strict").GetBoolean());
            Assert.False(format.GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
            var input = body.GetProperty("input");
            Assert.Equal(2, input.GetArrayLength());
            using var pair = JsonDocument.Parse(input[1].GetProperty("content").GetString()!);
            Assert.Equal("Mo7ab Mohamed", pair.RootElement.GetProperty("observed").GetString());
            Assert.Equal("S1", pair.RootElement.GetProperty("student").GetProperty("studentId").GetString());
            Assert.DoesNotContain("email", pair.RootElement.ToString());
            return Response(Envelope(Valid));
        }));
        var result = await new OpenAiNameMatcher(client, "configured-model", () => "test-key")
            .MatchAsync(MatchingTests.Student(), "Mo7ab Mohamed");
        Assert.True(result.Match);
        Assert.Equal(96, result.Confidence);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("```json\n{}\n```")]
    [InlineData("{\"match\":true,\"confidence\":101,\"studentId\":\"S1\",\"reason\":\"x\",\"needsReview\":false}")]
    [InlineData("{\"match\":true,\"confidence\":96,\"studentId\":\"OTHER\",\"reason\":\"x\",\"needsReview\":false}")]
    [InlineData("{\"match\":true,\"confidence\":96,\"studentId\":\"S1\",\"reason\":\"\",\"needsReview\":false}")]
    [InlineData("{\"match\":true,\"confidence\":96,\"studentId\":\"S1\",\"reason\":\"x\",\"reason\":\"y\",\"needsReview\":false}")]
    public void InvalidSchemaIsRejected(string json) => Assert.ThrowsAny<Exception>(() => OpenAiNameMatcher.ParseResult(json, "S1"));

    [Theory]
    [InlineData("incomplete", "output_text")]
    [InlineData("completed", "refusal")]
    public async Task IncompleteOrRefusalIsNotAMatch(string status, string type)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(Envelope(Valid, status, type)))));
        var matcher = new OpenAiNameMatcher(client, "configured-model", () => "test-key");
        await Assert.ThrowsAsync<InvalidDataException>(() => matcher.MatchAsync(MatchingTests.Student(), "Mo7ab Mohamed"));
    }

    [Fact]
    public async Task MissingKeyDoesNotSendNetworkRequest()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Must not send")));
        var matcher = new OpenAiNameMatcher(client, "configured-model", () => null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => matcher.MatchAsync(MatchingTests.Student(), "Mo7ab Mohamed"));
        Assert.Contains("not configured", ex.Message);
    }

    [Fact]
    public async Task HttpErrorIsSanitizedAndConvertedToReviewByEngine()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            { Content = new StringContent("private response data") })));
        var matcher = new OpenAiNameMatcher(client, "configured-model", () => "test-key");
        var result = await new AttendanceMatchingEngine(new MatchingTests.Memory(), matcher)
            .MatchAsync(MatchingTests.Group(MatchingTests.Student()), ["Mo7ab Mohamed"]);
        Assert.Single(result.ReviewQueue);
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("private response data"));
    }

    [Fact]
    public async Task TimeoutGoesToReviewWhileCallerCancellationPropagates()
    {
        using var client = new HttpClient(new Handler(async (_, token) =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Response(Envelope(Valid)); }));
        var ai = new OpenAiNameMatcher(client, "configured-model", () => "test-key");
        var engine = new AttendanceMatchingEngine(new MatchingTests.Memory(), ai, new() { AiTimeout = TimeSpan.FromMilliseconds(50) });
        var result = await engine.MatchAsync(MatchingTests.Group(MatchingTests.Student()), ["Mo7ab Mohamed"]);
        Assert.Single(result.ReviewQueue);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        var cancelEngine = new AttendanceMatchingEngine(new MatchingTests.Memory(), ai);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelEngine.MatchAsync(
            MatchingTests.Group(MatchingTests.Student()), ["Mo7ab Mohamed"], cancellation.Token));
    }
}
