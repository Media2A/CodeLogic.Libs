using System.Security.Cryptography;
using System.Text;

namespace CL.Common.Security;

/// <summary>
/// Provides AES-256-GCM authenticated encryption and decryption utilities with PBKDF2 key derivation.
/// AES-GCM is an AEAD (Authenticated Encryption with Associated Data) cipher that provides both
/// confidentiality and integrity. Unlike AES-CBC, it detects tampering and rejects modified ciphertext.
/// </summary>
/// <remarks>
/// <para><b>Wire format:</b> all output is structured as
/// <c>salt (16 bytes) + nonce (12 bytes) + tag (16 bytes) + ciphertext</c>.</para>
/// <para><b>Breaking change from earlier versions:</b> this class previously used AES-CBC which
/// provides no authentication and is vulnerable to padding-oracle attacks. Any data encrypted with
/// the previous (AES-CBC) implementation cannot be decrypted with this version.</para>
/// </remarks>
public static class Encryption
{
    private const int KeySize   = 32;  // AES-256
    private const int NonceSize = 12;  // GCM standard nonce
    private const int TagSize   = 16;  // GCM authentication tag
    private const int SaltSize  = 16;
    private const int Iterations = 100_000;

    /// <summary>
    /// Encrypts a plaintext string using AES-256-GCM with a PBKDF2-derived key.
    /// The output is a Base64-encoded string containing the salt, nonce, authentication tag, and ciphertext.
    /// </summary>
    /// <param name="plainText">The plaintext to encrypt. Must not be null.</param>
    /// <param name="password">The password used to derive the encryption key. Must not be null.</param>
    /// <returns>
    /// A Base64-encoded string containing salt (16 bytes) + nonce (12 bytes) + tag (16 bytes) + ciphertext.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="plainText"/> or <paramref name="password"/> is null.</exception>
    public static string EncryptAes(string plainText, string password)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        ArgumentNullException.ThrowIfNull(password);
        return Convert.ToBase64String(EncryptBytes(Encoding.UTF8.GetBytes(plainText), password));
    }

    /// <summary>
    /// Decrypts a Base64-encoded ciphertext string produced by <see cref="EncryptAes"/>.
    /// </summary>
    /// <param name="cipherText">The Base64-encoded ciphertext (salt + nonce + tag + ciphertext). Must not be null.</param>
    /// <param name="password">The password used during encryption. Must not be null.</param>
    /// <returns>The original plaintext string.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="cipherText"/> or <paramref name="password"/> is null.</exception>
    /// <exception cref="CryptographicException">Thrown when decryption or authentication fails (wrong password, corrupted data, or tampered ciphertext).</exception>
    public static string DecryptAes(string cipherText, string password)
    {
        ArgumentNullException.ThrowIfNull(cipherText);
        ArgumentNullException.ThrowIfNull(password);
        return Encoding.UTF8.GetString(DecryptBytes(Convert.FromBase64String(cipherText), password));
    }

    /// <summary>
    /// Encrypts a byte array using AES-256-GCM with a PBKDF2-derived key.
    /// </summary>
    /// <param name="data">The data to encrypt. Must not be null.</param>
    /// <param name="password">The password used to derive the encryption key. Must not be null.</param>
    /// <returns>
    /// A byte array containing salt (16 bytes) + nonce (12 bytes) + tag (16 bytes) + ciphertext.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> or <paramref name="password"/> is null.</exception>
    public static byte[] EncryptBytes(byte[] data, string password)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(password);

        var salt  = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key   = DeriveKey(password, salt);

        var ciphertext = new byte[data.Length];
        var tag        = new byte[TagSize];

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Encrypt(nonce, data, ciphertext, tag);

        var result = new byte[SaltSize + NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(salt,       0, result, 0,                              SaltSize);
        Buffer.BlockCopy(nonce,      0, result, SaltSize,                       NonceSize);
        Buffer.BlockCopy(tag,        0, result, SaltSize + NonceSize,           TagSize);
        Buffer.BlockCopy(ciphertext, 0, result, SaltSize + NonceSize + TagSize, ciphertext.Length);
        return result;
    }

    /// <summary>
    /// Decrypts a byte array produced by <see cref="EncryptBytes"/>.
    /// AES-GCM verifies the authentication tag before returning plaintext —
    /// any modification to the encrypted data causes a <see cref="CryptographicException"/>.
    /// </summary>
    /// <param name="encryptedData">
    /// The encrypted data containing salt (16 bytes) + nonce (12 bytes) + tag (16 bytes) + ciphertext.
    /// Must not be null.
    /// </param>
    /// <param name="password">The password used during encryption. Must not be null.</param>
    /// <returns>The decrypted plaintext bytes.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="encryptedData"/> or <paramref name="password"/> is null.</exception>
    /// <exception cref="CryptographicException">
    /// Thrown when authentication fails (wrong password, corrupted data, or tampered ciphertext).
    /// </exception>
    public static byte[] DecryptBytes(byte[] encryptedData, string password)
    {
        ArgumentNullException.ThrowIfNull(encryptedData);
        ArgumentNullException.ThrowIfNull(password);

        int minLength = SaltSize + NonceSize + TagSize;
        if (encryptedData.Length < minLength)
            throw new CryptographicException(
                $"Encrypted data is too short. Expected at least {minLength} bytes.");

        var salt       = encryptedData[..SaltSize];
        var nonce      = encryptedData[SaltSize..(SaltSize + NonceSize)];
        var tag        = encryptedData[(SaltSize + NonceSize)..(SaltSize + NonceSize + TagSize)];
        var ciphertext = encryptedData[(SaltSize + NonceSize + TagSize)..];
        var key        = DeriveKey(password, salt);

        var plaintext = new byte[ciphertext.Length];
        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    /// <summary>
    /// Generates a cryptographically random Base64-encoded key of the specified byte length.
    /// </summary>
    /// <param name="length">The number of random bytes to generate. Default: 32 (256-bit key).</param>
    /// <returns>A Base64-encoded string representing the random key.</returns>
    public static string GenerateKey(int length = 32)
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(length));

    private static byte[] DeriveKey(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize);
}
