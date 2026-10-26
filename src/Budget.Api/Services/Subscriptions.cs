using Budget.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Services;

public enum Cadence { Monthly, Quarterly, Yearly }

/// <summary>One spend as the detector sees it. Amount is what it costs you (your share of a shared bill).</summary>
public record Charge(DateOnly Date, decimal Amount, string Merchant, string Currency, string? Category = null, int? AccountId = null);

/// <summary>
/// A charge that repeats. Status: "active" (on time or not yet due), "late" (past due, not seen yet),
/// "cancelled" (a whole cycle missed: probably ended, not counted in totals).
/// </summary>
/// <param name="Pattern">The merchant text it's matched on, for dismissing it and for searching Spending.</param>
/// <param name="PreviousAmount">Set when the latest charge differs from the one before: a price change.</param>
/// <param name="MonthlyCost">The amount spread over a month (a yearly plan ÷ 12).</param>
public record RecurringCharge(
    string Pattern, string Name, Cadence Cadence, string Currency,
    decimal Amount, decimal? PreviousAmount, decimal MonthlyCost,
    DateOnly FirstCharged, DateOnly LastCharged, int Count,
    DateOnly NextDue, int DaysToNext, string Status,
    string? Category, int? AccountId);

/// <param name="MonthlyTotal">Active and late charges in the base currency, as a monthly cost.</param>
/// <param name="DueNext30Days">What's expected to be charged in the next 30 days, base currency.</param>
public record SubscriptionsView(
    decimal MonthlyTotal, decimal YearlyTotal, decimal DueNext30Days, string Currency,
    List<RecurringCharge> Items, int Dismissed);

/// <summary>
/// Finds subscriptions and regular bills in the spending already recorded: the same merchant, a similar
/// amount, about a month (or three, or twelve) apart. Nothing has to be set up; "not a subscription"
/// hides a false match. Weekly patterns aren't looked for: they're mostly groceries and coffee.
/// </summary>
public static class Recurring
{
    /// <summary>Amounts within 15% (or S$1) are the same charge, so a price rise doesn't start a new one.</summary>
    public static bool Similar(decimal a, decimal b) =>
        Math.Abs(a - b) <= Math.Max(1m, Math.Max(a, b) * 0.15m);

    private record Rule(Cadence Cadence, int Months, int MinDays, int MaxDays, int LateAfter, int CancelledAfter, int HiddenAfter);

    // Days are generous: statement imports post a few days late, and short months move the date.
    // A gap up to twice the cycle is allowed (one missed or unimported charge).
    private static readonly Rule[] Rules =
    [
        new(Cadence.Monthly, 1, 25, 35, LateAfter: 5, CancelledAfter: 35, HiddenAfter: 120),
        new(Cadence.Quarterly, 3, 80, 100, LateAfter: 10, CancelledAfter: 60, HiddenAfter: 270),
        new(Cadence.Yearly, 12, 350, 380, LateAfter: 20, CancelledAfter: 60, HiddenAfter: 400),
    ];

    public static List<RecurringCharge> Detect(
        IEnumerable<Charge> charges, DateOnly today, IEnumerable<(string Pattern, decimal Amount)>? dismissed = null)
    {
        var hidden = (dismissed ?? []).ToList();
        var result = new List<RecurringCharge>();

        var byMerchant = charges
            .Where(c => c.Amount > 0 && !MerchantText.IsTransit(c.Merchant))
            .GroupBy(c => (Pattern: MerchantText.SuggestPattern(c.Merchant), c.Currency))
            .Where(g => g.Key.Pattern.Length > 0);

        foreach (var merchant in byMerchant)
        {
            foreach (var run in SplitByAmount(merchant.OrderBy(c => c.Date)))
            {
                if (hidden.Any(d => d.Pattern == merchant.Key.Pattern && run.Any(c => Similar(c.Amount, d.Amount))))
                    continue;
                if (Classify(merchant.Key.Pattern, run, today) is { } found) result.Add(found);
            }
        }

        return result
            .OrderBy(r => r.Status == "cancelled")
            .ThenBy(r => r.NextDue)
            .ThenBy(r => r.Name)
            .ToList();
    }

    /// <summary>
    /// One merchant can bill several things (Apple: iCloud and Music). Each charge joins the run whose
    /// latest amount is closest and similar, else starts its own.
    /// </summary>
    private static List<List<Charge>> SplitByAmount(IEnumerable<Charge> ordered)
    {
        var runs = new List<List<Charge>>();
        foreach (var c in ordered)
        {
            var best = runs
                .Where(r => Similar(r[^1].Amount, c.Amount))
                .MinBy(r => Math.Abs(r[^1].Amount - c.Amount));
            if (best is null) runs.Add([c]);
            else best.Add(c);
        }
        return runs;
    }

    private static RecurringCharge? Classify(string pattern, List<Charge> run, DateOnly today)
    {
        var last = run[^1];
        // Two charges are enough only for something already filed as a subscription (a yearly plan
        // may never show a third); otherwise two similar payments a month apart is too often chance.
        var minCount = last.Category == "Subscriptions" || run.Any(c => c.Category == "Subscriptions") ? 2 : 3;
        if (run.Count < minCount) return null;

        var gaps = run.Zip(run.Skip(1), (a, b) => b.Date.DayNumber - a.Date.DayNumber).ToList();
        var median = gaps.Order().ElementAt(gaps.Count / 2);
        var rule = Rules.FirstOrDefault(r => median >= r.MinDays && median <= r.MaxDays);
        if (rule is null) return null;
        if (gaps.Any(g => g < rule.MinDays || g > rule.MaxDays * 2)) return null;

        var nextDue = NextDue(run, rule.Months);
        var late = today.DayNumber - nextDue.DayNumber;
        if (late > rule.HiddenAfter) return null;
        var status = late > rule.CancelledAfter ? "cancelled" : late > rule.LateAfter ? "late" : "active";

        var previous = run.Count >= 2 && run[^2].Amount != last.Amount ? run[^2].Amount : (decimal?)null;
        return new RecurringCharge(
            pattern, Name(pattern), rule.Cadence, last.Currency,
            last.Amount, previous, Math.Round(last.Amount / rule.Months, 2),
            run[0].Date, last.Date, run.Count,
            nextDue, nextDue.DayNumber - today.DayNumber, status,
            last.Category, last.AccountId);
    }

    /// <summary>
    /// The latest charge plus one cycle, on the usual day of the month: a charge on the 31st that fell
    /// on 28 Feb is due on the 31st again in March, not the 28th.
    /// </summary>
    public static DateOnly NextDue(IReadOnlyList<Charge> run, int months)
    {
        var last = run[^1].Date;
        var day = last.Day;
        if (day == DateTime.DaysInMonth(last.Year, last.Month))
            day = run.TakeLast(3).Max(c => c.Date.Day);
        var next = last.AddMonths(months);
        return StatementCycle.ClosingIn(next.Year, next.Month, day);
    }

    /// <summary>"NETFLIX.COM" → "Netflix.com": capital per word only (TextInfo.ToTitleCase gives "Netflix.Com").</summary>
    private static string Name(string pattern) =>
        string.Join(' ', pattern.ToLowerInvariant().Split(' ')
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}

public class Subscriptions(BudgetDbContext db, Clock clock)
{
    /// <summary>Far enough back to see a yearly plan charged twice.</summary>
    private const int LookBackMonths = 25;

    public async Task<SubscriptionsView> ListAsync(CancellationToken ct = default)
    {
        var settings = await db.Settings.AsNoTracking().SingleAsync(ct);
        var today = clock.Today;
        var from = clock.LocalDateStartToUtc(today.AddMonths(-LookBackMonths));

        // Money out that is spending: not transfers (card bill payments) or income.
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => !t.IsIncome && t.Amount > 0 && t.OccurredAtUtc >= from)
            .Where(t => t.Category == null || (t.Category.Bucket != Bucket.Transfer && t.Category.Bucket != Bucket.Income))
            .Select(t => new { t.OccurredAtUtc, t.Amount, t.MyShare, t.Merchant, t.Currency, Category = t.Category!.Name, t.AccountId })
            .ToListAsync(ct);
        var charges = rows.Select(t => new Charge(
            clock.ToLocalDate(t.OccurredAtUtc), t.MyShare ?? t.Amount, t.Merchant, t.Currency, t.Category, t.AccountId));

        var dismissed = await db.DismissedRecurring.AsNoTracking().ToListAsync(ct);
        var items = Recurring.Detect(charges, today, dismissed.Select(d => (d.Pattern, d.Amount)));

        var counted = items.Where(i => i.Status != "cancelled" && i.Currency == settings.BaseCurrency).ToList();
        var monthly = counted.Sum(i => i.MonthlyCost);
        var due30 = counted.Where(i => i.DaysToNext <= 30).Sum(i => i.Amount);
        return new SubscriptionsView(monthly, monthly * 12, due30, settings.BaseCurrency, items, dismissed.Count);
    }

    /// <summary>"Not a subscription": stops matching this merchant at about this amount.</summary>
    public async Task DismissAsync(string pattern, decimal amount, CancellationToken ct = default)
    {
        var p = MerchantText.Normalize(pattern);
        var existing = await db.DismissedRecurring.Where(d => d.Pattern == p).ToListAsync(ct);
        if (existing.Any(d => Recurring.Similar(d.Amount, amount))) return;
        db.DismissedRecurring.Add(new DismissedRecurring { Pattern = p, Amount = Math.Round(amount, 2), CreatedAtUtc = clock.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Everything marked "not a subscription", to bring one back.</summary>
    public async Task<List<DismissedRecurring>> DismissedAsync(CancellationToken ct = default) =>
        (await db.DismissedRecurring.AsNoTracking().ToListAsync(ct)).OrderBy(d => d.Pattern).ToList();

    public async Task<bool> RestoreAsync(int id, CancellationToken ct = default)
    {
        var d = await db.DismissedRecurring.FindAsync([id], ct);
        if (d is null) return false;
        db.DismissedRecurring.Remove(d);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
