using Budget.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Services;

/// <summary>
/// A goal as typed in or suggested. Kind: "Limit" | "Save". A Limit names a CategoryId, or a Bucket
/// ("Needs" | "Wants") for the whole bucket; a Save goal always counts the Savings bucket.
/// </summary>
public record GoalDraft(string Kind, string? Name, int? CategoryId, string? Bucket, decimal Amount, string? Reason,
    string? Category = null);

/// <summary>How a goal went in one finished pay period.</summary>
public record GoalPeriod(DateOnly Start, DateOnly EndExclusive, decimal Amount, bool Hit);

/// <param name="SoFar">Spent (Limit) or saved (Save) in the current pay period.</param>
/// <param name="Status">Limit: "ok" | "warning" (ahead of pace) | "over". Save: "ok" | "met".</param>
/// <param name="Note">One line for the screen: "S$62.40 left, S$4.80 a day".</param>
public record GoalProgress(
    int Id, string Kind, string Name, int? CategoryId, string? Category, string? Bucket, decimal Amount, string? Reason,
    decimal SoFar, decimal PercentUsed, string Status, string Note, int DaysLeft, IReadOnlyList<GoalPeriod> Recent);

public class GoalException(string message) : Exception(message);

public class GoalService(BudgetDbContext db, BudgetService budgets, Clock clock)
{
    public const int MaxGoals = 20;
    private const int RecentPeriods = 3;

    public async Task<IReadOnlyList<GoalProgress>> ListAsync(CancellationToken ct = default)
    {
        var goals = await db.Goals.AsNoTracking().OrderBy(g => g.Kind).ThenBy(g => g.Id).ToListAsync(ct);
        if (goals.Count == 0) return [];
        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var currency = (await budgets.GetSettingsAsync(ct)).BaseCurrency;

        var today = clock.Today;
        var (current, _) = await budgets.PeriodForAsync(today, ct);
        var currentSpends = await SpendsAsync(current.StartDate, current.EndDate, ct);
        var daysTotal = Math.Max(1, current.EndDate.DayNumber - current.StartDate.DayNumber);
        var daysGone = Math.Clamp(today.DayNumber - current.StartDate.DayNumber + 1, 1, daysTotal);
        var daysLeft = Math.Max(0, current.EndDate.DayNumber - today.DayNumber);

        // The last few finished pay periods, to show whether the goal would have been kept.
        var finished = (await db.BudgetPeriods.AsNoTracking().ToListAsync(ct))
            .Where(p => p.EndDate <= current.StartDate).OrderByDescending(p => p.StartDate).Take(RecentPeriods).ToList();
        var past = new List<(BudgetPeriod Period, List<Transaction> Spends)>();
        foreach (var p in finished) past.Add((p, await SpendsAsync(p.StartDate, p.EndDate, ct)));

        return goals.Select(g =>
        {
            var soFar = Total(g, currentSpends);
            var cat = g.CategoryId is int id ? categories.GetValueOrDefault(id) : null;
            var pct = g.Amount == 0 ? 0 : Math.Round(soFar / g.Amount * 100, 1);
            var (status, note) = g.Kind == GoalKind.Limit
                ? LimitStatus(g.Amount, soFar, daysGone, daysTotal, daysLeft, currency)
                : SaveStatus(g.Amount, soFar, currency);
            var recent = past.Select(x =>
            {
                var amount = Total(g, x.Spends);
                return new GoalPeriod(x.Period.StartDate, x.Period.EndDate, amount,
                    g.Kind == GoalKind.Limit ? amount <= g.Amount : amount >= g.Amount);
            }).ToList();
            var category = g.CategoryId is null ? null : cat?.Name ?? "(category removed)";
            return new GoalProgress(g.Id, g.Kind.ToString(), g.Name, g.CategoryId, category, g.Bucket?.ToString(),
                g.Amount, g.Reason, soFar, pct, status, note, daysLeft, recent);
        }).ToList();
    }

    public async Task<Goal> CreateAsync(GoalDraft draft, CancellationToken ct = default)
    {
        if (await db.Goals.CountAsync(ct) >= MaxGoals)
            throw new GoalException($"You already have {MaxGoals} goals. Remove one first.");
        var goal = await ValidateAsync(draft, ct);
        goal.CreatedAtUtc = clock.UtcNow;
        db.Goals.Add(goal);
        await db.SaveChangesAsync(ct);
        return goal;
    }

    public async Task<Goal?> UpdateAsync(int id, decimal amount, string? name, CancellationToken ct = default)
    {
        var goal = await db.Goals.FindAsync([id], ct);
        if (goal is null) return null;
        if (amount <= 0 || amount > 1_000_000) throw new GoalException("The amount must be more than zero.");
        goal.Amount = Math.Round(amount, 2);
        if (!string.IsNullOrWhiteSpace(name)) goal.Name = Trim(name, 100);
        await db.SaveChangesAsync(ct);
        return goal;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default) =>
        await db.Goals.Where(g => g.Id == id).ExecuteDeleteAsync(ct) > 0;

    /// <summary>Checks a draft and turns it into a goal (not saved). Throws GoalException with a message for the person.</summary>
    public async Task<Goal> ValidateAsync(GoalDraft d, CancellationToken ct = default)
    {
        if (!Enum.TryParse<GoalKind>(d.Kind, true, out var kind) || !Enum.IsDefined(kind))
            throw new GoalException("A goal is either a spending limit or an amount to save.");
        if (d.Amount <= 0 || d.Amount > 1_000_000)
            throw new GoalException("The amount must be more than zero.");

        var goal = new Goal { Kind = kind, Amount = Math.Round(d.Amount, 2), Reason = Trim(d.Reason, 300) };
        if (kind == GoalKind.Save)
        {
            goal.Bucket = Bucket.Savings;
            goal.Name = Trim(d.Name, 100) ?? "Save each pay period";
            return goal;
        }

        if (d.CategoryId is int id)
        {
            var cat = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct)
                      ?? throw new GoalException("That category doesn't exist.");
            if (cat.Bucket is Bucket.Income or Bucket.Transfer)
                throw new GoalException($"{cat.Name} isn't spending, so it can't have a limit.");
            goal.CategoryId = cat.Id;
            goal.Name = Trim(d.Name, 100) ?? cat.Name;
        }
        else
        {
            if (!Enum.TryParse<Bucket>(d.Bucket, true, out var bucket) || bucket is not (Bucket.Needs or Bucket.Wants))
                throw new GoalException("Pick a category, or Needs or Wants for the whole bucket.");
            goal.Bucket = bucket;
            goal.Name = Trim(d.Name, 100) ?? $"All {bucket}";
        }
        return goal;
    }

    private static (string Status, string Note) LimitStatus(decimal limit, decimal spent, int daysGone, int daysTotal,
        int daysLeft, string ccy)
    {
        if (spent > limit) return ("over", $"{Money(spent - limit, ccy)} over the limit");
        var left = limit - spent;
        var daily = daysLeft > 0 ? $", {Money(Math.Round(left / daysLeft, 2), ccy)} a day" : "";
        // Ahead of pace: more spent than an even spread over the period would allow by now.
        var pace = limit * daysGone / daysTotal;
        var status = daysGone < daysTotal && spent > pace ? "warning" : "ok";
        return (status, $"{Money(left, ccy)} left{daily}");
    }

    private static (string Status, string Note) SaveStatus(decimal target, decimal saved, string ccy) =>
        saved >= target ? ("met", "Done for this pay period") : ("ok", $"{Money(target - saved, ccy)} to go");

    private static decimal Total(Goal g, List<Transaction> spends) => spends.Where(t => g.CategoryId is int id
        ? t.CategoryId == id
        : t.Category?.Bucket == g.Bucket).Sum(t => t.CountedAmount);

    /// <summary>Spending in a pay period, counted the same way as the budget (no transfers or income).</summary>
    private async Task<List<Transaction>> SpendsAsync(DateOnly start, DateOnly endExclusive, CancellationToken ct)
    {
        var from = clock.LocalDateStartToUtc(start);
        var to = clock.LocalDateStartToUtc(endExclusive);
        return (await db.Transactions.AsNoTracking().Include(t => t.Category)
                .Where(t => t.OccurredAtUtc >= from && t.OccurredAtUtc < to && !t.IsIncome).ToListAsync(ct))
            .Where(t => t.Category?.Bucket is not (Bucket.Transfer or Bucket.Income)).ToList();
    }

    private static string Money(decimal amount, string ccy) => TransactionService.Money(amount, ccy);

    private static string? Trim(string? s, int max)
    {
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s.Length > max ? s[..max] : s;
    }
}
