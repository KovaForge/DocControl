using DocControl.Core.Models;
using Npgsql;

namespace DocControl.Infrastructure.Data;

public sealed class DocumentRepository
{
    private readonly DbConnectionFactory factory;

    public DocumentRepository(DbConnectionFactory factory)
    {
        this.factory = factory;
    }

    public async Task<long> InsertAsync(AllocatedNumber allocated, CodeSeriesKey key, string? freeText, string fileName, long createdByUserId, DateTime createdAtUtc, string? originalQuery, CancellationToken cancellationToken = default)
    {
        var level4 = DbValue.NormalizeLevel(key.Level4);
        var level5 = DbValue.NormalizeLevel(key.Level5);
        var level6 = DbValue.NormalizeLevel(key.Level6);
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = @"
            INSERT INTO Documents (ProjectId, Level1, Level2, Level3, Level4, Level5, Level6, Number, FreeText, FileName, CreatedByUserId, CreatedAtUtc, OriginalQuery, CodeSeriesId)
            VALUES (@ProjectId, @Level1, @Level2, @Level3, @Level4, @Level5, @Level6, @Number, @FreeText, @FileName, @CreatedByUserId, @CreatedAtUtc, @OriginalQuery, @CodeSeriesId)
            RETURNING Id;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ProjectId", key.ProjectId);
        cmd.Parameters.AddWithValue("@Level1", key.Level1);
        cmd.Parameters.AddWithValue("@Level2", key.Level2);
        cmd.Parameters.AddWithValue("@Level3", key.Level3);
        cmd.Parameters.AddWithValue("@Level4", level4);
        cmd.Parameters.AddWithValue("@Level5", level5);
        cmd.Parameters.AddWithValue("@Level6", level6);
        cmd.Parameters.AddWithValue("@Number", allocated.Number);
        cmd.Parameters.AddWithValue("@FreeText", (object?)freeText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FileName", fileName);
        cmd.Parameters.AddWithValue("@CreatedByUserId", createdByUserId);
        cmd.Parameters.AddWithValue("@CreatedAtUtc", createdAtUtc);
        cmd.Parameters.AddWithValue("@OriginalQuery", (object?)originalQuery ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CodeSeriesId", allocated.SeriesId);

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result ?? throw new InvalidOperationException("Failed to insert document."));
    }

    public async Task<int?> GetMaxNumberAsync(CodeSeriesKey key, CancellationToken cancellationToken = default)
    {
        var level4 = DbValue.NormalizeLevel(key.Level4);
        var level5 = DbValue.NormalizeLevel(key.Level5);
        var level6 = DbValue.NormalizeLevel(key.Level6);
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = @"SELECT MAX(Number) FROM Documents WHERE ProjectId = @ProjectId AND Level1 = @Level1 AND Level2 = @Level2 AND Level3 = @Level3 AND (Level4 IS NOT DISTINCT FROM @Level4) AND (Level5 IS NOT DISTINCT FROM @Level5) AND (Level6 IS NOT DISTINCT FROM @Level6);";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ProjectId", key.ProjectId);
        cmd.Parameters.AddWithValue("@Level1", key.Level1);
        cmd.Parameters.AddWithValue("@Level2", key.Level2);
        cmd.Parameters.AddWithValue("@Level3", key.Level3);
        cmd.Parameters.AddWithValue("@Level4", level4);
        cmd.Parameters.AddWithValue("@Level5", level5);
        cmd.Parameters.AddWithValue("@Level6", level6);

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is DBNull or null) return null;
        return Convert.ToInt32(result);
    }

    public async Task<bool> ExistsAsync(CodeSeriesKey key, CancellationToken cancellationToken = default)
    {
        var level4 = DbValue.NormalizeLevel(key.Level4);
        var level5 = DbValue.NormalizeLevel(key.Level5);
        var level6 = DbValue.NormalizeLevel(key.Level6);
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        const string sql = @"SELECT 1 FROM Documents WHERE ProjectId = @ProjectId AND Level1=@Level1 AND Level2=@Level2 AND Level3=@Level3 AND (Level4 IS NOT DISTINCT FROM @Level4) AND (Level5 IS NOT DISTINCT FROM @Level5) AND (Level6 IS NOT DISTINCT FROM @Level6) LIMIT 1;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ProjectId", key.ProjectId);
        cmd.Parameters.AddWithValue("@Level1", key.Level1);
        cmd.Parameters.AddWithValue("@Level2", key.Level2);
        cmd.Parameters.AddWithValue("@Level3", key.Level3);
        cmd.Parameters.AddWithValue("@Level4", level4);
        cmd.Parameters.AddWithValue("@Level5", level5);
        cmd.Parameters.AddWithValue("@Level6", level6);

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result != null;
    }

    public async Task<int> PurgeAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = @"DELETE FROM Documents WHERE ProjectId = @ProjectId;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ProjectId", projectId);
        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected;
    }

    public async Task<IReadOnlyList<DocumentRecord>> GetRecentAsync(long projectId, int take = 50, CancellationToken cancellationToken = default)
    {
        var list = new List<DocumentRecord>();
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        const string sql = @"SELECT Id, ProjectId, Level1, Level2, Level3, Level4, Level5, Level6, Number, FreeText, FileName, CreatedByUserId, CreatedAtUtc, OriginalQuery, CodeSeriesId FROM Documents WHERE ProjectId = @ProjectId ORDER BY Id DESC LIMIT @take;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ProjectId", projectId);
        cmd.Parameters.AddWithValue("@take", take);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadDocument(reader));
        }
        return list;
    }

    public async Task<IReadOnlyList<DocumentRecord>> GetFilteredAsync(long projectId, string? level1Filter = null, string? level2Filter = null, string? level3Filter = null, string? fileNameFilter = null, int take = 1000, int skip = 0, CancellationToken cancellationToken = default)
    {
        var list = new List<DocumentRecord>();
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var whereConditions = new List<string> { "ProjectId = @ProjectId" };
        await using var cmd = new NpgsqlCommand();
        cmd.Connection = conn;
        cmd.Parameters.AddWithValue("@ProjectId", projectId);

        if (!string.IsNullOrWhiteSpace(level1Filter))
        {
            whereConditions.Add("Level1 ILIKE @level1");
            cmd.Parameters.AddWithValue("@level1", $"%{level1Filter}%");
        }

        if (!string.IsNullOrWhiteSpace(level2Filter))
        {
            whereConditions.Add("Level2 ILIKE @level2");
            cmd.Parameters.AddWithValue("@level2", $"%{level2Filter}%");
        }

        if (!string.IsNullOrWhiteSpace(level3Filter))
        {
            whereConditions.Add("Level3 ILIKE @level3");
            cmd.Parameters.AddWithValue("@level3", $"%{level3Filter}%");
        }

        if (!string.IsNullOrWhiteSpace(fileNameFilter))
        {
            whereConditions.Add("(FreeText ILIKE @filter OR FileName ILIKE @filter)");
            cmd.Parameters.AddWithValue("@filter", $"%{fileNameFilter}%");
        }

        var whereClause = string.Join(" AND ", whereConditions);
        cmd.CommandText = $@"
            SELECT Id, ProjectId, Level1, Level2, Level3, Level4, Level5, Level6, Number, FreeText, FileName, CreatedByUserId, CreatedAtUtc, OriginalQuery, CodeSeriesId
            FROM Documents 
            WHERE {whereClause}
            ORDER BY Id DESC 
            LIMIT @take
            OFFSET @skip;";
        cmd.Parameters.AddWithValue("@take", take);
        cmd.Parameters.AddWithValue("@skip", skip);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadDocument(reader));
        }
        return list;
    }

    public async Task<int> CountFilteredAsync(long projectId, string? level1Filter = null, string? level2Filter = null, string? level3Filter = null, string? fileNameFilter = null, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var whereConditions = new List<string> { "ProjectId = @ProjectId" };
        await using var cmd = new NpgsqlCommand();
        cmd.Connection = conn;
        cmd.Parameters.AddWithValue("@ProjectId", projectId);

        if (!string.IsNullOrWhiteSpace(level1Filter))
        {
            whereConditions.Add("Level1 ILIKE @level1");
            cmd.Parameters.AddWithValue("@level1", $"%{level1Filter}%");
        }

        if (!string.IsNullOrWhiteSpace(level2Filter))
        {
            whereConditions.Add("Level2 ILIKE @level2");
            cmd.Parameters.AddWithValue("@level2", $"%{level2Filter}%");
        }

        if (!string.IsNullOrWhiteSpace(level3Filter))
        {
            whereConditions.Add("Level3 ILIKE @level3");
            cmd.Parameters.AddWithValue("@level3", $"%{level3Filter}%");
        }

        if (!string.IsNullOrWhiteSpace(fileNameFilter))
        {
            whereConditions.Add("(FreeText ILIKE @filter OR FileName ILIKE @filter)");
            cmd.Parameters.AddWithValue("@filter", $"%{fileNameFilter}%");
        }

        var whereClause = string.Join(" AND ", whereConditions);
        cmd.CommandText = $"SELECT COUNT(*) FROM Documents WHERE {whereClause};";
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result ?? 0);
    }

    public async Task<IReadOnlyList<DocumentRecord>> GetAllAsync(long projectId, CancellationToken cancellationToken = default)
    {
        var list = new List<DocumentRecord>();
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = @"SELECT Id, ProjectId, Level1, Level2, Level3, Level4, Level5, Level6, Number, FreeText, FileName, CreatedByUserId, CreatedAtUtc, OriginalQuery, CodeSeriesId FROM Documents WHERE ProjectId = @ProjectId ORDER BY Level1, Level2, Level3, Level4, Level5, Level6, Number;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ProjectId", projectId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadDocument(reader));
        }
        return list;
    }

    public async Task<DocumentRecord?> GetByIdAsync(long projectId, long id, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        const string sql = @"SELECT Id, ProjectId, Level1, Level2, Level3, Level4, Level5, Level6, Number, FreeText, FileName, CreatedByUserId, CreatedAtUtc, OriginalQuery, CodeSeriesId FROM Documents WHERE ProjectId = @ProjectId AND Id = @id;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ProjectId", projectId);
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReadDocument(reader);
        }
        return null;
    }

    public async Task<int> DeleteAsync(long projectId, long documentId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        const string selectSql = @"
            SELECT CodeSeriesId
            FROM Documents
            WHERE ProjectId = @ProjectId AND Id = @Id
            FOR UPDATE;";
        long? codeSeriesId = null;
        await using (var selectCmd = new NpgsqlCommand(selectSql, conn, (NpgsqlTransaction)tx))
        {
            selectCmd.Parameters.AddWithValue("@ProjectId", projectId);
            selectCmd.Parameters.AddWithValue("@Id", documentId);
            var result = await selectCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is not null and not DBNull)
            {
                codeSeriesId = Convert.ToInt64(result);
            }
        }

        if (codeSeriesId is null)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        const string clearAuditSql = "UPDATE Audit SET DocumentId = NULL WHERE ProjectId = @ProjectId AND DocumentId = @Id;";
        await using (var clearAuditCmd = new NpgsqlCommand(clearAuditSql, conn, (NpgsqlTransaction)tx))
        {
            clearAuditCmd.Parameters.AddWithValue("@ProjectId", projectId);
            clearAuditCmd.Parameters.AddWithValue("@Id", documentId);
            await clearAuditCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        const string deleteSql = @"DELETE FROM Documents WHERE ProjectId = @ProjectId AND Id = @Id;";
        int affected;
        await using (var deleteCmd = new NpgsqlCommand(deleteSql, conn, (NpgsqlTransaction)tx))
        {
            deleteCmd.Parameters.AddWithValue("@ProjectId", projectId);
            deleteCmd.Parameters.AddWithValue("@Id", documentId);
            affected = await deleteCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        const string resetSeriesSql = @"
            UPDATE CodeSeries
            SET NextNumber = COALESCE((
                SELECT MAX(Number) + 1
                FROM Documents
                WHERE ProjectId = @ProjectId AND CodeSeriesId = @CodeSeriesId
            ), 1)
            WHERE ProjectId = @ProjectId AND Id = @CodeSeriesId;";
        await using (var resetSeriesCmd = new NpgsqlCommand(resetSeriesSql, conn, (NpgsqlTransaction)tx))
        {
            resetSeriesCmd.Parameters.AddWithValue("@ProjectId", projectId);
            resetSeriesCmd.Parameters.AddWithValue("@CodeSeriesId", codeSeriesId.Value);
            await resetSeriesCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affected;
    }

    public async Task ClearAllAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        const string delAudit = "DELETE FROM Audit WHERE ProjectId = @ProjectId;";
        await using (var cmdAudit = new NpgsqlCommand(delAudit, conn, (NpgsqlTransaction)tx))
        {
            cmdAudit.Parameters.AddWithValue("@ProjectId", projectId);
            await cmdAudit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        const string delDocs = "DELETE FROM Documents WHERE ProjectId = @ProjectId;";
        await using (var cmdDocs = new NpgsqlCommand(delDocs, conn, (NpgsqlTransaction)tx))
        {
            cmdDocs.Parameters.AddWithValue("@ProjectId", projectId);
            await cmdDocs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        const string resetSeries = "UPDATE CodeSeries SET NextNumber = 1 WHERE ProjectId = @ProjectId;";
        await using (var cmdSeries = new NpgsqlCommand(resetSeries, conn, (NpgsqlTransaction)tx))
        {
            cmdSeries.Parameters.AddWithValue("@ProjectId", projectId);
            await cmdSeries.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertImportedAsync(CodeSeriesKey key, int number, string freeText, string fileName, long createdByUserId, DateTime createdAtUtc, CancellationToken cancellationToken = default)
    {
        var level4 = DbValue.NormalizeLevel(key.Level4);
        var level5 = DbValue.NormalizeLevel(key.Level5);
        var level6 = DbValue.NormalizeLevel(key.Level6);
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = @"
            INSERT INTO Documents (ProjectId, Level1, Level2, Level3, Level4, Level5, Level6, Number, FreeText, FileName, CreatedByUserId, CreatedAtUtc, OriginalQuery, CodeSeriesId)
            VALUES (
                @ProjectId,
                @Level1,
                @Level2,
                @Level3,
                @Level4,
                @Level5,
                @Level6,
                @Number,
                @FreeText,
                @FileName,
                @CreatedByUserId,
                @CreatedAtUtc,
                NULL,
                (SELECT Id FROM CodeSeries WHERE ProjectId = @ProjectId AND Level1 = @Level1 AND Level2 = @Level2 AND Level3 = @Level3 AND (Level4 IS NOT DISTINCT FROM @Level4) AND (Level5 IS NOT DISTINCT FROM @Level5) AND (Level6 IS NOT DISTINCT FROM @Level6) LIMIT 1)
            )
            ON CONFLICT(ProjectId, Level1, Level2, Level3, Level4, Level5, Level6, Number)
            DO UPDATE SET
                FreeText = EXCLUDED.FreeText,
                FileName = EXCLUDED.FileName,
                CreatedByUserId = EXCLUDED.CreatedByUserId,
                CreatedAtUtc = EXCLUDED.CreatedAtUtc;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ProjectId", key.ProjectId);
        cmd.Parameters.AddWithValue("@Level1", key.Level1);
        cmd.Parameters.AddWithValue("@Level2", key.Level2);
        cmd.Parameters.AddWithValue("@Level3", key.Level3);
        cmd.Parameters.AddWithValue("@Level4", level4);
        cmd.Parameters.AddWithValue("@Level5", level5);
        cmd.Parameters.AddWithValue("@Level6", level6);
        cmd.Parameters.AddWithValue("@Number", number);
        cmd.Parameters.AddWithValue("@FreeText", (object?)freeText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FileName", fileName);
        cmd.Parameters.AddWithValue("@CreatedByUserId", createdByUserId);
        cmd.Parameters.AddWithValue("@CreatedAtUtc", createdAtUtc);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DocumentRecord ReadDocument(NpgsqlDataReader reader) =>
        new()
        {
            Id = reader.GetInt64(0),
            ProjectId = reader.GetInt64(1),
            Level1 = reader.GetString(2),
            Level2 = reader.GetString(3),
            Level3 = reader.GetString(4),
            Level4 = reader.IsDBNull(5) ? null : DbValue.NormalizeRead(reader.GetString(5)),
            Level5 = reader.IsDBNull(6) ? null : DbValue.NormalizeRead(reader.GetString(6)),
            Level6 = reader.IsDBNull(7) ? null : DbValue.NormalizeRead(reader.GetString(7)),
            Number = reader.GetInt32(8),
            FreeText = reader.IsDBNull(9) ? null : reader.GetString(9),
            FileName = reader.GetString(10),
            CreatedByUserId = reader.GetInt64(11),
            CreatedAtUtc = reader.GetDateTime(12),
            OriginalQuery = reader.IsDBNull(13) ? null : reader.GetString(13),
            CodeSeriesId = reader.GetInt64(14)
        };
}
