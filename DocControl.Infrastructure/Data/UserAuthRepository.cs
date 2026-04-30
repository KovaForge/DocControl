using System.Text.Json;
using DocControl.Core.Security;
using Microsoft.Extensions.Configuration;
using DocControl.Core.Models;
using Npgsql;

namespace DocControl.Infrastructure.Data;

public sealed class UserAuthRepository
{
    private readonly DbConnectionFactory factory;
    private readonly AesGcmSecretProtector? mfaProtector;

    public UserAuthRepository(DbConnectionFactory factory, IConfiguration configuration)
    {
        this.factory = factory;
        var key = configuration["MFA_SECRET_KEY"];
        if (!string.IsNullOrWhiteSpace(key))
        {
            mfaProtector = new AesGcmSecretProtector(key);
        }
    }

    public async Task EnsureExistsAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        const string sql = @"
            INSERT INTO UserAuth (UserId, ProviderSubject, ProviderName, MfaEnabled, MfaMethodsJson)
            VALUES (@userId, '', '', FALSE, NULL)
            ON CONFLICT (UserId) DO NOTHING;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@userId", userId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserAuthRecord?> GetAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = "SELECT UserId, MfaEnabled, MfaMethodsJson FROM UserAuth WHERE UserId = @userId;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@userId", userId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var json = reader.IsDBNull(2) ? null : reader.GetString(2);
            json = TryDecryptJson(json);
            return new UserAuthRecord
            {
                UserId = reader.GetInt64(0),
                MfaEnabled = reader.GetBoolean(1),
                MfaMethodsJson = json
            };
        }

        return null;
    }

    public async Task SaveTotpAsync(long userId, string secret, bool verified, CancellationToken cancellationToken = default)
    {
        var protectedSecret = ProtectSecret(secret);
        var state = new TotpState(protectedSecret, DateTime.UtcNow, verified ? DateTime.UtcNow : null, null, null, null);
        await SaveTotpStateAsync(userId, state, verified, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveTotpStateAsync(long userId, TotpState state, bool mfaEnabled, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(state);
        const string sql = @"
            INSERT INTO UserAuth (UserId, ProviderSubject, ProviderName, MfaEnabled, MfaMethodsJson)
            VALUES (@userId, '', '', @enabled, @json)
            ON CONFLICT (UserId) DO UPDATE SET
                MfaEnabled = EXCLUDED.MfaEnabled,
                MfaMethodsJson = EXCLUDED.MfaMethodsJson;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@userId", userId);
        cmd.Parameters.AddWithValue("@enabled", mfaEnabled);
        cmd.Parameters.AddWithValue("@json", (object?)json ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private string ProtectSecret(string secret)
    {
        if (mfaProtector is null || string.IsNullOrWhiteSpace(secret))
        {
            return secret;
        }

        if (secret.StartsWith("v1:", StringComparison.Ordinal))
        {
            return secret;
        }

        return mfaProtector.Encrypt(secret);
    }

    private string? TryDecryptJson(string? json)
    {
        if (mfaProtector is null || string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        try
        {
            var state = JsonSerializer.Deserialize<TotpState>(json);
            if (state is null || string.IsNullOrWhiteSpace(state.Secret))
            {
                return json;
            }

            var activeSecret = DecryptSecret(state.Secret);
            var pendingSecret = string.IsNullOrWhiteSpace(state.PendingSecret) ? null : DecryptSecret(state.PendingSecret);

            if (activeSecret is null && pendingSecret is null)
            {
                return json;
            }

            var updated = new TotpState(
                activeSecret ?? state.Secret,
                state.CreatedAtUtc,
                state.VerifiedAtUtc,
                pendingSecret,
                state.PendingCreatedAtUtc,
                state.BackupCodes);
            return JsonSerializer.Serialize(updated);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private string? DecryptSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return null;
        if (!secret.StartsWith("v1:", StringComparison.Ordinal)) return null;
        var decrypted = mfaProtector?.Decrypt(secret);
        return string.IsNullOrWhiteSpace(decrypted) ? null : decrypted;
    }
}

public sealed record BackupCode(string Salt, string Hash);

public sealed record TotpState(
    string Secret,
    DateTime CreatedAtUtc,
    DateTime? VerifiedAtUtc,
    string? PendingSecret,
    DateTime? PendingCreatedAtUtc,
    IReadOnlyList<BackupCode>? BackupCodes);
