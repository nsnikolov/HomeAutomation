using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HomeAutomation.Infrastructure.Daikin;

internal sealed record DaikinTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt)
{
    public bool NeedsRefresh(DateTimeOffset now) => ExpiresAt <= now.AddMinutes(2);
}

internal sealed class DaikinSecrets(DaikinOptions options)
{
    public byte[] Seal(string owner, DaikinTokens tokens)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(tokens);
        try
        {
            var envelope = new byte[1 + 12 + 16 + data.Length];
            envelope[0] = 1;
            RandomNumberGenerator.Fill(envelope.AsSpan(1, 12));
            using var cipher = new AesGcm(options.EncryptionKey()!, 16);
            cipher.Encrypt(envelope.AsSpan(1, 12), data, envelope.AsSpan(29), envelope.AsSpan(13, 16), Encoding.UTF8.GetBytes(owner));
            return envelope;
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    public DaikinTokens Open(string owner, byte[] envelope)
    {
        if (envelope.Length <= 29 || envelope[0] != 1) throw new CryptographicException();
        var data = new byte[envelope.Length - 29];
        try
        {
            using var cipher = new AesGcm(options.EncryptionKey()!, 16);
            cipher.Decrypt(envelope.AsSpan(1, 12), envelope.AsSpan(29), envelope.AsSpan(13, 16), data, Encoding.UTF8.GetBytes(owner));
            return JsonSerializer.Deserialize<DaikinTokens>(data) ?? throw new CryptographicException();
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
}
