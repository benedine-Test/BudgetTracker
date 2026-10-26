using Budget.Api.Data;
using Budget.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Budget.Tests;

public class RecurringDetectionTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static IEnumerable<Charge> Monthly(string merchant, decimal amount, DateOnly first, int count, string? category = null) =>
        Enumerable.Range(0, count).Select(i => new Charge(first.AddMonths(i), amount, merchant, "SGD", category));

    [Fact]
    public void Three_monthly_charges_are_a_subscription_with_the_next_one_due()
    {
        var found = Recurring.Detect(Monthly("NETFLIX.COM 866-579-7172", 17.98m, D(2026, 7, 3), 4), today: D(2026, 10, 20));

        var n = Assert.Single(found);
        Assert.Equal("NETFLIX.COM", n.Pattern);
        Assert.Equal("Netflix.com", n.Name);
        Assert.Equal(Cadence.Monthly, n.Cadence);
        Assert.Equal(17.98m, n.Amount);
        Assert.Equal(17.98m, n.MonthlyCost);
        Assert.Equal(D(2026, 11, 3), n.NextDue);
        Assert.Equal(14, n.DaysToNext);
        Assert.Equal("active", n.Status);
        Assert.Null(n.PreviousAmount);
        Assert.Equal(4, n.Count);
    }

    [Fact]
    public void Two_charges_need_the_subscriptions_category()
    {
        var today = D(2026, 10, 20);
        Assert.Empty(Recurring.Detect(Monthly("GYM CO", 120m, D(2026, 9, 1), 2), today));
        Assert.Single(Recurring.Detect(Monthly("SPOTIFY P1234", 11.98m, D(2026, 9, 1), 2, "Subscriptions"), today));
    }

    [Fact]
    public void A_price_rise_stays_one_subscription_and_is_flagged()
    {
        var charges = Monthly("NETFLIX.COM", 15.98m, D(2026, 6, 3), 3)
            .Append(new Charge(D(2026, 9, 3), 17.98m, "NETFLIX.COM", "SGD"))
            .Append(new Charge(D(2026, 10, 3), 17.98m, "NETFLIX.COM", "SGD"));

        var n = Assert.Single(Recurring.Detect(charges, D(2026, 10, 20)));
        Assert.Equal(17.98m, n.Amount);
        Assert.Null(n.PreviousAmount); // the rise was a month ago; the latest charge matches the one before

        var justRisen = Recurring.Detect(charges.Where(c => c.Date < D(2026, 10, 1)), D(2026, 9, 20));
        Assert.Equal(15.98m, Assert.Single(justRisen).PreviousAmount);
    }

    [Fact]
    public void Two_plans_from_one_merchant_are_kept_apart()
    {
        var charges = Monthly("APPLE.COM/BILL", 1.28m, D(2026, 7, 5), 4)
            .Concat(Monthly("APPLE.COM/BILL", 10.98m, D(2026, 7, 12), 4));

        var found = Recurring.Detect(charges, D(2026, 10, 20));
        Assert.Equal(new[] { 1.28m, 10.98m }, found.Select(f => f.Amount).Order());
    }

    [Fact]
    public void Yearly_and_quarterly_plans_are_found_and_spread_per_month()
    {
        var charges = new[]
        {
            new Charge(D(2024, 11, 2), 120m, "DROPBOX*ABC", "SGD", "Subscriptions"),
            new Charge(D(2025, 11, 2), 120m, "DROPBOX*DEF", "SGD", "Subscriptions"),
            new Charge(D(2026, 2, 15), 90m, "ICLOUD QTR", "SGD"),
            new Charge(D(2026, 5, 15), 90m, "ICLOUD QTR", "SGD"),
            new Charge(D(2026, 8, 15), 90m, "ICLOUD QTR", "SGD"),
        };

        var found = Recurring.Detect(charges, D(2026, 10, 20));
        var yearly = found.Single(f => f.Cadence == Cadence.Yearly);
        Assert.Equal("DROPBOX", yearly.Pattern);
        Assert.Equal(10m, yearly.MonthlyCost);
        Assert.Equal(D(2026, 11, 2), yearly.NextDue);

        var quarterly = found.Single(f => f.Cadence == Cadence.Quarterly);
        Assert.Equal(30m, quarterly.MonthlyCost);
        Assert.Equal(D(2026, 11, 15), quarterly.NextDue);
    }

    [Fact]
    public void Irregular_or_frequent_spending_is_not_a_subscription()
    {
        var today = D(2026, 10, 20);
        // Weekly coffee at the same price.
        var coffee = Enumerable.Range(0, 10).Select(i => new Charge(D(2026, 8, 1).AddDays(7 * i), 6.5m, "STARBUCKS", "SGD"));
        // Same shop, scattered dates.
        var shop = new[] { D(2026, 3, 2), D(2026, 3, 20), D(2026, 7, 9), D(2026, 10, 1) }
            .Select(d => new Charge(d, 30m, "UNIQLO", "SGD"));
        // Monthly but the amounts are all over the place.
        var dinners = new[] { 20m, 85m, 41m, 150m }
            .Select((a, i) => new Charge(D(2026, 6, 10).AddMonths(i), a, "HAIDILAO", "SGD"));

        Assert.Empty(Recurring.Detect(coffee.Concat(shop).Concat(dinners), today));
    }

    [Fact]
    public void Transit_charges_are_left_out()
    {
        Assert.Empty(Recurring.Detect(Monthly("BUS/MRT 123456", 2m, D(2026, 6, 1), 5), D(2026, 10, 20)));
    }

    [Fact]
    public void One_missed_month_is_tolerated()
    {
        var charges = new[] { D(2026, 5, 8), D(2026, 6, 8), D(2026, 8, 8), D(2026, 9, 8), D(2026, 10, 8) }
            .Select(d => new Charge(d, 9.99m, "YOUTUBE PREMIUM", "SGD"));
        Assert.Single(Recurring.Detect(charges, D(2026, 10, 20)));
    }

    [Theory]
    [InlineData(2026, 11, 7, "active")]   // not due yet
    [InlineData(2026, 11, 13, "active")]  // 5 days over: within the grace
    [InlineData(2026, 11, 14, "late")]
    [InlineData(2026, 12, 13, "late")]
    [InlineData(2026, 12, 14, "cancelled")]
    public void Status_follows_how_far_past_due(int y, int m, int d, string status)
    {
        var found = Recurring.Detect(Monthly("NETFLIX.COM", 17.98m, D(2026, 7, 8), 4), D(y, m, d));
        Assert.Equal(status, Assert.Single(found).Status);
    }

    [Fact]
    public void Long_finished_subscriptions_are_dropped()
    {
        Assert.Empty(Recurring.Detect(Monthly("NETFLIX.COM", 17.98m, D(2025, 1, 8), 4), D(2026, 10, 20)));
    }

    [Fact]
    public void Due_on_the_usual_day_after_a_short_month()
    {
        var run = new[] { D(2026, 12, 31), D(2027, 1, 31), D(2027, 2, 28) }
            .Select(d => new Charge(d, 5m, "X", "SGD")).ToList();
        Assert.Equal(D(2027, 3, 31), Recurring.NextDue(run, 1));
    }

    [Fact]
    public void Dismissed_ones_are_hidden_only_at_that_amount()
    {
        var charges = Monthly("APPLE.COM/BILL", 1.28m, D(2026, 7, 5), 4)
            .Concat(Monthly("APPLE.COM/BILL", 10.98m, D(2026, 7, 12), 4));

        var found = Recurring.Detect(charges, D(2026, 10, 20), [("APPLE.COM/BILL", 10.98m)]);
        Assert.Equal(1.28m, Assert.Single(found).Amount);
    }
}

public sealed class SubscriptionServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly BudgetDbContext _db;
    private readonly Subscriptions _svc;
    private readonly Clock _clock = new(new ConfigurationBuilder().Build());

    public SubscriptionServiceTests()
    {
        _conn.Open();
        _db = new BudgetDbContext(new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        Seed.EnsureSeededAsync(_db).GetAwaiter().GetResult();
        _svc = new Subscriptions(_db, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private async Task AddAsync(DateOnly day, decimal amount, string merchant, string? category = null, bool income = false, decimal? myShare = null)
    {
        int? catId = category is null ? null : (await _db.Categories.SingleAsync(c => c.Name == category)).Id;
        _db.Transactions.Add(new Transaction
        {
            OccurredAtUtc = _clock.LocalDateStartToUtc(day).AddHours(12), Amount = amount, IsIncome = income,
            Merchant = merchant, CategoryId = catId, MyShare = myShare
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Totals_count_what_is_still_running_and_skip_transfers()
    {
        var today = _clock.Today;
        for (var i = 1; i <= 3; i++)
        {
            await AddAsync(today.AddMonths(-i), 17.98m, "NETFLIX.COM", "Subscriptions");
            // Family plan paid for others too: only your share counts.
            await AddAsync(today.AddMonths(-i).AddDays(1), 30m, "SPOTIFY FAMILY", "Subscriptions", myShare: 10m);
            // A monthly card bill payment is a transfer, not a subscription.
            await AddAsync(today.AddMonths(-i).AddDays(2), 800m, "PAYMENT - THANK YOU", "Transfer");
            // Monthly salary-like money in is not spending.
            await AddAsync(today.AddMonths(-i).AddDays(3), 50m, "INTEREST", income: true);
        }

        var view = await _svc.ListAsync();

        Assert.Equal(new[] { "NETFLIX.COM", "SPOTIFY FAMILY" }, view.Items.Select(i => i.Pattern).Order());
        Assert.Equal(10m, view.Items.Single(i => i.Pattern == "SPOTIFY FAMILY").Amount);
        Assert.Equal(27.98m, view.MonthlyTotal);
        Assert.Equal(27.98m * 12, view.YearlyTotal);
        Assert.Equal(27.98m, view.DueNext30Days);
        Assert.Equal("SGD", view.Currency);
    }

    [Fact]
    public async Task Not_a_subscription_hides_it_until_restored()
    {
        var today = _clock.Today;
        for (var i = 1; i <= 3; i++) await AddAsync(today.AddMonths(-i), 45m, "SP DIGITAL", "Utilities");

        var item = Assert.Single((await _svc.ListAsync()).Items);
        await _svc.DismissAsync(item.Pattern, item.Amount);
        await _svc.DismissAsync(item.Pattern, item.Amount); // twice is the same as once

        var view = await _svc.ListAsync();
        Assert.Empty(view.Items);
        Assert.Equal(1, view.Dismissed);

        var d = Assert.Single(await _svc.DismissedAsync());
        Assert.True(await _svc.RestoreAsync(d.Id));
        Assert.False(await _svc.RestoreAsync(d.Id));
        Assert.Single((await _svc.ListAsync()).Items);
    }
}

public class DismissedRecurringSchemaTests
{
    [Fact]
    public async Task Adds_the_table_to_an_existing_database()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(conn).Options;
        using (var fresh = new BudgetDbContext(options))
            await fresh.Database.EnsureCreatedAsync();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """DROP TABLE "DismissedRecurring";""";
            cmd.ExecuteNonQuery();
        }

        using var db = new BudgetDbContext(options);
        await SchemaUpgrade.ApplyAsync(db);
        await SchemaUpgrade.ApplyAsync(db); // second run is a no-op

        db.DismissedRecurring.Add(new DismissedRecurring { Pattern = "SP DIGITAL", Amount = 45m, CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        Assert.Equal(45m, (await db.DismissedRecurring.AsNoTracking().SingleAsync()).Amount);
    }
}
