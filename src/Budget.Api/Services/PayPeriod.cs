namespace Budget.Api.Services;

/// <summary>
/// Pure date maths for pay-period budgets. Periods run payday → next payday,
/// not calendar month: spending on the 26th belongs to the salary you got on the 25th.
/// </summary>
public static class PayPeriod
{
    /// <summary>How early salary may land and still count as that cycle's pay (weekends, public holidays).</summary>
    public const int EarlyPayToleranceDays = 7;

    public static DateOnly PaydayIn(int year, int month, int payDay)
    {
        var day = Math.Min(payDay, DateTime.DaysInMonth(year, month));
        return new DateOnly(year, month, day);
    }

    /// <summary>The nominal period (by payday setting) that contains a date.</summary>
    public static (DateOnly Start, DateOnly End) NominalFor(DateOnly date, int payDay)
    {
        var thisMonth = PaydayIn(date.Year, date.Month, payDay);
        var start = date >= thisMonth
            ? thisMonth
            : PaydayIn(date.AddMonths(-1).Year, date.AddMonths(-1).Month, payDay);
        var next = start.AddMonths(1);
        return (start, PaydayIn(next.Year, next.Month, payDay));
    }

    /// <summary>
    /// The period a salary credit starts. Salary a few days early (e.g. Friday before
    /// a Sunday payday) opens the upcoming cycle on the day it lands.
    /// </summary>
    public static (DateOnly Start, DateOnly End) ForSalary(DateOnly salaryDate, int payDay)
    {
        var upcoming = PaydayIn(salaryDate.Year, salaryDate.Month, payDay);
        if (salaryDate < upcoming && upcoming.DayNumber - salaryDate.DayNumber <= EarlyPayToleranceDays)
        {
            var next = upcoming.AddMonths(1);
            return (salaryDate, PaydayIn(next.Year, next.Month, payDay));
        }

        var nominal = NominalFor(salaryDate, payDay);
        // Late salary: the cycle still began on payday.
        return (nominal.Start, nominal.End);
    }
}
