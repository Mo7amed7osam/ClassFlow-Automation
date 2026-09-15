using ZoomAutoAdmit.WindowsUI.Services;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

public sealed class AiProxyTests
{
    private sealed class NoKey : IAiCredentialStore
    {
        public AiConnectionSettings? Read() => null;
        public void Save(AiConnectionSettings settings) => throw new NotSupportedException();
        public void Delete() { }
    }

    [Fact]
    public async Task WithoutTheAppsKeyThePageIsToldWhereToSaveItAndNothingIsSent()
    {
        var (status, body) = await AiProxy.ChatAsync("{\"model\":\"openai/gpt-4o-mini\",\"messages\":[]}", new NoKey());
        Assert.Equal(401, status);
        Assert.Contains("AI Engine page", body);
    }
}
