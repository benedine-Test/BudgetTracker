using Budget.Api.Data;
using Budget.Api.Services;

namespace Budget.Tests;

public class TrendTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);
    private static SpendRow S(DateOnly d, decimal amt, string shop = "SHOP", string? cat = "Food", Bucket? b = Bucket.Wants) =>
        new(d, amt, shop, cat, b);

    [Fact]
    public void Months_are_split_by_bucket_and_empty_months_show_as_zero()
    {
        var rows = new[]
        {
            S(D(2026, 11, 2), 40m, b: Bucket.Needs),
            S(D(2026, 11, 30), 10m, b: Bucket.Wants),
            S(D(2026, 11, 5), 200m, b: Bucket.Savings),
            S(D(2026, 9, 1), 7m, cat: null, b: null),
            S(D(2026, 12, 1), 999m), // after the last month shown
            S(D(2026, 5, 31), 999m), // before the first
        };

        var months = Trends.ByMonth(rows, D(2026, 11, 15));

        Assert.Equal(6, months.Count);
        Assert.Equal(D(2026, 6, 1), months[0].Month);
        var nov = months[^1];
        Assert.Equal((40m, 10m, 200m, 0m, 250m), (nov.Needs, nov.Wants, nov.Savings, nov.Other, nov.Total));
        Assert.Equal(7m, months.Single(m => m.Month == D(2026, 9, 1)).Other);
        Assert.Equal(0m, months.Single(m => m.Month == D(2026, 10, 1)).Total);
    }

    [Fact]
    public void Calendar_has_every_day_and_leaves_out_savings()
    {
        var rows = new[]
        {
            S(D(2026, 2, 3), 12m), S(D(2026, 2, 3), 8m),
            S(D(2026, 2, 3), 500m, b: Bucket.Savings),
        };

        var days = Trends.ByDay(rows, D(2026, 2, 20));

        Assert.Equal(28, days.Count);
        var third = days.Single(d => d.Date == D(2026, 2, 3));
        Assert.Equal((20m, 2), (third.Total, third.Count));
        Assert.Equal(0m, days[0].Total);
    }

    [Fact]
    public void Shops_group_by_brand_and_compare_with_last_month()
    {
        var rows = new[]
        {
            S(D(2026, 11, 1), 15m, "GRAB* A-5XYZ123 SINGAPORE"),
            S(D(2026, 11, 9), 10m, "GRAB* B-77"),
            S(D(2026, 10, 9), 6m, "GRAB* C-1"),
            S(D(2026, 11, 4), 30m, "TOAST BOX - TAMPINES 1 #01-23"),
            S(D(2026, 11, 4), 3m, "Unknown merchant"),
            S(D(2026, 11, 4), 900m, "GXS BANK", b: Bucket.Savings),
        };

        var shops = Trends.TopShops(rows, D(2026, 11, 1));

        Assert.Equal(new[] { "Toast Box", "Grab", "Unknown shop" }, shops.Select(s => s.Name));
        var grab = shops.Single(s => s.Name == "Grab");
        Assert.Equal((25m, 2, 6m), (grab.Total, grab.Count, grab.PreviousMonth));
    }

    [Fact]
    public void Usual_averages_earlier_periods_that_had_spending()
    {
        var p1 = new[] { S(D(2026, 8, 1), 300m), S(D(2026, 8, 2), 100m, cat: "Transport", b: Bucket.Needs) };
        var p2 = new[] { S(D(2026, 9, 1), 500m) };
        var empty = Array.Empty<SpendRow>(); // before you started using the app: not counted as a zero

        var now = new[]
        {
            S(D(2026, 10, 1), 450m),                                  // Food: usual 400, already past it
            S(D(2026, 10, 1), 25m, cat: "Transport", b: Bucket.Needs), // usual 50, 15 of 30 days: 40 is fast
            S(D(2026, 10, 2), 15m, cat: "Transport", b: Bucket.Needs),
            S(D(2026, 10, 1), 60m, cat: "Gifts"),                     // never seen before
        };

        var (compared, items) = Trends.VsUsual(now, [p2, p1, empty], daysGone: 15, daysTotal: 30);

        Assert.Equal(2, compared);
        var food = items.Single(i => i.Category == "Food");
        Assert.Equal((450m, 400m, "above"), (food.SoFar, food.Usual, food.Status));
        Assert.Equal("fast", items.Single(i => i.Category == "Transport").Status); // 40 vs 25 expected by now
        Assert.Equal("new", items.Single(i => i.Category == "Gifts").Status);
    }

    [Fact]
    public void A_monthly_bill_already_paid_is_not_spending_fast()
    {
        var p1 = new[] { S(D(2026, 8, 5), 118m, cat: "Utilities", b: Bucket.Needs) };
        var p2 = new[] { S(D(2026, 9, 5), 120m, cat: "Utilities", b: Bucket.Needs) };
        var now = new[] { S(D(2026, 10, 5), 119m, cat: "Utilities", b: Bucket.Needs) };

        var (_, items) = Trends.VsUsual(now, [p2, p1], daysGone: 5, daysTotal: 30);

        Assert.Equal("ok", Assert.Single(items).Status);
    }

    [Fact]
    public void Small_categories_are_not_flagged_for_a_few_dollars()
    {
        var past = new[] { S(D(2026, 9, 1), 20m, cat: "Snacks") };
        var now = new[] { S(D(2026, 10, 1), 12m, cat: "Snacks") }; // 12 vs 10 expected: under the S$10 floor

        var (_, items) = Trends.VsUsual(now, [past], daysGone: 15, daysTotal: 30);

        Assert.Equal("ok", Assert.Single(items).Status);
    }

    [Fact]
    public void With_no_history_nothing_is_flagged_as_new()
    {
        var (compared, items) = Trends.VsUsual([S(D(2026, 10, 1), 50m)], [], 3, 30);

        Assert.Equal(0, compared);
        Assert.Equal("ok", Assert.Single(items).Status);
    }
}
