using System.Security.Cryptography;

namespace PAYROLLSystemApp.Security;

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string storedHash);

    /// <summary>True when the stored hash uses weaker parameters than the current policy.</summary>
    bool NeedsRehash(string storedHash);
}

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing (NFR-014).
///
/// Stored format: <c>PBKDF2$SHA256$iterations$base64salt$base64hash</c>
/// The salt is per-password, so two users with the same password produce
/// different hashes, and the iteration count travels with the hash so it can be
/// raised later without invalidating existing credentials.
/// </summary>
public sealed class PasswordHasher : IPasswordHasher
{
    private const string Prefix = "PBKDF2";
    private const string Algorithm = "SHA256";
    private const int SaltBytes = 16;   // 128-bit
    private const int HashBytes = 32;   // 256-bit
    private const int CurrentIterations = 210_000; // OWASP guidance for PBKDF2-SHA256

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, CurrentIterations);

        return string.Join('$',
            Prefix,
            Algorithm,
            CurrentIterations.ToString(),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash))
            return false;

        if (!TryParse(storedHash, out var iterations, out var salt, out var expected))
            return false;

        var actual = Derive(password, salt, iterations);

        // Fixed-time comparison: never leak how much of the hash matched.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public bool NeedsRehash(string storedHash) =>
        !TryParse(storedHash, out var iterations, out _, out _) || iterations < CurrentIterations;

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);

    private static bool TryParse(string storedHash, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];

        var parts = storedHash.Split('$');
        if (parts.Length != 5 || parts[0] != Prefix || parts[1] != Algorithm)
            return false;

        if (!int.TryParse(parts[2], out iterations) || iterations <= 0)
            return false;

        try
        {
            salt = Convert.FromBase64String(parts[3]);
            hash = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length == SaltBytes && hash.Length == HashBytes;
    }
}
