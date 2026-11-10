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

            // ---- Undoable statement imports (ImportBatches table + Transactions.ImportBatchId) ----
            if (!await TableExistsAsync(conn, "ImportBatches", sqlServer, ct))
            {
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? """
                      CREATE TABLE [ImportBatches] (
                          [Id] int NOT NULL IDENTITY,
                          [AccountId] int NOT NULL,
                          [CreatedAtUtc] datetime2 NOT NULL,
                          [FileName] nvarchar(200) NULL,
                          [Added] int NOT NULL,
                          [LinkedIds] nvarchar(max) NULL,
                          [ResetBalance] bit NOT NULL,
                          [PreviousAnchorBalance] decimal(18,2) NOT NULL,
                          [PreviousBalanceAsOfUtc] datetime2 NOT NULL,
                          [SetBalanceAsOfUtc] datetime2 NULL,
                          [SetAnchorBalance] decimal(18,2) NULL,
                          [Recategorised] nvarchar(max) NULL,
                          [PreviousLastImportAtUtc] datetime2 NULL,
                          [UndoneAtUtc] datetime2 NULL,
                          CONSTRAINT [PK_ImportBatches] PRIMARY KEY ([Id])
                      );
                      """
                    : """
                      CREATE TABLE "ImportBatches" (
                          "Id" INTEGER NOT NULL CONSTRAINT "PK_ImportBatches" PRIMARY KEY AUTOINCREMENT,
                          "AccountId" INTEGER NOT NULL,
                          "CreatedAtUtc" TEXT NOT NULL,
                          "FileName" TEXT NULL,
                          "Added" INTEGER NOT NULL,
                          "LinkedIds" TEXT NULL,
                          "ResetBalance" INTEGER NOT NULL,
                          "PreviousAnchorBalance" TEXT NOT NULL,
                          "PreviousBalanceAsOfUtc" TEXT NOT NULL,
                          "SetBalanceAsOfUtc" TEXT NULL,
                          "SetAnchorBalance" TEXT NULL,
                          "Recategorised" TEXT NULL,
                          "PreviousLastImportAtUtc" TEXT NULL,
                          "UndoneAtUtc" TEXT NULL
                      );
                      """, ct);
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "CREATE INDEX [IX_ImportBatches_AccountId] ON [ImportBatches] ([AccountId]);"
                    : "CREATE INDEX \"IX_ImportBatches_AccountId\" ON \"ImportBatches\" (\"AccountId\");", ct);
            }

            // Added after the table first shipped: a database upgraded in between has the table without it.
            if (!await ColumnExistsAsync(conn, "ImportBatches", "Recategorised", sqlServer, ct))
            {
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "ALTER TABLE [ImportBatches] ADD [Recategorised] nvarchar(max) NULL;"
                    : "ALTER TABLE \"ImportBatches\" ADD COLUMN \"Recategorised\" TEXT NULL;", ct);
            }

            if (!await ColumnExistsAsync(conn, "Transactions", "ImportBatchId", sqlServer, ct))
            {
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "ALTER TABLE [Transactions] ADD [ImportBatchId] int NULL;"
                    : "ALTER TABLE \"Transactions\" ADD COLUMN \"ImportBatchId\" INTEGER NULL;", ct);
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "CREATE INDEX [IX_Transactions_ImportBatchId] ON [Transactions] ([ImportBatchId]);"
                    : "CREATE INDEX \"IX_Transactions_ImportBatchId\" ON \"Transactions\" (\"ImportBatchId\");", ct);
            }

            // ---- CPF paid on a salary, added to the CPF holdings ----
            if (!await ColumnExistsAsync(conn, "Transactions", "CpfContribution", sqlServer, ct))
            {
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "ALTER TABLE [Transactions] ADD [CpfContribution] decimal(18,2) NULL;"
                    : "ALTER TABLE \"Transactions\" ADD COLUMN \"CpfContribution\" TEXT NULL;", ct);
            }

            // ---- Credit card statement day ----
            if (!await ColumnExistsAsync(conn, "Accounts", "StatementDay", sqlServer, ct))
            {
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "ALTER TABLE [Accounts] ADD [StatementDay] int NULL;"
                    : "ALTER TABLE \"Accounts\" ADD COLUMN \"StatementDay\" INTEGER NULL;", ct);
            }

            // ---- Date of birth on the Profile page ----
            if (await TableExistsAsync(conn, "Settings", sqlServer, ct)
                && !await ColumnExistsAsync(conn, "Settings", "BirthDate", sqlServer, ct))
            {
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "ALTER TABLE [Settings] ADD [BirthDate] date NULL;"
                    : "ALTER TABLE \"Settings\" ADD COLUMN \"BirthDate\" TEXT NULL;", ct);
            }

            // ---- Shared bills (your share, who owes the rest, repayments) ----
            if (!await ColumnExistsAsync(conn, "Transactions", "RepaysId", sqlServer, ct))
            {
                foreach (var (column, sqlServerType, sqliteType) in new[]
                {
                    ("MyShare", "decimal(18,2) NULL", "TEXT NULL"),
                    ("SharedWith", "nvarchar(100) NULL", "TEXT NULL"),
                    ("RepaysId", "int NULL", "INTEGER NULL"),
                })
                {
                    if (await ColumnExistsAsync(conn, "Transactions", column, sqlServer, ct)) continue;
                    await db.Database.ExecuteSqlRawAsync(sqlServer
                        ? $"ALTER TABLE [Transactions] ADD [{column}] {sqlServerType};"
                        : $"ALTER TABLE \"Transactions\" ADD COLUMN \"{column}\" {sqliteType};", ct);
                }
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "CREATE INDEX [IX_Transactions_RepaysId] ON [Transactions] ([RepaysId]);"
                    : "CREATE INDEX \"IX_Transactions_RepaysId\" ON \"Transactions\" (\"RepaysId\");", ct);
            }

            // ---- Automatic prices and regular contributions on holdings ----
            if (await TableExistsAsync(conn, "Holdings", sqlServer, ct)
                && !await ColumnExistsAsync(conn, "Holdings", "AutoPrice", sqlServer, ct))
            {
                foreach (var (column, sqlServerType, sqliteType) in new[]
                {
                    ("AutoPrice", "bit NOT NULL DEFAULT 0", "INTEGER NOT NULL DEFAULT 0"),
                    ("PriceError", "nvarchar(300) NULL", "TEXT NULL"),
                    ("FiguresAsOfUtc", "datetime2 NULL", "TEXT NULL"),
                    ("ContributionMatch", "nvarchar(100) NULL", "TEXT NULL"),
                    ("ContributionAmount", "decimal(18,2) NULL", "TEXT NULL"),
                    ("ContributionAccountId", "int NULL", "INTEGER NULL"),
                })
                {
                    if (await ColumnExistsAsync(conn, "Holdings", column, sqlServer, ct)) continue;
                    await db.Database.ExecuteSqlRawAsync(sqlServer
                        ? $"ALTER TABLE [Holdings] ADD [{column}] {sqlServerType};"
                        : $"ALTER TABLE \"Holdings\" ADD COLUMN \"{column}\" {sqliteType};", ct);
                }

                // Shares, ETFs and crypto already carry a ticker, so they start fetching their price.
                // Existing figures are taken as up to date, so no past payment is counted twice.
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "UPDATE [Holdings] SET [AutoPrice] = 1 WHERE [AssetClass] IN (0, 1, 3);"
                    : "UPDATE \"Holdings\" SET \"AutoPrice\" = 1 WHERE \"AssetClass\" IN (0, 1, 3);", ct);
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "UPDATE [Holdings] SET [FiguresAsOfUtc] = SYSUTCDATETIME();"
                    : "UPDATE \"Holdings\" SET \"FiguresAsOfUtc\" = strftime('%Y-%m-%d %H:%M:%f', 'now');", ct);
            }

            // ---- ILP contract terms and statement values ----
            if (!await TableExistsAsync(conn, "PolicyTerms", sqlServer, ct))
            {
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? """
                      CREATE TABLE [PolicyTerms] (
                          [Id] int NOT NULL IDENTITY,
                          [HoldingId] int NOT NULL,
                          [CommencementDate] date NOT NULL,
                          [MonthlyPremium] decimal(18,2) NOT NULL,
                          [InitialPeriodMonths] int NOT NULL,
                          [MinimumInvestmentYears] int NOT NULL,
                          [PremiumTermYears] int NULL,
                          [ScheduleJson] nvarchar(max) NOT NULL,
                          CONSTRAINT [PK_PolicyTerms] PRIMARY KEY ([Id])
                      );
                      """
                    : """
                      CREATE TABLE "PolicyTerms" (
                          "Id" INTEGER NOT NULL CONSTRAINT "PK_PolicyTerms" PRIMARY KEY AUTOINCREMENT,
                          "HoldingId" INTEGER NOT NULL,
                          "CommencementDate" TEXT NOT NULL,
                          "MonthlyPremium" TEXT NOT NULL,
                          "InitialPeriodMonths" INTEGER NOT NULL,
                          "MinimumInvestmentYears" INTEGER NOT NULL,
                          "PremiumTermYears" INTEGER NULL,
                          "ScheduleJson" TEXT NOT NULL
                      );
                      """, ct);
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "CREATE UNIQUE INDEX [IX_PolicyTerms_HoldingId] ON [PolicyTerms] ([HoldingId]);"
                    : "CREATE UNIQUE INDEX \"IX_PolicyTerms_HoldingId\" ON \"PolicyTerms\" (\"HoldingId\");", ct);
            }

            // Added with the second plan (FWD): a PolicyTerms table from before has no premium term.
            if (!await ColumnExistsAsync(conn, "PolicyTerms", "PremiumTermYears", sqlServer, ct))
            {
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "ALTER TABLE [PolicyTerms] ADD [PremiumTermYears] int NULL;"
                    : "ALTER TABLE \"PolicyTerms\" ADD COLUMN \"PremiumTermYears\" INTEGER NULL;", ct);
            }

            if (!await TableExistsAsync(conn, "PolicyValuations", sqlServer, ct))
            {
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? """
                      CREATE TABLE [PolicyValuations] (
                          [Id] int NOT NULL IDENTITY,
                          [HoldingId] int NOT NULL,
                          [AsOf] date NOT NULL,
                          [InitialUnits] decimal(18,2) NOT NULL,
                          [AccumulationUnits] decimal(18,2) NOT NULL,
                          [CreatedAtUtc] datetime2 NOT NULL,
                          CONSTRAINT [PK_PolicyValuations] PRIMARY KEY ([Id])
                      );
                      """
                    : """
                      CREATE TABLE "PolicyValuations" (
                          "Id" INTEGER NOT NULL CONSTRAINT "PK_PolicyValuations" PRIMARY KEY AUTOINCREMENT,
                          "HoldingId" INTEGER NOT NULL,
                          "AsOf" TEXT NOT NULL,
                          "InitialUnits" TEXT NOT NULL,
                          "AccumulationUnits" TEXT NOT NULL,
                          "CreatedAtUtc" TEXT NOT NULL
                      );
                      """, ct);
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "CREATE INDEX [IX_PolicyValuations_HoldingId] ON [PolicyValuations] ([HoldingId]);"
                    : "CREATE INDEX \"IX_PolicyValuations_HoldingId\" ON \"PolicyValuations\" (\"HoldingId\");", ct);
            }

            // ---- Subscriptions marked "not a subscription" ----
            if (!await TableExistsAsync(conn, "DismissedRecurring", sqlServer, ct))
            {
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? """
                      CREATE TABLE [DismissedRecurring] (
                          [Id] int NOT NULL IDENTITY,
                          [Pattern] nvarchar(100) NOT NULL,
                          [Amount] decimal(18,2) NOT NULL,
                          [CreatedAtUtc] datetime2 NOT NULL,
                          CONSTRAINT [PK_DismissedRecurring] PRIMARY KEY ([Id])
                      );
                      """
                    : """
                      CREATE TABLE "DismissedRecurring" (
                          "Id" INTEGER NOT NULL CONSTRAINT "PK_DismissedRecurring" PRIMARY KEY AUTOINCREMENT,
                          "Pattern" TEXT NOT NULL,
                          "Amount" TEXT NOT NULL,
                          "CreatedAtUtc" TEXT NOT NULL
                      );
                      """, ct);
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "CREATE INDEX [IX_DismissedRecurring_Pattern] ON [DismissedRecurring] ([Pattern]);"
                    : "CREATE INDEX \"IX_DismissedRecurring_Pattern\" ON \"DismissedRecurring\" (\"Pattern\");", ct);
            }

            // ---- Spending and saving goals ----
            if (!await TableExistsAsync(conn, "Goals", sqlServer, ct))
            {
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? """
                      CREATE TABLE [Goals] (
                          [Id] int NOT NULL IDENTITY,
                          [Kind] int NOT NULL,
                          [Name] nvarchar(100) NOT NULL,
                          [CategoryId] int NULL,
                          [Bucket] int NULL,
                          [Amount] decimal(18,2) NOT NULL,
                          [Reason] nvarchar(300) NULL,
                          [CreatedAtUtc] datetime2 NOT NULL,
                          CONSTRAINT [PK_Goals] PRIMARY KEY ([Id])
                      );
                      """
                    : """
                      CREATE TABLE "Goals" (
                          "Id" INTEGER NOT NULL CONSTRAINT "PK_Goals" PRIMARY KEY AUTOINCREMENT,
                          "Kind" INTEGER NOT NULL,
                          "Name" TEXT NOT NULL,
                          "CategoryId" INTEGER NULL,
                          "Bucket" INTEGER NULL,
                          "Amount" TEXT NOT NULL,
                          "Reason" TEXT NULL,
                          "CreatedAtUtc" TEXT NOT NULL
                      );
                      """, ct);
            }

            // ---- Savings pockets, the Safety Fund, planned transfers and reminders ----
            if (await TableExistsAsync(conn, "Settings", sqlServer, ct))
            {
                if (!await ColumnExistsAsync(conn, "Settings", "EmergencyTarget", sqlServer, ct))
                    await db.Database.ExecuteSqlRawAsync(sqlServer
                        ? "ALTER TABLE [Settings] ADD [EmergencyTarget] decimal(18,2) NOT NULL DEFAULT 12000;"
                        : "ALTER TABLE \"Settings\" ADD COLUMN \"EmergencyTarget\" TEXT NOT NULL DEFAULT '12000.0';", ct);
                if (!await ColumnExistsAsync(conn, "Settings", "ParentsTarget", sqlServer, ct))
                    await db.Database.ExecuteSqlRawAsync(sqlServer
                        ? "ALTER TABLE [Settings] ADD [ParentsTarget] decimal(18,2) NULL;"
                        : "ALTER TABLE \"Settings\" ADD COLUMN \"ParentsTarget\" TEXT NULL;", ct);
            }

            if (!await ColumnExistsAsync(conn, "Transactions", "PairId", sqlServer, ct))
            {
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "ALTER TABLE [Transactions] ADD [PairId] int NULL;"
                    : "ALTER TABLE \"Transactions\" ADD COLUMN \"PairId\" INTEGER NULL;", ct);
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "CREATE INDEX [IX_Transactions_PairId] ON [Transactions] ([PairId]);"
                    : "CREATE INDEX \"IX_Transactions_PairId\" ON \"Transactions\" (\"PairId\");", ct);
            }

            if (!await TableExistsAsync(conn, "PocketTerms", sqlServer, ct))
            {
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? """
                      CREATE TABLE [PocketTerms] (
                          [Id] int NOT NULL IDENTITY,
                          [AccountId] int NOT NULL,
                          [Bank] nvarchar(100) NOT NULL,
                          [Type] int NOT NULL,
                          [Purpose] int NOT NULL,
                          [InterestRatePct] decimal(7,4) NOT NULL,
                          [BonusRatePct] decimal(7,4) NULL,
                          [StartDate] date NULL,
                          [TermMonths] int NULL,
                          [MaturityDate] date NULL,
                          [PromoCashback] decimal(18,2) NULL,
                          [GoalAmount] decimal(18,2) NULL,
                          CONSTRAINT [PK_PocketTerms] PRIMARY KEY ([Id])
                      );
                      """
                    : """
                      CREATE TABLE "PocketTerms" (
                          "Id" INTEGER NOT NULL CONSTRAINT "PK_PocketTerms" PRIMARY KEY AUTOINCREMENT,
                          "AccountId" INTEGER NOT NULL,
                          "Bank" TEXT NOT NULL,
                          "Type" INTEGER NOT NULL,
                          "Purpose" INTEGER NOT NULL,
                          "InterestRatePct" TEXT NOT NULL,
                          "BonusRatePct" TEXT NULL,
                          "StartDate" TEXT NULL,
                          "TermMonths" INTEGER NULL,
                          "MaturityDate" TEXT NULL,
                          "PromoCashback" TEXT NULL,
                          "GoalAmount" TEXT NULL
                      );
                      """, ct);
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "CREATE UNIQUE INDEX [IX_PocketTerms_AccountId] ON [PocketTerms] ([AccountId]);"
                    : "CREATE UNIQUE INDEX \"IX_PocketTerms_AccountId\" ON \"PocketTerms\" (\"AccountId\");", ct);
            }

            if (!await TableExistsAsync(conn, "PlannedTransfers", sqlServer, ct))
            {
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? """
                      CREATE TABLE [PlannedTransfers] (
                          [Id] int NOT NULL IDENTITY,
                          [Name] nvarchar(100) NOT NULL,
                          [Amount] decimal(18,2) NOT NULL,
                          [FromAccountId] int NOT NULL,
                          [ToAccountId] int NOT NULL,
                          [StartsOn] date NOT NULL,
                          [IsActive] bit NOT NULL,
                          [CreatedAtUtc] datetime2 NOT NULL,
                          CONSTRAINT [PK_PlannedTransfers] PRIMARY KEY ([Id])
                      );
                      """
                    : """
                      CREATE TABLE "PlannedTransfers" (
                          "Id" INTEGER NOT NULL CONSTRAINT "PK_PlannedTransfers" PRIMARY KEY AUTOINCREMENT,
                          "Name" TEXT NOT NULL,
                          "Amount" TEXT NOT NULL,
                          "FromAccountId" INTEGER NOT NULL,
                          "ToAccountId" INTEGER NOT NULL,
                          "StartsOn" TEXT NOT NULL,
                          "IsActive" INTEGER NOT NULL,
                          "CreatedAtUtc" TEXT NOT NULL
                      );
                      """, ct);
            }

            if (!await TableExistsAsync(conn, "PlannedTransferTicks", sqlServer, ct))
            {
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? """
                      CREATE TABLE [PlannedTransferTicks] (
                          [Id] int NOT NULL IDENTITY,
                          [PlannedTransferId] int NOT NULL,
                          [PeriodStart] date NOT NULL,
                          [TransactionId] int NOT NULL,
                          [DoneAtUtc] datetime2 NOT NULL,
                          CONSTRAINT [PK_PlannedTransferTicks] PRIMARY KEY ([Id])
                      );
                      """
                    : """
                      CREATE TABLE "PlannedTransferTicks" (
                          "Id" INTEGER NOT NULL CONSTRAINT "PK_PlannedTransferTicks" PRIMARY KEY AUTOINCREMENT,
                          "PlannedTransferId" INTEGER NOT NULL,
                          "PeriodStart" TEXT NOT NULL,
                          "TransactionId" INTEGER NOT NULL,
                          "DoneAtUtc" TEXT NOT NULL
                      );
                      """, ct);
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "CREATE UNIQUE INDEX [IX_PlannedTransferTicks_PlannedTransferId_PeriodStart] ON [PlannedTransferTicks] ([PlannedTransferId], [PeriodStart]);"
                    : "CREATE UNIQUE INDEX \"IX_PlannedTransferTicks_PlannedTransferId_PeriodStart\" ON \"PlannedTransferTicks\" (\"PlannedTransferId\", \"PeriodStart\");", ct);
            }

            if (!await TableExistsAsync(conn, "Reminders", sqlServer, ct))
            {
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? """
                      CREATE TABLE [Reminders] (
                          [Id] int NOT NULL IDENTITY,
                          [Text] nvarchar(300) NOT NULL,
                          [NextDue] date NOT NULL,
                          [RepeatMonths] int NOT NULL,
                          [AccountId] int NULL,
                          [CreatedAtUtc] datetime2 NOT NULL,
                          CONSTRAINT [PK_Reminders] PRIMARY KEY ([Id])
                      );
                      """
                    : """
                      CREATE TABLE "Reminders" (
                          "Id" INTEGER NOT NULL CONSTRAINT "PK_Reminders" PRIMARY KEY AUTOINCREMENT,
                          "Text" TEXT NOT NULL,
                          "NextDue" TEXT NOT NULL,
                          "RepeatMonths" INTEGER NOT NULL,
                          "AccountId" INTEGER NULL,
                          "CreatedAtUtc" TEXT NOT NULL
                      );
                      """, ct);
            }

            // ---- Income tax plans (one row per income year) ----
            if (!await TableExistsAsync(conn, "TaxPlans", sqlServer, ct))
            {
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? """
                      CREATE TABLE [TaxPlans] (
                          [Id] int NOT NULL IDENTITY,
                          [Year] int NOT NULL,
                          [EmploymentIncomeOverride] decimal(18,2) NULL,
                          [ExpectedMonthlyPay] decimal(18,2) NULL,
                          [ExpectedBonus] decimal(18,2) NOT NULL,
                          [OtherIncome] decimal(18,2) NOT NULL,
                          [SrsContributed] decimal(18,2) NOT NULL,
                          [CpfTopUpSelf] decimal(18,2) NOT NULL,
                          [CpfTopUpFamily] decimal(18,2) NOT NULL,
                          [Donations] decimal(18,2) NOT NULL,
                          [ParentsLivingWith] int NOT NULL,
                          [ParentsNotLivingWith] int NOT NULL,
                          [NsmanRelief] decimal(18,2) NOT NULL,
                          [OtherReliefs] decimal(18,2) NOT NULL,
                          [Foreigner] bit NOT NULL,
                          [UpdatedAtUtc] datetime2 NOT NULL,
                          CONSTRAINT [PK_TaxPlans] PRIMARY KEY ([Id])
                      );
                      """
                    : """
                      CREATE TABLE "TaxPlans" (
                          "Id" INTEGER NOT NULL CONSTRAINT "PK_TaxPlans" PRIMARY KEY AUTOINCREMENT,
                          "Year" INTEGER NOT NULL,
                          "EmploymentIncomeOverride" TEXT NULL,
                          "ExpectedMonthlyPay" TEXT NULL,
                          "ExpectedBonus" TEXT NOT NULL,
                          "OtherIncome" TEXT NOT NULL,
                          "SrsContributed" TEXT NOT NULL,
                          "CpfTopUpSelf" TEXT NOT NULL,
                          "CpfTopUpFamily" TEXT NOT NULL,
                          "Donations" TEXT NOT NULL,
                          "ParentsLivingWith" INTEGER NOT NULL,
                          "ParentsNotLivingWith" INTEGER NOT NULL,
                          "NsmanRelief" TEXT NOT NULL,
                          "OtherReliefs" TEXT NOT NULL,
                          "Foreigner" INTEGER NOT NULL,
                          "UpdatedAtUtc" TEXT NOT NULL
                      );
                      """, ct);
                await db.Database.ExecuteSqlRawAsync(sqlServer
                    ? "CREATE UNIQUE INDEX [IX_TaxPlans_Year] ON [TaxPlans] ([Year]);"
                    : "CREATE UNIQUE INDEX \"IX_TaxPlans_Year\" ON \"TaxPlans\" (\"Year\");", ct);
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
