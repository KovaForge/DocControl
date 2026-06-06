using System.Security.Cryptography;
using System.Text;
using DocControl.Core.Models;
using Npgsql;

namespace DocControl.Infrastructure.Data;

public sealed class AgentTokenRepository
{
    private const string TokenPrefix = "dcag_";
    private readonly DbConnectionFactory factory;

    public AgentTokenRepository(DbConnectionFactory factory)
    {
        this.factory = factory;
    }

    public async Task<IReadOnlyList<AgentTokenRecord>> ListActiveAsync(long userId, CancellationToken cancellationToken = default)
    {
        var list = new List<AgentTokenRecord>();
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = @"
            SELECT Id, UserId, Name, Prefix, CreatedAtUtc, LastUsedAtUtc, ExpiresAtUtc, RevokedAtUtc
            FROM AgentTokens
            WHERE UserId = @UserId
              AND RevokedAtUtc IS NULL
              AND (ExpiresAtUtc IS NULL OR ExpiresAtUtc > now())
            ORDER BY CreatedAtUtc DESC;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@UserId", userId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadToken(reader));
        }

        return list;
    }

    public async Task<CreatedAgentToken> CreateAsync(long userId, string? name, DateTime? expiresAtUtc = null, CancellationToken cancellationToken = default)
    {
        var token = GenerateToken();
        var tokenHash = HashToken(token);
        var prefix = token.Length <= 12 ? token : token[..12];
        var cleanName = string.IsNullOrWhiteSpace(name) ? "Agent token" : name.Trim();
        if (cleanName.Length > 120)
        {
            cleanName = cleanName[..120];
        }

        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = @"
            INSERT INTO AgentTokens (UserId, Name, TokenHash, Prefix, CreatedAtUtc, ExpiresAtUtc)
            VALUES (@UserId, @Name, @TokenHash, @Prefix, @CreatedAtUtc, @ExpiresAtUtc)
            RETURNING Id, UserId, Name, Prefix, CreatedAtUtc, LastUsedAtUtc, ExpiresAtUtc, RevokedAtUtc;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@UserId", userId);
        cmd.Parameters.AddWithValue("@Name", cleanName);
        cmd.Parameters.AddWithValue("@TokenHash", tokenHash);
        cmd.Parameters.AddWithValue("@Prefix", prefix);
        cmd.Parameters.AddWithValue("@CreatedAtUtc", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("@ExpiresAtUtc", (object?)expiresAtUtc ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Failed to create agent token.");
        }

        return new CreatedAgentToken(ReadToken(reader), token);
    }

    public async Task<bool> RevokeAsync(long userId, long tokenId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = @"
            UPDATE AgentTokens
            SET RevokedAtUtc = @RevokedAtUtc
            WHERE UserId = @UserId AND Id = @Id AND RevokedAtUtc IS NULL;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@UserId", userId);
        cmd.Parameters.AddWithValue("@Id", tokenId);
        cmd.Parameters.AddWithValue("@RevokedAtUtc", DateTime.UtcNow);
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<AgentTokenRecord?> ValidateAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var tokenHash = HashToken(token.Trim());
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        const string selectSql = @"
            SELECT Id, UserId, Name, Prefix, CreatedAtUtc, LastUsedAtUtc, ExpiresAtUtc, RevokedAtUtc
            FROM AgentTokens
            WHERE TokenHash = @TokenHash
              AND RevokedAtUtc IS NULL
              AND (ExpiresAtUtc IS NULL OR ExpiresAtUtc > now())
            FOR UPDATE;";
        AgentTokenRecord? record = null;
        await using (var selectCmd = new NpgsqlCommand(selectSql, conn, (NpgsqlTransaction)tx))
        {
            selectCmd.Parameters.AddWithValue("@TokenHash", tokenHash);
            await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                record = ReadToken(reader);
            }
        }

        if (record is null)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        const string updateSql = "UPDATE AgentTokens SET LastUsedAtUtc = @LastUsedAtUtc WHERE Id = @Id;";
        await using (var updateCmd = new NpgsqlCommand(updateSql, conn, (NpgsqlTransaction)tx))
        {
            updateCmd.Parameters.AddWithValue("@Id", record.Id);
            updateCmd.Parameters.AddWithValue("@LastUsedAtUtc", DateTime.UtcNow);
            await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return TokenPrefix + Base64UrlEncode(bytes);
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static AgentTokenRecord ReadToken(NpgsqlDataReader reader) =>
        new()
        {
            Id = reader.GetInt64(0),
            UserId = reader.GetInt64(1),
            Name = reader.GetString(2),
            Prefix = reader.GetString(3),
            CreatedAtUtc = reader.GetDateTime(4),
            LastUsedAtUtc = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            ExpiresAtUtc = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            RevokedAtUtc = reader.IsDBNull(7) ? null : reader.GetDateTime(7)
        };
}
