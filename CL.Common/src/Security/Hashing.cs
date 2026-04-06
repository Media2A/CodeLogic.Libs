using System.Security.Cryptography;
using System.Text;

namespace CL.Common.Security;

/// <summary>
/// Provides hashing utilities including SHA-256, SHA-512, MD5 (obsolete),
/// PBKDF2 password hashing, salt generation, and HMAC variants.
/// All methods are thread-safe and stateless.
/// </summary>
public static class Hashing
{
    /// <summary>
    /// Computes the SHA-256 hash of the given UTF-8 string.
    /// </summary>
    /// <param name="input">The string to hash. Must not be null.</param>
    /// <returns>A lowercase hexadecimal string representing the SHA-256 hash.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="input"/> is null.</exception>
    public static string Sha256(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Computes the SHA-512 hash of the given UTF-8 string.
    /// </summary>
    /// <param name="input">The string to hash. Must not be null.</param>
    /// <returns>A lowercase hexadecimal string representing the SHA-512 hash.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="input"/> is null.</exception>
    public static string Sha512(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var bytes = System.Security.Cryptography.SHA512.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Computes the MD5 hash of the given UTF-8 string.
    /// </summary>
    /// <param name="input">The string to hash. Must not be null.</param>
    /// <returns>A lowercase hexadecimal string representing the MD5 hash.</returns>
    /// <remarks>
    /// MD5 is cryptographically broken and should not be used for security-sensitive purposes.
    /// Use <see cref="Sha256"/> or <see cref="Sha512"/> instead.
    /// </remarks>
    [Obsolete("MD5 is cryptographically broken. Use Sha256 or Sha512 instead.")]
    public static string Md5(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var bytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Hashes a password using PBKDF2-SHA256 with a random salt.
    /// The returned string contains both the salt and hash encoded as Base64,
    /// suitable for storage in a database.
    /// </summary>
    /// <param name="password">The plaintext password to hash. Must not be null.</param>
    /// <param name="iterations">The number of PBKDF2 iterations. Higher values are more secure but slower. Default: 100000.</param>
    /// <returns>A Base64-encoded string containing the 16-byte salt followed by the 32-byte hash.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="password"/> is null.</exception>
    public static string HashPassword(string password, int iterations = 100000)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            32);

        var result = new byte[salt.Length + hash.Length];
        Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
        Buffer.BlockCopy(hash, 0, result, salt.Length, hash.Length);
        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// Verifies a plaintext password against a hashed password produced by <see cref="HashPassword"/>.
    /// Uses constant-time comparison to prevent timing attacks.
    /// </summary>
    /// <param name="password">The plaintext password to verify. Must not be null.</param>
    /// <param name="hashedPassword">The Base64-encoded salt+hash string returned by <see cref="HashPassword"/>. Must not be null.</param>
    /// <param name="iterations">The number of PBKDF2 iterations used when hashing. Must match the value used in <see cref="HashPassword"/>. Default: 100000.</param>
    /// <returns><c>true</c> if the password matches the hash; otherwise <c>false</c>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="password"/> or <paramref name="hashedPassword"/> is null.</exception>
    public static bool VerifyPassword(string password, string hashedPassword, int iterations = 100000)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(hashedPassword);

        try
        {
            var decoded = Convert.FromBase64String(hashedPassword);
            if (decoded.Length < 48) return false;

            var salt = decoded[..16];
            var storedHash = decoded[16..];

            var computedHash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                32);

            return CryptographicOperations.FixedTimeEquals(storedHash, computedHash);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Generates a cryptographically random salt encoded as Base64.
    /// </summary>
    /// <param name="size">The number of random bytes to generate. Default: 16.</param>
    /// <returns>A Base64-encoded string of <paramref name="size"/> random bytes.</returns>
    public static string GenerateSalt(int size = 16)
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(size));
    }

    /// <summary>
    /// Computes an HMAC-SHA256 of the given input using the specified key.
    /// </summary>
    /// <param name="input">The message to authenticate. Must not be null.</param>
    /// <param name="key">The secret key. Must not be null.</param>
    /// <returns>A lowercase hexadecimal string of the HMAC-SHA256 result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="input"/> or <paramref name="key"/> is null.</exception>
    public static string HmacSha256(string input, string key)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(key);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Computes an HMAC-SHA512 of the given input using the specified key.
    /// </summary>
    /// <param name="input">The message to authenticate. Must not be null.</param>
    /// <param name="key">The secret key. Must not be null.</param>
    /// <returns>A lowercase hexadecimal string of the HMAC-SHA512 result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="input"/> or <paramref name="key"/> is null.</exception>
    public static string HmacSha512(string input, string key)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(key);
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(key));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
