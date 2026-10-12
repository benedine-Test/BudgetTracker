using System.Text.Json;
using System.Text.Json.Serialization;
using Budget.Api.Data;

namespace Budget.Api.Services;

/// <summary>A length of time set by the contract, so a fee can run "to the end of" it rather than to a fixed year.</summary>
public enum PolicyPeriod { MinimumInvestmentPeriod, PremiumTerm }

/// <summary>When a bonus is credited: a twelfth each month, or the full rate on the last day of the policy year.</summary>
public enum BonusTiming { Monthly, PolicyYearEnd }

/// <summary>What a bonus is a percentage of.</summary>
public enum BonusBase { TotalAccount, AccumulationAccount }

/// <summary>
/// How a bonus is scaled down. PremiumsPaidLessWithdrawals: (regular premiums paid in the policy year
/// − withdrawals in it) ÷ the annual premium, kept between 0 and 1.
/// </summary>
public enum BonusAdjustment { None, PremiumsPaidLessWithdrawals }

public enum PremiumChargeKind
{
    /// <summary>While premiums are missed, after <see cref="PremiumCharge.AfterMissedMonths"/> in a row: % a year of the annual premium.</summary>
    Shortfall,
    /// <summary>After the premium is lowered, in the charge's years: % a year of the drop in annual premium. Stored only (the app keeps one premium).</summary>
    Reduction
}

/// <summary>
/// A rate that applies in policy years <see cref="FromYear"/> to <see cref="ToYear"/> inclusive (null = for good),
/// or, with <see cref="Until"/>, to the end of a contract period. Fees may have a yearly cap in the policy
/// currency; bonuses say when they are paid, on what, and how they are scaled.
/// </summary>
public record YearRate(string Name, int FromYear, int? ToYear, decimal PercentPerYear,
    PolicyPeriod? Until = null, decimal? CapPerYear = null,
    BonusTiming Timing = BonusTiming.Monthly, BonusBase Base = BonusBase.TotalAccount,
    BonusAdjustment Adjustment = BonusAdjustment.None)
{
    /// <summary>The last policy year it applies in: the named period's end when <see cref="Until"/> is set, else <see cref="ToYear"/>.</summary>
    public int? LastYear(PolicyTerms terms) => Until switch
    {
        PolicyPeriod.MinimumInvestmentPeriod => terms.MinimumInvestmentYears,
        PolicyPeriod.PremiumTerm => terms.PremiumTermYears,
        _ => ToYear,
    };

    public bool AppliesIn(int year, PolicyTerms terms) => year >= FromYear && (LastYear(terms) is not { } last || year <= last);

    /// <summary>A twelfth of the yearly rate on <paramref name="value"/>, no more than a twelfth of the cap.</summary>
    public decimal MonthlyOn(decimal value) =>
        CapPerYear is { } cap ? Math.Min(value * PercentPerYear / 1200m, cap / 12m) : value * PercentPerYear / 1200m;
}

/// <summary>A charge triggered by missed or lowered premiums, taken from the accumulation account.</summary>
public record PremiumCharge(string Name, PremiumChargeKind Kind, decimal PercentPerYear,
    int? AfterMissedMonths = null, int? FromYear = null, int? ToYear = null, PolicyPeriod? Until = null);

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
    // Bonuses on account value, into the Accumulation Units Account. Each says its timing, base and adjustment.
    List<YearRate> AccountValueBonuses,
    // Early Encashment Charge: % of the Initial Units Account value kept on surrender, by policy year.
    List<decimal> EarlyEncashmentPercent,
    // Fund managers' charges, already inside fund prices. Projections take it off the gross return.
    decimal FundChargePercent,
    // % of an Accumulation Units withdrawal kept, to the end of WithdrawalChargeUntil. Stored, not used yet.
    decimal PartialWithdrawalChargePercent,
    List<FundShare> Funds,
    // Added for a second plan; missing in older saved schedules, which read as "none" (how they always worked).
    List<PremiumCharge>? PremiumCharges = null,
    PolicyPeriod? WithdrawalChargeUntil = null,
    // First policy month a withdrawal is allowed (25 = from month 25). Stored only.
    int? WithdrawalsFromMonth = null,
    // % of each top-up premium kept. Stored only: top-ups aren't modelled.
    decimal? TopUpChargePercent = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static PolicySchedule Read(string json) =>
        JsonSerializer.Deserialize<PolicySchedule>(json, Json) ?? throw new InvalidOperationException("Empty policy schedule.");

    public string Write() => JsonSerializer.Serialize(this, Json);

    public decimal StartUpBonusIn(int year) => ByYear(StartUpBonusPercent, year);
    public decimal EarlyEncashmentIn(int year) => ByYear(EarlyEncashmentPercent, year);

    private static decimal ByYear(List<decimal> list, int year) => year >= 1 && year <= list.Count ? list[year - 1] : 0;

    public IEnumerable<PremiumCharge> PremiumChargesOrNone => PremiumCharges ?? [];

    /// <summary>The fees on an account for a month, each capped on its own.</summary>
    public static decimal MonthlyFees(IEnumerable<YearRate> fees, int year, PolicyTerms terms, decimal value) =>
        fees.Where(f => f.AppliesIn(year, terms)).Sum(f => f.MonthlyOn(value));
}

/// <summary>Where a policy stands on a statement date.</summary>
public record PolicyPosition(
    int ValuationId, DateOnly AsOf, int PolicyYear, decimal PremiumsPaid,
    decimal InitialUnits, decimal AccumulationUnits, decimal AccountValue,
    decimal EarlyEncashmentPercent, decimal EarlyEncashmentCharge, decimal SurrenderValue,
    // Premiums paid minus surrender value: what you'd be short if you surrendered on that date.
    decimal Gap);

/// <summary>One policy in the combined view. Values are from its latest statement (null before the first).</summary>
public record PolicyLine(int HoldingId, string Name, decimal MonthlyPremium, decimal PremiumsPaid, int PolicyYear,
    DateOnly? StatementDate, decimal? PremiumsPaidAtStatement, decimal? PolicyValue, decimal? SurrenderValue, decimal? Gap);

/// <summary>
/// Every ILP together. Monthly premium counts only policies still in their premium term; premiums paid is to
/// today. Policy and surrender value add up each policy's latest statement, and the gap compares them with
/// premiums paid to those same dates, so dates never mix within a policy.
/// </summary>
public record PoliciesTotal(decimal MonthlyPremium, decimal PremiumsPaid, decimal PolicyValue, decimal SurrenderValue,
    decimal Gap, int WithoutStatement, List<PolicyLine> Policies);

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

    /// <summary>Premiums due on or before <paramref name="asOf"/>, none after the premium term ends.</summary>
    public static int PremiumsDue(PolicyTerms terms, DateOnly asOf)
    {
        var due = PremiumsDue(terms.CommencementDate, asOf);
        return terms.PremiumTermYears is { } years ? Math.Min(due, years * 12) : due;
    }

    /// <summary>(Premiums paid in the year − withdrawals in it) ÷ the annual premium, kept between 0 and 1.</summary>
    public static decimal LoyaltyFactor(decimal premiumsPaid, decimal withdrawals, decimal annualPremium) =>
        annualPremium <= 0 ? 0 : Math.Clamp((premiumsPaid - withdrawals) / annualPremium, 0m, 1m);

    /// <summary>
    /// Surrender value from statement figures: the account value less the Early Encashment Charge
    /// for the policy year the statement falls in, which is taken from the IUA only.
    /// </summary>
    public static PolicyPosition Position(PolicyTerms terms, PolicySchedule schedule, PolicyValuation v)
    {
        var year = PolicyYear(terms.CommencementDate, v.AsOf);
        var paid = PremiumsDue(terms, v.AsOf) * terms.MonthlyPremium;
        var value = v.InitialUnits + v.AccumulationUnits;
        var eec = schedule.EarlyEncashmentIn(year);
        var charge = Math.Round(v.InitialUnits * eec / 100m, 2);
        var surrender = value - charge;
        return new PolicyPosition(v.Id, v.AsOf, year, paid, v.InitialUnits, v.AccumulationUnits, value, eec, charge, surrender, paid - surrender);
    }

    /// <summary>
    /// Month by month to the end of <paramref name="toYear"/>. Each month, in this order:
    /// 1. the premium goes in (none after the premium term, or in a <paramref name="missedMonths"/> month):
    ///    to the IUA during the Initial Contribution Period, to the AUA after; its premium bonus (by policy
    ///    year) always goes to the IUA, and isn't paid on a missed premium;
    /// 2. both accounts grow at the gross return less fund charges (an annual rate, compounded monthly);
    /// 3. a twelfth of each yearly IUA and AUA fee comes off, each no more than its cap;
    /// 4. premium charges: the shortfall charge once enough premiums in a row are missed, from the AUA
    ///    (what the AUA can't cover is owed and taken later);
    /// 5. monthly bonuses go into the AUA, all on the value after fees;
    /// 6. on the policy year's last month, the year-end bonuses, scaled by their adjustment.
    /// With <paramref name="from"/>, starts from a statement's figures, taken as standing just before the next premium.
    /// Months count from 0 at commencement.
    /// </summary>
    public static IReadOnlyList<ProjectionYear> Project(
        PolicyTerms terms, PolicySchedule schedule, decimal grossReturnPercent, int toYear, PolicyValuation? from = null,
        IReadOnlySet<int>? missedMonths = null)
    {
        var monthlyGrowth = (decimal)Math.Pow((double)(1m + (grossReturnPercent - schedule.FundChargePercent) / 100m), 1.0 / 12);
        var premium = terms.MonthlyPremium;
        var annualPremium = premium * 12;
        var premiumMonths = terms.PremiumTermYears is { } term ? term * 12 : int.MaxValue;
        var shortfall = schedule.PremiumChargesOrNone.Where(c => c.Kind == PremiumChargeKind.Shortfall).ToList();
        var monthlyBonuses = schedule.AccountValueBonuses.Where(b => b.Timing == BonusTiming.Monthly).ToList();
        var yearEndBonuses = schedule.AccountValueBonuses.Where(b => b.Timing == BonusTiming.PolicyYearEnd).ToList();

        decimal iua = 0, aua = 0, owed = 0, paidThisYear = 0, paidSoFar = 0;
        var first = 0;
        if (from is not null)
        {
            first = PremiumsDue(terms.CommencementDate, from.AsOf);
            iua = from.InitialUnits;
            aua = from.AccumulationUnits;
            paidSoFar = PremiumsDue(terms, from.AsOf) * premium;
            // Premiums already paid in the statement's policy year count toward its year-end adjustment.
            paidThisYear = Math.Min(first % 12, Math.Max(0, premiumMonths - first / 12 * 12)) * premium;
        }

        var missedInARow = 0;
        var rows = new List<ProjectionYear>();
        for (var month = first; month < toYear * 12; month++)
        {
            var year = month / 12 + 1;
            var due = month < premiumMonths;
            var paid = due && missedMonths?.Contains(month) != true;
            if (paid)
            {
                if (month < terms.InitialPeriodMonths) iua += premium;
                else aua += premium;
                iua += premium * schedule.StartUpBonusIn(year) / 100m;
                paidThisYear += premium;
                paidSoFar += premium;
            }
            missedInARow = due && !paid ? missedInARow + 1 : 0;

            iua *= monthlyGrowth;
            aua *= monthlyGrowth;

            iua -= PolicySchedule.MonthlyFees(schedule.InitialUnitsFees, year, terms, iua);
            aua -= PolicySchedule.MonthlyFees(schedule.AccumulationUnitsFees, year, terms, aua);

            owed += shortfall.Where(c => missedInARow > (c.AfterMissedMonths ?? 0) && ChargeApplies(c, year, terms))
                .Sum(c => annualPremium * c.PercentPerYear / 1200m);
            var take = Math.Min(owed, Math.Max(0, aua));
            aua -= take;
            owed -= take;

            var monthly = monthlyBonuses.Where(b => b.AppliesIn(year, terms)).Sum(b => Base(b, iua, aua) * b.PercentPerYear / 1200m);
            aua += monthly;

            if (month % 12 != 11) continue;
            var yearEnd = yearEndBonuses.Where(b => b.AppliesIn(year, terms)).Sum(b => Base(b, iua, aua) * b.PercentPerYear / 100m
                * (b.Adjustment == BonusAdjustment.PremiumsPaidLessWithdrawals ? LoyaltyFactor(paidThisYear, 0, annualPremium) : 1));
            aua += yearEnd;
            paidThisYear = 0;

            var eec = schedule.EarlyEncashmentIn(year);
            var value = Math.Round(iua + aua, 2);
            rows.Add(new ProjectionYear(year, terms.CommencementDate.AddYears(year).AddDays(-1), paidSoFar,
                Math.Round(iua, 2), Math.Round(aua, 2), value, eec, Math.Round(value - iua * eec / 100m, 2)));
        }
        return rows;
    }

    private static decimal Base(YearRate bonus, decimal iua, decimal aua) =>
        bonus.Base == BonusBase.AccumulationAccount ? aua : iua + aua;

    private static bool ChargeApplies(PremiumCharge c, int year, PolicyTerms terms)
    {
        var last = c.Until switch
        {
            PolicyPeriod.MinimumInvestmentPeriod => terms.MinimumInvestmentYears,
            PolicyPeriod.PremiumTerm => terms.PremiumTermYears,
            _ => c.ToYear,
        };
        return year >= (c.FromYear ?? 1) && (last is not { } l || year <= l);
    }

    /// <summary>The year after the surrender-charge table ends: when the charge no longer applies.</summary>
    public static int ProjectionYears(PolicySchedule schedule) => Math.Max(1, schedule.EarlyEncashmentPercent.Count + 1);

    /// <summary>
    /// The policy's line in the portfolio: cost = premiums due so far; value = the latest statement's
    /// account value plus premiums due since it, at cost (none before a first statement).
    /// </summary>
    public static (decimal Cost, decimal Value) Figures(PolicyTerms terms, PolicyValuation? latest, DateOnly today)
    {
        var cost = PremiumsDue(terms, today) * terms.MonthlyPremium;
        if (latest is null) return (cost, cost);
        var since = PremiumsDue(terms, today) - PremiumsDue(terms, latest.AsOf);
        return (cost, latest.InitialUnits + latest.AccumulationUnits + Math.Max(0, since) * terms.MonthlyPremium);
    }

    public static PoliciesTotal Combine(
        IEnumerable<(int HoldingId, string Name, PolicyTerms Terms, PolicySchedule Schedule, PolicyValuation? Latest)> policies, DateOnly today)
    {
        var lines = policies.Select(p =>
        {
            var due = PremiumsDue(p.Terms, today);
            var stillPaying = PremiumsDue(p.Terms, today.AddMonths(1)) > due || p.Terms.PremiumTermYears is null;
            var at = p.Latest is null ? null : Position(p.Terms, p.Schedule, p.Latest);
            return new PolicyLine(p.HoldingId, p.Name, stillPaying ? p.Terms.MonthlyPremium : 0, due * p.Terms.MonthlyPremium,
                PolicyYear(p.Terms.CommencementDate, today), at?.AsOf, at?.PremiumsPaid, at?.AccountValue, at?.SurrenderValue, at?.Gap);
        }).OrderBy(l => l.Name).ToList();
        var valued = lines.Where(l => l.StatementDate is not null).ToList();
        return new PoliciesTotal(lines.Sum(l => l.MonthlyPremium), lines.Sum(l => l.PremiumsPaid),
            valued.Sum(l => l.PolicyValue!.Value), valued.Sum(l => l.SurrenderValue!.Value), valued.Sum(l => l.Gap!.Value),
            lines.Count - valued.Count, lines);
    }

    /// <summary>How far a bank debit can be from its due date and still be that month's premium.</summary>
    public const int PremiumWindowDays = 15;

    /// <summary>
    /// Due dates with no bank debit near them, from the first premium found in the bank (earlier
    /// months are before the app's bank history) up to <paramref name="today"/> less the window,
    /// so a premium that is simply not imported yet isn't flagged. Each debit pays the nearest
    /// unpaid due date within <see cref="PremiumWindowDays"/>.
    /// </summary>
    public static IReadOnlyList<DateOnly> MissedPremiums(DateOnly commencement, IEnumerable<DateOnly> debits, DateOnly today,
        int? premiumTermYears = null)
    {
        var count = PremiumsDue(commencement, today.AddDays(PremiumWindowDays));
        if (premiumTermYears is { } years) count = Math.Min(count, years * 12);
        var due = Enumerable.Range(0, count)
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
            if (r.CapPerYear is < 0) return $"{r.Name}: the yearly cap can't be negative.";
            if (r.Adjustment != BonusAdjustment.None && r.Timing != BonusTiming.PolicyYearEnd)
                return $"{r.Name}: only a bonus paid at the end of the policy year can be scaled by premiums paid.";
        }
        foreach (var c in s.PremiumChargesOrNone)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) return "Give each premium charge a name.";
            if (!Pct(c.PercentPerYear)) return $"{c.Name}: the rate must be between 0 and 100%.";
            if (c.Kind == PremiumChargeKind.Shortfall && c.AfterMissedMonths is not >= 1) return $"{c.Name}: say after how many missed months it starts.";
        }
        if (s.TopUpChargePercent is { } top && !Pct(top)) return "The top-up charge must be between 0 and 100%.";
        if (s.WithdrawalsFromMonth is < 1) return "Withdrawals can start from month 1 at the earliest.";
        if (s.Funds.Count > 0 && s.Funds.Sum(f => f.Percent) != 100) return "The fund split must add up to 100%.";
        return null;
    }
}
