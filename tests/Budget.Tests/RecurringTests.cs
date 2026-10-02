using Budget.Api.Data;
using Budget.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Budget.Tests;

public class RecurrenceTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void Month_end_anchor_does_not_drift()
    {
        var start = D(2026, 1, 31);
        Assert.Equal(D(2026, 2, 28), Recurrence.Occurrence(start, RecurringFrequency.Monthly, 1, 1));
        Assert.Equal(D(2026, 3, 31), Recurrence.Occurrence(start, RecurringFrequency.Monthly, 1, 2));
        Assert.Equal(D(2026, 4, 30), Recurrence.After(start, RecurringFrequency.Monthly, 1, D(2026, 3, 31)));
    }

    [Theory]
    [InlineData(RecurringFrequency.Monthly, 1, "2026-10-02", "2026-11-01")]
    [InlineData(RecurringFrequency.Monthly, 1, "2026-10-01", "2026-10-01")]
    [InlineData(RecurringFrequency.Monthly, 3, "2026-10-02", "2027-01-01")]
    [InlineData(RecurringFrequency.Weekly, 1, "2026-01-02", "2026-01-08")]   // Thu 1 Jan → next Thu
    [InlineData(RecurringFrequency.Weekly, 2, "2026-01-09", "2026-01-15")]
    [InlineData(RecurringFrequency.Yearly, 1, "2026-01-02", "2027-01-01")]
    [InlineData(RecurringFrequency.Monthly, 1, "2025-06-01", "2026-01-01")]  // before the start → the start
    public void On_or_after(RecurringFrequency f, int interval, string date, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), Recurrence.OnOrAfter(D(2026, 1, 1), f, interval, DateOnly.Parse(date)));

    [Fact]
    public void Old_anchor_is_fast_and_correct()
    {
        Assert.Equal(D(2026, 10, 15), Recurrence.OnOrAfter(D(1990, 3, 15), RecurringFrequency.Monthly, 1, D(2026, 10, 2)));
        Assert.Equal(D(2026, 10, 5), Recurrence.OnOrAfter(D(2000, 1, 3), RecurringFrequency.Weekly, 1, D(2026, 10, 2)));
    }

    [Theory]
    [InlineData(RecurringFrequency.Monthly, 1, 100, 100)]
    [InlineData(RecurringFrequency.Yearly, 1, 1200, 100)]
    [InlineData(RecurringFrequency.Weekly, 1, 12, 52)]
    [InlineData(RecurringFrequency.Monthly, 3, 300, 100)]
    public void Monthly_equivalent(RecurringFrequency f, int interval, decimal amount, decimal expected) =>
        Assert.Equal(expected, Recurrence.MonthlyEquivalent(amount, f, interval));
}

/// <summary>Runs the poster against a real (in-memory SQLite) database with a pinned clock.</summary>
public sealed class RecurringServiceTests : IDisposable
{
    private sealed class FixedClock(DateTime utc) : Clock(new ConfigurationBuilder().Build())
    {
        public DateTime Now { get; set; } = utc;
        public override DateTime UtcNow => Now;
    }

    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly BudgetDbContext _db;
    // 2 Oct 2026, 10:00 in Singapore.
    private readonly FixedClock _clock = new(new DateTime(2026, 10, 2, 2, 0, 0, DateTimeKind.Utc));

    public RecurringServiceTests()
    {
        _conn.Open();
        _db = new BudgetDbContext(new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        Seed.EnsureSeededAsync(_db).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private RecurringItem Add(string name, decimal amount, DateOnly start, string category = "Subscriptions")
    {
        var item = new RecurringItem
        {
            Name = name, Amount = amount, StartDate = start, NextDueDate = start, Frequency = RecurringFrequency.Monthly,
            CategoryId = _db.Categories.Single(c => c.Name == category).Id
        };
        _db.RecurringItems.Add(item);
        _db.SaveChanges();
        return item;
    }

    [Fact]
    public async Task Posts_once_and_advances()
    {
        var item = Add("Netflix", 19.98m, new DateOnly(2026, 10, 2));
        var svc = new RecurringService(_db, _clock);

        Assert.Equal(1, await svc.PostDueAsync());
        Assert.Equal(0, await svc.PostDueAsync()); // same day again: nothing new

        var tx = Assert.Single(_db.Transactions.Where(t => t.Source == TransactionSource.Recurring));
        Assert.Equal(19.98m, tx.Amount);
        Assert.True(tx.CategoryConfirmed);
        Assert.Equal(new DateOnly(2026, 10, 2), _clock.ToLocalDate(tx.OccurredAtUtc));
        Assert.Equal(new DateOnly(2026, 11, 2), item.NextDueDate);
    }

    [Fact]
    public async Task Catches_up_after_the_server_slept()
    {
        Add("Rent", 2000m, new DateOnly(2026, 8, 1), "Housing");
        Assert.Equal(3, await new RecurringService(_db, _clock).PostDueAsync()); // Aug, Sep, Oct
    }

    [Fact]
    public async Task Stops_after_end_date()
    {
        var item = Add("Gym", 80m, new DateOnly(2026, 8, 1), "Personal Care");
        item.EndDate = new DateOnly(2026, 9, 15);
        _db.SaveChanges();

        Assert.Equal(2, await new RecurringService(_db, _clock).PostDueAsync());
        Assert.False(item.IsActive);
    }

    [Fact]
    public async Task Bank_alert_that_came_first_is_not_doubled()
    {
        _db.Transactions.Add(new Transaction
        {
            OccurredAtUtc = new DateTime(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc), Amount = 19.98m, Currency = "SGD",
            Merchant = "NETFLIX.COM SINGAPORE", Source = TransactionSource.EmailAlert
        });
        _db.SaveChanges();
        Add("Netflix", 19.98m, new DateOnly(2026, 10, 2));

        Assert.Equal(0, await new RecurringService(_db, _clock).PostDueAsync());
        var only = Assert.Single(_db.Transactions);
        Assert.Contains("Recurring", only.MergedSources);
        Assert.Equal("Subscriptions", _db.Categories.Find(only.CategoryId)!.Name);
    }

    [Fact]
    public async Task Bank_alert_that_comes_later_merges_into_the_posted_entry()
    {
        Add("Netflix", 19.98m, new DateOnly(2026, 10, 2));
        var budgets = new BudgetService(_db, _clock);
        await new RecurringService(_db, _clock).PostDueAsync();

        var ingest = new TransactionService(_db, new Categorizer(_db), budgets, _clock);
        var chargedAt = new DateTime(2026, 10, 3, 7, 0, 0, DateTimeKind.Utc);
        var result = await ingest.IngestSpendAsync(19.98m, "SGD", "NETFLIX.COM", "Visa", chargedAt, TransactionSource.EmailAlert, null);

        Assert.True(result.WasDuplicate);
        var only = Assert.Single(_db.Transactions);
        Assert.Equal(chargedAt, only.OccurredAtUtc);
        Assert.Equal("Visa", only.CardName);
    }

    [Fact]
    public async Task Different_amount_is_not_merged()
    {
        Add("Netflix", 19.98m, new DateOnly(2026, 10, 2));
        await new RecurringService(_db, _clock).PostDueAsync();

        var ingest = new TransactionService(_db, new Categorizer(_db), new BudgetService(_db, _clock), _clock);
        var result = await ingest.IngestSpendAsync(25.98m, "SGD", "NETFLIX.COM", null, _clock.UtcNow, TransactionSource.EmailAlert, null);

        Assert.False(result.WasDuplicate);
        Assert.Equal(2, _db.Transactions.Count());
    }

    [Fact]
    public async Task Editing_after_todays_post_does_not_repost_today()
    {
        var item = Add("Netflix", 19.98m, new DateOnly(2026, 10, 2));
        var svc = new RecurringService(_db, _clock);
        await svc.PostDueAsync();

        item.Amount = 22.98m;
        item.NextDueDate = svc.NextDue(item);
        _db.SaveChanges();

        Assert.Equal(new DateOnly(2026, 11, 2), item.NextDueDate);
        Assert.Equal(0, await svc.PostDueAsync());
    }

    [Fact]
    public async Task Schema_upgrade_adds_the_table_to_an_existing_database()
    {
        _db.Database.ExecuteSqlRaw("DROP TABLE RecurringItems");
        await SchemaUpgrade.ApplyAsync(_db);
        await SchemaUpgrade.ApplyAsync(_db); // and is safe to run every start-up

        Add("Netflix", 19.98m, new DateOnly(2026, 10, 2));
        Assert.Equal(1, await _db.RecurringItems.CountAsync());
    }
}
