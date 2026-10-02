using Budget.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Services;

public record IngestResult(Transaction Transaction, bool WasDuplicate, string Message);

public class TransactionService(BudgetDbContext db, Categorizer categorizer, BudgetService budgets, Clock clock)
{
    /// <summary>How far apart two reports of the same purchase can be (bank emails can lag the tap).</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Adds a spend from any automatic source. If another source already reported the
    /// same purchase (same amount, close in time, similar merchant) it's merged, not doubled.
    /// Same-source repeats are NOT merged — two identical coffees are two coffees.
    /// </summary>
    public async Task<IngestResult> IngestSpendAsync(
        decimal amount, string currency, string merchant, string? cardName,
        DateTime occurredAtUtc, TransactionSource source, string? notes, CancellationToken ct = default)
    {
        // Auto-posted bills are matched over a wider window: the debit rarely lands on the exact scheduled day.
        var from = occurredAtUtc - RecurringService.MatchWindow;
        var to = occurredAtUtc + RecurringService.MatchWindow;

        var candidates = await db.Transactions
            .Where(t => !t.IsIncome && t.OccurredAtUtc >= from && t.OccurredAtUtc <= to && t.Source != source)
            .ToListAsync(ct);

        var dup = candidates
            .Where(t => t.Source == TransactionSource.Recurring || (t.OccurredAtUtc - occurredAtUtc).Duration() <= DuplicateWindow)
            .Where(t => t.Amount == amount && t.Currency == currency && MerchantText.LooksLikeSameMerchant(t.Merchant, merchant))
            .Where(t => t.MergedSources is null || !t.MergedSources.Contains(source.ToString()))
            .OrderBy(t => Math.Abs((t.OccurredAtUtc - occurredAtUtc).Ticks))
            .FirstOrDefault();

        if (dup is not null)
        {
            dup.MergedSources = string.IsNullOrEmpty(dup.MergedSources) ? source.ToString() : $"{dup.MergedSources},{source}";
            dup.CardName ??= cardName;
            if (string.IsNullOrWhiteSpace(dup.Merchant)) dup.Merchant = merchant;
            var wasScheduled = dup.Source == TransactionSource.Recurring;
            // The real charge beats the scheduled guess for when it happened.
            if (wasScheduled) dup.OccurredAtUtc = occurredAtUtc;
            await db.SaveChangesAsync(ct);
            await db.Entry(dup).Reference(t => t.Category).LoadAsync(ct);
            return new IngestResult(dup, true, wasScheduled
                ? $"{Money(dup.Amount, dup.Currency)} at {merchant} matches recurring {dup.Merchant}. Already counted."
                : $"Already logged: {Money(dup.Amount, dup.Currency)} at {dup.Merchant}.");
        }

        var tx = new Transaction
        {
            OccurredAtUtc = occurredAtUtc,
            Amount = amount,
            Currency = currency,
            Merchant = merchant.Trim(),
            CardName = cardName?.Trim(),
            Notes = notes,
            Source = source,
            CategoryId = await categorizer.MatchAsync(merchant, ct)
        };
        db.Transactions.Add(tx);
        await db.SaveChangesAsync(ct);
        await db.Entry(tx).Reference(t => t.Category).LoadAsync(ct);

        return new IngestResult(tx, false, await BuildMessageAsync(tx, ct));
    }

    /// <summary>One-line text for the iPhone notification after a tap.</summary>
    private async Task<string> BuildMessageAsync(Transaction tx, CancellationToken ct)
    {
        var head = $"{Money(tx.Amount, tx.Currency)} at {tx.Merchant}";
        if (tx.Category is null)
            return $"{head} — uncategorised. Sort it in the app.";

        var bucket = tx.Category.Bucket;
        if (bucket is not (Bucket.Needs or Bucket.Wants))
            return $"{head} → {tx.Category.Name}.";

        var summary = await budgets.SummaryAsync(clock.ToLocalDate(tx.OccurredAtUtc), ct);
        if (!summary.SalaryRecorded)
            return $"{head} → {tx.Category.Name} ({bucket}). Record salary to see what's left.";

        var b = summary.Buckets.Single(x => x.Bucket == bucket.ToString());
        return b.Remaining < 0
            ? $"{head} → {tx.Category.Name}. {bucket} is over by {Money(-b.Remaining, "SGD")}."
            : $"{head} → {tx.Category.Name}. {bucket} left: {Money(b.Remaining, "SGD")} ({summary.DaysLeft}d, ~{Money(b.DailyAllowance, "SGD")}/day).";
    }

    public static string Money(decimal amount, string currency) =>
        currency == "SGD" ? $"S${amount:N2}" : $"{currency} {amount:N2}";
}
