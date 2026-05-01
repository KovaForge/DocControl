using DocControl.Core.Models;
using Npgsql;

namespace DocControl.Infrastructure.Data;

public sealed class LevelCodeRepository
{
    private readonly DbConnectionFactory factory;

    public LevelCodeRepository(DbConnectionFactory factory)
    {
        this.factory = factory;
    }

    public async Task<IReadOnlyList<LevelCodeRecord>> ListAsync(long projectId, int? level = null, CancellationToken cancellationToken = default)
    {
        var list = new List<LevelCodeRecord>();
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var sql = @"
            SELECT Id, ProjectId, Level, Code, Description, CreatedAtUtc, UpdatedAtUtc
            FROM LevelCodes
            WHERE ProjectId = @ProjectId";
        if (level.HasValue)
        {
            sql += " AND Level = @Level";
        }
        sql += " ORDER BY Level, Code;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ProjectId", projectId);
        if (level.HasValue)
        {
            cmd.Parameters.AddWithValue("@Level", level.Value);
        }

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadRecord(reader));
        }

        return list;
    }

    public async Task<LevelCodeRecord?> GetAsync(long projectId, int level, string code, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = @"
            SELECT Id, ProjectId, Level, Code, Description, CreatedAtUtc, UpdatedAtUtc
            FROM LevelCodes
            WHERE ProjectId = @ProjectId AND Level = @Level AND Code = @Code
            LIMIT 1;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ProjectId", projectId);
        cmd.Parameters.AddWithValue("@Level", level);
        cmd.Parameters.AddWithValue("@Code", NormalizeCode(code));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadRecord(reader);
    }

    public async Task<LevelCodeRecord> UpsertAsync(long projectId, int level, string code, string? description, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = @"
            INSERT INTO LevelCodes (ProjectId, Level, Code, Description)
            VALUES (@ProjectId, @Level, @Code, @Description)
            ON CONFLICT(ProjectId, Level, Code)
            DO UPDATE SET
                Description = EXCLUDED.Description,
                UpdatedAtUtc = now()
            RETURNING Id, ProjectId, Level, Code, Description, CreatedAtUtc, UpdatedAtUtc;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ProjectId", projectId);
        cmd.Parameters.AddWithValue("@Level", level);
        cmd.Parameters.AddWithValue("@Code", NormalizeCode(code));
        cmd.Parameters.AddWithValue("@Description", (object?)NormalizeDescription(description) ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Failed to upsert level code.");
        }

        return ReadRecord(reader);
    }

    private static LevelCodeRecord ReadRecord(NpgsqlDataReader reader)
    {
        return new LevelCodeRecord
        {
            Id = reader.GetInt64(0),
            ProjectId = reader.GetInt64(1),
            Level = reader.GetInt32(2),
            Code = reader.GetString(3),
            Description = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAtUtc = reader.GetDateTime(5),
            UpdatedAtUtc = reader.GetDateTime(6)
        };
    }

    private static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();

    private static string? NormalizeDescription(string? description)
    {
        var trimmed = description?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
