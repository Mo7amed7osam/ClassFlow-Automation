using System.Net.Mail;

namespace ZoomAutoAdmit.Roster;

public sealed record Student(string StudentId, string FullName, IReadOnlyList<string> Aliases, string? Email = null);

public interface IStudentRosterService
{
    Task<IReadOnlyList<Student>> ListAsync(CancellationToken token = default);
    Task AddAsync(Student student, CancellationToken token = default);
    Task UpdateAsync(Student student, Student expected, CancellationToken token = default);
    Task DeleteAsync(Student expected, CancellationToken token = default);
    Task<int> ImportFileAsync(string path, CancellationToken token = default);
}

public static class StudentValidation
{
    public static Student Normalize(Student student)
    {
        ArgumentNullException.ThrowIfNull(student);
        static string Required(string? value, string field, int max)
        {
            var text = value?.Trim() ?? string.Empty;
            if (text.Length == 0 || text.Length > max || text.Any(char.IsControl))
                throw new ArgumentException($"{field} is required, must be at most {max} characters and cannot contain control characters.");
            return text;
        }
        var id = Required(student.StudentId, "Student ID", 128);
        var name = Required(student.FullName, "Full name", 500);
        if (student.Aliases == null || student.Aliases.Count > 50)
            throw new ArgumentException("Aliases must be a list with no more than 50 entries.");
        var aliases = student.Aliases.Select(alias => Required(alias, "Alias", 500)).ToArray();
        string? email = string.IsNullOrWhiteSpace(student.Email) ? null : student.Email.Trim();
        if (email != null && (email.Length > 254 || email.Any(char.IsControl) ||
            !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email))
            throw new ArgumentException("Email must be a valid email address, or left blank.");
        return new(id, name, Array.AsReadOnly(aliases), email);
    }

    internal static bool Same(Student left, Student right) =>
        left.StudentId == right.StudentId && left.FullName == right.FullName &&
        left.Email == right.Email && left.Aliases.SequenceEqual(right.Aliases);
}
