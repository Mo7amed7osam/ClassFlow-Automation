using System.Globalization;
using System.Text;

namespace ZoomAutoAdmit.AttendanceMatching;

public static class NameNormalizer
{
    // Intentionally small equivalence vocabulary, not a general transliteration oracle.
    private static readonly Dictionary<string, string> Variants = new(StringComparer.Ordinal)
    {
        ["mohammed"] = "mohamed", ["mohammad"] = "mohamed", ["muhammad"] = "mohamed",
        ["muhammed"] = "mohamed", ["محمد"] = "mohamed", ["ahmad"] = "ahmed", ["احمد"] = "ahmed",
        ["مهاب"] = "mohab", ["اسامة"] = "osama", ["اسامه"] = "osama", ["usama"] = "osama",
        ["سيد"] = "sayed", ["سعيد"] = "saeed", ["علي"] = "ali", ["aly"] = "ali",
        ["حسن"] = "hassan", ["hasan"] = "hassan", ["عمر"] = "omar", ["umar"] = "omar",
        ["يوسف"] = "youssef", ["yousef"] = "youssef", ["yusuf"] = "youssef",
        ["خالد"] = "khaled", ["khalid"] = "khaled", ["ابراهيم"] = "ibrahim"
    };

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (value.Length > 500) throw new ArgumentException("Participant names must be at most 500 characters.");
        var builder = new StringBuilder();
        foreach (char c in value.Normalize(NormalizationForm.FormKD).ToLowerInvariant())
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format || c == 'ـ') continue;
            char normalized = c switch { 'أ' or 'إ' or 'آ' or 'ٱ' => 'ا', 'ى' => 'ي', _ => c };
            builder.Append(char.IsLetterOrDigit(normalized) ? normalized : ' ');
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(CanonicalToken));
    }

    private static string CanonicalToken(string token)
    {
        // Digits inside Latin name tokens only. Never turn meeting IDs into names.
        if (token.Any(c => c is >= 'a' and <= 'z'))
        {
            token = token.Replace("3", "a").Replace("7", "h").Replace("5", "kh")
                .Replace("6", "t").Replace("8", "gh").Replace("9", "s").Replace("2", "a");
        }
        return Variants.GetValueOrDefault(token, token);
    }

    public static string[] Tokens(string value) => Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);
    internal static bool HasMultipleNames(string value)
    {
        var words = Tokens(value);
        return words.Distinct(StringComparer.Ordinal).Count() >= 2 && words.All(w => w.Length >= 2 && w.All(char.IsLetter));
    }
}
