using Budget.Api.Data;

namespace Budget.Api.Services;

/// <summary>A transaction already in the app, as the matcher sees it.</summary>
public record KnownTx(int Id, DateOnly Date, decimal Amount, bool IsIncome, string Merchant, int? AccountId);

/// <summary>
/// Pairs statement lines with transactions already in the app, so only the missing ones are added.
/// </summary>
public static class StatementMatcher
{
    /// <summary>
    /// Banks list a card purchase on the day it posts, often 1–3 days after the tap
    /// (weekends longer), so a statement line can match an entry up to this many days earlier…
    /// </summary>
    public const int DaysBefore = 5;
    /// <summary>…or a day later (time zones, entries typed in by hand with the wrong day).</summary>
    public const int DaysAfter = 1;

    /// <summary>
    /// For each statement row, the id of the existing transaction it is (or null = missing).
    /// Same direction and exact amount are required; merchant names are only a tie-breaker,
    /// because bank text ("NETS QR 1234 KOPITIAM") rarely looks like the Apple Pay name.
    /// Each existing entry is used once, so two S$1.50 coffees on a statement need two entries.
    /// Entries already filed under a different account are never matched.
    /// </summary>
    public static int?[] Match(IReadOnlyList<StatementRow> rows, IReadOnlyList<KnownTx> known, int accountId)
    {
        var pairs = new List<(int Row, KnownTx Tx, int Score)>();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            foreach (var k in known)
            {
                if (k.AccountId is int a && a != accountId) continue;
                if (k.IsIncome != row.IsCredit || k.Amount != row.Amount) continue;
                var lag = row.Date.DayNumber - k.Date.DayNumber; // positive: statement is later
                if (lag > DaysBefore || lag < -DaysAfter) continue;

                // Lower is better: already on this account, similar name, then fewest days apart.
                var score = (k.AccountId == accountId ? 0 : 100)
                            + (MerchantText.LooksLikeSameMerchant(k.Merchant, row.Description) ? 0 : 50)
                            + Math.Abs(lag) * 2 + (lag < 0 ? 1 : 0);
                pairs.Add((r, k, score));
            }
        }

        var result = new int?[rows.Count];
        var used = new HashSet<int>();
        foreach (var (row, tx, _) in pairs.OrderBy(p => p.Score).ThenBy(p => p.Row))
        {
            if (result[row] is not null || used.Contains(tx.Id)) continue;
            result[row] = tx.Id;
            used.Add(tx.Id);
        }
        return result;
    }

    /// <summary>
    /// A card bill paid from a bank account, or the payment landing on the card. Both sides are
    /// moving your own money, so they're filed as Transfer and never count as spending.
    /// </summary>
    public static bool LooksLikeCardPayment(string description)
    {
        var d = MerchantText.Normalize(description);
        if (d.Contains("PAYMENT THANK YOU") || d.Contains("PAYMENT - THANK YOU") || d.Contains("THANK YOU FOR YOUR PAYMENT"))
            return true;
        var isPayment = d.Contains("BILL PAYMENT") || d.Contains("CARD PAYMENT") || d.Contains("PAYMENT TO") || d.Contains("AUTO PAYMENT")
                        || d.Contains("GIRO PAYMENT");
        var isCard = d.Contains("CARD") || d.Contains("VISA") || d.Contains("MASTERCARD") || d.Contains("AMEX") || d.Contains("CREDIT");
        return isPayment && isCard;
    }

    /// <summary>"SALARY", "PAYROLL", "GIRO - SALARY"… a credit worth offering as this month's pay.</summary>
    public static bool LooksLikeSalary(string description)
    {
        var d = MerchantText.Normalize(description);
        return d.Contains("SALARY") || d.Contains("PAYROLL") || d.Split(' ').Contains("SAL");
    }
}

public static class AccountMath
{
    /// <summary>
    /// Balance at a moment, worked out from the known one. Entries at or after the anchor
    /// are added on; going back before the anchor takes them off again.
    /// </summary>
    public static decimal BalanceAt(decimal anchorBalance, DateTime anchorUtc,
        IEnumerable<(DateTime AtUtc, decimal Signed)> txs, DateTime atUtc)
    {
        var balance = anchorBalance;
        foreach (var (when, signed) in txs)
        {
            if (when >= anchorUtc && when < atUtc) balance += signed;
            else if (when < anchorUtc && when >= atUtc) balance -= signed;
        }
        return balance;
    }

    public static decimal Signed(Transaction t) => t.IsIncome ? t.Amount : -t.Amount;

    /// <summary>
    /// Card statements show what you owe as a positive balance. In the app a card balance
    /// is negative when you owe, so money in and out add up the same way for every account.
    /// </summary>
    public static decimal FromStatement(decimal statementBalance, bool positiveIsOwed) =>
        positiveIsOwed ? -statementBalance : statementBalance;
}
