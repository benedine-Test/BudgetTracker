using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Data;

/// <summary>
/// EnsureCreated builds a fresh database but never changes one that already exists, so a
/// live database from before accounts were added is missing them. These steps bring it up
/// to date on startup. Each one checks first, so running them again does nothing.
/// The SQL matches what EF itself generates for the model (see BudgetDbContext).
/// </summary>
public static class SchemaUpgrade
{
    public static async Task ApplyAsync(BudgetDbContext db, CancellationToken ct = default)
    {
        var sqlServer = db.Database.IsSqlServer();
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            // ---- Bank accounts (Accounts table + Transactions.AccountId) ----
            if (!await TableExistsAsync(conn, "Accounts", sqlServer, ct))
            {
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? """
                      CREATE TABLE [Accounts] (
                          [Id] int NOT NULL IDENTITY,
                          [Name] nvarchar(100) NOT NULL,
                          [Kind] int NOT NULL,
                          [Currency] nvarchar(3) NOT NULL,
                          [AnchorBalance] decimal(18,2) NOT NULL,
                          [BalanceAsOfUtc] datetime2 NOT NULL,
                          [CardNames] nvarchar(500) NULL,
                          [IsArchived] bit NOT NULL,
                          [LastImportAtUtc] datetime2 NULL,
                          CONSTRAINT [PK_Accounts] PRIMARY KEY ([Id])
                      );
                      """
                    : """
                      CREATE TABLE "Accounts" (
                          "Id" INTEGER NOT NULL CONSTRAINT "PK_Accounts" PRIMARY KEY AUTOINCREMENT,
                          "Name" TEXT NOT NULL,
                          "Kind" INTEGER NOT NULL,
                          "Currency" TEXT NOT NULL,
                          "AnchorBalance" TEXT NOT NULL,
                          "BalanceAsOfUtc" TEXT NOT NULL,
                          "CardNames" TEXT NULL,
                          "IsArchived" INTEGER NOT NULL,
                          "LastImportAtUtc" TEXT NULL
                      );
                      """, ct);
            }

            if (!await ColumnExistsAsync(conn, "Transactions", "AccountId", sqlServer, ct))
            {
                if (sqlServer)
                {
                    await db.Database.ExecuteSqlRawAsync("ALTER TABLE [Transactions] ADD [AccountId] int NULL;", ct);
                    await db.Database.ExecuteSqlRawAsync(
                        "ALTER TABLE [Transactions] ADD CONSTRAINT [FK_Transactions_Accounts_AccountId] " +
                        "FOREIGN KEY ([AccountId]) REFERENCES [Accounts] ([Id]) ON DELETE SET NULL;", ct);
                    await db.Database.ExecuteSqlRawAsync(
                        "CREATE INDEX [IX_Transactions_AccountId] ON [Transactions] ([AccountId]);", ct);
                }
                else
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "ALTER TABLE \"Transactions\" ADD COLUMN \"AccountId\" INTEGER NULL " +
                        "CONSTRAINT \"FK_Transactions_Accounts_AccountId\" REFERENCES \"Accounts\" (\"Id\") ON DELETE SET NULL;", ct);
                    await db.Database.ExecuteSqlRawAsync(
                        "CREATE INDEX \"IX_Transactions_AccountId\" ON \"Transactions\" (\"AccountId\");", ct);
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> TableExistsAsync(DbConnection conn, string table, bool sqlServer, CancellationToken ct) =>
        await ScalarAsync(conn, sqlServer
            ? "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @p"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @p", ct, table) > 0;

    private static async Task<bool> ColumnExistsAsync(DbConnection conn, string table, string column, bool sqlServer, CancellationToken ct) =>
        await ScalarAsync(conn, sqlServer
            ? "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @t AND COLUMN_NAME = @p"
            : "SELECT COUNT(*) FROM pragma_table_info(@t) WHERE name = @p", ct, column, table) > 0;

    private static async Task<long> ScalarAsync(DbConnection conn, string sql, CancellationToken ct, string p, string? t = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddParam(cmd, "@p", p);
        if (t is not null) AddParam(cmd, "@t", t);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static void AddParam(DbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
