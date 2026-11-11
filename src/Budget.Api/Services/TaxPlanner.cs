using Budget.Api.Data;
using Budget.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Services;

public class TaxException(string message) : Exception(message);

/// <summary>
/// Builds a year's income tax estimate from the salary entries already in the app plus what you've
/// typed in on the Tax screen (bonus, SRS, top-ups, reliefs), and lists what would bring it down.
/// The maths is <see cref="IncomeTax"/>; this only gathers the figures.
/// </summary>
public class TaxPlanner(BudgetDbContext db, Clock clock)
{
    /// <summary>The most a bonus counts for CPF in a year: 102,000 less the year's ordinary wages up to the ceiling.</summary>
    public const decimal AnnualWageCeiling = 102_000m;

    public static TaxPlanDto Empty => new(null, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false);

    public async Task<TaxView> ViewAsync(int year, CancellationToken ct = default)
    {
        var plan = await db.TaxPlans.AsNoTracking().SingleOrDefaultAsync(p => p.Year == year, ct);
        var dto = plan is null ? Empty : ToDto(plan);
        var birth = (await db.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, ct)).BirthDate;

        var from = clock.LocalDateStartToUtc(new DateOnly(year, 1, 1));
        var to = clock.LocalDateStartToUtc(new DateOnly(year + 1, 1, 1));
        var salaries = await db.Transactions.AsNoTracking()
            .Where(t => t.IsIncome && t.Category != null && t.Category.Name == "Salary"
                        && t.OccurredAtUtc >= from && t.OccurredAtUtc < to)
            .Select(t => new { t.OccurredAtUtc, t.Amount })
            .ToListAsync(ct);
        var paid = salaries.Select(s => (clock.ToLocalDate(s.OccurredAtUtc), s.Amount)).ToList();

        return Build(year, clock.Today, dto, birth, paid);
    }

    /// <summary>The whole view from plain figures, so it can be tested without a database.</summary>
    public static TaxView Build(int year, DateOnly today, TaxPlanDto plan, DateOnly? birthDate,
        IReadOnlyList<(DateOnly Date, decimal TakeHome)> salaries)
    {
        var notes = new List<string>();
        var income = Income(year, today, plan, birthDate, salaries, notes);

        int? age = birthDate is { } b ? Payslip.AgeOn(b, new DateOnly(year, 12, 31)) : null;
        if (age is null) notes.Add("Add your date of birth under Settings → Profile for the right CPF rates and earned income relief. Until then, under 55 is assumed.");

        var inputs = new TaxInputs(year, income.EmploymentIncome, plan.OtherIncome, income.EmployeeCpf, age, plan.Foreigner,
            plan.SrsContributed, plan.CpfTopUpSelf, plan.CpfTopUpFamily, plan.Donations,
            plan.ParentsLivingWith, plan.ParentsNotLivingWith, plan.NsmanRelief, plan.OtherReliefs);
        var result = IncomeTax.Estimate(inputs);

        var actBy = new DateOnly(year, 12, 31);
        var days = actBy.DayNumber - today.DayNumber;
        var tips = days >= 0 ? IncomeTax.Tips(inputs) : [];
        if (days < 0)
            notes.Add($"It's past 31 Dec {year}, so nothing new counts for {year} income. Anything you do now counts for {today.Year} — switch to {today.Year} to plan it.");
        if (result.ReliefsBeforeCap > IncomeTax.ReliefCap)
            notes.Add($"Your reliefs add up to more than the S$80,000 cap; only S$80,000 counts.");
        if (result.Tax <= 0 && result.TotalIncome > 0)
            notes.Add("You're below the taxable line: no tax to pay, so there's no reason to lock money up just to cut tax.");

        return new TaxView(year, year + 1, actBy, days, new DateOnly(year + 1, 4, 18), plan, income, result, tips,
            Math.Round(result.Tax / 12m, 2), notes);
    }

    private static TaxIncomeView Income(int year, DateOnly today, TaxPlanDto plan, DateOnly? birthDate,
        IReadOnlyList<(DateOnly Date, decimal TakeHome)> salaries, List<string> notes)
    {
        CpfAgeBand Band(DateOnly d) => plan.Foreigner ? CpfAgeBand.NoCpf
            : birthDate is { } b ? Payslip.AgeBandOn(b, d) : CpfAgeBand.UpTo55;

        // Base pay worked back from take-home pay, as the CPF holdings do (see Payslip.BasePayFromTakeHome).
        var entries = salaries.Where(s => s.Date.Year == year)
            .Select(s => (s.Date, Gross: plan.Foreigner ? s.TakeHome : Payslip.BasePayFromTakeHome(s.TakeHome, Band(s.Date))))
            .OrderBy(s => s.Date).ToList();
        var paidSoFar = entries.Sum(e => e.Gross);
        var cpfSoFar = entries.Sum(e => Payslip.EmployeeCpf(e.Gross, Band(e.Date)));
        var owSoFar = entries.Sum(e => Math.Min(e.Gross, Payslip.OrdinaryWageCeiling));

        // The whole income year, Jan to Dec: every month without a salary entry is counted at the monthly
        // pay, whether it's before or after the entries. A past year counts only what was recorded.
        var monthsWithPay = entries.Select(e => e.Date.Month).ToHashSet();
        List<DateOnly> gaps = year < today.Year ? []
            : Enumerable.Range(1, 12).Where(m => !monthsWithPay.Contains(m)).Select(m => new DateOnly(year, m, 1)).ToList();
        var monthly = plan.ExpectedMonthlyPay ?? (entries.Count > 0 ? entries[^1].Gross : 0m);
        if (gaps.Count > 0 && monthly <= 0)
        {
            gaps = [];
            notes.Add("No salary recorded this year yet. Type your monthly base pay below to count the whole year.");
        }
        var latest = entries.Count > 0 ? entries[^1].Date.Month : 0;
        var gapsBefore = gaps.Count(g => g.Month < latest || (latest == 0 && year == today.Year && g.Month < today.Month));
        if (gapsBefore > 0)
            notes.Add($"{gapsBefore} earlier month(s) have no salary entry and are counted at {monthly:#,0.##} each. If you weren't paid then (e.g. you started mid-year), type the year's total as your employment income.");

        var dec = new DateOnly(year, 12, 1);
        var stillToCome = monthly * gaps.Count;
        var cpfToCome = gaps.Sum(g => Payslip.EmployeeCpf(monthly, Band(g)));
        var ow = owSoFar + gaps.Count * Math.Min(monthly, Payslip.OrdinaryWageCeiling);

        // A bonus pays CPF up to the annual ceiling left after ordinary wages.
        var bonus = Math.Max(0, plan.ExpectedBonus);
        var awCovered = Math.Min(bonus, Math.Max(0, AnnualWageCeiling - ow));
        var bonusCpf = Math.Floor(awCovered * Payslip.EmployeeRatePct(Band(dec)) / 100m);

        if (plan.EmploymentIncomeOverride is { } typed)
        {
            // Typed from the IR8A, bonuses included. CPF is estimated as 12 equal months of ordinary wages,
            // with anything above the monthly ceiling treated as bonus.
            var cpf = 0m;
            if (!plan.Foreigner)
            {
                var band = Band(dec);
                var owMonth = Math.Min(typed / 12m, Payslip.OrdinaryWageCeiling);
                var aw = Math.Min(typed - 12 * owMonth, Math.Max(0, AnnualWageCeiling - 12 * owMonth));
                cpf = 12 * Payslip.EmployeeCpf(owMonth, band) + Math.Floor(aw * Payslip.EmployeeRatePct(band) / 100m);
            }
            return new TaxIncomeView(entries.Count, paidSoFar, cpfSoFar, 0, 0, 0, 0, typed, cpf, true);
        }

        return new TaxIncomeView(entries.Count, Math.Round(paidSoFar, 2), cpfSoFar, gaps.Count, monthly,
            Math.Round(stillToCome, 2), bonus,
            Math.Round(paidSoFar + stillToCome + bonus, 2), cpfSoFar + cpfToCome + bonusCpf, false);
    }

    public async Task<TaxView> SaveAsync(int year, TaxPlanDto body, CancellationToken ct = default)
    {
        if (year < 2020 || year > clock.Today.Year + 1) throw new TaxException("Pick a year from 2020 to next year.");
        decimal[] amounts = [body.EmploymentIncomeOverride ?? 0, body.ExpectedMonthlyPay ?? 0, body.ExpectedBonus, body.OtherIncome,
            body.SrsContributed, body.CpfTopUpSelf, body.CpfTopUpFamily, body.Donations, body.NsmanRelief, body.OtherReliefs];
        if (amounts.Any(a => a < 0)) throw new TaxException("Amounts can't be negative.");
        if (amounts.Any(a => a > 100_000_000m)) throw new TaxException("That amount looks too big. Check the figures.");
        if (body.ParentsLivingWith is < 0 or > 2 || body.ParentsNotLivingWith is < 0 or > 2
            || body.ParentsLivingWith + body.ParentsNotLivingWith > 2)
            throw new TaxException("Parent relief covers at most two parents.");

        var plan = await db.TaxPlans.SingleOrDefaultAsync(p => p.Year == year, ct);
        if (plan is null) db.TaxPlans.Add(plan = new TaxPlan { Year = year });
        plan.EmploymentIncomeOverride = body.EmploymentIncomeOverride is > 0 ? body.EmploymentIncomeOverride : null;
        plan.ExpectedMonthlyPay = body.ExpectedMonthlyPay is > 0 ? body.ExpectedMonthlyPay : null;
        plan.ExpectedBonus = body.ExpectedBonus;
        plan.OtherIncome = body.OtherIncome;
        plan.SrsContributed = body.SrsContributed;
        plan.CpfTopUpSelf = body.CpfTopUpSelf;
        plan.CpfTopUpFamily = body.CpfTopUpFamily;
        plan.Donations = body.Donations;
        plan.ParentsLivingWith = body.ParentsLivingWith;
        plan.ParentsNotLivingWith = body.ParentsNotLivingWith;
        plan.NsmanRelief = body.NsmanRelief;
        plan.OtherReliefs = body.OtherReliefs;
        plan.Foreigner = body.Foreigner;
        plan.UpdatedAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return await ViewAsync(year, ct);
    }

    private static TaxPlanDto ToDto(TaxPlan p) => new(p.EmploymentIncomeOverride, p.ExpectedMonthlyPay, p.ExpectedBonus,
        p.OtherIncome, p.SrsContributed, p.CpfTopUpSelf, p.CpfTopUpFamily, p.Donations, p.ParentsLivingWith,
        p.ParentsNotLivingWith, p.NsmanRelief, p.OtherReliefs, p.Foreigner);
}
