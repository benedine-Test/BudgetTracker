using Budget.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Services;

public record IngestResult(Transaction Transaction, bool WasDuplicate, string Message);

public class TransactionService(BudgetDbContext db, Categorizer categorizer, BudgetService budgets, Clock clock)
{
    /// <summary>How far apart two reports of the same purchase can be (bank emails can lag the tap).</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(30);

    /// <summary>Marks a S$0 tap whose fare hasn't been charged yet (bus/MRT).</summary>
    public const string FarePendingNote = "Fare pending — bus/MRT fares are charged later, usually as one BUS/MRT charge per day.";

    /// <summary>How far back a BUS/MRT charge looks for the taps it pays for (charges post a day or two late).</summary>
    public static readonly TimeSpan PendingFareLookback = TimeSpan.FromDays(3);

    /// <summary>
    /// A tap with no amount yet — a bus or MRT gate. Saved as S$0 so the trip is on record
    /// without touching the budget; the real BUS/MRT charge replaces it when it arrives.
    /// </summary>
    public async Task<IngestResult> LogPendingFareAsync(
        string merchant, string? cardName, DateTime occurredAtUtc, TransactionSource source, CancellationToken ct = default)
    {
        var categoryId = await categorizer.MatchAsync(merchant, ct)
            ?? (await db.Categories.FirstOrDefaultAsync(c => c.Name == "Transport", ct))?.Id;

        var tx = new Transaction
        {
            OccurredAtUtc = occurredAtUtc,
            Amount = 0,
            Currency = (await budgets.GetSettingsAsync(ct)).BaseCurrency,
            Merchant = merchant.Trim(),
            CardName = cardName?.Trim(),
            Notes = FarePendingNote,
            Source = source,
            CategoryId = categoryId
        };
        await LinkByCardAsync(tx, ct);
        db.Transactions.Add(tx);
        await db.SaveChangesAsync(ct);
        await LoadRefsAsync(tx, ct);

        return new IngestResult(tx, false, $"Trip at {tx.Merchant} logged. The fare is charged later and will show up then.");
    }

    /// <summary>
    /// When a transit charge with a real amount comes in, the S$0 taps it pays for are
    /// removed so each trip isn't listed twice. Call before SaveChanges.
    /// </summary>
    public async Task AbsorbPendingFaresAsync(Transaction charge, CancellationToken ct = default)
    {
        if (charge.IsIncome || charge.Amount <= 0 || !MerchantText.IsTransit(charge.Merchant)) return;

        var from = charge.OccurredAtUtc - PendingFareLookback;
        // Amount is checked after loading: SQLite can't compare decimals in SQL.
        var pending = (await db.Transactions
            .Where(t => !t.IsIncome && t.Notes == FarePendingNote
                        && t.OccurredAtUtc >= from && t.OccurredAtUtc <= charge.OccurredAtUtc)
            .ToListAsync(ct))
            .Where(t => t.Amount == 0)
            .ToList();
        if (pending.Count == 0) return;

        db.Transactions.RemoveRange(pending);
        var covers = $"Covers {pending.Count} tapped trip{(pending.Count == 1 ? "" : "s")}.";
        charge.Notes = string.IsNullOrWhiteSpace(charge.Notes) ? covers : $"{charge.Notes} {covers}";
    }

    /// <summary>
    /// Adds a spend from any automatic source. If another source already reported the
    /// same purchase (same amount, close in time, similar merchant) it's merged, not doubled.
    /// Same-source repeats are NOT merged — two identical coffees are two coffees.
    /// </summary>
    public async Task<IngestResult> IngestSpendAsync(
        decimal amount, string currency, string merchant, string? cardName,
        DateTime occurredAtUtc, TransactionSource source, string? notes, CancellationToken ct = default)
    {
        var from = occurredAtUtc - DuplicateWindow;
        var to = occurredAtUtc + DuplicateWindow;

        var candidates = await db.Transactions
            .Where(t => !t.IsIncome && t.OccurredAtUtc >= from && t.OccurredAtUtc <= to && t.Source != source)
            .ToListAsync(ct);

        var dup = candidates
            .Where(t => t.Amount == amount && t.Currency == currency && MerchantText.LooksLikeSameMerchant(t.Merchant, merchant))
            .Where(t => t.MergedSources is null || !t.MergedSources.Contains(source.ToString()))
            .OrderBy(t => Math.Abs((t.OccurredAtUtc - occurredAtUtc).Ticks))
            .FirstOrDefault();

        if (dup is not null)
        {
            dup.MergedSources = string.IsNullOrEmpty(dup.MergedSources) ? source.ToString() : $"{dup.MergedSources},{source}";
            dup.CardName ??= cardName;
            if (string.IsNullOrWhiteSpace(dup.Merchant)) dup.Merchant = merchant;
            await LinkByCardAsync(dup, ct);
            await db.SaveChangesAsync(ct);
            await LoadRefsAsync(dup, ct);
            return new IngestResult(dup, true, $"Already logged: {Money(dup.Amount, dup.Currency)} at {dup.Merchant}.");
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
        await LinkByCardAsync(tx, ct);
        db.Transactions.Add(tx);
        await AbsorbPendingFaresAsync(tx, ct);
        await db.SaveChangesAsync(ct);
        await LoadRefsAsync(tx, ct);

        return new IngestResult(tx, false, await BuildMessageAsync(tx, ct));
    }

    /// <summary>Files a tap under the account its card belongs to (see Account.CardNames). Call before SaveChanges.</summary>
    public async Task LinkByCardAsync(Transaction tx, CancellationToken ct)
    {
        if (tx.AccountId is not null || string.IsNullOrWhiteSpace(tx.CardName)) return;
        var accounts = await db.Accounts.AsNoTracking().Where(a => !a.IsArchived && a.CardNames != null).ToListAsync(ct);
        tx.AccountId = AccountService.ForCard(accounts, tx.CardName)?.Id;
    }

    private async Task LoadRefsAsync(Transaction tx, CancellationToken ct)
    {
        await db.Entry(tx).Reference(t => t.Category).LoadAsync(ct);
        await db.Entry(tx).Reference(t => t.Account).LoadAsync(ct);
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
        (amount < 0 ? "-" : "") + (currency == "SGD" ? $"S${Math.Abs(amount):N2}" : $"{currency} {Math.Abs(amount):N2}");
}
