using DocControl.Core.Models;
using Npgsql;
using System.Data;

namespace DocControl.Infrastructure.Data;

/// <summary>
/// Allocates document numbers within a code series using a smallest-free-slot strategy
/// (gap filling) instead of monotonic <c>MAX+1</c>. Concurrency safety comes from
/// (a) Serializable isolation, (b) a row-level <c>SELECT ... FOR UPDATE</c> on the
/// matching <c>CodeSeries</c> row, and (c) the existing
/// <c>UNIQUE(ProjectId, Level1..Level6, Number)</c> index on <c>Documents</c> as a hard
/// backstop. A small retry loop absorbs the rare serialization / unique-violation races.
/// </summary>
public sealed class NumberAllocator
{
    private const int MaxAttempts = 5;
    private const string UniqueViolationSqlState = "23505";
    private const string SerializationFailureSqlState = "40001";
    private const string DeadlockDetectedSqlState = "40P01";

    private readonly DbConnectionFactory factory;

    public NumberAllocator(DbConnectionFactory factory)
    {
        this.factory = factory;
    }

    public async Task<AllocatedNumber> AllocateAsync(CodeSeriesKey key, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AllocateCoreAsync(key, mutateSeries: true, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (IsRetriable(ex) && attempt < MaxAttempts)
            {
                // Tiny randomized backoff to break ties across concurrent allocators.
                var jitterMs = Random.Shared.Next(0, 25);
                await Task.Delay(jitterMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<int> PeekNextAsync(CodeSeriesKey key, CancellationToken cancellationToken = default)
    {
        // Peek never mutates the series; retries are not necessary but cheap to keep symmetric.
        var allocated = await AllocateCoreAsync(key, mutateSeries: false, cancellationToken).ConfigureAwait(false);
        return allocated.Number;
    }

    private async Task<AllocatedNumber> AllocateCoreAsync(CodeSeriesKey key, bool mutateSeries, CancellationToken cancellationToken)
    {
        var level4 = DbValue.NormalizeLevel(key.Level4);
        var level5 = DbValue.NormalizeLevel(key.Level5);
        var level6 = DbValue.NormalizeLevel(key.Level6);
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        // Ensure series exists for this project/key. This INSERT is also the one that
        // races on first-touch; ON CONFLICT DO NOTHING means a parallel inserter just
        // no-ops and we both fall through to the row-locking SELECT below.
        const string ensureSql = @"
            INSERT INTO CodeSeries (ProjectId, Level1, Level2, Level3, Level4, Level5, Level6, NextNumber)
            VALUES (@ProjectId, @Level1, @Level2, @Level3, @Level4, @Level5, @Level6, 1)
            ON CONFLICT(ProjectId, Level1, Level2, Level3, Level4, Level5, Level6) DO NOTHING;";
        await using (var ensureCmd = new NpgsqlCommand(ensureSql, conn, (NpgsqlTransaction)tx))
        {
            ensureCmd.Parameters.AddWithValue("@ProjectId", key.ProjectId);
            ensureCmd.Parameters.AddWithValue("@Level1", key.Level1);
            ensureCmd.Parameters.AddWithValue("@Level2", key.Level2);
            ensureCmd.Parameters.AddWithValue("@Level3", key.Level3);
            ensureCmd.Parameters.AddWithValue("@Level4", level4);
            ensureCmd.Parameters.AddWithValue("@Level5", level5);
            ensureCmd.Parameters.AddWithValue("@Level6", level6);
            await ensureCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Lock the series row for the duration of the transaction. Two parallel
        // allocators for the SAME (ProjectId, L1..L6) tuple will queue here, which
        // is what makes the gap-fill race-free.
        const string lockSql = @"
            SELECT Id, NextNumber FROM CodeSeries
            WHERE ProjectId = @ProjectId AND Level1 = @Level1 AND Level2 = @Level2 AND Level3 = @Level3
              AND (Level4 IS NOT DISTINCT FROM @Level4)
              AND (Level5 IS NOT DISTINCT FROM @Level5)
              AND (Level6 IS NOT DISTINCT FROM @Level6)
            FOR UPDATE;";

        long seriesId;
        int nextNumberHint;
        await using (var lockCmd = new NpgsqlCommand(lockSql, conn, (NpgsqlTransaction)tx))
        {
            lockCmd.Parameters.AddWithValue("@ProjectId", key.ProjectId);
            lockCmd.Parameters.AddWithValue("@Level1", key.Level1);
            lockCmd.Parameters.AddWithValue("@Level2", key.Level2);
            lockCmd.Parameters.AddWithValue("@Level3", key.Level3);
            lockCmd.Parameters.AddWithValue("@Level4", level4);
            lockCmd.Parameters.AddWithValue("@Level5", level5);
            lockCmd.Parameters.AddWithValue("@Level6", level6);

            await using var reader = await lockCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Failed to load code series after insert.");
            }
            seriesId = reader.GetInt64(0);
            nextNumberHint = reader.GetInt32(1);
        }

        // Read every Number already used in this series, ordered ascending. The set
        // is small in practice (project-level, not table-level) so materialising is
        // fine. Walking it is O(n) and avoids the deadband that the previous MAX+1
        // strategy left behind when an old record was deleted.
        const string numbersSql = @"
            SELECT Number FROM Documents
            WHERE ProjectId = @ProjectId AND Level1 = @Level1 AND Level2 = @Level2 AND Level3 = @Level3
              AND (Level4 IS NOT DISTINCT FROM @Level4)
              AND (Level5 IS NOT DISTINCT FROM @Level5)
              AND (Level6 IS NOT DISTINCT FROM @Level6)
            ORDER BY Number ASC;";

        var used = new HashSet<int>();
        var maxUsed = 0;
        await using (var numbersCmd = new NpgsqlCommand(numbersSql, conn, (NpgsqlTransaction)tx))
        {
            numbersCmd.Parameters.AddWithValue("@ProjectId", key.ProjectId);
            numbersCmd.Parameters.AddWithValue("@Level1", key.Level1);
            numbersCmd.Parameters.AddWithValue("@Level2", key.Level2);
            numbersCmd.Parameters.AddWithValue("@Level3", key.Level3);
            numbersCmd.Parameters.AddWithValue("@Level4", level4);
            numbersCmd.Parameters.AddWithValue("@Level5", level5);
            numbersCmd.Parameters.AddWithValue("@Level6", level6);

            await using var reader = await numbersCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var n = reader.GetInt32(0);
                used.Add(n);
                if (n > maxUsed) maxUsed = n;
            }
        }

        var allocatedNumber = FindSmallestFreeSlot(used, maxUsed, nextNumberHint);

        if (mutateSeries)
        {
            // Keep CodeSeries.NextNumber as a high-water mark for read-only peek callers
            // and for the importer's seed logic. The unique index on Documents is the
            // real correctness guarantee; NextNumber is advisory only.
            var newNext = Math.Max(allocatedNumber + 1, Math.Max(maxUsed + 1, nextNumberHint));
            const string updateSql = @"UPDATE CodeSeries SET NextNumber = @newNext WHERE Id = @id;";
            await using var updateCmd = new NpgsqlCommand(updateSql, conn, (NpgsqlTransaction)tx);
            updateCmd.Parameters.AddWithValue("@id", seriesId);
            updateCmd.Parameters.AddWithValue("@newNext", newNext);
            await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new AllocatedNumber(seriesId, allocatedNumber);
    }

    /// <summary>
    /// Returns the smallest positive integer that is not present in <paramref name="used"/>.
    /// If every slot in [1, maxUsed] is taken, returns maxUsed + 1 (extends the range).
    /// </summary>
    private static int FindSmallestFreeSlot(HashSet<int> used, int maxUsed, int nextNumberHint)
    {
        // Scan from 1 upward. Worst case is O(n) and n is per-series.
        for (var candidate = 1; candidate <= maxUsed; candidate++)
        {
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
        return Math.Max(maxUsed + 1, 1);
    }

    private static bool IsRetriable(PostgresException ex)
    {
        return ex.SqlState == SerializationFailureSqlState
            || ex.SqlState == DeadlockDetectedSqlState
            || ex.SqlState == UniqueViolationSqlState;
    }
}
