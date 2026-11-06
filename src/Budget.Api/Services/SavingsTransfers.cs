using Budget.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Services;

/// <summary>Thrown when a savings move can't be made; the message is for the person.</summary>
public class SavingsException(string message) : Exception(message);

/// <summary>
/// How a move between your own accounts counts in 50/30/20. Only the money-out side can ever count,
/// and only as Savings: Needs and Wants never see a transfer. The money-in side is always Transfer.
/// </summary>
public static class TransferRules
{
    /// <summary>
    /// Savings when money from a pay period goes into a pocket kept for something (Safety Fund, Travel);
    /// otherwise Transfer, which budgets ignore. Not savings: money you'd already saved being moved
    /// (<paramref name="alreadySaved"/>), money moving between savings pockets (a Saving Pocket into a
    /// Boost Pocket), and money into an account that isn't a savings pocket.
    /// </summary>
    public static Bucket OutSide(PocketTerms? from, PocketTerms? to, bool alreadySaved)
    {
        if (alreadySaved) return Bucket.Transfer;
        if (to is null || to.Purpose == PocketPurpose.Other) return Bucket.Transfer;
        if (from is not null && from.Purpose != PocketPurpose.Other) return Bucket.Transfer;
        return Bucket.Savings;
    }

    /// <summary>The category the money-out side is filed under.</summary>
    public static string OutCategory(PocketTerms? from, PocketTerms? to, bool alreadySaved) =>
        OutSide(from, to, alreadySaved) == Bucket.Transfer ? "Transfer"
        : to!.Purpose == PocketPurpose.SafetyFund ? Seed.SafetyFundCategory
        : Seed.SavingsGoalsCategory;
}

/// <summary>
/// Moves money between your own accounts as two linked entries: money out of one, money into the other
/// (see <see cref="Transaction.PairId"/>). Both balances move; how it counts in the budget is
/// <see cref="TransferRules"/>.
/// </summary>
public class SavingsTransfers(BudgetDbContext db, Clock clock)
{
    /// <summary>Months between "move the Safety fund Saving Pocket into a Boost Pocket" reminders.</summary>
    public const int RolloverMonths = 4;

    public async Task<(Transaction Out, Transaction In)> RecordAsync(int fromAccountId, int toAccountId, decimal amount,
        DateTime atUtc, bool alreadySaved, string? note, CancellationToken ct = default)
    {
        if (amount <= 0) throw new SavingsException("Enter an amount above zero.");
        if (fromAccountId == toAccountId) throw new SavingsException("Pick two different accounts.");
        var from = await db.Accounts.FirstOrDefaultAsync(a => a.Id == fromAccountId, ct)
                   ?? throw new SavingsException("The account the money leaves from no longer exists.");
        var to = await db.Accounts.FirstOrDefaultAsync(a => a.Id == toAccountId, ct)
                 ?? throw new SavingsException("The account the money goes into no longer exists.");
        if (to.Kind == AccountKind.CreditCard)
            throw new SavingsException("To pay a card bill, open the payment under Spending → Paid a card bill?");

        var terms = await db.PocketTerms.AsNoTracking()
            .Where(p => p.AccountId == fromAccountId || p.AccountId == toAccountId).ToListAsync(ct);
        var fromTerms = terms.FirstOrDefault(p => p.AccountId == fromAccountId);
        var toTerms = terms.FirstOrDefault(p => p.AccountId == toAccountId);

        var outName = TransferRules.OutCategory(fromTerms, toTerms, alreadySaved);
        var categories = await db.Categories.AsNoTracking()
            .Where(c => c.Name == outName || c.Name == "Transfer").ToListAsync(ct);
        var outCat = categories.FirstOrDefault(c => c.Name == outName)?.Id;
        var transferCat = categories.FirstOrDefault(c => c.Name == "Transfer")?.Id;
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        var amt = Math.Round(amount, 2);
        var moneyOut = new Transaction
        {
            OccurredAtUtc = atUtc, Amount = amt, Currency = from.Currency, IsIncome = false,
            Merchant = $"To {to.Name}", Notes = note, Source = TransactionSource.Manual,
            AccountId = from.Id, CategoryId = outCat, CategoryConfirmed = true
        };
        var moneyIn = new Transaction
        {
            OccurredAtUtc = atUtc, Amount = amt, Currency = to.Currency, IsIncome = true,
            Merchant = $"From {from.Name}", Notes = note, Source = TransactionSource.Manual,
            AccountId = to.Id, CategoryId = transferCat, CategoryConfirmed = true
        };
        db.Transactions.AddRange(moneyOut, moneyIn);
        await db.SaveChangesAsync(ct);
        moneyOut.PairId = moneyIn.Id;
        moneyIn.PairId = moneyOut.Id;

        // The first money into a Safety Fund Saving Pocket starts its "move it into a Boost Pocket" reminder.
        if (toTerms is { Type: PocketType.Saving, Purpose: PocketPurpose.SafetyFund }
            && !await db.Reminders.AnyAsync(r => r.AccountId == to.Id, ct))
        {
            db.Reminders.Add(new Reminder
            {
                AccountId = to.Id,
                Text = $"Move the {to.Name} Saving Pocket into a new Boost Pocket.",
                NextDue = clock.ToLocalDate(atUtc).AddMonths(RolloverMonths),
                RepeatMonths = RolloverMonths,
                CreatedAtUtc = clock.UtcNow
            });
        }
        await db.SaveChangesAsync(ct);
        return (moneyOut, moneyIn);
    }

    /// <summary>
    /// For deleting an entry: the entry, its other side if it was a linked move, and any planned-transfer
    /// tick they made. Removes them from the context; the caller saves.
    /// </summary>
    public static async Task<List<Transaction>> RemovePairAsync(BudgetDbContext db, Transaction t, CancellationToken ct = default)
    {
        var gone = new List<Transaction> { t };
        if (t.PairId is int pairId && await db.Transactions.FirstOrDefaultAsync(x => x.Id == pairId, ct) is { } other)
            gone.Add(other);
        var ids = gone.Select(x => x.Id).ToList();
        db.PlannedTransferTicks.RemoveRange(await db.PlannedTransferTicks.Where(k => ids.Contains(k.TransactionId)).ToListAsync(ct));
        db.Transactions.RemoveRange(gone);
        return gone;
    }

    /// <summary>A moment for an entry on <paramref name="day"/>: now if it's today (or none given), else midday.</summary>
    public DateTime WhenUtc(DateOnly? day) =>
        day is not { } d || d == clock.Today ? clock.UtcNow : clock.LocalDateStartToUtc(d).AddHours(12);
}
