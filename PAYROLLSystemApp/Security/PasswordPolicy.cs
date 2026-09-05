using System.Security.Cryptography;
using System.Text;

namespace PAYROLLSystemApp.Security;

/// <summary>One rule from the password policy, and whether a candidate satisfies it.</summary>
public sealed record PasswordRule(string Description, bool Satisfied);

public sealed record PasswordPolicyResult(IReadOnlyList<PasswordRule> Rules)
{
    public bool IsValid => Rules.All(r => r.Satisfied);

    /// <summary>Unmet rules, phrased for inline display next to the field (NFR-023).</summary>
    public IReadOnlyList<string> Failures =>
        Rules.Where(r => !r.Satisfied).Select(r => r.Description).ToList();

    public string FailureSummary => string.Join("\n", Failures.Select(f => "• " + f));
}

/// <summary>
/// NFR-013: at least 8 characters with an upper case letter, a lower case
/// letter, a digit, and a special character. Evaluated as a list rather than a
/// bool so the UI can show a live checklist.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 8;

    private const string SpecialCharacters = "!@#$%^&*()-_=+[]{};:'\",.<>/?\\|`~";

    public static PasswordPolicyResult Evaluate(string? password)
    {
        var value = password ?? string.Empty;

        var rules = new List<PasswordRule>
        {
            new($"At least {MinimumLength} characters", value.Length >= MinimumLength),
            new("One upper case letter (A-Z)", value.Any(char.IsUpper)),
            new("One lower case letter (a-z)", value.Any(char.IsLower)),
            new("One digit (0-9)", value.Any(char.IsDigit)),
            new("One special character (! @ # $ …)", value.Any(SpecialCharacters.Contains))
        };

        return new PasswordPolicyResult(rules);
    }

    public static bool IsValid(string? password) => Evaluate(password).IsValid;

    /// <summary>
    /// Generates a temporary password that satisfies the policy, for an
    /// administrator-issued reset (FR-005). Uses a cryptographic RNG.
    /// </summary>
    public static string GenerateTemporaryPassword(int length = 12)
    {
        if (length < MinimumLength)
            length = MinimumLength;

        // Ambiguous glyphs (O/0, l/1/I) are excluded — the value is read off a
        // screen and typed by hand.
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string specials = "!@#$%^&*?-_";
        const string all = upper + lower + digits + specials;

        var chars = new List<char>(length)
        {
            Pick(upper),
            Pick(lower),
            Pick(digits),
            Pick(specials)
        };

        while (chars.Count < length)
            chars.Add(Pick(all));

        // Fisher-Yates with a cryptographic RNG so the guaranteed characters
        // do not always land in the first four positions.
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars.ToArray());

        static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
    }
}
