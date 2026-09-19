using Xunit;

using ZoomAutoAdmit.WebAutomation;
using ZoomAutoAdmit.WebAutomation.Lms;

namespace ZoomAutoAdmit.WebAutomation.Tests;

/// <summary>
/// Where an LMS password rests is the one thing the automation could not do off Windows: everything
/// else in this assembly is Playwright and plain .NET, but the store called advapi32 directly.
///
/// These run on both targets. On net8.0 - the build the cloud worker uses - they are the proof that
/// the automation can be driven by a password that never came from Credential Manager.
/// </summary>
public sealed class LmsCredentialBackendTests : IDisposable
{
    public void Dispose() => LmsCredentialBackend.Reset();

    [Fact]
    public void A_password_given_to_the_process_is_read_back_by_target()
    {
        var backend = new InMemoryLmsCredentialBackend();
        backend.Save("ZoomAutoAdmit/LMS/omar", new LmsAccount("omar@lms.example.com", "her password"));
        LmsCredentialBackend.Current = backend;

        var read = new LmsCredentialStore("ZoomAutoAdmit/LMS/omar").Read();

        Assert.NotNull(read);
        Assert.Equal("omar@lms.example.com", read!.Email);
        Assert.Equal("her password", read.Password);
    }

    [Fact]
    public void Each_account_keeps_its_own_password_and_an_unknown_target_has_none()
    {
        var backend = new InMemoryLmsCredentialBackend();
        backend.Save("target/omar", new LmsAccount("omar@lms.example.com", "one"));
        backend.Save("target/sara", new LmsAccount("sara@lms.example.com", "two"));
        LmsCredentialBackend.Current = backend;

        Assert.Equal("one", new LmsCredentialStore("target/omar").Read()!.Password);
        Assert.Equal("two", new LmsCredentialStore("target/sara").Read()!.Password);
        Assert.Null(new LmsCredentialStore("target/nobody").Read());
    }

    [Fact]
    public void Saving_and_deleting_go_to_the_backend_in_use()
    {
        var backend = new InMemoryLmsCredentialBackend();
        LmsCredentialBackend.Current = backend;
        var store = new LmsCredentialStore("target/omar");

        store.Save(new LmsAccount("omar@lms.example.com", "her password"));
        Assert.NotNull(backend.Read("target/omar"));

        store.Delete();
        Assert.Null(backend.Read("target/omar"));
        store.Delete();      // deleting what is not there is not an error, as on Windows
    }

    [Fact]
    public void Draining_forgets_every_password_held()
    {
        var backend = new InMemoryLmsCredentialBackend();
        backend.Save("target/omar", new LmsAccount("omar@lms.example.com", "one"));
        backend.Save("target/sara", new LmsAccount("sara@lms.example.com", "two"));

        backend.Clear();

        Assert.Null(backend.Read("target/omar"));
        Assert.Null(backend.Read("target/sara"));
    }

    [Fact]
    public void A_password_is_never_in_what_an_account_prints()
    {
        var account = new LmsAccount("omar@lms.example.com", "her password");
        Assert.DoesNotContain("her password", account.ToString());
        Assert.Contains("omar@lms.example.com", account.ToString());
    }

    [Fact]
    public void An_empty_sign_in_is_refused_rather_than_stored()
    {
        var backend = new InMemoryLmsCredentialBackend();
        Assert.ThrowsAny<ArgumentException>(() => backend.Save("t", new LmsAccount("", "password")));
        Assert.ThrowsAny<ArgumentException>(() => backend.Save("t", new LmsAccount("omar@lms.example.com", "")));
    }

    [Fact]
    public void Windows_reaches_its_credential_manager_without_being_told_to()
    {
        // Nothing was set, so the platform decides. This is what every existing PC relies on.
        Assert.Equal(OperatingSystem.IsWindows(), LmsCredentialBackend.IsConfigured);
        if (OperatingSystem.IsWindows())
            Assert.IsType<WindowsCredentialManagerBackend>(LmsCredentialBackend.Current);
        else
            Assert.Throws<PlatformNotSupportedException>(() => LmsCredentialBackend.Current);
    }
}

public sealed class ZoomSignInReferenceTests : IDisposable
{
    public void Dispose() => ZoomSignInCredential.Resolver = null;

    [Fact]
    public void A_reference_the_server_owns_is_answered_by_the_resolver()
    {
        ZoomSignInCredential.Resolver = reference => reference == "server:zoom/7f3a"
            ? new ZoomSignInCredential("mona@zoom.example.com", "her password")
            : null;

        var read = ZoomSignInCredential.Read("server:zoom/7f3a");

        Assert.NotNull(read);
        Assert.Equal("mona@zoom.example.com", read!.Email);
        Assert.Null(ZoomSignInCredential.Read("server:zoom/unknown"));
    }

    [Fact]
    public void Nothing_at_all_stays_nothing()
    {
        ZoomSignInCredential.Resolver = _ => new ZoomSignInCredential("a@b.c", "p");
        Assert.Null(ZoomSignInCredential.Read(null));
        Assert.Null(ZoomSignInCredential.Read("   "));
    }

    [Fact]
    public void A_PCs_reference_reaching_a_server_is_offered_to_the_resolver_rather_than_failing()
    {
        // A coordinator's PC saved "wincred:...". That reference travels with the account, and on a
        // server there is no Credential Manager to read it from - but the server may still know the
        // password by another route, so it is asked instead of the read failing.
        string? seen = null;
        ZoomSignInCredential.Resolver = reference => { seen = reference; return null; };

        ZoomSignInCredential.Read("wincred:ZoomAutoAdmit/ZoomProfile/CAI5_AIS4_S7");

        if (OperatingSystem.IsWindows())
            Assert.Null(seen);                     // on a PC it is read directly, as it always was
        else
            Assert.Equal("wincred:ZoomAutoAdmit/ZoomProfile/CAI5_AIS4_S7", seen);
    }

    [Fact]
    public void A_password_is_never_in_what_a_zoom_sign_in_prints()
    {
        var credential = new ZoomSignInCredential("mona@zoom.example.com", "her password");
        Assert.DoesNotContain("her password", credential.ToString());
    }
}
