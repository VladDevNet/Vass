using System.Security.Cryptography;
using VoiceAssistant.API.Data;
using Xunit;

namespace VoiceAssistant.API.Tests;

public class ApiKeyEncryptionTests
{
    private static readonly byte[] Key = ApiKeyEncryption.DeriveKey("test-secret-do-not-use-in-production");
    private static readonly byte[] OtherKey = ApiKeyEncryption.DeriveKey("a-completely-different-secret");

    [Fact]
    public void DeriveKey_SameInput_ProducesSameKey()
    {
        var key1 = ApiKeyEncryption.DeriveKey("some-secret");
        var key2 = ApiKeyEncryption.DeriveKey("some-secret");

        Assert.Equal(key1, key2);
        Assert.Equal(32, key1.Length); // AES-256
    }

    [Fact]
    public void DeriveKey_DifferentInput_ProducesDifferentKey()
    {
        Assert.NotEqual(Key, OtherKey);
    }

    [Fact]
    public void EncryptThenDecrypt_RoundTrips()
    {
        var plaintext = "AIzaSyD-fake-gemini-key-1234567890";

        var encrypted = ApiKeyEncryption.Encrypt(plaintext, Key);
        var decrypted = ApiKeyEncryption.Decrypt(encrypted, Key);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void EncryptThenDecryptStrict_RoundTrips()
    {
        var plaintext = "sk-fake-openai-key";

        var encrypted = ApiKeyEncryption.Encrypt(plaintext, Key);
        var decrypted = ApiKeyEncryption.DecryptStrict(encrypted, Key);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_SamePlaintextTwice_ProducesDifferentCiphertext()
    {
        var plaintext = "same-key-value";

        var encrypted1 = ApiKeyEncryption.Encrypt(plaintext, Key);
        var encrypted2 = ApiKeyEncryption.Encrypt(plaintext, Key);

        Assert.NotEqual(encrypted1, encrypted2); // fresh random nonce each time
        Assert.Equal(plaintext, ApiKeyEncryption.Decrypt(encrypted1, Key));
        Assert.Equal(plaintext, ApiKeyEncryption.Decrypt(encrypted2, Key));
    }

    [Fact]
    public void Encrypt_NullOrEmpty_PassesThroughUnchanged()
    {
        Assert.Null(ApiKeyEncryption.Encrypt(null, Key));
        Assert.Equal("", ApiKeyEncryption.Encrypt("", Key));
    }

    [Fact]
    public void Decrypt_NullOrEmpty_PassesThroughUnchanged()
    {
        Assert.Null(ApiKeyEncryption.Decrypt(null, Key));
        Assert.Equal("", ApiKeyEncryption.Decrypt("", Key));
    }

    [Fact]
    public void Decrypt_WithWrongKey_FallsBackToStoredValueRatherThanThrow()
    {
        var encryptedUnderKey = ApiKeyEncryption.Encrypt("secret-value", Key);

        var result = ApiKeyEncryption.Decrypt(encryptedUnderKey, OtherKey);

        // Simulates the pre-encryption-key-rotation scenario -- must not throw
        // and must not silently return something that looks like the real key.
        Assert.Equal(encryptedUnderKey, result);
    }

    [Fact]
    public void DecryptStrict_WithWrongKey_Throws()
    {
        var encryptedUnderKey = ApiKeyEncryption.Encrypt("secret-value", Key);

        // AesGcm throws AuthenticationTagMismatchException specifically (a
        // CryptographicException subtype) for a tag mismatch -- ThrowsAny
        // checks assignability, matching the production code's own
        // `catch (CryptographicException)`, which catches this too.
        Assert.ThrowsAny<CryptographicException>(() => ApiKeyEncryption.DecryptStrict(encryptedUnderKey, OtherKey));
    }

    [Theory]
    [InlineData("AIzaSyD-a-real-looking-legacy-plaintext-key")]
    [InlineData("sk-1234567890abcdefghijklmnop")]
    [InlineData("not-base64-at-all-!!!")]
    public void Decrypt_LegacyPlaintext_ReturnsAsIsWithoutThrowing(string legacyPlaintext)
    {
        var result = ApiKeyEncryption.Decrypt(legacyPlaintext, Key);

        Assert.Equal(legacyPlaintext, result);
    }

    [Theory]
    [InlineData("AIzaSyD-a-real-looking-legacy-plaintext-key")]
    [InlineData("not-base64-at-all-!!!")]
    public void DecryptStrict_LegacyPlaintext_Throws(string legacyPlaintext)
    {
        Assert.ThrowsAny<Exception>(() => ApiKeyEncryption.DecryptStrict(legacyPlaintext, Key));
    }

    [Fact]
    public void DecryptStrict_ValidBase64ButTooShortForNonceAndTag_ThrowsCryptographicException()
    {
        var tooShort = Convert.ToBase64String(new byte[10]); // less than nonce(12)+tag(16)

        Assert.Throws<CryptographicException>(() => ApiKeyEncryption.DecryptStrict(tooShort, Key));
    }
}
