using Budget.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Services;

public record BucketStatus(
    string Bucket,
    decimal Budget,
    decimal Spent,
    decimal Remaining,
    decimal PercentUsed,
    string Status,              // "ok" | "warning" (≥80%) | "over"
    decimal DailyAllowance);    // remaining ÷ days left, never negative

public record CategoryTotal(int? CategoryId, string Category, string Bucket, decimal Total, int Count);

public record BudgetSummary(
    DateOnly PeriodStart,
    DateOnly PeriodEndExclusive,
    int DaysLeft,
    bool SalaryRecorded,
    decimal TakeHomeIncome,
    IReadOnlyList<BucketStatus> Buckets,
    decimal UncategorisedSpend,
    int UncategorisedCount,
    decimal TotalSpent,
    decimal LeftToSpend,
    IReadOnlyList<CategoryTotal> TopCategories);

public class BudgetService(BudgetDbContext db, Clock clock)
{
    public Task<AppSettings> GetSettingsAsync(CancellationToken ct = default) =>
        db.Settings.SingleAsync(s => s.Id == 1, ct);

    /// <summary>
    /// Records a salary credit: logs the income transaction and opens (or tops up)
    /// the budget period with the current 50/30/20 split.
    /// </summary>
    public async Task<(Transaction Tx, BudgetPeriod Period)> RecordSalaryAsync(
        decimal amount, DateTime occurredAtUtc, string? note, int? accountId = null,
        TransactionSource source = TransactionSource.Manual, decimal? cpfContribution = null, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        var salaryCat = await db.Categories.SingleAsync(c => c.Name == "Salary", ct);

        var tx = new Transaction
        {
            OccurredAtUtc = occurredAtUtc,
            Amount = amount,
            Currency = settings.BaseCurrency,
            IsIncome = true,
            Merchant = "Salary",
            Notes = note,
            Source = source,
            AccountId = accountId,
            CategoryId = salaryCat.Id,
            CategoryConfirmed = true,
            CpfContribution = cpfContribution is > 0 ? Math.Round(cpfContribution.Value, 2) : null
        };
        db.Transactions.Add(tx);

        var (start, end) = PayPeriod.ForSalary(clock.ToLocalDate(occurredAtUtc), settings.PayDay);
        var periods = await db.BudgetPeriods.ToListAsync(ct);

        // Same cycle = same nominal payday it runs to. (Overlap alone isn't enough: a salary
        // that lands 2 days early overlaps the old cycle but opens a new one.)
        var same = periods.FirstOrDefault(p => p.CycleEnd == end);
        if (same is not null)
        {
            // Second credit in the same cycle (split pay, backpay): add to it.
            same.TakeHomeIncome += amount;
            if (start < same.StartDate) same.StartDate = start;
        }
        else
        {
            same = new BudgetPeriod
            {
                StartDate = start,
                EndDate = end,
                CycleEnd = end,
                TakeHomeIncome = amount,
                NeedsPct = settings.NeedsPct,
                WantsPct = settings.WantsPct,
                SavingsPct = settings.SavingsPct
            };
            db.BudgetPeriods.Add(same);
        }

        // Early salary shortens the previous cycle so no day belongs to two budgets.
        foreach (var prev in periods.Where(p => p != same && p.StartDate < same.StartDate && p.EndDate > same.StartDate))
            prev.EndDate = same.StartDate;
        // Salaries entered out of order: a later cycle already exists, so stop where it starts.
        var next = periods.Where(p => p != same && p.StartDate > same.StartDate).OrderBy(p => p.StartDate).FirstOrDefault();
        if (next is not null && next.StartDate < same.EndDate)
            same.EndDate = next.StartDate;

        await db.SaveChangesAsync(ct);
        return (tx, same);
    }

    /// <summary>Period containing a date, or a provisional one (no income yet) from the payday setting.</summary>
    public async Task<(BudgetPeriod Period, bool Recorded)> PeriodForAsync(DateOnly date, CancellationToken ct = default)
    {
        var periods = await db.BudgetPeriods.AsNoTracking().ToListAsync(ct);
        var found = periods.FirstOrDefault(p => p.StartDate <= date && date < p.EndDate);
        if (found is not null) return (found, true);

        var settings = await GetSettingsAsync(ct);
        var (start, end) = PayPeriod.NominalFor(date, settings.PayDay);
        // Don't overlap a recorded period that started early.
        var nextRecorded = periods.Where(p => p.StartDate > date).OrderBy(p => p.StartDate).FirstOrDefault();
        if (nextRecorded is not null && nextRecorded.StartDate < end) end = nextRecorded.StartDate;
        var prevRecorded = periods.Where(p => p.EndDate <= date).OrderByDescending(p => p.EndDate).FirstOrDefault();
        if (prevRecorded is not null && prevRecorded.EndDate > start) start = prevRecorded.EndDate;

        return (new BudgetPeriod
        {
            StartDate = start,
            EndDate = end,
            TakeHomeIncome = 0,
            NeedsPct = settings.NeedsPct,
            WantsPct = settings.WantsPct,
            SavingsPct = settings.SavingsPct
        }, false);
    }

    /// <summary>
    /// Undoes a salary entry's effect on its pay period (used when the entry is deleted).
    /// If that was the period's only salary, the period goes too. Caller saves changes.
    /// </summary>
    public async Task ReverseSalaryAsync(Transaction salary, CancellationToken ct = default)
    {
        var day = clock.ToLocalDate(salary.OccurredAtUtc);
        var periods = await db.BudgetPeriods.ToListAsync(ct);
        var period = periods.FirstOrDefault(p => p.StartDate <= day && day < p.EndDate);
        if (period is null) return;

        period.TakeHomeIncome -= salary.Amount;
        if (period.TakeHomeIncome > 0) return;

        db.BudgetPeriods.Remove(period);
        // If this period had started early and shortened the one before it, give those days back.
        foreach (var prev in periods.Where(p => p != period && p.EndDate == period.StartDate && p.CycleEnd > p.EndDate))
            prev.EndDate = prev.CycleEnd;
    }

    /// <summary>Budget vs actual for every recorded pay period, newest first.</summary>
    public async Task<IReadOnlyList<BudgetSummary>> HistoryAsync(int take, CancellationToken ct = default)
    {
        var starts = (await db.BudgetPeriods.AsNoTracking().Select(p => p.StartDate).ToListAsync(ct))
            .OrderByDescending(d => d).Take(take).ToList();
        var result = new List<BudgetSummary>();
        foreach (var start in starts)
            result.Add(await SummaryAsync(start, ct));
        return result;
    }

    public async Task<BudgetSummary> SummaryAsync(DateOnly date, CancellationToken ct = default)
    {
        var (period, recorded) = await PeriodForAsync(date, ct);
        var fromUtc = clock.LocalDateStartToUtc(period.StartDate);
        var toUtc = clock.LocalDateStartToUtc(period.EndDate);

        var txs = await db.Transactions.AsNoTracking()
            .Include(t => t.Category)
            .Where(t => t.OccurredAtUtc >= fromUtc && t.OccurredAtUtc < toUtc && !t.IsIncome)
            .ToListAsync(ct);

        var spends = txs.Where(t => t.Category?.Bucket is not (Bucket.Transfer or Bucket.Income)).ToList();
        // A bill paid for others counts only your share, here in the period you paid it,
        // however much later they pay you back.

        var today = clock.Today;
        var daysLeft = Math.Max(0, period.EndDate.DayNumber - Math.Max(today.DayNumber, period.StartDate.DayNumber));

        BucketStatus Status(Bucket bucket, decimal budget)
        {
            var spent = spends.Where(t => t.Category?.Bucket == bucket).Sum(t => t.CountedAmount);
            var remaining = budget - spent;
            var pct = budget == 0 ? (spent > 0 ? 100 : 0) : Math.Round(spent / budget * 100, 1);
            // For savings, "over" is good news — you saved more than planned.
            var status = bucket == Bucket.Savings
                ? (spent >= budget ? "met" : "ok")
                : pct >= 100 ? "over" : pct >= 80 ? "warning" : "ok";
            var daily = bucket != Bucket.Savings && daysLeft > 0 && remaining > 0 ? Math.Round(remaining / daysLeft, 2) : 0;
            return new BucketStatus(bucket.ToString(), budget, spent, remaining, pct, status, daily);
        }

        var buckets = new[]
        {
            Status(Bucket.Needs, period.NeedsBudget),
            Status(Bucket.Wants, period.WantsBudget),
            Status(Bucket.Savings, period.SavingsBudget),
        };

        var uncategorised = spends.Where(t => t.CategoryId is null).ToList();
        var totalSpent = spends.Sum(t => t.CountedAmount);

        var top = spends
            .GroupBy(t => t.CategoryId)
            .Select(g => new CategoryTotal(
                g.Key,
                g.First().Category?.Name ?? "Uncategorised",
                g.First().Category?.Bucket.ToString() ?? "Unassigned",
                g.Sum(t => t.CountedAmount),
                g.Count()))
            .OrderByDescending(c => c.Total)
            .Take(8)
            .ToList();

        return new BudgetSummary(
            period.StartDate, period.EndDate, daysLeft, recorded, period.TakeHomeIncome,
            buckets, uncategorised.Sum(t => t.CountedAmount), uncategorised.Count,
            totalSpent, period.TakeHomeIncome - totalSpent, top);
    }
}
