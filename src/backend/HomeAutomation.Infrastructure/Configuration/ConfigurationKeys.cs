using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HomeAutomation.Infrastructure.Configuration;

public sealed class ConfigurationVaultOptions
{
    public string? KeyDirectory { get; set; }
    public string? ActiveKeyId { get; set; }
    public string? AdministratorUserId { get; set; }

    public bool IsAdministrator(string actor) =>
        Guid.TryParseExact(AdministratorUserId, "D", out var admin) &&
        Guid.TryParseExact(actor, "D", out var id) && id == admin;
}

internal sealed record CredentialEnvelope(int Version, string KeyId, byte[] Nonce, byte[] Tag, byte[] Ciphertext);

internal sealed class ConfigurationKeys(ConfigurationVaultOptions options)
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    public const int MaximumSecretBytes = 4096;
    public string? ActiveKeyId => options.ActiveKeyId;

    public bool Available()
    {
        try
        {
            var key = Load(options.ActiveKeyId);
            CryptographicOperations.ZeroMemory(key);
            return true;
        }
        catch (CryptographicException) { return false; }
    }

    internal static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceSize);

    public CredentialEnvelope Seal(long scope, string provider, string slot, ReadOnlySpan<byte> plaintext) =>
        Seal(scope, provider, slot, plaintext, options.ActiveKeyId, NewNonce());

    internal CredentialEnvelope Seal(long scope, string provider, string slot, ReadOnlySpan<byte> plaintext,
        string? keyId, byte[] nonce)
    {
        var key = Load(keyId);
        try
        {
            var envelope = new CredentialEnvelope(1, keyId!, nonce, new byte[TagSize],
                new byte[plaintext.Length]);
            using var cipher = new AesGcm(key, TagSize);
            cipher.Encrypt(envelope.Nonce, plaintext, envelope.Ciphertext, envelope.Tag, Aad(scope, provider, slot, envelope));
            return envelope;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public byte[] Open(long scope, string provider, string slot, CredentialEnvelope envelope)
    {
        if (envelope is not { Version: 1, Nonce.Length: NonceSize, Tag.Length: TagSize,
            Ciphertext.Length: > 0 and <= MaximumSecretBytes })
            throw new CryptographicException("Invalid credential envelope.");
        var key = Load(envelope.KeyId);
        var plaintext = new byte[envelope.Ciphertext.Length];
        try
        {
            using var cipher = new AesGcm(key, TagSize);
            cipher.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, Aad(scope, provider, slot, envelope));
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private byte[] Load(string? keyId)
    {
        if (keyId is not { Length: > 0 and <= 64 } ||
            !keyId.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_') ||
            options.KeyDirectory is not { Length: > 0 } directory || !Path.IsPathFullyQualified(directory))
            throw new CryptographicException("Set the external configuration vault key directory and key ID.");

        var key = new byte[KeySize];
        try
        {
            var path = Path.Combine(directory, keyId + ".key");
            if (new FileInfo(path).Length != KeySize) throw new CryptographicException("Invalid configuration vault key length.");
            using var input = File.OpenRead(path);
            input.ReadExactly(key);
            if (input.ReadByte() != -1) throw new CryptographicException("Invalid configuration vault key length.");
            return key;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("The external configuration vault key is missing, unreadable or invalid.");
        }
    }

    private static byte[] Aad(long scope, string provider, string slot, CredentialEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(new object[] { "HomeAutomation.Configuration", envelope.Version, envelope.KeyId,
            scope, provider, slot });
}
