using Budget.Api.Data;
using Budget.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Budget.Tests;

public class StatementCycleTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Theory]
    [InlineData(2026, 10, 12, 2026, 9, 13, 2026, 10, 12)]   // closing day is on that statement
    [InlineData(2026, 10, 13, 2026, 10, 13, 2026, 11, 12)]  // day after starts the next one
    [InlineData(2026, 10, 1, 2026, 9, 13, 2026, 10, 12)]
    [InlineData(2026, 12, 25, 2026, 12, 13, 2027, 1, 12)]   // across the year end
    public void Day_12_cycles(int y, int m, int d, int sy, int sm, int sd, int cy, int cm, int cd)
    {
        var (start, closes) = StatementCycle.For(D(y, m, d), 12);
        Assert.Equal(D(sy, sm, sd), start);
        Assert.Equal(D(cy, cm, cd), closes);
    }

    [Fact]
    public void Day_31_closes_on_the_last_day_of_short_months()
    {
        var (start, closes) = StatementCycle.For(D(2026, 2, 10), 31);
        Assert.Equal(D(2026, 2, 1), start);   // January closed on the 31st
        Assert.Equal(D(2026, 2, 28), closes);

        (start, closes) = StatementCycle.For(D(2026, 3, 1), 31);
        Assert.Equal(D(2026, 3, 1), start);
        Assert.Equal(D(2026, 3, 31), closes);

        (start, closes) = StatementCycle.For(D(2028, 2, 29), 30); // leap year
        Assert.Equal(D(2028, 1, 31), start);
        Assert.Equal(D(2028, 2, 29), closes);
    }

    [Fact]
    public void Consecutive_cycles_leave_no_gap_or_overlap()
    {
        foreach (var day in new[] { 1, 12, 28, 29, 30, 31 })
        {
            var date = D(2026, 1, 1);
            var (_, closes) = StatementCycle.For(date, day);
            for (var i = 0; i < 24; i++)
            {
                var (nextStart, nextCloses) = StatementCycle.For(closes.AddDays(1), day);
                Assert.Equal(closes.AddDays(1), nextStart);
                Assert.True(nextCloses > nextStart || nextCloses == nextStart);
                closes = nextCloses;
            }
        }
    }

    [Fact]
    public void Charges_less_refunds_ignoring_payments_and_pending_taps()
    {
        var (charged, count) = StatementMath.Charged(new[]
        {
            (50m, false, false),   // purchase
            (20m, false, false),   // purchase
            (5m, true, false),     // refund
            (900m, true, true),    // bill payment to the card
            (0m, false, false),    // pending S$0 tap
        });
        Assert.Equal(65m, charged);
        Assert.Equal(2, count);
    }
}

public sealed class CardStatementTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly BudgetDbContext _db;
    private readonly AccountService _accounts;
    private readonly Clock _clock = new(new ConfigurationBuilder().Build());

    public CardStatementTests()
    {
        _conn.Open();
        _db = new BudgetDbContext(new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        Seed.EnsureSeededAsync(_db).GetAwaiter().GetResult();
        var categorizer = new Categorizer(_db);
        var budgets = new BudgetService(_db, _clock);
        var tx = new TransactionService(_db, categorizer, budgets, _clock);
        _accounts = new AccountService(_db, categorizer, budgets, tx, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private DateTime At(DateOnly day) => _clock.LocalDateStartToUtc(day).AddHours(12);

    private async Task AddAsync(Account a, DateOnly day, decimal amount, bool income = false, string? category = null)
    {
        int? catId = category is null ? null : (await _db.Categories.SingleAsync(c => c.Name == category)).Id;
        _db.Transactions.Add(new Transaction
        {
            AccountId = a.Id, OccurredAtUtc = At(day), Amount = amount, IsIncome = income,
            Merchant = "Test", CategoryId = catId
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Each_card_shows_its_own_cycle_and_last_statement()
    {
        var today = _clock.Today;
        var uob = new Account { Name = "UOB One", Kind = AccountKind.CreditCard, StatementDay = 12, BalanceAsOfUtc = At(today.AddMonths(-3)) };
        var yuu = new Account { Name = "yuu Visa", Kind = AccountKind.CreditCard, StatementDay = 25, BalanceAsOfUtc = At(today.AddMonths(-3)) };
        var noDay = new Account { Name = "Other card", Kind = AccountKind.CreditCard, BalanceAsOfUtc = At(today.AddMonths(-3)) };
        var bank = new Account { Name = "DBS", Kind = AccountKind.Bank, StatementDay = 12, BalanceAsOfUtc = At(today.AddMonths(-3)) };
        _db.Accounts.AddRange(uob, yuu, noDay, bank);
        await _db.SaveChangesAsync();

        var (start, closes) = StatementCycle.For(today, 12);
        var (lastStart, lastCloses) = StatementCycle.For(start.AddDays(-1), 12);

        await AddAsync(uob, start, 40m);                          // first day of the cycle
        await AddAsync(uob, today, 10m);
        await AddAsync(uob, today, 3m, income: true, category: "Refund");
        await AddAsync(uob, today, 500m, income: true, category: "Transfer"); // bill payment
        await AddAsync(uob, lastCloses, 99m);                     // closing day of the last statement
        await AddAsync(uob, lastStart, 1m);
        await AddAsync(yuu, today, 7m);

        var list = await _accounts.StatementsAsync();

        Assert.Equal(new[] { "UOB One", "yuu Visa" }, list.Select(s => s.Name));
        var u = list[0];
        Assert.Equal(start, u.CycleStart);
        Assert.Equal(closes, u.ClosesOn);
        Assert.Equal(closes.DayNumber - today.DayNumber, u.DaysToClose);
        Assert.Equal(47m, u.ChargedSoFar);
        Assert.Equal(2, u.Count);
        Assert.Equal(lastCloses, u.LastClosedOn);
        Assert.Equal(100m, u.LastCharged);

        var y = list[1];
        Assert.Equal(StatementCycle.For(today, 25).Closes, y.ClosesOn);
        Assert.Equal(7m, y.ChargedSoFar);
    }

    [Fact]
    public async Task No_cards_with_a_statement_day_gives_an_empty_list()
    {
        _db.Accounts.Add(new Account { Name = "UOB One", Kind = AccountKind.CreditCard, BalanceAsOfUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        Assert.Empty(await _accounts.StatementsAsync());
    }
}

public class StatementDaySchemaTests
{
    [Fact]
    public async Task Adds_the_statement_day_column_to_an_existing_accounts_table()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(conn).Options;
        using (var fresh = new BudgetDbContext(options))
            await fresh.Database.EnsureCreatedAsync();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """ALTER TABLE "Accounts" DROP COLUMN "StatementDay";""";
            cmd.ExecuteNonQuery();
        }

        using var db = new BudgetDbContext(options);
        await SchemaUpgrade.ApplyAsync(db);
        await SchemaUpgrade.ApplyAsync(db); // second run is a no-op

        db.Accounts.Add(new Account { Name = "UOB One", Kind = AccountKind.CreditCard, StatementDay = 12, BalanceAsOfUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        Assert.Equal(12, (await db.Accounts.AsNoTracking().SingleAsync()).StatementDay);
    }
}
