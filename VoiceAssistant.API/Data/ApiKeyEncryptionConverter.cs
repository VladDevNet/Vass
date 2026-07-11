using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace VoiceAssistant.API.Data;

// AES-256-GCM at the EF Core value-conversion boundary, so every existing
// read/write of UserSettings.{Gemini,OpenAi,Anthropic}ApiKey is transparently
// encrypted at rest with no call-site changes anywhere else in the app
// (PROJECT-AUDIT-2026-07-10 SEC-03 — these were plain `text` columns).
//
// Stored format: base64(nonce(12 bytes) || ciphertext || tag(16 bytes)).
// A fresh random nonce is generated on every encryption, so re-saving the
// same plaintext produces different ciphertext each time — expected for
// GCM, not a bug.
//
// Decrypt() never throws: a value that isn't valid base64, or is but fails
// GCM tag verification (near-certain for anything not encrypted with this
// exact key), is treated as a pre-encryption legacy plaintext value and
// returned as-is. This is what makes the deploy safe for existing rows —
// see Program.cs's one-time startup migration, which uses DecryptStrict
// (below) specifically to detect and re-save those rows through the
// encrypting write path.
public static class ApiKeyEncryption
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    // A blank secret (not just a missing/null one -- ${ENCRYPTION_KEY} in
    // docker-compose.yml expands to "" if the operator forgets to set it,
    // NOT to a missing key) would silently derive the well-known, publicly
    // computable SHA256("") -- "encrypting" under it protects nothing while
    // looking like it worked. Fail loudly instead (review finding).
    public static byte[] DeriveKey(string configuredSecret)
    {
        if (string.IsNullOrWhiteSpace(configuredSecret) || configuredSecret.Length < 16)
        {
            throw new InvalidOperationException(
                "Encryption:Key must be set to a real secret at least 16 characters long -- refusing to derive an " +
                "encryption key from a blank or too-short value, which would protect nothing.");
        }
        return SHA256.HashData(Encoding.UTF8.GetBytes(configuredSecret));
    }

    public static string? Encrypt(string? plaintext, byte[] key)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using (var aesGcm = new AesGcm(key, TagSize))
        {
            aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        }

        var combined = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, combined, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, NonceSize + ciphertext.Length, TagSize);

        return Convert.ToBase64String(combined);
    }

    // Throws (FormatException, CryptographicException) if `stored` isn't a
    // value this key actually encrypted. Used directly only by the startup
    // migration to distinguish legacy plaintext from real ciphertext -- the
    // EF Core converter itself always goes through the wrapping Decrypt()
    // below instead.
    public static string? DecryptStrict(string? stored, byte[] key)
    {
        if (string.IsNullOrEmpty(stored)) return stored;

        var combined = Convert.FromBase64String(stored);
        if (combined.Length < NonceSize + TagSize)
            throw new CryptographicException("Value too short to contain a nonce and tag.");

        var nonce = combined[..NonceSize];
        var tag = combined[^TagSize..];
        var ciphertext = combined[NonceSize..^TagSize];
        var plaintextBytes = new byte[ciphertext.Length];

        using (var aesGcm = new AesGcm(key, TagSize))
        {
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintextBytes);
        }

        return Encoding.UTF8.GetString(plaintextBytes);
    }

    public static string? Decrypt(string? stored, byte[] key)
    {
        if (string.IsNullOrEmpty(stored)) return stored;

        try
        {
            return DecryptStrict(stored, key);
        }
        catch (FormatException)
        {
            return stored;
        }
        catch (CryptographicException)
        {
            return stored;
        }
    }
}

public class ApiKeyEncryptionConverter : ValueConverter<string?, string?>
{
    public ApiKeyEncryptionConverter(byte[] key)
        : base(
            plaintext => ApiKeyEncryption.Encrypt(plaintext, key),
            stored => ApiKeyEncryption.Decrypt(stored, key))
    {
    }
}
