using System.Security.Cryptography;
using System.Text;
using HomeAutomation.Infrastructure.Configuration;

namespace HomeAutomation.UnitTests;

public sealed class ConfigurationKeysTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("ha-vault-test-");

    [Fact]
    public void RoundTripAuthenticatesScopeProviderSlotAndEnvelope()
    {
        var material = RandomNumberGenerator.GetBytes(32);
        WriteKey("test", material);
        var keys = Create("test");
        var plaintext = Encoding.UTF8.GetBytes("synthetic-refresh-token");
        var first = keys.Seal(3, "daikin", "refreshToken", plaintext);
        var second = keys.Seal(3, "daikin", "refreshToken", plaintext);
        Assert.Equal(12, first.Nonce.Length);
        Assert.Equal(16, first.Tag.Length);
        Assert.False(first.Nonce.SequenceEqual(second.Nonce));
        Assert.Equal(plaintext, keys.Open(3, "daikin", "refreshToken", first));
        Assert.ThrowsAny<CryptographicException>(() => keys.Open(4, "daikin", "refreshToken", first));
        Assert.ThrowsAny<CryptographicException>(() => keys.Open(3, "ewelink", "refreshToken", first));
        Assert.ThrowsAny<CryptographicException>(() => keys.Open(3, "daikin", "clientSecret", first));
        Assert.ThrowsAny<CryptographicException>(() => keys.Open(3, "daikin", "refreshToken", first with { Version = 2 }));
        WriteKey("same-material", material);
        Assert.ThrowsAny<CryptographicException>(() => keys.Open(3, "daikin", "refreshToken", first with { KeyId = "same-material" }));
        first.Ciphertext[0] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => keys.Open(3, "daikin", "refreshToken", first));
        second.Tag[0] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => keys.Open(3, "daikin", "refreshToken", second));
    }

    [Fact]
    public void MissingInvalidAndIncorrectKeysFailClosed()
    {
        var keys = Create("test");
        Assert.False(keys.Available());
        Assert.Throws<CryptographicException>(() => keys.Seal(0, "growatt", "apiToken", "synthetic"u8));
        Assert.Empty(directory.GetFiles());
        WriteKey("test", new byte[31]);
        Assert.False(keys.Available());
        WriteKey("test", new byte[33]);
        Assert.False(keys.Available());
        WriteKey("test", RandomNumberGenerator.GetBytes(32));
        var envelope = keys.Seal(0, "growatt", "apiToken", "synthetic"u8);
        WriteKey("test", RandomNumberGenerator.GetBytes(32));
        Assert.ThrowsAny<CryptographicException>(() => keys.Open(0, "growatt", "apiToken", envelope));
        Assert.False(Create("../test").Available());
        Assert.False(Create("TEST").Available());
    }

    [Fact]
    public void ABackupNeedsItsOriginalKeysAndRotationUsesTheActiveKey()
    {
        var original = RandomNumberGenerator.GetBytes(32);
        var current = RandomNumberGenerator.GetBytes(32);
        WriteKey("original", original);
        var encrypted = Create("original").Seal(0, "tuya", "accessSecret", "synthetic"u8);
        WriteKey("current", current);
        var rotating = Create("current");
        var rotated = rotating.Seal(0, "tuya", "accessSecret", rotating.Open(0, "tuya", "accessSecret", encrypted));
        Assert.Equal("current", rotated.KeyId);
        File.Delete(Path.Combine(directory.FullName, "original.key"));
        Assert.Throws<CryptographicException>(() => rotating.Open(0, "tuya", "accessSecret", encrypted));
        Assert.Equal("synthetic"u8.ToArray(), rotating.Open(0, "tuya", "accessSecret", rotated));
        WriteKey("original", original);
        Assert.Equal("synthetic"u8.ToArray(), rotating.Open(0, "tuya", "accessSecret", encrypted));
    }

    private ConfigurationKeys Create(string id) => new(new() { KeyDirectory = directory.FullName, ActiveKeyId = id });
    private void WriteKey(string id, byte[] key) => File.WriteAllBytes(Path.Combine(directory.FullName, id + ".key"), key);
    public void Dispose() => directory.Delete(recursive: true);
}
