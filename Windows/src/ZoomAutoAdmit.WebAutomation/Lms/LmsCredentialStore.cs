namespace ZoomAutoAdmit.WebAutomation.Lms;

// Never a record: a generated ToString must not be able to print the password.
public sealed class LmsAccount(string email, string password)
{
    public string Email { get; } = email;
    public string Password { get; } = password;
    public override string ToString() => $"LMS account {Email} (password redacted)";
}

public interface ILmsCredentialStore
{
    LmsAccount? Read();
    void Save(LmsAccount account);
    void Delete();

    /// <summary>
    /// The browser profile this account signs in with. Two accounts must never share one: an LMS
    /// session belongs to whoever signed in last, so a shared profile signs one coordinator out
    /// every time the other runs. It is on the interface because it belongs to the account, not to
    /// where the password happens to be kept.
    /// </summary>
    string Profile { get; }
}

/// <summary>
/// One account's LMS sign-in, and the browser profile it signs in with.
///
/// This says which account is meant; where its password rests is
/// <see cref="LmsCredentialBackend"/>'s business - Windows Credential Manager on a PC, and the
/// central backend on a server. The password reaches this process only to be typed into the LMS
/// login form, and is never in the app's JSON, in a log line, or in a file anyone can copy.
/// </summary>
public sealed class LmsCredentialStore(string? target = null, string? profile = null) : ILmsCredentialStore
{
    /// <summary>Without a target, the account chosen in the app (<see cref="LmsAccountDirectory"/>).</summary>
    private string Target => target ?? new LmsAccountDirectory().Active().Target;

    /// <summary>
    /// The browser profile this sign-in uses, so two accounts never share one LMS session. A caller
    /// that already knows the account gives it here; otherwise it is looked up by target.
    /// </summary>
    public string Profile => profile ?? (target == null
        ? new LmsAccountDirectory().Active().Profile
        : new LmsAccountDirectory().List().FirstOrDefault(a => a.Target == target)?.Profile ?? LmsAccountDirectory.LegacyProfile);

    public LmsAccount? Read() => LmsCredentialBackend.Current.Read(Target);

    public void Save(LmsAccount account) => LmsCredentialBackend.Current.Save(Target, account);

    public void Delete() => LmsCredentialBackend.Current.Delete(Target);
}
