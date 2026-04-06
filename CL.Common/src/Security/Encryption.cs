using System.Security.Cryptography;
using System.Text;

namespace CL.Common.Security;

/// <summary>
/// Provides AES-256 symmetric encryption and decryption utilities with PBKDF2 key derivation.
/// All methods use AES-CBC with a random IV prepended to the ciphertext for safe transmission.
/// </summary>
public static class Encryption
{
    private const int KeySize = 32;       // AES-256
    private const int IvSize = 16;        // AES block size
    private const int SaltSize = 16;
    private const int Iterations = 100000;

    /// <summary>
    /// Encrypts a plaintext string using AES-256-CBC with PBKDF2-derived key.
    /// The output is a Base64-encoded string containing the salt, IV, and ciphertext.
    /// </summary>
    /// <param name="plainText">The plaintext to encrypt. Must not be null.</param>
    /// <param name="password">The password used to derive the encryption key. Must not be null.</param>
    /// <returns>A Base64-encoded string containing salt (16 bytes) + IV (16 bytes) + ciphertext.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="plainText"/> or <paramref name="password"/> is null.</exception>
    public static string EncryptAes(string plainText, string password)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        ArgumentNullException.ThrowIfNull(password);
        var encrypted = EncryptBytes(Encoding.UTF8.GetBytes(plainText), password);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// Decrypts a Base64-encoded ciphertext string produced by <see cref="EncryptAes"/>.
    /// </summary>
    /// <param name="cipherText">The Base64-encoded ciphertext (salt + IV + ciphertext). Must not be null.</param>
    /// <param name="password">The password used during encryption. Must not be null.</param>
    /// <returns>The original plaintext string.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="cipherText"/> or <paramref name="password"/> is null.</exception>
    /// <exception cref="CryptographicException">Thrown when decryption fails (wrong password or corrupted data).</exception>
    public static string DecryptAes(string cipherText, string password)
    {
        ArgumentNullException.ThrowIfNull(cipherText);
        ArgumentNullException.ThrowIfNull(password);
        var decrypted = DecryptBytes(Convert.FromBase64String(cipherText), password);
        return Encoding.UTF8.GetString(decrypted);
    }

    /// <summary>
    /// Encrypts a byte array using AES-256-CBC with a PBKDF2-derived key.
    /// The returned byte array is structured as: salt (16 bytes) + IV (16 bytes) + ciphertext.
    /// </summary>
    /// <param name="data">The data to encrypt. Must not be null.</param>
    /// <param name="password">The password used to derive the encryption key. Must not be null.</param>
    /// <returns>A byte array containing salt, IV, and ciphertext concatenated.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> or <paramref name="password"/> is null.</exception>
    public static byte[] EncryptBytes(byte[] data, string password)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = DeriveKey(password, salt);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(data, 0, data.Length);

        var result = new byte[SaltSize + IvSize + ciphertext.Length];
        Buffer.BlockCopy(salt, 0, result, 0, SaltSize);
        Buffer.BlockCopy(aes.IV, 0, result, SaltSize, IvSize);
        Buffer.BlockCopy(ciphertext, 0, result, SaltSize + IvSize, ciphertext.Length);
        return result;
    }

    /// <summary>
    /// Decrypts a byte array produced by <see cref="EncryptBytes"/>.
    /// Expects the input to be structured as: salt (16 bytes) + IV (16 bytes) + ciphertext.
    /// </summary>
    /// <param name="encryptedData">The encrypted data containing salt, IV, and ciphertext. Must not be null.</param>
    /// <param name="password">The password used during encryption. Must not be null.</param>
    /// <returns>The decrypted plaintext bytes.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="encryptedData"/> or <paramref name="password"/> is null.</exception>
    /// <exception cref="CryptographicException">Thrown when decryption fails (wrong password or corrupted data).</exception>
    public static byte[] DecryptBytes(byte[] encryptedData, string password)
    {
        ArgumentNullException.ThrowIfNull(encryptedData);
        ArgumentNullException.ThrowIfNull(password);

        if (encryptedData.Length < SaltSize + IvSize)
            throw new CryptographicException("Encrypted data is too short.");

        var salt = encryptedData[..SaltSize];
        var iv = encryptedData[SaltSize..(SaltSize + IvSize)];
        var ciphertext = encryptedData[(SaltSize + IvSize)..];
        var key = DeriveKey(password, salt);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    /// <summary>
    /// Generates a cryptographically random Base64-encoded key of the specified byte length.
    /// </summary>
    /// <param name="length">The number of random bytes to generate. Default: 32 (256-bit key).</param>
    /// <returns>A Base64-encoded string representing the random key.</returns>
    public static string GenerateKey(int length = 32)
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(length));
    }

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize);
    }
}
