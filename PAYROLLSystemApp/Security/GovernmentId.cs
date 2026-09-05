namespace PAYROLLSystemApp.Security;

/// <summary>The four government identifiers an employee record carries (FR-014).</summary>
public enum GovernmentIdKind
{
    Sss,
    PhilHealth,
    PagIbig,
    Tin
}

/// <summary>Outcome of validating one identifier, phrased for inline display (NFR-023).</summary>
public sealed record GovernmentIdResult(bool IsValid, string Normalised, string Message)
{
    public static GovernmentIdResult Ok(string normalised) => new(true, normalised, string.Empty);

    public static GovernmentIdResult Fail(string message) => new(false, string.Empty, message);
}

/// <summary>
/// Validation, normalisation, formatting and masking for Philippine government
/// identifiers (FR-014, NFR-020).
///
/// <para><b>Identifiers are stored digits only.</b> <c>123-456-789</c> and
/// <c>123456789</c> are the same TIN, and storing them as typed would let the
/// same person be entered twice past the uniqueness check. Separators are a
/// presentation concern, applied by <see cref="Format"/> on the way out.</para>
///
/// <para>Every identifier is optional on the record — an employee may genuinely
/// not have one yet — so an empty value is accepted. A value that is present
/// must be well formed, because a malformed number is rejected by the agency at
/// the counter rather than by us, months after the remittance was filed.</para>
/// </summary>
public static class GovernmentId
{
    /// <summary>Digit counts each agency issues. TIN is 9, or 12 with a branch code.</summary>
    private static readonly IReadOnlyDictionary<GovernmentIdKind, int[]> Lengths =
        new Dictionary<GovernmentIdKind, int[]>
        {
            [GovernmentIdKind.Sss] = [10],
            [GovernmentIdKind.PhilHealth] = [12],
            [GovernmentIdKind.PagIbig] = [12],
            [GovernmentIdKind.Tin] = [9, 12]
        };

    public static string DisplayName(GovernmentIdKind kind) => kind switch
    {
        GovernmentIdKind.Sss => "SSS number",
        GovernmentIdKind.PhilHealth => "PhilHealth number",
        GovernmentIdKind.PagIbig => "Pag-IBIG MID number",
        GovernmentIdKind.Tin => "TIN",
        _ => kind.ToString()
    };

    /// <summary>The shape shown as a field placeholder, so the user is not guessing.</summary>
    public static string Mask(GovernmentIdKind kind) => kind switch
    {
        GovernmentIdKind.Sss => "00-0000000-0",
        GovernmentIdKind.PhilHealth => "00-000000000-0",
        GovernmentIdKind.PagIbig => "0000-0000-0000",
        GovernmentIdKind.Tin => "000-000-000 or 000-000-000-000",
        _ => string.Empty
    };

    /// <summary>Strips every separator, leaving only the digits that identify the member.</summary>
    public static string Digits(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : new string(value.Where(char.IsDigit).ToArray());

    /// <summary>
    /// Validates a typed value and returns the digits-only form to store.
    /// A blank value is valid and normalises to empty.
    /// </summary>
    public static GovernmentIdResult Validate(GovernmentIdKind kind, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return GovernmentIdResult.Ok(string.Empty);

        // Anything that is neither a digit nor a recognised separator is a typo
        // rather than a formatting choice, and is worth naming as one.
        if (value.Any(c => !char.IsDigit(c) && c is not ('-' or ' ' or '.')))
            return GovernmentIdResult.Fail($"{DisplayName(kind)} may contain digits and dashes only.");

        var digits = Digits(value);
        var accepted = Lengths[kind];

        if (!accepted.Contains(digits.Length))
        {
            var expected = accepted.Length == 1
                ? $"{accepted[0]} digits"
                : $"{string.Join(" or ", accepted)} digits";

            return GovernmentIdResult.Fail(
                $"{DisplayName(kind)} must be {expected} — you entered {digits.Length}.");
        }

        // An all-zero identifier passes a length check and is never a real
        // member number; it is what gets typed to get past a required field.
        if (digits.All(c => c == '0'))
            return GovernmentIdResult.Fail($"{DisplayName(kind)} cannot be all zeroes.");

        return GovernmentIdResult.Ok(digits);
    }

    /// <summary>Re-applies the agency's separators to a stored digits-only value.</summary>
    public static string Format(GovernmentIdKind kind, string? stored)
    {
        var digits = Digits(stored);
        if (digits.Length == 0)
            return string.Empty;

        return kind switch
        {
            GovernmentIdKind.Sss when digits.Length == 10 =>
                $"{digits[..2]}-{digits[2..9]}-{digits[9..]}",

            GovernmentIdKind.PhilHealth when digits.Length == 12 =>
                $"{digits[..2]}-{digits[2..11]}-{digits[11..]}",

            GovernmentIdKind.PagIbig when digits.Length == 12 =>
                $"{digits[..4]}-{digits[4..8]}-{digits[8..]}",

            GovernmentIdKind.Tin when digits.Length is 9 or 12 =>
                string.Join('-', Chunk(digits, 3)),

            // A stored value of an unexpected length is shown as it stands
            // rather than chopped into a shape it does not have.
            _ => digits
        };
    }

    /// <summary>
    /// NFR-020: the masked form for lists and reports — the last four digits
    /// only, which is enough to tell two employees apart without putting a
    /// full member number on a screen that does not need it.
    /// </summary>
    public static string Masked(string? stored)
    {
        var digits = Digits(stored);

        return digits.Length switch
        {
            0 => "—",
            <= 4 => new string('•', digits.Length),
            _ => new string('•', digits.Length - 4) + digits[^4..]
        };
    }

    private static IEnumerable<string> Chunk(string value, int size)
    {
        for (var i = 0; i < value.Length; i += size)
            yield return value.Substring(i, Math.Min(size, value.Length - i));
    }
}
