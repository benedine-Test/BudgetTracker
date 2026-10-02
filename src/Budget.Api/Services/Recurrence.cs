using Budget.Api.Data;

namespace Budget.Api.Services;

/// <summary>Pure date maths for recurring items.</summary>
public static class Recurrence
{
    /// <summary>
    /// The n-th occurrence (0 = the start date). Always counted from the anchor, never
    /// from the previous occurrence, so a schedule starting on the 31st lands on Feb 28
    /// and then goes back to Mar 31 instead of drifting to the 28th for good.
    /// </summary>
    public static DateOnly Occurrence(DateOnly start, RecurringFrequency frequency, int interval, int n) => frequency switch
    {
        RecurringFrequency.Weekly => start.AddDays(7 * interval * n),
        RecurringFrequency.Yearly => start.AddYears(interval * n),
        _ => start.AddMonths(interval * n)
    };

    /// <summary>First occurrence on or after a date.</summary>
    public static DateOnly OnOrAfter(DateOnly start, RecurringFrequency frequency, int interval, DateOnly date)
    {
        if (date <= start) return start;

        // Jump close to the answer, then step: avoids looping thousands of times for old anchors.
        var n = frequency switch
        {
            RecurringFrequency.Weekly => (date.DayNumber - start.DayNumber) / (7 * interval),
            RecurringFrequency.Yearly => (date.Year - start.Year) / interval,
            _ => ((date.Year - start.Year) * 12 + date.Month - start.Month) / interval
        };
        n = Math.Max(0, n - 1);
        while (Occurrence(start, frequency, interval, n) < date) n++;
        return Occurrence(start, frequency, interval, n);
    }

    /// <summary>First occurrence strictly after a date.</summary>
    public static DateOnly After(DateOnly start, RecurringFrequency frequency, int interval, DateOnly date) =>
        OnOrAfter(start, frequency, interval, date.AddDays(1));

    /// <summary>Rough cost per month, for "you have S$X a month committed".</summary>
    public static decimal MonthlyEquivalent(decimal amount, RecurringFrequency frequency, int interval) => Math.Round(frequency switch
    {
        RecurringFrequency.Weekly => amount * 52m / 12m / interval,
        RecurringFrequency.Yearly => amount / 12m / interval,
        _ => amount / interval
    }, 2);
}
