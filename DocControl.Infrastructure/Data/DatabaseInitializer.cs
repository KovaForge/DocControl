using Npgsql;

namespace DocControl.Infrastructure.Data;

public sealed class DatabaseInitializer
{
    private readonly DbConnectionFactory factory;

    public DatabaseInitializer(DbConnectionFactory factory)
    {
        this.factory = factory;
    }

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Basic schema aligned to Neon/PostgreSQL. Serial types are fine for serverless.
        var sql = @"
        CREATE TABLE IF NOT EXISTS Users (
            Id BIGSERIAL PRIMARY KEY,
            Email TEXT NOT NULL UNIQUE,
            DisplayName TEXT NOT NULL,
            OpenAiKeyEncrypted TEXT,
            GeminiKeyEncrypted TEXT,
            PasswordHash TEXT,
            PasswordSalt TEXT,
            KeySalt TEXT,
            LegacyEmail TEXT,
            CreatedAtUtc TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS UserAuth (
            UserId BIGINT PRIMARY KEY REFERENCES Users(Id),
            ProviderSubject TEXT NOT NULL,
            ProviderName TEXT NOT NULL,
            MfaEnabled BOOLEAN NOT NULL DEFAULT FALSE,
            MfaMethodsJson TEXT,
            LastLoginUtc TIMESTAMPTZ
        );

        CREATE TABLE IF NOT EXISTS Projects (
            Id BIGSERIAL PRIMARY KEY,
            Name TEXT NOT NULL,
            Description TEXT NOT NULL,
            Separator TEXT NOT NULL DEFAULT '-',
            PaddingLength INTEGER NOT NULL DEFAULT 3,
            LevelCount INTEGER NOT NULL DEFAULT 3,
            Level1Label TEXT NOT NULL DEFAULT 'Level1',
            Level2Label TEXT NOT NULL DEFAULT 'Level2',
            Level3Label TEXT NOT NULL DEFAULT 'Level3',
            Level4Label TEXT NOT NULL DEFAULT 'Level4',
            Level5Label TEXT NOT NULL DEFAULT 'Level5',
            Level6Label TEXT NOT NULL DEFAULT 'Level6',
            Level1Length INTEGER NOT NULL DEFAULT 3,
            Level2Length INTEGER NOT NULL DEFAULT 3,
            Level3Length INTEGER NOT NULL DEFAULT 3,
            Level4Length INTEGER NOT NULL DEFAULT 3,
            Level5Length INTEGER NOT NULL DEFAULT 3,
            Level6Length INTEGER NOT NULL DEFAULT 3,
            CreatedByUserId BIGINT NOT NULL REFERENCES Users(Id),
            CreatedAtUtc TIMESTAMPTZ NOT NULL DEFAULT now(),
            IsArchived BOOLEAN NOT NULL DEFAULT FALSE
        );

        CREATE TABLE IF NOT EXISTS ProjectMembers (
            Id BIGSERIAL PRIMARY KEY,
            ProjectId BIGINT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            UserId BIGINT NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
            Role TEXT NOT NULL,
            IsDefault BOOLEAN NOT NULL DEFAULT FALSE,
            AddedByUserId BIGINT NOT NULL REFERENCES Users(Id),
            AddedAtUtc TIMESTAMPTZ NOT NULL DEFAULT now(),
            UNIQUE(ProjectId, UserId)
        );

        CREATE TABLE IF NOT EXISTS ProjectInvites (
            Id BIGSERIAL PRIMARY KEY,
            ProjectId BIGINT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            InvitedEmail TEXT NOT NULL,
            Role TEXT NOT NULL,
            InviteTokenHash TEXT NOT NULL,
            InviteToken TEXT,
            ExpiresAtUtc TIMESTAMPTZ NOT NULL,
            CreatedByUserId BIGINT NOT NULL REFERENCES Users(Id),
            CreatedAtUtc TIMESTAMPTZ NOT NULL DEFAULT now(),
            AcceptedByUserId BIGINT,
            AcceptedAtUtc TIMESTAMPTZ,
            UNIQUE(ProjectId, InvitedEmail, Role)
        );

        -- Plaintext token column retained for legacy compatibility. New invites only store hashes.
        ALTER TABLE ProjectInvites ADD COLUMN IF NOT EXISTS InviteToken TEXT;
        ALTER TABLE Users ADD COLUMN IF NOT EXISTS OpenAiKeyEncrypted TEXT;
        ALTER TABLE Users ADD COLUMN IF NOT EXISTS GeminiKeyEncrypted TEXT;
        ALTER TABLE Users ADD COLUMN IF NOT EXISTS PasswordHash TEXT;
        ALTER TABLE Users ADD COLUMN IF NOT EXISTS PasswordSalt TEXT;
        ALTER TABLE Users ADD COLUMN IF NOT EXISTS KeySalt TEXT;
        ALTER TABLE Users ADD COLUMN IF NOT EXISTS LegacyEmail TEXT;
        ALTER TABLE Projects ADD COLUMN IF NOT EXISTS Separator TEXT NOT NULL DEFAULT '-';
        ALTER TABLE Projects ADD COLUMN IF NOT EXISTS PaddingLength INTEGER NOT NULL DEFAULT 3;
        ALTER TABLE Projects ADD COLUMN IF NOT EXISTS LevelCount INTEGER NOT NULL DEFAULT 3;
        ALTER TABLE Projects ADD COLUMN IF NOT EXISTS Level1Label TEXT NOT NULL DEFAULT 'Level1';
        ALTER TABLE Projects ADD COLUMN IF NOT EXISTS Level2Label TEXT NOT NULL DEFAULT 'Level2';
        ALTER TABLE Projects ADD COLUMN IF NOT EXISTS Level3Label TEXT NOT NULL DEFAULT 'Level3';
        ALTER TABLE Projects ADD COLUMN IF NOT EXISTS Level4Label TEXT NOT NULL DEFAULT 'Level4';
        ALTER TABLE Projects ADD COLUMN IF NOT EXISTS Level5Label TEXT NOT NULL DEFAULT 'Level5';
        ALTER TABLE Projects ADD COLUMN IF NOT EXISTS Level6Label TEXT NOT NULL DEFAULT 'Level6';
        ALTER TABLE Projects ADD COLUMN IF NOT EXISTS Level1Length INTEGER NOT NULL DEFAULT 3;
        ALTER TABLE Projects ADD COLUMN IF NOT EXISTS Level2Length INTEGER NOT NULL DEFAULT 3;
        ALTER TABLE Projects ADD COLUMN IF NOT EXISTS Level3Length INTEGER NOT NULL DEFAULT 3;
        ALTER TABLE Projects ADD COLUMN IF NOT EXISTS Level4Length INTEGER NOT NULL DEFAULT 3;
        ALTER TABLE Projects ADD COLUMN IF NOT EXISTS Level5Length INTEGER NOT NULL DEFAULT 3;
        ALTER TABLE Projects ADD COLUMN IF NOT EXISTS Level6Length INTEGER NOT NULL DEFAULT 3;
        ALTER TABLE ProjectMembers ADD COLUMN IF NOT EXISTS IsDefault BOOLEAN NOT NULL DEFAULT FALSE;
        ALTER TABLE CodeSeries ADD COLUMN IF NOT EXISTS Level5 TEXT;
        ALTER TABLE CodeSeries ADD COLUMN IF NOT EXISTS Level6 TEXT;
        ALTER TABLE Documents ADD COLUMN IF NOT EXISTS Level5 TEXT;
        ALTER TABLE Documents ADD COLUMN IF NOT EXISTS Level6 TEXT;
        ALTER TABLE CodeSeries DROP CONSTRAINT IF EXISTS codeseries_projectid_level1_level2_level3_level4_key;
        ALTER TABLE Documents DROP CONSTRAINT IF EXISTS documents_projectid_level1_level2_level3_level4_number_key;

        DROP INDEX IF EXISTS IX_CodeSeries_Unique;
        DROP INDEX IF EXISTS IX_Documents_Unique;

        WITH normalized AS (
            SELECT Id,
                   ProjectId,
                   Level1,
                   Level2,
                   Level3,
                   COALESCE(Level4, '') AS Level4N,
                   COALESCE(Level5, '') AS Level5N,
                   COALESCE(Level6, '') AS Level6N,
                   ROW_NUMBER() OVER (PARTITION BY ProjectId, Level1, Level2, Level3, COALESCE(Level4,''), COALESCE(Level5,''), COALESCE(Level6,'') ORDER BY Id) AS rn,
                   MIN(Id) OVER (PARTITION BY ProjectId, Level1, Level2, Level3, COALESCE(Level4,''), COALESCE(Level5,''), COALESCE(Level6,'')) AS keep_id,
                   MAX(NextNumber) OVER (PARTITION BY ProjectId, Level1, Level2, Level3, COALESCE(Level4,''), COALESCE(Level5,''), COALESCE(Level6,'')) AS max_next,
                   MAX(NULLIF(Description, '')) OVER (PARTITION BY ProjectId, Level1, Level2, Level3, COALESCE(Level4,''), COALESCE(Level5,''), COALESCE(Level6,'')) AS max_desc
            FROM CodeSeries
        ),
        update_docs AS (
            UPDATE Documents d
            SET CodeSeriesId = n.keep_id
            FROM normalized n
            WHERE d.CodeSeriesId = n.Id
        )
        UPDATE CodeSeries c
        SET Level4 = n.Level4N,
            Level5 = n.Level5N,
            Level6 = n.Level6N,
            NextNumber = n.max_next,
            Description = COALESCE(n.max_desc, c.Description)
        FROM normalized n
        WHERE c.Id = n.Id;

        DELETE FROM CodeSeries c
        USING (
            SELECT Id,
                   ROW_NUMBER() OVER (PARTITION BY ProjectId, Level1, Level2, Level3, COALESCE(Level4,''), COALESCE(Level5,''), COALESCE(Level6,'') ORDER BY Id) AS rn
            FROM CodeSeries
        ) d
        WHERE c.Id = d.Id AND d.rn > 1;

        UPDATE Documents
        SET Level4 = COALESCE(Level4, ''),
            Level5 = COALESCE(Level5, ''),
            Level6 = COALESCE(Level6, '')
        WHERE Level4 IS NULL OR Level5 IS NULL OR Level6 IS NULL;

        ALTER TABLE CodeSeries ALTER COLUMN Level4 SET DEFAULT '';
        ALTER TABLE CodeSeries ALTER COLUMN Level5 SET DEFAULT '';
        ALTER TABLE CodeSeries ALTER COLUMN Level6 SET DEFAULT '';
        ALTER TABLE CodeSeries ALTER COLUMN Level4 SET NOT NULL;
        ALTER TABLE CodeSeries ALTER COLUMN Level5 SET NOT NULL;
        ALTER TABLE CodeSeries ALTER COLUMN Level6 SET NOT NULL;

        ALTER TABLE Documents ALTER COLUMN Level4 SET DEFAULT '';
        ALTER TABLE Documents ALTER COLUMN Level5 SET DEFAULT '';
        ALTER TABLE Documents ALTER COLUMN Level6 SET DEFAULT '';
        ALTER TABLE Documents ALTER COLUMN Level4 SET NOT NULL;
        ALTER TABLE Documents ALTER COLUMN Level5 SET NOT NULL;
        ALTER TABLE Documents ALTER COLUMN Level6 SET NOT NULL;

        CREATE UNIQUE INDEX IF NOT EXISTS IX_CodeSeries_Unique ON CodeSeries(ProjectId, Level1, Level2, Level3, Level4, Level5, Level6);
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Documents_Unique ON Documents(ProjectId, Level1, Level2, Level3, Level4, Level5, Level6, Number);

        CREATE UNIQUE INDEX IF NOT EXISTS IX_ProjectMembers_DefaultProject
            ON ProjectMembers(UserId)
            WHERE IsDefault = TRUE;

        CREATE TABLE IF NOT EXISTS Config (
            Id BIGSERIAL PRIMARY KEY,
            ScopeType TEXT NOT NULL, -- Global|Project
            ProjectId BIGINT,
            Key TEXT NOT NULL,
            Value TEXT NOT NULL,
            UNIQUE(ScopeType, ProjectId, Key)
        );

        CREATE TABLE IF NOT EXISTS CodeSeries (
            Id BIGSERIAL PRIMARY KEY,
            ProjectId BIGINT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            Level1 TEXT NOT NULL,
            Level2 TEXT NOT NULL,
            Level3 TEXT NOT NULL,
            Level4 TEXT NOT NULL DEFAULT '',
            Level5 TEXT NOT NULL DEFAULT '',
            Level6 TEXT NOT NULL DEFAULT '',
            Description TEXT,
            NextNumber INTEGER NOT NULL DEFAULT 1,
            UNIQUE(ProjectId, Level1, Level2, Level3, Level4, Level5, Level6)
        );

        CREATE TABLE IF NOT EXISTS CodeCatalog (
            Id BIGSERIAL PRIMARY KEY,
            ProjectId BIGINT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            Level1 TEXT NOT NULL,
            Level2 TEXT NOT NULL,
            Level3 TEXT NOT NULL,
            Level4 TEXT NOT NULL DEFAULT '',
            Level5 TEXT NOT NULL DEFAULT '',
            Level6 TEXT NOT NULL DEFAULT '',
            Description TEXT,
            UNIQUE(ProjectId, Level1, Level2, Level3, Level4, Level5, Level6)
        );

        CREATE TABLE IF NOT EXISTS LevelCodes (
            Id BIGSERIAL PRIMARY KEY,
            ProjectId BIGINT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            Level INTEGER NOT NULL CHECK (Level >= 1 AND Level <= 6),
            Code TEXT NOT NULL,
            Description TEXT,
            CreatedAtUtc TIMESTAMPTZ NOT NULL DEFAULT now(),
            UpdatedAtUtc TIMESTAMPTZ NOT NULL DEFAULT now(),
            UNIQUE(ProjectId, Level, Code)
        );

        INSERT INTO LevelCodes (ProjectId, Level, Code, Description)
        SELECT DISTINCT ProjectId, Level, Code, Description
        FROM (
            SELECT ProjectId, 1 AS Level, Level1 AS Code, Description FROM CodeCatalog WHERE Level1 <> ''
            UNION ALL SELECT ProjectId, 2, Level2, Description FROM CodeCatalog WHERE Level2 <> ''
            UNION ALL SELECT ProjectId, 3, Level3, Description FROM CodeCatalog WHERE Level3 <> ''
            UNION ALL SELECT ProjectId, 4, Level4, Description FROM CodeCatalog WHERE Level4 <> ''
            UNION ALL SELECT ProjectId, 5, Level5, Description FROM CodeCatalog WHERE Level5 <> ''
            UNION ALL SELECT ProjectId, 6, Level6, Description FROM CodeCatalog WHERE Level6 <> ''
        ) source
        ON CONFLICT(ProjectId, Level, Code) DO NOTHING;

        CREATE TABLE IF NOT EXISTS Documents (
            Id BIGSERIAL PRIMARY KEY,
            ProjectId BIGINT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            Level1 TEXT NOT NULL,
            Level2 TEXT NOT NULL,
            Level3 TEXT NOT NULL,
            Level4 TEXT NOT NULL DEFAULT '',
            Level5 TEXT NOT NULL DEFAULT '',
            Level6 TEXT NOT NULL DEFAULT '',
            Number INTEGER NOT NULL,
            FreeText TEXT,
            FileName TEXT NOT NULL,
            CreatedByUserId BIGINT NOT NULL REFERENCES Users(Id),
            CreatedAtUtc TIMESTAMPTZ NOT NULL DEFAULT now(),
            OriginalQuery TEXT,
            CodeSeriesId BIGINT NOT NULL REFERENCES CodeSeries(Id),
            UNIQUE(ProjectId, Level1, Level2, Level3, Level4, Level5, Level6, Number)
        );

        CREATE TABLE IF NOT EXISTS Audit (
            Id BIGSERIAL PRIMARY KEY,
            ProjectId BIGINT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            Action TEXT NOT NULL,
            Payload TEXT,
            CreatedByUserId BIGINT NOT NULL REFERENCES Users(Id),
            CreatedAtUtc TIMESTAMPTZ NOT NULL DEFAULT now(),
            DocumentId BIGINT REFERENCES Documents(Id)
        );

        CREATE INDEX IF NOT EXISTS IX_Documents_ProjectId ON Documents(ProjectId);
        CREATE INDEX IF NOT EXISTS IX_Documents_Search ON Documents(ProjectId, Level1, Level2, Level3, Level4, Level5, Level6, Number);
        CREATE INDEX IF NOT EXISTS IX_Audit_ProjectId ON Audit(ProjectId, CreatedAtUtc DESC);
        CREATE INDEX IF NOT EXISTS IX_LevelCodes_ProjectLevel ON LevelCodes(ProjectId, Level, Code);
        ";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
