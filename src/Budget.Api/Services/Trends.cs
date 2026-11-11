using Budget.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Services;

/// <summary>One bit of spending, already in local dates and counted at your share.</summary>
public record SpendRow(DateOnly Date, decimal Amount, string Merchant, string? Category, Bucket? Bucket);

public record MonthTotal(DateOnly Month, decimal Needs, decimal Wants, decimal Savings, decimal Other, decimal Total);

public record DayTotal(DateOnly Date, decimal Total, int Count);

/// <summary>Spending at one shop in the month shown, and in the month before it.</summary>
public record ShopTotal(string Name, decimal Total, int Count, decimal PreviousMonth);

/// <summary>Status: "above" (already past your usual) | "fast" (well ahead of your usual pace) | "new" (nothing here before) | "ok".</summary>
public record CategoryVsUsual(string Category, string Bucket, decimal SoFar, decimal Usual, string Status);

public record UsualView(DateOnly PeriodStart, DateOnly PeriodEndExclusive, int DaysGone, int DaysTotal,
    int PeriodsCompared, IReadOnlyList<CategoryVsUsual> Items);

public record TrendsView(DateOnly Month, DateOnly? FirstMonth, string Currency,
    IReadOnlyList<MonthTotal> Months, IReadOnlyList<DayTotal> Days, IReadOnlyList<ShopTotal> Shops, UsualView Usual);

/// <summary>The sums behind the Trends screen. Pure, so they can be tested without a database.</summary>
public static class Trends
{
    public const int MonthsShown = 6;
    public const int PeriodsForUsual = 3;

    private static DateOnly MonthOf(DateOnly d) => new(d.Year, d.Month, 1);

    /// <summary>Savings is money kept, not spent, so the calendar, shops and usual leave it out.</summary>
    private static bool IsSpending(SpendRow r) => r.Bucket != Bucket.Savings;

    /// <summary>Totals per calendar month, by bucket, for the <paramref name="count"/> months ending with <paramref name="lastMonth"/>.</summary>
    public static List<MonthTotal> ByMonth(IEnumerable<SpendRow> rows, DateOnly lastMonth, int count = MonthsShown)
    {
        var last = MonthOf(lastMonth);
        var first = last.AddMonths(-(count - 1));
        var groups = rows.Where(r => r.Date >= first && r.Date < last.AddMonths(1))
            .GroupBy(r => MonthOf(r.Date)).ToDictionary(g => g.Key, g => g.ToList());

        return Enumerable.Range(0, count).Select(i =>
        {
            var m = first.AddMonths(i);
            var g = groups.GetValueOrDefault(m) ?? [];
            decimal Sum(Func<SpendRow, bool> f) => g.Where(f).Sum(r => r.Amount);
            var needs = Sum(r => r.Bucket == Bucket.Needs);
            var wants = Sum(r => r.Bucket == Bucket.Wants);
            var savings = Sum(r => r.Bucket == Bucket.Savings);
            var other = Sum(r => r.Bucket is not (Bucket.Needs or Bucket.Wants or Bucket.Savings));
            return new MonthTotal(m, needs, wants, savings, other, needs + wants + savings + other);
        }).ToList();
    }

    /// <summary>Every day of the month with what was spent on it (zero days included).</summary>
    public static List<DayTotal> ByDay(IEnumerable<SpendRow> rows, DateOnly month)
    {
        var first = MonthOf(month);
        var days = first.AddMonths(1).DayNumber - first.DayNumber;
        var groups = rows.Where(IsSpending).Where(r => MonthOf(r.Date) == first)
            .GroupBy(r => r.Date).ToDictionary(g => g.Key, g => (Total: g.Sum(r => r.Amount), Count: g.Count()));

        return Enumerable.Range(0, days).Select(i =>
        {
            var d = first.AddDays(i);
            var (total, n) = groups.GetValueOrDefault(d);
            return new DayTotal(d, total, n);
        }).ToList();
    }

    /// <summary>The shops that took the most in the month, grouped by brand ("GRAB* A-123" and "GRAB* B-9" are one shop).</summary>
    public static List<ShopTotal> TopShops(IEnumerable<SpendRow> rows, DateOnly month, int take = 10)
    {
        var first = MonthOf(month);
        var prev = first.AddMonths(-1);
        var spend = rows.Where(IsSpending).ToList();

        static string Key(SpendRow r) => MerchantText.IsUnknown(r.Merchant) ? "" : MerchantText.SuggestPattern(r.Merchant);

        var before = spend.Where(r => MonthOf(r.Date) == prev)
            .GroupBy(Key).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));

        return spend.Where(r => MonthOf(r.Date) == first)
            .GroupBy(Key)
            .Select(g => new ShopTotal(g.Key == "" ? "Unknown shop" : Pretty(g.Key),
                g.Sum(r => r.Amount), g.Count(), before.GetValueOrDefault(g.Key)))
            .OrderByDescending(s => s.Total).ThenBy(s => s.Name)
            .Take(take).ToList();
    }

    /// <summary>
    /// Each category so far this pay period against its average over earlier pay periods.
    /// Only earlier periods that had any spending count toward the average, so a new user isn't compared with zeros.
    /// </summary>
    public static (int Compared, List<CategoryVsUsual> Items) VsUsual(
        IReadOnlyCollection<SpendRow> current, IEnumerable<IReadOnlyCollection<SpendRow>> earlier,
        int daysGone, int daysTotal, int take = 10)
    {
        static string Cat(SpendRow r) => r.Category ?? "Uncategorised";
        static string BucketName(SpendRow r) => r.Bucket?.ToString() ?? "Unassigned";

        var past = earlier.Select(p => p.Where(IsSpending).ToList()).Where(p => p.Count > 0).ToList();
        var now = current.Where(IsSpending).ToList();

        var usual = past.SelectMany(p => p.GroupBy(Cat).Select(g => (Cat: g.Key, Bucket: BucketName(g.First()), Total: g.Sum(r => r.Amount), Count: g.Count())))
            .GroupBy(x => x.Cat)
            .ToDictionary(g => g.Key, g => (g.First().Bucket, Avg: Math.Round(g.Sum(x => x.Total) / past.Count, 2),
                AvgCount: (decimal)g.Sum(x => x.Count) / past.Count));
        var soFar = now.GroupBy(Cat).ToDictionary(g => g.Key, g => (Bucket: BucketName(g.First()), Total: g.Sum(r => r.Amount), Count: g.Count()));

        var share = daysTotal <= 0 ? 1m : Math.Clamp((decimal)daysGone / daysTotal, 0m, 1m);

        var items = soFar.Keys.Union(usual.Keys).Select(cat =>
        {
            var (bucketNow, spent, count) = soFar.GetValueOrDefault(cat);
            var (bucketUsual, avg, avgCount) = usual.GetValueOrDefault(cat);
            var expected = avg * share;
            // A bill paid once a period (electricity, rent) lands all at once, so being "ahead of pace" means nothing.
            var oneOffBill = count <= 1 && avgCount <= 1.5m;
            var status =
                avg == 0 ? (spent > 0 && past.Count > 0 ? "new" : "ok")
                : spent > avg ? "above"
                // Well ahead of where you'd usually be by now. The S$10 floor stops one coffee flagging a small category.
                : !oneOffBill && spent > expected * 1.25m && spent - expected >= 10 ? "fast"
                : "ok";
            return new CategoryVsUsual(cat, bucketNow ?? bucketUsual ?? "Unassigned", spent, avg, status);
        })
        .OrderByDescending(c => Math.Max(c.SoFar, c.Usual)).ThenBy(c => c.Category)
        .Take(take).ToList();

        return (past.Count, items);
    }

    /// <summary>"GRAB" → "Grab", "TOAST BOX" → "Toast Box".</summary>
    public static string Pretty(string pattern) =>
        string.Join(' ', pattern.ToLowerInvariant().Split(' ')
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}

public class TrendService(BudgetDbContext db, BudgetService budgets, Clock clock)
{
    public async Task<TrendsView> GetAsync(DateOnly? month, CancellationToken ct = default)
    {
        var settings = await budgets.GetSettingsAsync(ct);
        var today = clock.Today;
        var shown = new DateOnly((month ?? today).Year, (month ?? today).Month, 1);

        // This pay period and the ones before it, for "vs usual".
        var (period, _) = await budgets.PeriodForAsync(today, ct);
        var earlier = new List<BudgetPeriod>();
        var cursor = period.StartDate;
        for (var i = 0; i < Trends.PeriodsForUsual; i++)
        {
            var (p, _) = await budgets.PeriodForAsync(cursor.AddDays(-1), ct);
            earlier.Add(p);
            cursor = p.StartDate;
        }

        // One read covers every view: six months of trend (plus the month before for shops) and the pay periods.
        var from = new[] { shown.AddMonths(-Math.Max(Trends.MonthsShown - 1, 1)), cursor }.Min();
        var to = new[] { shown.AddMonths(1), period.EndDate }.Max();
        var fromUtc = clock.LocalDateStartToUtc(from);
        var toUtc = clock.LocalDateStartToUtc(to);

        // Spending, as the Budget screen counts it: money out, not transfers or income, at your share of a shared bill.
        var raw = await db.Transactions.AsNoTracking()
            .Where(t => !t.IsIncome && t.Amount > 0 && t.OccurredAtUtc >= fromUtc && t.OccurredAtUtc < toUtc)
            .Where(t => t.Category == null || (t.Category.Bucket != Bucket.Transfer && t.Category.Bucket != Bucket.Income))
            .Select(t => new { t.OccurredAtUtc, t.Amount, t.MyShare, t.Merchant, Category = (string?)t.Category!.Name, Bucket = (Bucket?)t.Category!.Bucket })
            .ToListAsync(ct);
        var rows = raw.Select(t => new SpendRow(clock.ToLocalDate(t.OccurredAtUtc), t.MyShare ?? t.Amount, t.Merchant, t.Category, t.Bucket)).ToList();

        var firstUtc = await db.Transactions.AsNoTracking().Where(t => !t.IsIncome)
            .OrderBy(t => t.OccurredAtUtc).Select(t => (DateTime?)t.OccurredAtUtc).FirstOrDefaultAsync(ct);
        DateOnly? firstMonth = firstUtc is { } f ? new DateOnly(clock.ToLocalDate(f).Year, clock.ToLocalDate(f).Month, 1) : null;

        List<SpendRow> In(BudgetPeriod p) => rows.Where(r => r.Date >= p.StartDate && r.Date < p.EndDate).ToList();
        var daysTotal = period.EndDate.DayNumber - period.StartDate.DayNumber;
        var daysGone = Math.Clamp(today.DayNumber - period.StartDate.DayNumber + 1, 0, daysTotal);
        var (compared, items) = Trends.VsUsual(In(period), earlier.Select(In).ToList(), daysGone, daysTotal);

        return new TrendsView(shown, firstMonth, settings.BaseCurrency,
            Trends.ByMonth(rows, shown), Trends.ByDay(rows, shown), Trends.TopShops(rows, shown),
            new UsualView(period.StartDate, period.EndDate, daysGone, daysTotal, compared, items));
    }
}
