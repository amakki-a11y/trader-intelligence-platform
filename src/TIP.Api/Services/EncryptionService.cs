using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace TIP.Api.Services;

/// <summary>
/// Provides AES-256 encryption/decryption for sensitive data (MT5 manager passwords).
///
/// Design rationale:
/// - AES-256-CBC with random IV per encryption (IV prepended to ciphertext).
/// - Encryption key loaded from environment variable or user secrets, never in config files.
/// - Used only for MT5 manager passwords stored in the auth database.
/// - 32-byte key derived from configured string via SHA256 hash.
/// </summary>
public sealed class EncryptionService
{
    private readonly ILogger<EncryptionService> _logger;
    private readonly byte[] _key;

    /// <summary>
    /// Initializes the encryption service with key from configuration.
    /// </summary>
    /// <param name="logger">Logger for encryption events.</param>
    /// <param name="config">Configuration containing Encryption:Key.</param>
    public EncryptionService(ILogger<EncryptionService> logger, IConfiguration config)
    {
        _logger = logger;

        var keyString = config["Encryption:Key"]
            ?? Environment.GetEnvironmentVariable("TIP_ENCRYPTION_KEY")
            ?? throw new InvalidOperationException("Encryption key not configured. Set Encryption:Key in user secrets or TIP_ENCRYPTION_KEY environment variable.");

        // Derive a 32-byte key from the configured string
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(keyString));
        _logger.LogInformation("Encryption service initialized");
    }

    /// <summary>
    /// Encrypts a plain text string using AES-256-CBC.
    /// </summary>
    /// <param name="plainText">The text to encrypt.</param>
    /// <returns>Base64-encoded ciphertext with prepended IV.</returns>
    public string Encrypt(string plainText)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Prepend IV to ciphertext
        var result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// Decrypts a Base64-encoded ciphertext (with prepended IV) back to plain text.
    /// </summary>
    /// <param name="cipherText">Base64-encoded ciphertext from Encrypt().</param>
    /// <returns>The original plain text.</returns>
    public string Decrypt(string cipherText)
    {
        var fullBytes = Convert.FromBase64String(cipherText);

        using var aes = Aes.Create();
        aes.Key = _key;

        // Extract IV (first 16 bytes)
        var iv = new byte[16];
        Buffer.BlockCopy(fullBytes, 0, iv, 0, 16);
        aes.IV = iv;

        // Extract ciphertext (remaining bytes)
        var cipherBytes = new byte[fullBytes.Length - 16];
        Buffer.BlockCopy(fullBytes, 16, cipherBytes, 0, cipherBytes.Length);

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

        return Encoding.UTF8.GetString(plainBytes);
    }
}
