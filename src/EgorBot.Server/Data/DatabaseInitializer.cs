using Microsoft.EntityFrameworkCore;

namespace EgorBot.Server.Data;

/// <summary>
/// Creates a new database and applies idempotent schema upgrades to existing
/// SQLite databases. EnsureCreated does not add tables or columns after the
/// database file already exists.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(
        AppDbContext db,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken);

        (string Table, string Column, string Type)[] addedColumns =
        [
            ("Jobs", "PerfStatEvents", "TEXT NULL"),
            ("Jobs", "UseGcProfiler", "INTEGER NOT NULL DEFAULT 0"),
            // NOT NULL + default: the column is read back into a non-nullable enum,
            // so pre-existing rows must not be left as NULL.
            ("Jobs", "Kind", "TEXT NOT NULL DEFAULT 'Bdn'"),
        ];

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        foreach (var (table, column, type) in addedColumns)
        {
            if (await ColumnExistsAsync(connection, table, column, cancellationToken))
                continue;

            // Values are compile-time constants from the list above, not user input.
            var alterSql = "ALTER TABLE " + table + " ADD COLUMN " + column + " " + type + ";";
            await db.Database.ExecuteSqlRawAsync(alterSql, cancellationToken);
            logger.LogWarning("Added missing column {Table}.{Column} to the existing database", table, column);
        }

        var hadAdmissions = await TableExistsAsync(connection, "JobAdmissions", cancellationToken);
        var hadUserLimits = await TableExistsAsync(connection, "UserJobLimits", cancellationToken);
        var hadRequestLimit = await TableExistsAsync(connection, "JobRequestLimits", cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "JobAdmissions" (
                "JobId" TEXT NOT NULL CONSTRAINT "PK_JobAdmissions" PRIMARY KEY,
                "UserKey" TEXT COLLATE NOCASE NOT NULL,
                "AdmittedAt" TEXT NOT NULL,
                CONSTRAINT "FK_JobAdmissions_Jobs_JobId"
                    FOREIGN KEY ("JobId") REFERENCES "Jobs" ("Id") ON DELETE CASCADE
            );
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS "IX_JobAdmissions_UserKey_AdmittedAt"
                ON "JobAdmissions" ("UserKey", "AdmittedAt");
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "UserJobLimits" (
                "UserKey" TEXT COLLATE NOCASE NOT NULL CONSTRAINT "PK_UserJobLimits" PRIMARY KEY,
                "MaxJobs" INTEGER NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "JobRequestLimits" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_JobRequestLimits" PRIMARY KEY,
                "MaxJobs" INTEGER NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "CK_JobRequestLimits_Singleton" CHECK ("Id" = 1)
            );
            """,
            cancellationToken);

        // Existing accepted jobs must count immediately after deployment; otherwise
        // restarting the upgraded service would grant every user a fresh window.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT OR IGNORE INTO "JobAdmissions" ("JobId", "UserKey", "AdmittedAt")
            SELECT
                "Id",
                CASE
                    WHEN "RequestedBy" IS NULL OR trim("RequestedBy") = '' THEN '(anonymous)'
                    ELSE lower(ltrim(trim("RequestedBy"), '@'))
                END,
                "CreatedAt"
            FROM "Jobs";
            """,
            cancellationToken);

        if (!hadAdmissions)
            logger.LogWarning("Created JobAdmissions and backfilled existing jobs for rolling rate limits");
        if (!hadUserLimits)
            logger.LogWarning("Created UserJobLimits for persistent per-user rate-limit overrides");
        if (!hadRequestLimit)
            logger.LogWarning("Created JobRequestLimits for the persistent per-request limit override");
    }

    private static async Task<bool> TableExistsAsync(
        System.Data.Common.DbConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> ColumnExistsAsync(
        System.Data.Common.DbConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // Table names are compile-time constants from addedColumns.
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
