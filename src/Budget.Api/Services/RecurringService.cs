using Budget.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Services;

public class RecurringService(BudgetDbContext db, Clock clock)
{
    /// <summary>
    /// How far a bank alert or Apple Pay tap can be from the scheduled date and still be
    /// treated as the same bill. Direct debits often land a day or two either side.
    /// </summary>
    public static readonly TimeSpan MatchWindow = TimeSpan.FromDays(3);

    /// <summary>Posted entries get a mid-morning time so they sit on the right local day in every list.</summary>
    private static readonly TimeSpan PostAtLocalTime = TimeSpan.FromHours(9);

    /// <summary>Safety cap per item per run, in case a schedule is badly wrong.</summary>
    private const int MaxPostsPerRun = 60;

    // One poster at a time: the hourly timer and a page load can both trigger a run.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Posts every occurrence that has come due up to and including today. Safe to call
    /// as often as you like: each item remembers its next due date, so nothing posts twice.
    /// If the server was asleep for days, the missed dates are caught up on the next call.
    /// </summary>
    public async Task<int> PostDueAsync(CancellationToken ct = default)
    {
        var today = clock.Today;
        await Gate.WaitAsync(ct);
        try
        {
            var due = await db.RecurringItems
                .Where(r => r.IsActive && r.NextDueDate <= today)
                .ToListAsync(ct);

            var posted = 0;
            foreach (var item in due)
            {
                var count = 0;
                while (item.NextDueDate <= today && (item.EndDate is null || item.NextDueDate <= item.EndDate) && count < MaxPostsPerRun)
                {
                    if (await PostOneAsync(item, item.NextDueDate, ct)) posted++;
                    item.LastPostedDate = item.NextDueDate;
                    item.NextDueDate = Recurrence.After(item.StartDate, item.Frequency, item.Interval, item.NextDueDate);
                    count++;
                }
                if (item.EndDate is { } end && item.NextDueDate > end) item.IsActive = false;
                // Save per item so the transactions and the advanced due date commit together.
                await db.SaveChangesAsync(ct);
            }
            return posted;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// The next date this item should post on: the first scheduled date that is today or
    /// later and hasn't been posted yet. Used when an item is created or its schedule edited.
    /// </summary>
    public DateOnly NextDue(RecurringItem item)
    {
        var from = clock.Today;
        if (item.LastPostedDate is { } last && last >= from) from = last.AddDays(1);
        return Recurrence.OnOrAfter(item.StartDate, item.Frequency, item.Interval, from);
    }

    /// <returns>False when the payment had already come in from the bank or Apple Pay, so nothing new was added.</returns>
    private async Task<bool> PostOneAsync(RecurringItem item, DateOnly date, CancellationToken ct)
    {
        var at = clock.LocalDateStartToUtc(date) + PostAtLocalTime;
        var from = at - MatchWindow;
        var to = at + MatchWindow;

        // The bank email or Apple Pay tap may have arrived first. Don't double-count it.
        var candidates = await db.Transactions
            .Where(t => !t.IsIncome && t.Source != TransactionSource.Recurring
                        && t.Amount == item.Amount && t.OccurredAtUtc >= from && t.OccurredAtUtc <= to)
            .ToListAsync(ct);
        var existing = candidates
            .Where(t => MerchantText.LooksLikeSameMerchant(t.Merchant, item.Name))
            .Where(t => t.MergedSources is null || !t.MergedSources.Contains(nameof(TransactionSource.Recurring)))
            .OrderBy(t => Math.Abs((t.OccurredAtUtc - at).Ticks))
            .FirstOrDefault();

        if (existing is not null)
        {
            existing.MergedSources = string.IsNullOrEmpty(existing.MergedSources)
                ? nameof(TransactionSource.Recurring)
                : $"{existing.MergedSources},{nameof(TransactionSource.Recurring)}";
            if (!existing.CategoryConfirmed)
            {
                existing.CategoryId = item.CategoryId;
                existing.CategoryConfirmed = true;
            }
            return false;
        }

        var settings = await db.Settings.SingleAsync(s => s.Id == 1, ct);
        db.Transactions.Add(new Transaction
        {
            OccurredAtUtc = at,
            Amount = item.Amount,
            Currency = settings.BaseCurrency,
            Merchant = item.Name,
            Notes = item.Notes,
            Source = TransactionSource.Recurring,
            CategoryId = item.CategoryId,
            CategoryConfirmed = true
        });
        return true;
    }
}

/// <summary>
/// Runs the poster on start-up and then hourly. On hosting plans that sleep when idle this
/// timer stops too, which is why the budget and spending endpoints also trigger a run.
/// </summary>
public class RecurringPoster(IServiceScopeFactory scopes, ILogger<RecurringPoster> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var posted = await scope.ServiceProvider.GetRequiredService<RecurringService>().PostDueAsync(stoppingToken);
                if (posted > 0) log.LogInformation("Posted {Count} recurring transaction(s).", posted);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Posting recurring transactions failed; will retry next hour.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
