using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.WebAutomation.Lms;

namespace ZoomAutoAdmit.Inspector.Commands;

/// <summary>
/// Saves the LMS sign-in, and presses "Run Session" on the dashboard for one group.
///
/// The password is typed here by the person running the command and goes straight into Windows
/// Credential Manager; it is never passed as an argument, where it would end up in the console
/// history, and it is never written to a log.
/// </summary>
public static class LmsSessionCommand
{
    public static async Task<int> ExecuteAsync(CliOptions options, CancellationToken cancellationToken = default)
    {
        var store = new LmsCredentialStore();

        if (options.SaveLmsLogin)
        {
            Console.Write("LMS email: ");
            string email = (Console.ReadLine() ?? string.Empty).Trim();
            if (email.Length == 0) { ConsoleLogger.Error("An email is required."); return 1; }

            Console.Write("LMS password (not shown): ");
            string password = ReadHidden();
            if (password.Length == 0) { ConsoleLogger.Error("A password is required."); return 1; }

            store.Save(new LmsAccount(email, password));
            ConsoleLogger.Success("The LMS sign-in is saved in Windows Credential Manager for this Windows account.");
            if (string.IsNullOrWhiteSpace(options.LmsGroup)) return 0;
        }

        if (string.IsNullOrWhiteSpace(options.LmsGroup))
        {
            ConsoleLogger.Error("lms-run-session needs --group <GROUP>, or --save-login to store the sign-in.");
            return 1;
        }

        if (store.Read() == null)
        {
            ConsoleLogger.Error("No LMS sign-in is saved. Run this command with --save-login first.");
            return 1;
        }

        var result = await new LmsSessionRunner(store).RunAsync(
            options.LmsGroup!,
            // An explicit timetable time identifies the right row when a group has two sessions.
            startTime: options.LmsTime ?? TimeOnly.FromDateTime(DateTime.Now),
            day: options.LmsDay,
            headed: options.WebHeaded,
            dryRun: options.DryRun,
            // Shown to be watched, so it is left up rather than closed the instant it finishes.
            keepBrowserOpen: options.WebHeaded,
            cancellationToken: cancellationToken);

        if (result.IsSuccess) ConsoleLogger.Success($"[LMS] {result.Message}");
        else ConsoleLogger.Error($"[LMS] {result.Message}");
        return result.IsSuccess ? 0 : 1;
    }

    /// <summary>Reads a password without echoing it, and without leaving it on screen.</summary>
    private static string ReadHidden()
    {
        var builder = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0) builder.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) builder.Append(key.KeyChar);
        }
        return builder.ToString();
    }
}
