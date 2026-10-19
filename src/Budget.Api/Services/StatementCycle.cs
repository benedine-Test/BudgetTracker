namespace Budget.Api.Services;

/// <summary>
/// Pure date maths for credit card statement cycles. A card with statement day 12 closes on the
/// 12th: spending on the 12th is on that statement, spending on the 13th is on the next one.
/// Day 29–31 closes on the last day of shorter months.
/// </summary>
public static class StatementCycle
{
    public static DateOnly ClosingIn(int year, int month, int statementDay)
    {
        var day = Math.Clamp(statementDay, 1, DateTime.DaysInMonth(year, month));
        return new DateOnly(year, month, day);
    }

    /// <summary>The cycle a date falls in: first day (inclusive) and closing day (inclusive).</summary>
    public static (DateOnly Start, DateOnly Closes) For(DateOnly date, int statementDay)
    {
        var thisMonth = ClosingIn(date.Year, date.Month, statementDay);
        DateOnly closes;
        if (date <= thisMonth)
        {
            closes = thisMonth;
        }
        else
        {
            var next = date.AddMonths(1);
            closes = ClosingIn(next.Year, next.Month, statementDay);
        }

        var prevMonth = closes.AddMonths(-1);
        var previousClose = ClosingIn(prevMonth.Year, prevMonth.Month, statementDay);
        return (previousClose.AddDays(1), closes);
    }
}

public static class StatementMath
{
    /// <summary>
    /// What a statement cycle adds to the bill: purchases less refunds. Money in that is a
    /// payment (filed as Transfer) pays off the card, so it doesn't reduce the cycle's charges.
    /// Pending S$0 taps aren't counted.
    /// </summary>
    public static (decimal Charged, int Count) Charged(IEnumerable<(decimal Amount, bool IsIncome, bool IsPayment)> entries)
    {
        decimal total = 0;
        var count = 0;
        foreach (var (amount, isIncome, isPayment) in entries)
        {
            if (amount == 0) continue;
            if (!isIncome)
            {
                total += amount;
                count++;
            }
            else if (!isPayment)
            {
                total -= amount;
            }
        }
        return (total, count);
    }
}
