using System.Text.Json;
using Budget.Api.Data;

namespace Budget.Api.Services;

/// <summary>A rate that applies in policy years <see cref="FromYear"/> to <see cref="ToYear"/> inclusive (null = for good).</summary>
public record YearRate(string Name, int FromYear, int? ToYear, decimal PercentPerYear)
{
    public bool AppliesIn(int year) => year >= FromYear && (ToYear is null || year <= ToYear);
}

public record FundShare(string Name, string Currency, decimal Percent);

/// <summary>
/// The tables in a policy contract. Lists "by policy year" start at year 1; a year past the end of
/// the list is 0. Rates are percent, e.g. 3.4 for 3.4%.
/// </summary>
public record PolicySchedule(
    // % of each regular premium added to the Initial Units Account, by policy year.
    List<decimal> StartUpBonusPercent,
    // % a year of the Initial Units Account value, taken monthly.
    List<YearRate> InitialUnitsFees,
    // % a year of the Accumulation Units Account value, taken monthly.
    List<YearRate> AccumulationUnitsFees,
    // % a year of the total account value, paid monthly into the Accumulation Units Account.
    List<YearRate> AccountValueBonuses,
    // Early Encashment Charge: % of the Initial Units Account value kept on surrender, by policy year.
    List<decimal> EarlyEncashmentPercent,
    // Fund managers' charges, already inside fund prices. Projections take it off the gross return.
    decimal FundChargePercent,
    // % of an Accumulation Units withdrawal kept during the Minimum Investment Period. Stored, not used yet.
    decimal PartialWithdrawalChargePercent,
    List<FundShare> Funds)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static PolicySchedule Read(string json) =>
        JsonSerializer.Deserialize<PolicySchedule>(json, Json) ?? throw new InvalidOperationException("Empty policy schedule.");

    public string Write() => JsonSerializer.Serialize(this, Json);

    public decimal StartUpBonusIn(int year) => ByYear(StartUpBonusPercent, year);
    public decimal EarlyEncashmentIn(int year) => ByYear(EarlyEncashmentPercent, year);

    private static decimal ByYear(List<decimal> list, int year) => year >= 1 && year <= list.Count ? list[year - 1] : 0;

    public static decimal RateIn(IEnumerable<YearRate> rates, int year) => rates.Where(r => r.AppliesIn(year)).Sum(r => r.PercentPerYear);
}

/// <summary>Where a policy stands on a statement date.</summary>
public record PolicyPosition(
    int ValuationId, DateOnly AsOf, int PolicyYear, decimal PremiumsPaid,
    decimal InitialUnits, decimal AccumulationUnits, decimal AccountValue,
    decimal EarlyEncashmentPercent, decimal EarlyEncashmentCharge, decimal SurrenderValue,
    // Premiums paid minus surrender value: what you'd be short if you surrendered on that date.
    decimal Gap);

/// <summary>A projected policy year, valued on its last day (the day before the next anniversary).</summary>
public record ProjectionYear(
    int Year, DateOnly EndsOn, decimal PremiumsPaid, decimal InitialUnits, decimal AccumulationUnits,
    decimal AccountValue, decimal EarlyEncashmentPercent, decimal SurrenderValue);

/// <summary>
/// Pure maths for a regular-premium ILP with an Initial Units Account (IUA, premiums during the
/// Initial Contribution Period) and an Accumulation Units Account (AUA, premiums after it).
/// Policy years run anniversary to anniversary: commencing 8 Mar 2024, year 3 starts on 8 Mar 2026.
/// </summary>
public static class PolicyMath
{
    /// <summary>1 on the commencement date, 2 from the first anniversary, and so on. 0 before commencement.</summary>
    public static int PolicyYear(DateOnly commencement, DateOnly date)
    {
        if (date < commencement) return 0;
        var years = date.Year - commencement.Year;
        if (commencement.AddYears(years) > date) years--;
        return years + 1;
    }

    /// <summary>The n-th premium's due date, n from 0. Counted from commencement each time so the day never drifts.</summary>
    public static DateOnly DueDate(DateOnly commencement, int n) => commencement.AddMonths(n);

    /// <summary>Monthly premiums due on or before <paramref name="asOf"/>, the first being due on the commencement date.</summary>
    public static int PremiumsDue(DateOnly commencement, DateOnly asOf)
    {
        if (asOf < commencement) return 0;
        var months = (asOf.Year - commencement.Year) * 12 + asOf.Month - commencement.Month;
        if (DueDate(commencement, months) > asOf) months--;
        return months + 1;
    }

    /// <summary>
    /// Surrender value from statement figures: the account value less the Early Encashment Charge
    /// for the policy year the statement falls in, which is taken from the IUA only.
    /// </summary>
    public static PolicyPosition Position(PolicyTerms terms, PolicySchedule schedule, PolicyValuation v)
    {
        var year = PolicyYear(terms.CommencementDate, v.AsOf);
        var paid = PremiumsDue(terms.CommencementDate, v.AsOf) * terms.MonthlyPremium;
        var value = v.InitialUnits + v.AccumulationUnits;
        var eec = schedule.EarlyEncashmentIn(year);
        var charge = Math.Round(v.InitialUnits * eec / 100m, 2);
        var surrender = value - charge;
        return new PolicyPosition(v.Id, v.AsOf, year, paid, v.InitialUnits, v.AccumulationUnits, value, eec, charge, surrender, paid - surrender);
    }

    /// <summary>
    /// Month by month to the end of <paramref name="toYear"/>. Each month, in this order:
    /// 1. the premium goes in: to the IUA with its start-up bonus during the Initial Contribution Period, to the AUA after;
    /// 2. both accounts grow at the gross return less fund charges (an annual rate, compounded monthly);
    /// 3. a twelfth of the yearly IUA and AUA fees comes off each account;
    /// 4. a twelfth of the yearly account-value bonus is added to the AUA, on the total after fees.
    /// With <paramref name="from"/>, starts from a statement's figures, taken as standing just before the next premium.
    /// </summary>
    public static IReadOnlyList<ProjectionYear> Project(
        PolicyTerms terms, PolicySchedule schedule, decimal grossReturnPercent, int toYear, PolicyValuation? from = null)
    {
        var monthlyGrowth = (decimal)Math.Pow((double)(1m + (grossReturnPercent - schedule.FundChargePercent) / 100m), 1.0 / 12);
        var premium = terms.MonthlyPremium;
        decimal iua = 0, aua = 0;
        var first = 0;
        if (from is not null)
        {
            first = PremiumsDue(terms.CommencementDate, from.AsOf);
            iua = from.InitialUnits;
            aua = from.AccumulationUnits;
        }

        var rows = new List<ProjectionYear>();
        for (var month = first; month < toYear * 12; month++)
        {
            var year = month / 12 + 1;
            if (month < terms.InitialPeriodMonths) iua += premium * (1 + schedule.StartUpBonusIn(year) / 100m);
            else aua += premium;

            iua *= monthlyGrowth;
            aua *= monthlyGrowth;

            iua -= iua * PolicySchedule.RateIn(schedule.InitialUnitsFees, year) / 1200m;
            aua -= aua * PolicySchedule.RateIn(schedule.AccumulationUnitsFees, year) / 1200m;
            aua += (iua + aua) * PolicySchedule.RateIn(schedule.AccountValueBonuses, year) / 1200m;

            if (month % 12 != 11) continue;
            var eec = schedule.EarlyEncashmentIn(year);
            var value = Math.Round(iua + aua, 2);
            rows.Add(new ProjectionYear(year, terms.CommencementDate.AddYears(year).AddDays(-1), (month + 1) * premium,
                Math.Round(iua, 2), Math.Round(aua, 2), value, eec, Math.Round(value - iua * eec / 100m, 2)));
        }
        return rows;
    }

    /// <summary>
    /// The policy's line in the portfolio: cost = premiums due so far; value = the latest statement's
    /// account value plus premiums due since it, at cost (none before a first statement).
    /// </summary>
    public static (decimal Cost, decimal Value) Figures(PolicyTerms terms, PolicyValuation? latest, DateOnly today)
    {
        var cost = PremiumsDue(terms.CommencementDate, today) * terms.MonthlyPremium;
        if (latest is null) return (cost, cost);
        var since = PremiumsDue(terms.CommencementDate, today) - PremiumsDue(terms.CommencementDate, latest.AsOf);
        return (cost, latest.InitialUnits + latest.AccumulationUnits + Math.Max(0, since) * terms.MonthlyPremium);
    }

    /// <summary>How far a bank debit can be from its due date and still be that month's premium.</summary>
    public const int PremiumWindowDays = 15;

    /// <summary>
    /// Due dates with no bank debit near them, from the first premium found in the bank (earlier
    /// months are before the app's bank history) up to <paramref name="today"/> less the window,
    /// so a premium that is simply not imported yet isn't flagged. Each debit pays the nearest
    /// unpaid due date within <see cref="PremiumWindowDays"/>.
    /// </summary>
    public static IReadOnlyList<DateOnly> MissedPremiums(DateOnly commencement, IEnumerable<DateOnly> debits, DateOnly today)
    {
        var due = Enumerable.Range(0, PremiumsDue(commencement, today.AddDays(PremiumWindowDays)))
            .Select(n => DueDate(commencement, n)).ToList();
        var paid = new HashSet<DateOnly>();
        foreach (var d in debits.Order())
        {
            var match = due.Where(x => !paid.Contains(x) && Math.Abs(x.DayNumber - d.DayNumber) <= PremiumWindowDays)
                .OrderBy(x => Math.Abs(x.DayNumber - d.DayNumber)).Cast<DateOnly?>().FirstOrDefault();
            if (match is { } m) paid.Add(m);
        }
        if (paid.Count == 0) return [];
        var firstPaid = paid.Min();
        var checkUntil = today.AddDays(-PremiumWindowDays);
        return due.Where(x => x >= firstPaid && x <= checkUntil && !paid.Contains(x)).ToList();
    }

    /// <summary>A problem with an edited schedule, in plain words, or null.</summary>
    public static string? Invalid(PolicySchedule s)
    {
        static bool Pct(decimal p) => p is >= 0 and <= 100;
        if (s.StartUpBonusPercent is null || s.InitialUnitsFees is null || s.AccumulationUnitsFees is null
            || s.AccountValueBonuses is null || s.EarlyEncashmentPercent is null || s.Funds is null)
            return "Every table needs to be there, even if empty.";
        if (!s.StartUpBonusPercent.All(Pct) || !s.EarlyEncashmentPercent.All(Pct))
            return "Start-up bonus and surrender charge rates must be between 0 and 100%.";
        if (s.EarlyEncashmentPercent.Count > 100 || s.StartUpBonusPercent.Count > 100) return "A by-year table can have at most 100 years.";
        if (!Pct(s.FundChargePercent) || !Pct(s.PartialWithdrawalChargePercent)) return "Charges must be between 0 and 100%.";
        foreach (var r in s.InitialUnitsFees.Concat(s.AccumulationUnitsFees).Concat(s.AccountValueBonuses))
        {
            if (string.IsNullOrWhiteSpace(r.Name)) return "Give each fee and bonus a name.";
            if (r.FromYear < 1 || r.ToYear < r.FromYear) return $"{r.Name}: the years must start at 1 or later and not end before they start.";
            if (!Pct(r.PercentPerYear)) return $"{r.Name}: the rate must be between 0 and 100%.";
        }
        if (s.Funds.Count > 0 && s.Funds.Sum(f => f.Percent) != 100) return "The fund split must add up to 100%.";
        return null;
    }
}
