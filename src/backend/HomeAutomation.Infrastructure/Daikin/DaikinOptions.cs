namespace HomeAutomation.Infrastructure.Daikin;

public sealed class DaikinOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? RedirectUri { get; set; }
    public string? OperatorEmail { get; set; }
    public string? TokenKey { get; set; }
    public bool UsePkce { get; set; } = true;
    public bool EnableWrites { get; set; }
    public int PollMinutes { get; set; } = 15;
    public int DailyLimit { get; set; } = 200;
    public int ManualReserve { get; set; } = 40;
    public int FreshMinutes { get; set; } = 35;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(OperatorEmail) && ValidCallback() && EncryptionKey() is not null &&
        PollMinutes is >= 5 and <= 1440 && DailyLimit is >= 10 and <= 200 &&
        ManualReserve >= 2 && ManualReserve < DailyLimit && FreshMinutes >= PollMinutes * 2 && FreshMinutes <= 2880;

    public bool IsOperator(string? email) => IsConfigured &&
        string.Equals(email, OperatorEmail, StringComparison.OrdinalIgnoreCase);

    internal byte[]? EncryptionKey()
    {
        Span<byte> key = stackalloc byte[32];
        return TokenKey is not null && Convert.TryFromBase64String(TokenKey, key, out var count) && count == 32
            ? key.ToArray() : null;
    }

    private bool ValidCallback() => Uri.TryCreate(RedirectUri, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback)) &&
        uri.AbsolutePath == "/api/v1/daikin/callback" && uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0;
}
