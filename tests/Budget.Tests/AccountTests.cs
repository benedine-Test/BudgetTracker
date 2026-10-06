using Budget.Api.Data;
using Budget.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Budget.Tests;

public class StatementParserTests
{
    [Fact]
    public void Reads_a_bank_download_with_preamble_and_debit_credit_columns()
    {
        // Typical bank layout: account details first, then the table. Amounts in two columns.
        const string csv = """
            Account Details For:,eMySavings Account 123-456789-0
            Statement as at:,06 Oct 2026

            Transaction Date,Reference,Debit Amount,Credit Amount,Transaction Ref1,Transaction Ref2,Transaction Ref3
            01 Oct 2026,POS, 7.80, ,NETS QR,STARBUCKS JEWEL,
            02 Oct 2026,ICT, ,"4,200.00",SALARY,ACME PTE LTD,
            03 Oct 2026,UMC,"1,234.56", ,CARD PAYMENT,DBS VISA,
            Balance carried forward,,,,,,
            """;

        var p = StatementParser.Parse(csv, positiveIsSpend: false);

        Assert.Null(p.Problem);
        Assert.Equal("Transaction Date", p.Columns.Date);
        Assert.Equal("Debit Amount", p.Columns.Debit);
        Assert.Equal("Credit Amount", p.Columns.Credit);
        Assert.Equal(3, p.Rows.Count);
        Assert.Equal(1, p.SkippedLines);

        Assert.Equal(new DateOnly(2026, 10, 1), p.Rows[0].Date);
        Assert.Equal(7.80m, p.Rows[0].Amount);
        Assert.False(p.Rows[0].IsCredit);
        Assert.Equal("NETS QR STARBUCKS JEWEL", p.Rows[0].Description); // short "POS" code column dropped

        Assert.True(p.Rows[1].IsCredit);
        Assert.Equal(4200m, p.Rows[1].Amount);
        Assert.Equal(1234.56m, p.Rows[2].Amount);
    }

    [Fact]
    public void Signed_amount_column_follows_account_type()
    {
        const string csv = """
            Date,Description,Amount,Balance
            05/10/2026,GRAB* RIDE,-12.30,987.70
            04/10/2026,FAST TRANSFER FROM JOHN,50.00,1000.00
            """;

        var bank = StatementParser.Parse(csv, positiveIsSpend: false);
        Assert.False(bank.Rows[0].IsCredit);
        Assert.True(bank.Rows[1].IsCredit);
        Assert.Equal(new DateOnly(2026, 10, 5), bank.Rows[0].Date); // day-first

        // Newest first: the closing balance is the first row's.
        Assert.Equal(987.70m, bank.ClosingBalance);
        Assert.Equal(new DateOnly(2026, 10, 5), bank.ClosingDate);

        var card = StatementParser.Parse(csv, positiveIsSpend: true);
        Assert.True(card.Rows[0].IsCredit);
        Assert.False(card.Rows[1].IsCredit);
    }

    [Fact]
    public void Cr_marker_and_semicolons_and_quotes()
    {
        const string csv = "Post Date;Transaction Date;Description;Amount\n" +
                           "03-Oct-2026;01-Oct-2026;\"SHOPEE; SINGAPORE\";25.90\n" +
                           "04-Oct-2026;04-Oct-2026;PAYMENT - THANK YOU;500.00 CR\n";

        var p = StatementParser.Parse(csv, positiveIsSpend: true);

        Assert.Equal("Transaction Date", p.Columns.Date); // the day you paid, not the posting day
        Assert.Equal("SHOPEE; SINGAPORE", p.Rows[0].Description);
        Assert.False(p.Rows[0].IsCredit);
        Assert.True(p.Rows[1].IsCredit);
        Assert.Equal(500m, p.Rows[1].Amount);
    }

    [Fact]
    public void Direction_column_found_by_its_values()
    {
        const string csv = """
            Value Date,Narrative,Amount,Indicator
            2026-10-01,KOPITIAM,3.50,DR
            2026-10-02,INTEREST,0.12,CR
            """;

        var p = StatementParser.Parse(csv, positiveIsSpend: true);

        Assert.Equal("Indicator", p.Columns.Direction);
        Assert.False(p.Rows[0].IsCredit);
        Assert.True(p.Rows[1].IsCredit);
    }

    [Fact]
    public void Column_override_by_heading_name()
    {
        const string csv = """
            When,What,Out,In
            1 Oct 2026,LUNCH,8.00,
            """;

        Assert.NotNull(StatementParser.Parse(csv, false).Problem);

        var p = StatementParser.Parse(csv, false, new ColumnMap(Date: "When", Description: ["What"], Debit: "Out", Credit: "In"));
        Assert.Null(p.Problem);
        Assert.Equal(8m, p.Rows.Single().Amount);
        Assert.Equal("LUNCH", p.Rows.Single().Description);
    }

    [Theory]
    [InlineData("PK\u0003\u0004rest", "Excel")]
    [InlineData("%PDF-1.7", "PDF")]
    [InlineData("hello,world\n1,2", "headings")]
    public void Explains_files_it_cannot_read(string text, string mention) =>
        Assert.Contains(mention, StatementParser.Parse(text, false).Problem);

    [Theory]
    [InlineData("01/02/2026", 2026, 2, 1)]
    [InlineData("1 Feb 2026", 2026, 2, 1)]
    [InlineData("01-Feb-26", 2026, 2, 1)]
    [InlineData("2026-02-01", 2026, 2, 1)]
    [InlineData("2026-02-01T09:15:00", 2026, 2, 1)]
    [InlineData("01/02/2026 10:30 AM", 2026, 2, 1)]
    public void Dates_are_day_first(string raw, int y, int m, int d)
    {
        Assert.True(StatementParser.TryParseDate(raw, out var date));
        Assert.Equal(new DateOnly(y, m, d), date);
    }

    [Theory]
    [InlineData("1,234.56", 1234.56, null)]
    [InlineData("-12.50", -12.50, null)]
    [InlineData("(12.50)", -12.50, null)]
    [InlineData("S$ 7.80", 7.80, null)]
    [InlineData("500.00 CR", 500, "CR")]
    [InlineData("12.00DR", 12, "DR")]
    public void Money_values(string raw, decimal expected, string? marker)
    {
        Assert.True(StatementParser.TryParseMoney(raw, out var v, out var m));
        Assert.Equal(expected, v);
        Assert.Equal(marker, m);
    }
}

public class StatementMatcherTests
{
    private static StatementRow Row(int day, decimal amount, string text = "X", bool credit = false) =>
        new(0, new DateOnly(2026, 10, day), text, amount, credit, null);

    private static KnownTx Tx(int id, int day, decimal amount, string merchant = "Y", int? account = null, bool income = false) =>
        new(id, new DateOnly(2026, 10, day), amount, income, merchant, account);

    [Fact]
    public void Matches_a_tap_that_posted_days_later()
    {
        var m = StatementMatcher.Match([Row(4, 7.80m, "NETS QR STARBUCKS")], [Tx(1, 1, 7.80m, "Starbucks")], accountId: 9);
        Assert.Equal(1, m[0]);
    }

    [Fact]
    public void Each_entry_is_used_once_so_two_coffees_need_two()
    {
        var m = StatementMatcher.Match(
            [Row(2, 1.50m), Row(2, 1.50m)],
            [Tx(1, 2, 1.50m)], accountId: 9);
        Assert.Equal(1, m.Count(x => x == 1));
        Assert.Contains(null, m);
    }

    [Fact]
    public void Closest_day_wins_when_amounts_repeat()
    {
        var m = StatementMatcher.Match(
            [Row(1, 1.50m), Row(3, 1.50m)],
            [Tx(10, 3, 1.50m), Tx(11, 1, 1.50m)], accountId: 9);
        Assert.Equal(11, m[0]);
        Assert.Equal(10, m[1]);
    }

    [Fact]
    public void Never_matches_across_accounts_direction_or_amount()
    {
        var m = StatementMatcher.Match(
            [Row(2, 10m), Row(2, 20m, credit: true), Row(2, 30m)],
            [Tx(1, 2, 10m, account: 5), Tx(2, 2, 20m), Tx(3, 2, 30.01m)], accountId: 9);
        Assert.All(m, x => Assert.Null(x));
    }

    [Fact]
    public void Too_far_apart_is_a_different_purchase()
    {
        var m = StatementMatcher.Match([Row(20, 5m)], [Tx(1, 10, 5m)], accountId: 9);
        Assert.Null(m[0]);
    }

    [Theory]
    [InlineData("PAYMENT - THANK YOU", true)]
    [InlineData("BILL PAYMENT DBS VISA CARD", true)]
    [InlineData("SHOPEE SINGAPORE", false)]
    [InlineData("FAST PAYMENT TO JOHN TAN", false)]
    public void Spots_card_bill_payments(string text, bool expected) =>
        Assert.Equal(expected, StatementMatcher.LooksLikeCardPayment(text));

    [Fact]
    public void Balance_walks_forward_and_back_from_the_anchor()
    {
        var anchor = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
        var txs = new[]
        {
            (new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), -20m),  // before: already in the anchor
            (new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc), -30m),
            (new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), 100m),
        };

        Assert.Equal(1070m, AccountMath.BalanceAt(1000m, anchor, txs, DateTime.MaxValue));
        Assert.Equal(970m, AccountMath.BalanceAt(1000m, anchor, txs, new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(1020m, AccountMath.BalanceAt(1000m, anchor, txs, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Card_names_pick_the_most_specific_account()
    {
        var accounts = new[]
        {
            new Account { Id = 1, Name = "DBS", CardNames = "DBS" },
            new Account { Id = 2, Name = "Altitude", CardNames = "DBS Altitude, Altitude" },
            new Account { Id = 3, Name = "Old", CardNames = "UOB One", IsArchived = true },
        };
        Assert.Equal(2, AccountService.ForCard(accounts, "DBS Altitude Visa")?.Id);
        Assert.Equal(1, AccountService.ForCard(accounts, "DBS PayLah")?.Id);
        Assert.Null(AccountService.ForCard(accounts, "UOB One"));
        Assert.Null(AccountService.ForCard(accounts, null));
    }
}

/// <summary>Import end to end against a real (in-memory SQLite) database.</summary>
public sealed class StatementImportTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly BudgetDbContext _db;
    private readonly AccountService _accounts;
    private readonly BudgetService _budgets;
    private readonly TransactionService _tx;
    private readonly Clock _clock = new(new ConfigurationBuilder().Build());

    public StatementImportTests()
    {
        _conn.Open();
        _db = new BudgetDbContext(new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        Seed.EnsureSeededAsync(_db).GetAwaiter().GetResult();
        var categorizer = new Categorizer(_db);
        _budgets = new BudgetService(_db, _clock);
        _tx = new TransactionService(_db, categorizer, _budgets, _clock);
        _accounts = new AccountService(_db, categorizer, _budgets, _tx, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private DateTime Local(int day, int hour = 12) => _clock.LocalDateStartToUtc(new DateOnly(2026, 10, day)).AddHours(hour);

    private async Task<Account> AddAccountAsync(decimal balance, int asOfDay, string? cards = null)
    {
        var a = new Account { Name = "DBS Savings", Kind = AccountKind.Bank, AnchorBalance = balance, BalanceAsOfUtc = Local(asOfDay, 0), CardNames = cards };
        _db.Accounts.Add(a);
        await _db.SaveChangesAsync();
        return a;
    }

    [Fact]
    public async Task Taps_on_a_known_card_land_in_its_account()
    {
        var a = await AddAccountAsync(1000m, 1, cards: "DBS Visa Debit");
        var r = await _tx.IngestSpendAsync(7.80m, "SGD", "Starbucks", "DBS Visa Debit", Local(2), TransactionSource.ApplePayShortcut, null);

        Assert.Equal(a.Id, r.Transaction.AccountId);
        Assert.Equal(992.20m, await _accounts.BalanceAsync(a));
    }

    [Fact]
    public async Task Import_adds_only_what_is_missing_and_resets_to_the_bank_balance()
    {
        var a = await AddAccountAsync(1000m, 1);
        // Already in the app from Apple Pay, not yet filed under any account.
        var tap = await _tx.IngestSpendAsync(7.80m, "SGD", "Starbucks", "iPhone", Local(2), TransactionSource.ApplePayShortcut, null);

        const string csv = """
            Transaction Date,Description,Debit,Credit,Balance
            03 Oct 2026,NETS QR STARBUCKS,7.80,,992.20
            03 Oct 2026,SP DIGITAL UTILITIES,120.00,,872.20
            04 Oct 2026,BILL PAYMENT DBS VISA CARD,300.00,,572.20
            """;
        var preview = await _accounts.PreviewAsync(a, StatementParser.Parse(csv, false), false);

        Assert.Equal(["matched", "new", "new"], preview.Rows.Select(r => r.Status));
        Assert.Equal(tap.Transaction.Id, preview.Rows[0].MatchId);
        Assert.Equal("Utilities", preview.Rows[1].Category);
        Assert.Equal("Transfer", preview.Rows[2].Category);
        Assert.Equal(572.20m, preview.StatementBalance);
        Assert.Equal(572.20m, preview.AppBalanceThatDayAfterImport); // nothing else missing: the books agree

        var add = preview.Rows.Where(r => r.Status == "new")
            .Select(r => new ImportRowIn(r.Date, r.Description, r.Amount, r.IsCredit, r.SuggestSalary)).ToList();
        var result = await _accounts.CommitAsync(a, add, [tap.Transaction.Id], preview.StatementBalance, preview.StatementBalanceDate);

        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.Linked);
        Assert.Equal(572.20m, result.Balance);

        // The card payment is your own money moving, so it doesn't eat the budget.
        var summary = await _budgets.SummaryAsync(new DateOnly(2026, 10, 4));
        Assert.Equal(127.80m, summary.TotalSpent);

        // Importing the same file again finds everything already there.
        var again = await _accounts.PreviewAsync(a, StatementParser.Parse(csv, false), false);
        Assert.All(again.Rows, r => Assert.Equal("matched", r.Status));
    }

    [Fact]
    public async Task Salary_from_a_statement_opens_the_pay_period_once()
    {
        var a = await AddAccountAsync(0m, 20);
        const string csv = """
            Date,Description,Amount
            25/10/2026,GIRO SALARY ACME PTE LTD,4200.00
            """;
        var preview = await _accounts.PreviewAsync(a, StatementParser.Parse(csv, false), false);
        var row = preview.Rows.Single();
        Assert.True(row.SuggestSalary);

        await _accounts.CommitAsync(a, [new ImportRowIn(row.Date, row.Description, row.Amount, true, true)], [], null, null);
        var period = await _db.BudgetPeriods.SingleAsync();
        Assert.Equal(4200m, period.TakeHomeIncome);

        // Salary typed in by hand from a payslip that's a few cents off is flagged, not doubled.
        const string csv2 = """
            Date,Description,Amount
            26/10/2026,SALARY,4200.35
            """;
        var check = await _accounts.PreviewAsync(a, StatementParser.Parse(csv2, false), false);
        Assert.Equal("check", check.Rows.Single().Status);
    }

    [Fact]
    public async Task Undo_puts_everything_back_as_it_was()
    {
        var a = await AddAccountAsync(1000m, 1);
        var tap = await _tx.IngestSpendAsync(7.80m, "SGD", "Starbucks", "iPhone", Local(2), TransactionSource.ApplePayShortcut, null);

        var result = await _accounts.CommitAsync(a,
            [
                new ImportRowIn(new DateOnly(2026, 10, 3), "SP DIGITAL", 120m, false, false),
                new ImportRowIn(new DateOnly(2026, 10, 25), "SALARY ACME", 4200m, true, true),
            ],
            [tap.Transaction.Id], 5000m, new DateOnly(2026, 10, 26), fileName: "oct.pdf");
        Assert.Equal(1, await _db.BudgetPeriods.CountAsync());
        Assert.Equal(5000m, result.Balance);

        var undo = await _accounts.UndoAsync(a, result.BatchId);

        Assert.Equal(1, await _db.Transactions.CountAsync());          // only the original tap is left
        var back = await _db.Transactions.AsNoTracking().SingleAsync();
        Assert.Null(back.AccountId);                                      // unfiled again
        Assert.Null(back.MergedSources);
        Assert.Equal(0, await _db.BudgetPeriods.CountAsync());           // the salary's budget is gone too
        Assert.Equal(1000m, undo.Balance);                                // balance as before the import
        var acct = await _db.Accounts.AsNoTracking().SingleAsync();
        Assert.Equal(Local(1, 0), acct.BalanceAsOfUtc);
        Assert.Null(acct.LastImportAtUtc);

        var history = await _accounts.ImportsAsync(a.Id);
        Assert.NotNull(history.Single().UndoneAt);
        await Assert.ThrowsAsync<ImportUndoException>(() => _accounts.UndoAsync(a, result.BatchId)); // not twice

        // And the same statement can be imported again afterwards.
        var again = await _accounts.CommitAsync(a, [new ImportRowIn(new DateOnly(2026, 10, 3), "SP DIGITAL", 120m, false, false)], [], null, null);
        Assert.Equal(880m, again.Balance);
    }

    [Fact]
    public async Task Only_the_latest_import_can_be_undone_and_a_later_manual_balance_is_kept()
    {
        var a = await AddAccountAsync(1000m, 1);
        var first = await _accounts.CommitAsync(a, [new ImportRowIn(new DateOnly(2026, 10, 3), "SHOP", 10m, false, false)], [], 990m, new DateOnly(2026, 10, 3));
        var second = await _accounts.CommitAsync(a, [new ImportRowIn(new DateOnly(2026, 10, 5), "SHOP 2", 20m, false, false)], [], 970m, new DateOnly(2026, 10, 5));

        await Assert.ThrowsAsync<ImportUndoException>(() => _accounts.UndoAsync(a, first.BatchId));

        // The person corrects the balance by hand after the import: undo mustn't overwrite that.
        var tracked = await _db.Accounts.SingleAsync();
        tracked.AnchorBalance = 2000m;
        tracked.BalanceAsOfUtc = Local(6, 0);
        await _db.SaveChangesAsync();

        var undo = await _accounts.UndoAsync(a, second.BatchId);
        Assert.Contains("set by hand", undo.Message);
        Assert.Equal(2000m, undo.Balance);
        Assert.Equal(1, await _db.Transactions.CountAsync());

        await _accounts.UndoAsync(a, first.BatchId); // now the first is the latest
        Assert.Equal(0, await _db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Pdf_balance_of_a_bank_account_stays_positive()
    {
        var a = await AddAccountAsync(0m, 1);
        var parsed = new ParsedStatement([], new ColumnMap(), [new StatementRow(1, new DateOnly(2026, 10, 3), "SHOP", 10m, false, 990m)],
            0, 990m, new DateOnly(2026, 10, 3), null);
        var preview = await _accounts.PreviewAsync(a, parsed, positiveIsSpend: true);
        Assert.Equal(990m, preview.StatementBalance);
    }

    [Fact]
    public async Task Unknown_money_in_is_other_income_not_left_to_sort()
    {
        var a = await AddAccountAsync(0m, 1);
        await _accounts.CommitAsync(a, [new ImportRowIn(new DateOnly(2026, 10, 2), "FAST TRANSFER FROM JANE TAN", 250m, true, false)], [], null, null);
        var t = await _db.Transactions.Include(x => x.Category).SingleAsync();
        Assert.Equal("Other Income", t.Category?.Name);
        Assert.Equal(250m, await _accounts.BalanceAsync(a));
    }

    [Fact]
    public async Task Bus_mrt_charge_from_statement_replaces_pending_taps()
    {
        var a = await AddAccountAsync(100m, 1);
        await _tx.LogPendingFareAsync("BUS/MRT", "iPhone", Local(3, 8), TransactionSource.ApplePayShortcut);
        await _tx.LogPendingFareAsync("BUS/MRT", "iPhone", Local(3, 18), TransactionSource.ApplePayShortcut);

        await _accounts.CommitAsync(a, [new ImportRowIn(new DateOnly(2026, 10, 4), "BUS/MRT 123456", 3.40m, false, false)], [], null, null);

        Assert.Equal(1, await _db.Transactions.CountAsync());
        Assert.Equal(96.60m, await _accounts.BalanceAsync(a));
    }
}

public class SchemaUpgradeTests
{
    [Fact]
    public async Task Adds_accounts_to_a_database_made_before_they_existed()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            // The pre-accounts Transactions table (only the columns that matter here).
            cmd.CommandText = """
                CREATE TABLE "Transactions" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Transactions" PRIMARY KEY AUTOINCREMENT,
                  "Amount" TEXT NOT NULL, "Merchant" TEXT NOT NULL);
                INSERT INTO "Transactions" ("Amount", "Merchant") VALUES ('7.80', 'Starbucks');
                """;
            cmd.ExecuteNonQuery();
        }

        using var db = new BudgetDbContext(new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(conn).Options);
        await SchemaUpgrade.ApplyAsync(db);
        await SchemaUpgrade.ApplyAsync(db); // second run is a no-op

        using var check = conn.CreateCommand();
        check.CommandText = """SELECT COUNT(*) FROM pragma_table_info('Transactions') WHERE name = 'AccountId'""";
        Assert.Equal(1L, check.ExecuteScalar());
        check.CommandText = """SELECT COUNT(*) FROM "Accounts" """;
        Assert.Equal(0L, check.ExecuteScalar());
        check.CommandText = """SELECT "Merchant" FROM "Transactions" WHERE "AccountId" IS NULL""";
        Assert.Equal("Starbucks", check.ExecuteScalar());
    }
}
