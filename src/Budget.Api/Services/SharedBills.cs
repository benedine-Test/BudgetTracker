using Budget.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Services;

/// <summary>A shared bill that still has money owed on it.</summary>
public record OwedItem(Transaction Bill, decimal Owed, decimal Repaid);

/// <summary>Thrown for a shared-bill change that doesn't add up; the message is for the person.</summary>
public class SharedBillException(string message) : Exception(message);

/// <summary>
/// Paying for friends: a bill keeps its full amount (so balances and statement matching stay right)
/// but only <see cref="Transaction.MyShare"/> counts toward the budget, in the period you paid.
/// The rest is owed back. Money coming in can be linked as a repayment (filed as Paid Back, which
/// never counts as spend or income), and whatever is never repaid can be written off into your share.
/// </summary>
public class SharedBills(BudgetDbContext db)
{
    public const string RepaymentCategory = "Paid Back";

    /// <summary>Owed on a bill: the others' part less what they've paid back.</summary>
    public static decimal Owed(Transaction bill, decimal repaid) =>
        bill.MyShare is decimal mine ? Math.Max(0, bill.Amount - mine - repaid) : 0;

    /// <summary>Total repaid per shared bill, for the given bill ids.</summary>
    public static async Task<Dictionary<int, decimal>> RepaidAsync(BudgetDbContext db, IEnumerable<int> billIds, CancellationToken ct = default)
    {
        var ids = billIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        // Summed after loading: SQLite can't sum decimals in SQL.
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => t.RepaysId != null && ids.Contains(t.RepaysId.Value))
            .Select(t => new { Bill = t.RepaysId!.Value, t.Amount }).ToListAsync(ct);
        return rows.GroupBy(r => r.Bill).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
    }

    /// <summary>
    /// Bills about to be deleted: their repayments stay (the money did come in) but become plain
    /// Other Income. Caller saves changes.
    /// </summary>
    public static async Task DetachRepaymentsAsync(BudgetDbContext db, ICollection<int> billIds, CancellationToken ct = default)
    {
        if (billIds.Count == 0) return;
        var repayments = await db.Transactions.Where(t => t.RepaysId != null && billIds.Contains(t.RepaysId.Value)).ToListAsync(ct);
        if (repayments.Count == 0) return;
        var other = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Name == "Other Income", ct);
        foreach (var r in repayments.Where(r => !billIds.Contains(r.Id)))
        {
            r.RepaysId = null;
            r.CategoryId = other?.Id;
        }
    }

    /// <summary>Open shared bills, the ones matching <paramref name="amount"/> first, then newest first.</summary>
    public async Task<IReadOnlyList<OwedItem>> OpenAsync(decimal? amount = null, CancellationToken ct = default)
    {
        var bills = await db.Transactions.AsNoTracking().Include(t => t.Category).Include(t => t.Account)
            .Where(t => t.MyShare != null && !t.IsIncome).ToListAsync(ct);
        var repaid = await RepaidAsync(db, bills.Select(b => b.Id), ct);
        var open = bills
            .Select(b => new OwedItem(b, Owed(b, repaid.GetValueOrDefault(b.Id)), repaid.GetValueOrDefault(b.Id)))
            .Where(o => o.Owed > 0);
        return (amount is decimal a
                ? open.OrderBy(o => o.Owed == a ? 0 : o.Owed > a ? 1 : 2).ThenByDescending(o => o.Bill.OccurredAtUtc)
                : open.OrderByDescending(o => o.Bill.OccurredAtUtc))
            .ToList();
    }

    /// <summary>Total still owed to you, in the given currency.</summary>
    public async Task<decimal> TotalOwedAsync(string currency, CancellationToken ct = default) =>
        (await OpenAsync(ct: ct)).Where(o => o.Bill.Currency == currency).Sum(o => o.Owed);

    /// <summary>Sets your share of a bill; null makes it all yours again (only while nothing has been repaid).</summary>
    public async Task<Transaction> SetShareAsync(int billId, decimal? myShare, string? sharedWith, CancellationToken ct = default)
    {
        var bill = await BillAsync(billId, ct);
        var repaid = (await RepaidAsync(db, [billId], ct)).GetValueOrDefault(billId);
        if (myShare is null)
        {
            if (repaid > 0)
                throw new SharedBillException("Some of this has been paid back. Unlink those repayments first, or write off what's left.");
            bill.MyShare = null;
            bill.SharedWith = null;
        }
        else
        {
            var mine = Math.Round(myShare.Value, 2);
            if (mine < 0) throw new SharedBillException("Your share can't be below zero.");
            if (mine >= bill.Amount && repaid == 0)
                throw new SharedBillException($"Your share has to be less than the {TransactionService.Money(bill.Amount, bill.Currency)} you paid.");
            if (mine + repaid > bill.Amount)
                throw new SharedBillException(
                    $"{TransactionService.Money(repaid, bill.Currency)} has been paid back already, so your share can be at most {TransactionService.Money(bill.Amount - repaid, bill.Currency)}.");
            bill.MyShare = mine;
            bill.SharedWith = Clean(sharedWith) ?? bill.SharedWith;
        }
        await db.SaveChangesAsync(ct);
        return bill;
    }

    /// <summary>Links money that came in as a repayment of a bill.</summary>
    public async Task<Transaction> LinkAsync(int billId, int repaymentId, CancellationToken ct = default)
    {
        var bill = await BillAsync(billId, ct);
        var repayment = await db.Transactions.Include(t => t.Category).FirstOrDefaultAsync(t => t.Id == repaymentId, ct)
                        ?? throw new SharedBillException("That money-in entry doesn't exist.");
        if (!repayment.IsIncome) throw new SharedBillException("Only money coming in can pay back a bill.");
        if (repayment.Category?.Name == "Salary") throw new SharedBillException("A salary can't pay back a bill.");
        if (repayment.RepaysId == billId) return repayment;
        if (repayment.RepaysId is not null) throw new SharedBillException("That money already pays back another bill. Unlink it there first.");
        await CheckFitsAsync(bill, repayment.Amount, ct);

        repayment.RepaysId = billId;
        repayment.CategoryId = await RepaymentCategoryIdAsync(ct);
        repayment.CategoryConfirmed = true;
        await db.SaveChangesAsync(ct);
        return repayment;
    }

    /// <summary>Records a repayment that isn't in the app yet (cash, or before the bank statement).</summary>
    public async Task<Transaction> RecordAsync(int billId, decimal amount, DateTime occurredAtUtc, int? accountId, string? note,
        CancellationToken ct = default)
    {
        var bill = await BillAsync(billId, ct);
        amount = Math.Round(amount, 2);
        if (amount <= 0) throw new SharedBillException("Amount must be above zero.");
        if (accountId is int aid && !await db.Accounts.AnyAsync(a => a.Id == aid, ct))
            throw new SharedBillException($"Account {aid} doesn't exist.");
        await CheckFitsAsync(bill, amount, ct);

        var repayment = new Transaction
        {
            OccurredAtUtc = occurredAtUtc, Amount = amount, Currency = bill.Currency, IsIncome = true,
            Merchant = Clean(note) ?? (bill.SharedWith is { } who ? $"{who} paid back" : "Paid back"),
            Source = TransactionSource.Manual, AccountId = accountId, RepaysId = bill.Id,
            CategoryId = await RepaymentCategoryIdAsync(ct), CategoryConfirmed = true
        };
        db.Transactions.Add(repayment);
        await db.SaveChangesAsync(ct);
        return repayment;
    }

    /// <summary>Makes a repayment plain Other Income again.</summary>
    public async Task<Transaction> UnlinkAsync(int repaymentId, CancellationToken ct = default)
    {
        var repayment = await db.Transactions.FirstOrDefaultAsync(t => t.Id == repaymentId, ct)
                        ?? throw new SharedBillException("That entry doesn't exist.");
        if (repayment.RepaysId is null) return repayment;
        repayment.RepaysId = null;
        repayment.CategoryId = (await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Name == "Other Income", ct))?.Id;
        await db.SaveChangesAsync(ct);
        return repayment;
    }

    /// <summary>
    /// They're not paying: what's still owed becomes your share, so it counts as spending in the
    /// period you paid the bill.
    /// </summary>
    public async Task<Transaction> WriteOffAsync(int billId, CancellationToken ct = default)
    {
        var bill = await BillAsync(billId, ct);
        if (bill.MyShare is null) throw new SharedBillException("This bill isn't shared, so nothing is owed on it.");
        var repaid = (await RepaidAsync(db, [billId], ct)).GetValueOrDefault(billId);
        bill.MyShare = bill.Amount - repaid;
        await db.SaveChangesAsync(ct);
        return bill;
    }

    private async Task<Transaction> BillAsync(int id, CancellationToken ct)
    {
        var bill = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id, ct)
                   ?? throw new SharedBillException("That entry doesn't exist.");
        if (bill.IsIncome) throw new SharedBillException("Only spending can be shared.");
        if (bill.Amount <= 0) throw new SharedBillException("This entry has no amount yet.");
        return bill;
    }

    private async Task CheckFitsAsync(Transaction bill, decimal amount, CancellationToken ct)
    {
        if (bill.MyShare is null) throw new SharedBillException("Set your share of this bill first, so the app knows what's owed.");
        var owed = Owed(bill, (await RepaidAsync(db, [bill.Id], ct)).GetValueOrDefault(bill.Id));
        if (amount > owed)
            throw new SharedBillException(owed == 0
                ? "Nothing is owed on this bill any more."
                : $"Only {TransactionService.Money(owed, bill.Currency)} is still owed on this bill, less than {TransactionService.Money(amount, bill.Currency)}.");
    }

    private async Task<int> RepaymentCategoryIdAsync(CancellationToken ct) =>
        (await db.Categories.AsNoTracking().SingleAsync(c => c.Name == RepaymentCategory, ct)).Id;

    private static string? Clean(string? s)
    {
        var t = s?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > 100 ? t[..100] : t;
    }
}
