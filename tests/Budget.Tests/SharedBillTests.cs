using Budget.Api.Data;
using Budget.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Budget.Tests;

public sealed class SharedBillTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly BudgetDbContext _db;
    private readonly BudgetService _budgets;
    private readonly AccountService _accounts;
    private readonly SharedBills _bills;
    private readonly Clock _clock = new(new ConfigurationBuilder().Build());

    public SharedBillTests()
    {
        _conn.Open();
        _db = new BudgetDbContext(new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        Seed.EnsureSeededAsync(_db).GetAwaiter().GetResult();
        var categorizer = new Categorizer(_db);
        _budgets = new BudgetService(_db, _clock);
        var tx = new TransactionService(_db, categorizer, _budgets, _clock);
        _accounts = new AccountService(_db, categorizer, _budgets, tx, _clock);
        _bills = new SharedBills(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private DateTime Local(int month, int day) => _clock.LocalDateStartToUtc(new DateOnly(2026, month, day)).AddHours(12);

    private async Task<Account> BankAsync()
    {
        var a = new Account { Name = "DBS Savings", Kind = AccountKind.Bank, AnchorBalance = 1000m, BalanceAsOfUtc = Local(9, 1) };
        _db.Accounts.Add(a);
        await _db.SaveChangesAsync();
        return a;
    }

    private async Task<Transaction> DinnerAsync(decimal amount, int month, int day, int? accountId = null)
    {
        var dining = await _db.Categories.SingleAsync(c => c.Name == "Dining & Food Delivery");
        var t = new Transaction
        {
            OccurredAtUtc = Local(month, day), Amount = amount, Merchant = "Hanami Ramen",
            CategoryId = dining.Id, CategoryConfirmed = true, AccountId = accountId
        };
        _db.Transactions.Add(t);
        await _db.SaveChangesAsync();
        return t;
    }

    private async Task<Transaction> MoneyInAsync(decimal amount, int month, int day, int? accountId = null)
    {
        var other = await _db.Categories.SingleAsync(c => c.Name == "Other Income");
        var t = new Transaction
        {
            OccurredAtUtc = Local(month, day), Amount = amount, IsIncome = true, Merchant = "PAYNOW FROM ALEX TAN",
            CategoryId = other.Id, AccountId = accountId
        };
        _db.Transactions.Add(t);
        await _db.SaveChangesAsync();
        return t;
    }

    private async Task<decimal> WantsSpentAsync(int month, int day) =>
        (await _budgets.SummaryAsync(new DateOnly(2026, month, day))).Buckets.Single(b => b.Bucket == "Wants").Spent;

    [Fact]
    public async Task Only_your_share_counts_in_the_period_you_paid_even_when_repaid_next_period()
    {
        var bank = await BankAsync();
        await _budgets.RecordSalaryAsync(4000m, Local(9, 25), null);
        await _budgets.RecordSalaryAsync(4000m, Local(10, 25), null);
        var dinner = await DinnerAsync(100m, 10, 20, bank.Id);

        await _bills.SetShareAsync(dinner.Id, 50m, "Alex");
        Assert.Equal(50m, await WantsSpentAsync(10, 20));
        Assert.Equal(50m, await _bills.TotalOwedAsync("SGD"));

        // Alex pays back in the next pay period: it lands as plain money in, then gets linked.
        var payNow = await MoneyInAsync(50m, 10, 27, bank.Id);
        await _bills.LinkAsync(dinner.Id, payNow.Id);

        Assert.Equal(50m, await WantsSpentAsync(10, 20));
        Assert.Equal(0m, await WantsSpentAsync(10, 27)); // the repayment never lands in the later period
        Assert.Equal(0m, await _bills.TotalOwedAsync("SGD"));
        Assert.Equal(950m, await _accounts.BalanceAsync(bank)); // the bank saw -100 then +50
        Assert.Equal(SharedBills.RepaymentCategory, (await _db.Transactions.Include(t => t.Category).SingleAsync(t => t.Id == payNow.Id)).Category!.Name);
    }

    [Fact]
    public async Task Partial_repayments_add_up_and_more_than_is_owed_is_refused()
    {
        var dinner = await DinnerAsync(120m, 10, 3);
        await _bills.SetShareAsync(dinner.Id, 40m, "Alex and Sam");

        await _bills.RecordAsync(dinner.Id, 40m, Local(10, 4), null, null);
        var tooMuch = await MoneyInAsync(50m, 10, 5);
        var e = await Assert.ThrowsAsync<SharedBillException>(() => _bills.LinkAsync(dinner.Id, tooMuch.Id));
        Assert.Contains("40.00", e.Message);

        await _bills.RecordAsync(dinner.Id, 40m, Local(10, 6), null, "Sam paid back");
        Assert.Empty(await _bills.OpenAsync());
        Assert.Equal(2, await _db.Transactions.CountAsync(t => t.RepaysId == dinner.Id));
    }

    [Fact]
    public async Task Writing_off_counts_what_was_never_repaid_as_your_spending()
    {
        await _budgets.RecordSalaryAsync(4000m, Local(9, 25), null);
        var dinner = await DinnerAsync(100m, 10, 3);
        await _bills.SetShareAsync(dinner.Id, 25m, "Sam");
        await _bills.RecordAsync(dinner.Id, 30m, Local(10, 10), null, null);

        await _bills.WriteOffAsync(dinner.Id);

        Assert.Equal(70m, await WantsSpentAsync(10, 3));
        Assert.Empty(await _bills.OpenAsync());
    }

    [Fact]
    public async Task A_bill_stops_being_shared_only_while_nothing_is_repaid()
    {
        var dinner = await DinnerAsync(100m, 10, 3);
        await _bills.SetShareAsync(dinner.Id, 50m, null);
        await _bills.SetShareAsync(dinner.Id, null, null);
        Assert.Null((await _db.Transactions.AsNoTracking().SingleAsync(t => t.Id == dinner.Id)).MyShare);

        await _bills.SetShareAsync(dinner.Id, 50m, null);
        await _bills.RecordAsync(dinner.Id, 20m, Local(10, 4), null, null);
        await Assert.ThrowsAsync<SharedBillException>(() => _bills.SetShareAsync(dinner.Id, null, null));
        // Raising your share past what's left after the repayment doesn't add up either.
        await Assert.ThrowsAsync<SharedBillException>(() => _bills.SetShareAsync(dinner.Id, 90m, null));
        await _bills.SetShareAsync(dinner.Id, 80m, null);
    }

    [Fact]
    public async Task Salary_and_spending_cannot_pay_back_a_bill()
    {
        var dinner = await DinnerAsync(100m, 10, 3);
        await _bills.SetShareAsync(dinner.Id, 50m, null);
        var (salary, _) = await _budgets.RecordSalaryAsync(50m, Local(10, 4), null);
        var coffee = await DinnerAsync(50m, 10, 4);

        await Assert.ThrowsAsync<SharedBillException>(() => _bills.LinkAsync(dinner.Id, salary.Id));
        await Assert.ThrowsAsync<SharedBillException>(() => _bills.LinkAsync(dinner.Id, coffee.Id));
        await Assert.ThrowsAsync<SharedBillException>(() => _bills.SetShareAsync(salary.Id, 10m, null));
    }

    [Fact]
    public async Task A_repayment_the_size_of_what_is_owed_is_suggested_first()
    {
        var older = await DinnerAsync(60m, 10, 1);
        var exact = await DinnerAsync(100m, 10, 2);
        var newer = await DinnerAsync(30m, 10, 3);
        await _bills.SetShareAsync(older.Id, 20m, null);  // 40 owed
        await _bills.SetShareAsync(exact.Id, 75m, null);  // 25 owed
        await _bills.SetShareAsync(newer.Id, 15m, null);  // 15 owed

        var order = (await _bills.OpenAsync(25m)).Select(o => o.Bill.Id).ToList();
        Assert.Equal([exact.Id, older.Id, newer.Id], order);
    }

    [Fact]
    public async Task Undoing_the_import_that_added_a_bill_keeps_its_repayment_as_plain_income()
    {
        var bank = await BankAsync();
        var result = await _accounts.CommitAsync(bank,
            [new ImportRowIn(new DateOnly(2026, 10, 3), "HANAMI RAMEN", 100m, false, false)], [], null, null);
        var dinner = await _db.Transactions.SingleAsync(t => t.ImportBatchId == result.BatchId);
        await _bills.SetShareAsync(dinner.Id, 50m, "Alex");
        var back = await _bills.RecordAsync(dinner.Id, 50m, Local(10, 5), bank.Id, null);

        await _accounts.UndoAsync(bank, result.BatchId);

        var left = await _db.Transactions.Include(t => t.Category).AsNoTracking().SingleAsync();
        Assert.Equal(back.Id, left.Id);
        Assert.Null(left.RepaysId);
        Assert.Equal("Other Income", left.Category!.Name);
    }

    [Fact]
    public async Task An_existing_database_gets_the_new_columns_and_the_paid_back_category()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(conn).Options;
        using (var fresh = new BudgetDbContext(options))
        {
            await fresh.Database.EnsureCreatedAsync();
            await Seed.EnsureSeededAsync(fresh);
        }
        using (var cmd = conn.CreateCommand())
        {
            // As a database from before shared bills.
            cmd.CommandText = """
                DROP INDEX "IX_Transactions_RepaysId";
                ALTER TABLE "Transactions" DROP COLUMN "MyShare";
                ALTER TABLE "Transactions" DROP COLUMN "SharedWith";
                ALTER TABLE "Transactions" DROP COLUMN "RepaysId";
                DELETE FROM "Categories" WHERE "Name" = 'Paid Back';
                """;
            cmd.ExecuteNonQuery();
        }

        using var db = new BudgetDbContext(options);
        await SchemaUpgrade.ApplyAsync(db);
        await SchemaUpgrade.ApplyAsync(db); // second run is a no-op
        await Seed.EnsureSeededAsync(db);
        await Seed.EnsureSeededAsync(db);

        Assert.Equal(1, await db.Categories.CountAsync(c => c.Name == SharedBills.RepaymentCategory));
        db.Transactions.Add(new Transaction { Amount = 10m, Merchant = "X", MyShare = 4m, SharedWith = "Sam", OccurredAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        Assert.Equal(4m, (await db.Transactions.AsNoTracking().SingleAsync()).MyShare);
    }
}
