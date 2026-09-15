using System.Windows.Input;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.WebAutomation.Lms;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

/// <summary>
/// The LMS sign-in and the "Run Session" press on the DEPI dashboard.
///
/// The password is never held here beyond the moment it is saved: it goes straight to Windows
/// Credential Manager and the box is cleared, so it is not left in memory behind an open window.
/// </summary>
public sealed class LmsViewModel : ObservableObject
{
    private readonly ILmsCredentialStore _store;
    private readonly Func<LmsSessionRunner> _runner;
    private readonly LmsFollowUpQueue _followUp;
    private readonly LmsFollowUpProcessor _followUpProcessor;
    private string _email = string.Empty;
    private string _status = string.Empty;
    private bool _isBusy;
    private bool _runOnStart = true;

    public LmsViewModel(ILmsCredentialStore? store = null, Func<LmsSessionRunner>? runner = null,
        LmsFollowUpQueue? followUp = null, LmsFollowUpProcessor? followUpProcessor = null)
    {
        _store = store ?? new LmsCredentialStore();
        _runner = runner ?? (() => new LmsSessionRunner(_store));
        _followUp = followUp ?? new LmsFollowUpQueue();
        _followUpProcessor = followUpProcessor ?? new LmsFollowUpProcessor(_followUp, runner: _runner);
        SaveLoginCommand = new RelayCommand(parameter => SaveLogin(parameter as string));
        ForgetLoginCommand = new RelayCommand(_ => ForgetLogin());
        Reload();
    }

    public string Email { get => _email; set => SetProperty(ref _email, value); }
    /// <summary>
    /// Press Run Session on the dashboard whenever a meeting is started from this app. The group
    /// is the meeting's own account, so there is nothing to type and nothing to keep in step.
    /// </summary>
    public bool RunOnMeetingStart
    {
        get => _runOnStart;
        set { if (SetProperty(ref _runOnStart, value)) ZoomAutoAdmit.Inspector.Runtime.LmsMeetingBridge.RunOnMeetingStart = value; }
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    /// <summary>True once a sign-in is stored; the password itself is never read back into the app.</summary>
    public bool HasSavedLogin { get; private set; }

    /// <summary>The one processor: the Sessions page's buttons go through it, so they never run beside the queue.</summary>
    public LmsFollowUpProcessor FollowUpProcessor => _followUpProcessor;

    public ICommand SaveLoginCommand { get; }
    public ICommand ForgetLoginCommand { get; }

    public void Reload()
    {
        try
        {
            var saved = _store.Read();
            HasSavedLogin = saved != null;
            if (saved != null && Email.Length == 0) Email = saved.Email;
            Status = HasSavedLogin
                ? $"Signed in as {Email}. The password is kept by Windows, not by this app."
                : "Enter the dashboard email and password once; Windows keeps them for later runs.";
        }
        catch (Exception ex) { Status = ex.Message; }
        OnPropertyChanged(nameof(HasSavedLogin));
    }

    /// <summary>The password arrives from the PasswordBox and is not stored on this object.</summary>
    public void SaveLogin(string? password)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Email)) { Status = "An email is required."; return; }
            if (string.IsNullOrWhiteSpace(password)) { Status = "A password is required."; return; }
            _store.Save(new LmsAccount(Email.Trim(), password));
            Reload();
            Status = $"Saved. {Email.Trim()} will be used to start sessions on the dashboard.";
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    public void ForgetLogin()
    {
        try
        {
            _store.Delete();
            Reload();
            Status = "The saved dashboard sign-in was removed from Windows.";
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    /// <summary>
    /// Writes down the attendance steps this class still owes: the upload an hour in,
    /// the late-joiner pass at three hours. They are due whenever the app is next running.
    /// </summary>
    public async Task ScheduleFollowUpAsync(string group, DateOnly date, TimeOnly start)
    {
        if (string.IsNullOrWhiteSpace(group)) return;
        var written = await _followUp.ScheduleAsync(group.Trim(), date, start);
        foreach (var item in written.Where(item =>
                     item.Group.Equals(group.Trim(), StringComparison.OrdinalIgnoreCase) && item.SessionDate == date))
            ConsoleLogger.Info($"[LMS] Due {item.DueAt.LocalDateTime:g}: {item.Describe}");
    }

    /// <summary>Everything owed and not yet done, for the window to show.</summary>
    public Task<IReadOnlyList<LmsFollowUp>> ReadFollowUpAsync() => _followUp.ReadAsync();

    /// <summary>Runs any durable attendance/completion work that has become due.</summary>
    public async Task ProcessDueFollowUpAsync(bool dryRun = false, CancellationToken token = default)
    {
        if (!HasSavedLogin) return;
        var messages = await _followUpProcessor.ProcessDueAsync(dryRun: dryRun, token: token);
        foreach (var message in messages) ConsoleLogger.Info($"[LMS] {message}");
        if (messages.Count > 0) Status = messages[^1];
    }

    /// <summary>
    /// Presses Run Session for one group, picked out of today's list by the time the class starts.
    /// </summary>
    public async Task RunSessionAsync(string group, TimeOnly? startTime = null, bool headed = true)
    {
        if (string.IsNullOrWhiteSpace(group)) { Status = "No group was given, so no session was started."; return; }
        if (!HasSavedLogin) { Status = "Save the dashboard sign-in first."; return; }
        IsBusy = true;
        Status = $"Opening the dashboard for {group.Trim()}...";
        try
        {
            var result = await Task.Run(() => _runner().RunAsync(
                group.Trim(),
                startTime ?? TimeOnly.FromDateTime(DateTime.Now),
                day: null,
                headed: headed,
                dryRun: false,
                // A browser that was opened to be watched stays up until it is closed by hand.
                keepBrowserOpen: headed));
            Status = result.Message;
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }
}
