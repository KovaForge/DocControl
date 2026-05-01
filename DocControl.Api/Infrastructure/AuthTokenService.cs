using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace DocControl.Api.Infrastructure;

public sealed class AuthTokenService
{
    private const int CurrentTokenVersion = 2;
    private static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromDays(30);
    private readonly byte[]? key;
    private readonly TimeSpan tokenLifetime;

    public AuthTokenService(IConfiguration configuration)
    {
        var secret = configuration["AuthTokenSecret"];
        if (!string.IsNullOrWhiteSpace(secret))
        {
            key = Encoding.UTF8.GetBytes(secret);
        }

        var lifetimeHours = configuration.GetValue<double?>("AuthTokenLifetimeHours");
        tokenLifetime = lifetimeHours is > 0
            ? TimeSpan.FromHours(lifetimeHours.Value)
            : DefaultTokenLifetime;
    }

    public string? IssueToken(long userId, string email, bool mfaSatisfied = false)
    {
        if (key is null) return null;
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = $"{CurrentTokenVersion}|{userId}|{email}|{issuedAt}|{(mfaSatisfied ? "1" : "0")}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var signature = ComputeSignature(payloadBytes);
        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signature)}";
    }

    public bool TryValidate(string token, out long userId, out string email)
    {
        return TryValidate(token, out userId, out email, out _);
    }

    public bool TryValidate(string token, out long userId, out string email, out bool mfaSatisfied)
    {
        userId = 0;
        email = string.Empty;
        mfaSatisfied = false;
        if (key is null) return false;

        var parts = token.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;

        byte[] payloadBytes;
        byte[] signatureBytes;
        try
        {
            payloadBytes = Base64UrlDecode(parts[0]);
            signatureBytes = Base64UrlDecode(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = ComputeSignature(payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expected, signatureBytes))
        {
            return false;
        }

        var payload = Encoding.UTF8.GetString(payloadBytes);
        var fields = payload.Split('|', StringSplitOptions.None);
        if (fields.Length is not (4 or 5)) return false;
        if (!int.TryParse(fields[0], out var version) || version is not (1 or CurrentTokenVersion)) return false;
        if (version == 1 && fields.Length != 4) return false;
        if (version == CurrentTokenVersion && fields.Length != 5) return false;
        if (!long.TryParse(fields[1], out var parsedUserId) || parsedUserId <= 0) return false;

        email = fields[2];
        if (string.IsNullOrWhiteSpace(email)) return false;

        if (!long.TryParse(fields[3], out var issuedSeconds)) return false;
        if (version == CurrentTokenVersion)
        {
            mfaSatisfied = fields[4] == "1";
        }
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(issuedSeconds);
        if (DateTimeOffset.UtcNow - issuedAt > tokenLifetime) return false;

        userId = parsedUserId;
        return true;
    }

    private byte[] ComputeSignature(byte[] payload)
    {
        using var hmac = new HMACSHA256(key!);
        return hmac.ComputeHash(payload);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }
        return Convert.FromBase64String(padded);
    }
}
