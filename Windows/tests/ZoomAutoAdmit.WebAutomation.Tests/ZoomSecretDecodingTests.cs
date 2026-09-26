using System.Text;
using ZoomAutoAdmit.WebAutomation;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

/// <summary>
/// A Zoom password as Credential Manager keeps it: UTF-8 when the app saved it, UTF-16 when it was
/// typed into Windows' own Credential Manager window. Both must come back as the same password.
/// </summary>
public sealed class ZoomSecretDecodingTests
{
    [Theory]
    [InlineData("Dep!86N$kr")]
    [InlineData("a")]
    [InlineData("كلمة-سر-9")]
    public void TheAppsOwnUtf8PasswordReadsBackAsItIs(string password) =>
        Assert.Equal(password, ZoomSignInCredential.DecodeSecret(Encoding.UTF8.GetBytes(password)));

    [Theory]
    [InlineData("Dep!86N$kr")]
    [InlineData("ab")]
    public void APasswordTypedIntoWindowsCredentialManagerHasNoNulBetweenItsLetters(string password)
    {
        string read = ZoomSignInCredential.DecodeSecret(Encoding.Unicode.GetBytes(password));
        Assert.Equal(password, read);
        Assert.DoesNotContain('\0', read);
    }
}
