namespace Budget.Web.Services;

/// <summary>What goes into one year's estimate. Income year = the calendar year the money was earned.</summary>
/// <param name="EmploymentIncome">Salary, bonus and allowances for the year, before CPF (what the IR8A will show).</param>
/// <param name="EmployeeCpf">Your own compulsory CPF on that pay: the CPF relief.</param>
/// <param name="Age">Age on 31 Dec of the income year. Sets the earned income relief. Null = under 55.</param>
/// <param name="Foreigner">Not a Singapore citizen or PR: no CPF and a higher SRS cap.</param>
public record TaxInputs(
    int IncomeYear,
    decimal EmploymentIncome,
    decimal OtherIncome,
    decimal EmployeeCpf,
    int? Age,
    bool Foreigner,
    decimal SrsContributed,
    decimal CpfTopUpSelf,
    decimal CpfTopUpFamily,
    decimal Donations,
    int ParentsLivingWith,
    int ParentsNotLivingWith,
    decimal NsmanRelief,
    decimal OtherReliefs);

public record ReliefLine(string Name, decimal Amount);

/// <param name="ReliefsClaimed">After the S$80,000 cap.</param>
/// <param name="MarginalRatePct">The rate on the last dollar: what each extra dollar of relief saves.</param>
public record TaxResult(
    int IncomeYear,
    int AssessmentYear,
    decimal TotalIncome,
    decimal DonationDeduction,
    decimal AssessableIncome,
    IReadOnlyList<ReliefLine> Reliefs,
    decimal ReliefsBeforeCap,
    decimal ReliefsClaimed,
    decimal ChargeableIncome,
    decimal TaxBeforeRebate,
    decimal Rebate,
    decimal Tax,
    decimal MarginalRatePct,
    decimal EffectiveRatePct,
    string? RebateNote);

/// <summary>One thing to do before the year ends, with what it saves worked out exactly.</summary>
/// <param name="Room">How much more you could put in and still get relief.</param>
/// <param name="Saves">Tax saved if you use all of <see cref="Room"/>.</param>
/// <param name="SavesPer1000">Tax saved per S$1,000 put in (at the current rate).</param>
/// <param name="Catch">The downside, in one line. Every one of these has one.</param>
public record TaxTip(string Key, string Title, string What, decimal Room, decimal Saves, decimal SavesPer1000, string Catch);

/// <summary>
/// Singapore resident income tax, rates from YA2024. A Year of Assessment (YA) taxes the previous
/// calendar year's income: money earned in 2026 is YA2027, filed by 18 April 2027.
/// Covers tax residents only (citizens, PRs, and foreigners working here 183 days or more in the year).
/// Runs on the phone and the server alike.
/// </summary>
public static class IncomeTax
{
    /// <summary>All personal reliefs together are capped at this each YA.</summary>
    public const decimal ReliefCap = 80_000m;
    public const decimal SrsCapLocal = 15_300m;
    public const decimal SrsCapForeigner = 35_700m;
    /// <summary>CPF cash top-up relief: up to this for yourself, and the same again for family.</summary>
    public const decimal CpfTopUpCap = 8_000m;
    public const decimal ParentLivingWith = 9_000m;
    public const decimal ParentNotLivingWith = 5_500m;

    /// <summary>(Upper limit of the band, rate %). Chargeable income above the last limit is taxed at 24%.</summary>
    private static readonly (decimal UpTo, decimal RatePct)[] Bands =
    [
        (20_000m, 0m),
        (30_000m, 2m),
        (40_000m, 3.5m),
        (80_000m, 7m),
        (120_000m, 11.5m),
        (160_000m, 15m),
        (200_000m, 18m),
        (240_000m, 19m),
        (280_000m, 19.5m),
        (320_000m, 20m),
        (500_000m, 22m),
        (1_000_000m, 23m),
        (decimal.MaxValue, 24m),
    ];

    /// <summary>
    /// The Budget's personal tax rebate for a YA, when one is known. Only years already announced are listed:
    /// an unknown year gets none, so the estimate errs on the high side.
    /// </summary>
    public static (decimal Pct, decimal Cap)? RebateFor(int assessmentYear) => assessmentYear switch
    {
        2024 => (50m, 200m),
        2025 => (60m, 200m),
        _ => null,
    };

    /// <summary>Donations to approved charities (IPCs) are deducted at 250% for gifts made up to 31 Dec 2026.</summary>
    public static decimal DonationMultiplier(int incomeYear) => incomeYear <= 2026 ? 2.5m : 1m;

    /// <summary>Tax on chargeable income, before any rebate, to the cent.</summary>
    public static decimal TaxOn(decimal chargeable)
    {
        if (chargeable <= 0) return 0m;
        decimal tax = 0, from = 0;
        foreach (var (upTo, rate) in Bands)
        {
            if (chargeable <= from) break;
            var slice = Math.Min(chargeable, upTo) - from;
            tax += slice * rate / 100m;
            from = upTo;
        }
        return Math.Round(tax, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>The rate on the next dollar of chargeable income.</summary>
    public static decimal MarginalRatePct(decimal chargeable)
    {
        foreach (var (upTo, rate) in Bands)
            if (chargeable < upTo) return rate;
        return Bands[^1].RatePct;
    }

    /// <summary>S$1,000 under 55, S$6,000 from 55 to 59, S$8,000 from 60. Never more than what you earned.</summary>
    public static decimal EarnedIncomeRelief(int? age, decimal earned)
    {
        var relief = age switch { >= 60 => 8_000m, >= 55 => 6_000m, _ => 1_000m };
        return Math.Max(0, Math.Min(relief, earned));
    }

    public static decimal SrsCap(bool foreigner) => foreigner ? SrsCapForeigner : SrsCapLocal;

    public static TaxResult Estimate(TaxInputs i)
    {
        var ya = i.IncomeYear + 1;
        var total = Math.Max(0, i.EmploymentIncome) + Math.Max(0, i.OtherIncome);
        var donationDeduction = Math.Round(Math.Max(0, i.Donations) * DonationMultiplier(i.IncomeYear), 2);
        var assessable = Math.Max(0, total - donationDeduction);

        var reliefs = new List<ReliefLine>();
        void Add(string name, decimal amount)
        {
            if (amount > 0) reliefs.Add(new ReliefLine(name, Math.Round(amount, 2)));
        }

        Add("Earned income relief", EarnedIncomeRelief(i.Age, i.EmploymentIncome + Math.Max(0, i.OtherIncome)));
        if (!i.Foreigner) Add("CPF relief (your own CPF)", i.EmployeeCpf);
        Add("SRS contributions", Math.Min(Math.Max(0, i.SrsContributed), SrsCap(i.Foreigner)));
        if (!i.Foreigner)
        {
            Add("CPF cash top-up: yourself", Math.Min(Math.Max(0, i.CpfTopUpSelf), CpfTopUpCap));
            Add("CPF cash top-up: family", Math.Min(Math.Max(0, i.CpfTopUpFamily), CpfTopUpCap));
        }
        var parents = Math.Clamp(i.ParentsLivingWith, 0, 2);
        var parentsApart = Math.Clamp(i.ParentsNotLivingWith, 0, 2 - parents);
        Add("Parent relief", parents * ParentLivingWith + parentsApart * ParentNotLivingWith);
        Add("NSman relief", i.NsmanRelief);
        Add("Other reliefs", i.OtherReliefs);

        var before = reliefs.Sum(r => r.Amount);
        var claimed = Math.Min(before, ReliefCap);
        var chargeable = Math.Max(0, assessable - claimed);
        var gross = TaxOn(chargeable);

        decimal rebate = 0;
        string? note;
        if (RebateFor(ya) is { } r)
        {
            rebate = Math.Min(Math.Round(gross * r.Pct / 100m, 2), r.Cap);
            note = $"Includes the YA{ya} rebate: {r.Pct:0}% of the tax, up to S${r.Cap:0}.";
        }
        else
        {
            note = $"No YA{ya} tax rebate is counted: none was known when this was written. If the Budget announces one, your bill will be lower.";
        }

        var tax = Math.Max(0, gross - rebate);
        return new TaxResult(i.IncomeYear, ya, total, donationDeduction, assessable, reliefs, before, claimed, chargeable,
            gross, rebate, tax, MarginalRatePct(chargeable),
            total > 0 ? Math.Round(tax / total * 100m, 2) : 0m, note);
    }

    /// <summary>
    /// What you could still do before 31 Dec to bring the bill down, most saved first. Each saving is the
    /// difference between two full estimates, so the S$80,000 cap and band edges are already counted.
    /// Nothing is suggested when you'd pay no tax anyway.
    /// </summary>
    public static IReadOnlyList<TaxTip> Tips(TaxInputs i)
    {
        var now = Estimate(i);
        var tips = new List<TaxTip>();
        if (now.TaxBeforeRebate <= 0) return tips;

        void Try(string key, string title, string what, decimal room, Func<TaxInputs, decimal, TaxInputs> apply, string downside, bool perAmount = true)
        {
            if (room < 1) return;
            var after = Estimate(apply(i, room));
            var saves = now.Tax - after.Tax;
            if (saves < 1) return;
            var per1000 = 0m;
            if (perAmount)
            {
                per1000 = now.Tax - Estimate(apply(i, Math.Min(1000m, room))).Tax;
                if (room < 1000m) per1000 = per1000 / room * 1000m;
            }
            tips.Add(new TaxTip(key, title, what, Math.Round(room, 2), Math.Round(saves, 2), Math.Round(per1000, 2), downside));
        }

        var srsRoom = SrsCap(i.Foreigner) - Math.Max(0, i.SrsContributed);
        Try("srs", "Put money into SRS",
            $"Open an SRS account at DBS, OCBC or UOB and pay in up to {Money(srsRoom)} more by 31 Dec. The money can then be invested.",
            srsRoom, (x, a) => x with { SrsContributed = x.SrsContributed + a },
            "Locked until the statutory retirement age (63 today). Taking it out early is taxed in full plus a 5% penalty; at retirement half of what you take out is taxed.");

        if (!i.Foreigner)
        {
            var selfRoom = CpfTopUpCap - Math.Max(0, i.CpfTopUpSelf);
            Try("cpf-self", "Top up your own CPF in cash",
                $"Top up your Special Account (or Retirement Account from 55) with up to {Money(selfRoom)} by 31 Dec, through the CPF website or app.",
                selfRoom, (x, a) => x with { CpfTopUpSelf = x.CpfTopUpSelf + a },
                "This never comes back out as cash before retirement: it pays your CPF LIFE payouts. Earns 4% or more a year. Only counts up to the Full Retirement Sum.");

            var familyRoom = CpfTopUpCap - Math.Max(0, i.CpfTopUpFamily);
            Try("cpf-family", "Top up your parents' CPF",
                $"Top up a parent's, grandparent's or sibling's Special or Retirement Account with up to {Money(familyRoom)} by 31 Dec. Money you'd give your parents anyway can go here first.",
                familyRoom, (x, a) => x with { CpfTopUpFamily = x.CpfTopUpFamily + a },
                "Only counts if they earned S$4,000 or less last year (not needed for a disabled family member). They get it as CPF LIFE payouts, not cash they can spend now.");
        }

        if (i.ParentsLivingWith + i.ParentsNotLivingWith < 2)
        {
            var room = (2 - i.ParentsLivingWith - i.ParentsNotLivingWith) * ParentNotLivingWith;
            Try("parents", "Check if you can claim parent relief",
                $"If a parent is 55 or older, lives in Singapore, earned S$8,000 or less last year, and you gave them at least S$2,000 this year, you can claim S$5,500 each (S$9,000 if they live with you). No money needs to move.",
                room, (x, _) => x with { ParentsNotLivingWith = 2 - x.ParentsLivingWith },
                "Only one child can claim for each parent; agree with your siblings who does. Rules shown are a summary: check IRAS before you claim.", perAmount: false);
        }

        var donate = 1000m;
        var multiplier = DonationMultiplier(i.IncomeYear);
        var donated = Estimate(i with { Donations = i.Donations + donate });
        var donationSaves = now.Tax - donated.Tax;
        if (donationSaves >= 1)
            tips.Add(new TaxTip("donate", "Give to an approved charity",
                $"Gifts to an IPC charity by 31 Dec are taken off your income at {multiplier * 100m:0}%. A S$1,000 gift costs you {Money(donate - donationSaves)} after tax.",
                0, Math.Round(donationSaves, 2), Math.Round(donationSaves, 2),
                "You still pay more than you save: only give what you meant to give. Give with your NRIC so it's counted for you."));

        return tips.OrderByDescending(t => t.Saves).ToList();
    }

    private static string Money(decimal v) => "S$" + v.ToString("#,##0", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>What you type in on the Tax screen for one income year. Blank override = from your salary entries.</summary>
public record TaxPlanDto(
    decimal? EmploymentIncomeOverride,
    decimal? ExpectedMonthlyPay,
    decimal ExpectedBonus,
    decimal OtherIncome,
    decimal SrsContributed,
    decimal CpfTopUpSelf,
    decimal CpfTopUpFamily,
    decimal Donations,
    int ParentsLivingWith,
    int ParentsNotLivingWith,
    decimal NsmanRelief,
    decimal OtherReliefs,
    bool Foreigner);

/// <summary>How the year's employment income was worked out.</summary>
/// <param name="SalaryEntries">Salary entries found in the year.</param>
/// <param name="PaidSoFar">Base pay before CPF on those entries (worked back from take-home pay).</param>
/// <param name="MonthsLeft">Months of the year (Jan to Dec) with no salary entry, counted at <see cref="MonthlyPay"/>.</param>
/// <param name="FromOverride">The year's figure was typed in, so entries and projection aren't used.</param>
public record TaxIncomeView(
    int SalaryEntries,
    decimal PaidSoFar,
    decimal CpfSoFar,
    int MonthsLeft,
    decimal MonthlyPay,
    decimal StillToCome,
    decimal Bonus,
    decimal EmploymentIncome,
    decimal EmployeeCpf,
    bool FromOverride);

/// <param name="ActBy">Last day to do anything that counts for this income year (31 Dec).</param>
/// <param name="DaysToAct">Days left until <see cref="ActBy"/>; negative once it's passed.</param>
/// <param name="FileBy">The e-filing deadline for this YA.</param>
/// <param name="GiroMonthly">The bill split over IRAS's 12 interest-free GIRO instalments.</param>
public record TaxView(
    int Year,
    int AssessmentYear,
    DateOnly ActBy,
    int DaysToAct,
    DateOnly FileBy,
    TaxPlanDto Plan,
    TaxIncomeView Income,
    TaxResult Result,
    IReadOnlyList<TaxTip> Tips,
    decimal GiroMonthly,
    IReadOnlyList<string> Notes);
