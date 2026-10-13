using System.Text.Json.Nodes;
using Budget.Api.Data;
using Budget.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Budget.Tests;

/// <summary>FWD Invest First Summit through the same model as the HSBC plan (see PolicyMathTests).</summary>
public class FwdPolicyTests
{
    private static readonly DateOnly Start = new(2025, 7, 21);
    private static readonly PolicySchedule Fwd = PolicyPlans.FwdInvestFirstSummit();

    private static PolicyTerms Terms(decimal premium = 300m) => new()
    {
        HoldingId = 2, CommencementDate = Start, MonthlyPremium = premium,
        InitialPeriodMonths = 24, MinimumInvestmentYears = 0, PremiumTermYears = 30,
    };

    /// <summary>FWD's schedule with fees, bonuses and charges off, to check one piece at a time.</summary>
    private static PolicySchedule Bare(decimal fundCharge = 0) => Fwd with
    {
        StartUpBonusPercent = [], InitialUnitsFees = [], AccumulationUnitsFees = [], AccountValueBonuses = [],
        PremiumCharges = [], FundChargePercent = fundCharge,
    };

    private static PolicyValuation Statement(int y, int m, int d, decimal initial, decimal accumulation) =>
        new() { HoldingId = 2, AsOf = new DateOnly(y, m, d), InitialUnits = initial, AccumulationUnits = accumulation };

    /// <summary>Month index (0 = first premium, 21 Jul 2025) of a premium due date.</summary>
    private static int Month(int y, int m) => (y - 2025) * 12 + m - 7;

    [Theory]
    [InlineData("2025-07-20", 0)]
    [InlineData("2025-07-21", 1)]
    [InlineData("2026-07-20", 1)]
    [InlineData("2026-07-21", 2)]
    [InlineData("2026-10-12", 2)]
    [InlineData("2027-07-20", 2)]
    [InlineData("2027-07-21", 3)]
    [InlineData("2055-07-21", 31)]
    public void Policy_year_changes_on_21_July(string date, int year) =>
        Assert.Equal(year, PolicyMath.PolicyYear(Start, DateOnly.Parse(date)));

    [Theory]
    [InlineData("2025-07-20", 0)]
    [InlineData("2025-07-21", 1)]
    [InlineData("2025-08-20", 1)]
    [InlineData("2025-08-21", 2)]
    [InlineData("2026-10-12", 15)]
    [InlineData("2055-06-21", 360)]
    [InlineData("2055-07-21", 360)]   // the premium term has ended: no 361st premium
    [InlineData("2060-01-01", 360)]
    public void Premiums_are_due_on_the_21st_and_stop_after_the_30_year_term(string date, int premiums) =>
        Assert.Equal(premiums, PolicyMath.PremiumsDue(Terms(), DateOnly.Parse(date)));

    [Fact]
    public void Surrender_value_takes_the_charge_for_the_statement_years_policy_year_from_the_initial_account()
    {
        // 20 Jul 2027 is the last day of year 2 (100%); 21 Jul 2027 is the first of year 3 (99%).
        var lastDay = PolicyMath.Position(Terms(), Fwd, Statement(2027, 7, 20, 10_000m, 3_000m));
        Assert.Equal(2, lastDay.PolicyYear);
        Assert.Equal(3_000m, lastDay.SurrenderValue);     // all of the initial account is kept
        Assert.Equal(7_200m, lastDay.PremiumsPaid);       // 24 premiums
        Assert.Equal(4_200m, lastDay.Gap);

        var firstDay = PolicyMath.Position(Terms(), Fwd, Statement(2027, 7, 21, 10_000m, 3_000m));
        Assert.Equal(3, firstDay.PolicyYear);
        Assert.Equal(99m, firstDay.EarlyEncashmentPercent);
        Assert.Equal(3_100m, firstDay.SurrenderValue);    // 13,000 − 99% × 10,000
        Assert.Equal(7_500m, firstDay.PremiumsPaid);      // the 25th premium is due that day

        // Year 31: no surrender charge.
        var after = PolicyMath.Position(Terms(), Fwd, Statement(2055, 7, 21, 7_000m, 160_000m));
        Assert.Equal(167_000m, after.SurrenderValue);
        Assert.Equal(108_000m, after.PremiumsPaid);
    }

    [Fact]
    public void Agrees_with_FWDs_statement_of_29_September_2026()
    {
        // Statement of Account: initial units S$6,798.55, accumulation S$0, policy value S$6,798.55,
        // indicative surrender value S$0.00, total premiums received S$4,500.00.
        var p = PolicyMath.Position(Terms(), Fwd, Statement(2026, 9, 29, 6_798.55m, 0m));

        Assert.Equal(6_798.55m, p.AccountValue);
        Assert.Equal(0m, p.SurrenderValue);
        Assert.Equal(4_500m, p.PremiumsPaid);
        Assert.Equal(4_500m, p.Gap);
        // That month's booster: S$45 on each S$150 half of the premium, i.e. 30%.
        Assert.Equal(45m / 150m * 100m, Fwd.StartUpBonusIn(2));
    }

    [Fact]
    public void Accumulation_charge_is_capped_at_756_a_year_above_50_400()
    {
        var schedule = Bare() with { AccumulationUnitsFees = Fwd.AccumulationUnitsFees };
        // From the last day of year 4, no premiums, no growth: only the charge moves the account.
        PolicyValuation From(decimal aua) => Statement(2029, 7, 20, 0m, aua);
        decimal Year5(decimal aua) => PolicyMath.Project(Terms(premium: 0m), schedule, 0m, 5, From(aua)).Single().AccumulationUnits;

        // Above the switch-over the cap applies every month: S$63 × 12.
        Assert.Equal(60_000m - 756m, Year5(60_000m));

        // Below it, 1.5% a year of the value, a twelfth each month.
        var below = 40_000m;
        for (var i = 0; i < 12; i++) below -= below * 1.5m / 1200m;
        Assert.Equal(Math.Round(below, 2), Year5(40_000m));

        // Crossing it: S$63 a month while the account is above S$50,400, then 1.5% once below.
        var crossing = 50_700m;
        var months = new List<decimal>();
        for (var i = 0; i < 12; i++)
        {
            var fee = Math.Min(crossing * 1.5m / 1200m, 63m);
            months.Add(fee);
            crossing -= fee;
        }
        Assert.Equal(Math.Round(crossing, 2), Year5(50_700m));
        Assert.Equal(63m, months[0]);
        Assert.True(months[^1] < 63m);
        Assert.Equal(63m, 50_400m * 1.5m / 1200m);       // where the two meet
    }

    [Theory]
    [InlineData(3_600, 0, 1)]
    [InlineData(2_700, 0, 0.75)]      // 3 premiums missed
    [InlineData(3_600, 900, 0.75)]    // a withdrawal
    [InlineData(0, 0, 0)]
    [InlineData(1_000, 2_000, 0)]     // can't go below 0
    [InlineData(4_000, 0, 1)]         // or above 1
    public void Loyalty_adjustment_factor(decimal paid, decimal withdrawn, decimal factor) =>
        Assert.Equal(factor, PolicyMath.LoyaltyFactor(paid, withdrawn, 3_600m));

    [Fact]
    public void Loyalty_bonus_is_paid_at_year_end_on_the_accumulation_account_scaled_by_premiums_paid()
    {
        var schedule = Bare() with { AccountValueBonuses = Fwd.AccountValueBonuses };
        // From the last day of year 3 with S$10,000 in the accumulation account (and S$5,000 in the initial
        // account, which the bonus ignores). Year 4 premiums all go to the accumulation account.
        var from = Statement(2028, 7, 20, 5_000m, 10_000m);

        var full = PolicyMath.Project(Terms(), schedule, 0m, 4, from).Single();
        Assert.Equal(13_600m + 13_600m * 0.006m, full.AccumulationUnits);          // 81.60 bonus

        // Same year with Oct, Nov and Dec 2028 missed: 9 premiums, factor 0.75.
        var missed = new HashSet<int> { Month(2028, 10), Month(2028, 11), Month(2028, 12) };
        var short3 = PolicyMath.Project(Terms(), schedule, 0m, 4, from, missed).Single();
        Assert.Equal(12_700m + 12_700m * 0.006m * 0.75m, short3.AccumulationUnits); // 57.15 bonus
        Assert.Equal(5_000m, short3.InitialUnits);
    }

    [Fact]
    public void Loyalty_bonus_steps_up_in_year_11_and_becomes_the_perpetual_bonus_after_the_premium_term()
    {
        var schedule = Bare() with { AccountValueBonuses = Fwd.AccountValueBonuses };
        var rows = PolicyMath.Project(Terms(premium: 0m), schedule, 0m, 31, Statement(2034, 7, 20, 0m, 100_000m));

        // No premiums at all here, so the premium-scaled loyalty bonus is 0; only the perpetual bonus pays.
        Assert.All(rows.Where(r => r.Year <= 30), r => Assert.Equal(100_000m, r.AccumulationUnits));
        Assert.Equal(101_000m, rows.Single(r => r.Year == 31).AccumulationUnits);

        // With premiums paid in full, years 10 and 11 pay 0.6% then 1.5% on the account at year end.
        var paid = PolicyMath.Project(Terms(), schedule, 0m, 11, Statement(2034, 7, 20, 0m, 100_000m));
        Assert.Equal((100_000m + 3_600m) * 1.006m, paid[0].AccumulationUnits);
        Assert.Equal(((100_000m + 3_600m) * 1.006m + 3_600m) * 1.015m, paid[1].AccumulationUnits, 2);
    }

    [Fact]
    public void Booster_goes_to_the_initial_account_for_3_years_even_after_premiums_switch_to_accumulation()
    {
        var schedule = Bare(fundCharge: 1.3m) with { StartUpBonusPercent = Fwd.StartUpBonusPercent };
        var rows = PolicyMath.Project(Terms(), schedule, 1.3m, 4);

        Assert.Equal(4_680m, rows[0].InitialUnits);         // 12 × 300 × 130%
        Assert.Equal(9_360m, rows[1].InitialUnits);
        Assert.Equal(10_440m, rows[2].InitialUnits);        // + 30% booster on year 3's premiums
        Assert.Equal(3_600m, rows[2].AccumulationUnits);    // year 3's premiums themselves
        Assert.Equal(10_440m, rows[3].InitialUnits);        // year 4: no booster
    }

    [Fact]
    public void Booster_is_not_paid_on_a_missed_premium()
    {
        var schedule = Bare(fundCharge: 1.3m) with { StartUpBonusPercent = Fwd.StartUpBonusPercent };
        var rows = PolicyMath.Project(Terms(), schedule, 1.3m, 1, missedMonths: new HashSet<int> { 3, 4 });
        Assert.Equal(3_900m, rows[0].InitialUnits);         // 10 × 300 × 130%
        Assert.Equal(3_000m, rows[0].PremiumsPaid);
    }

    [Fact]
    public void Shortfall_charge_starts_after_24_missed_months_in_a_row_and_stops_when_premiums_resume()
    {
        var schedule = Bare() with { PremiumCharges = Fwd.PremiumCharges };
        // Year 3's premiums go in, then 30 in a row are missed (Jul 2028 – Dec 2030), then they resume.
        var missed = Enumerable.Range(Month(2028, 7), 30).ToHashSet();
        var rows = PolicyMath.Project(Terms(), schedule, 0m, 6, missedMonths: missed);

        // Months 25–30 of the run (Jul–Dec 2030) are charged S$27 each (9% × 3,600 ÷ 12).
        Assert.Equal(3_600m, rows[4].AccumulationUnits);                       // year 5: none charged yet
        Assert.Equal(3_600m + 6 * 300m - 6 * 27m, rows[5].AccumulationUnits);   // year 6
    }

    [Fact]
    public void Projection_runs_to_year_31_with_premiums_stopping_after_year_30()
    {
        var rows = PolicyMath.Project(Terms(), Fwd, 4m, PolicyMath.ProjectionYears(Fwd));
        Assert.Equal(31, rows.Count);
        Assert.Equal(108_000m, rows[29].PremiumsPaid);
        Assert.Equal(108_000m, rows[30].PremiumsPaid);
        Assert.Equal(rows[30].AccountValue, rows[30].SurrenderValue);
    }

    /// <summary>
    /// FWD's own illustration at 4% gross (1.30% fund charge). Surrender values agree within 1%. Policy
    /// values run up to ~7% lower in early years: the illustration includes a promotional Booster Bonus
    /// (about an extra 30% in year 1) that isn't in the contract, so the model leaves it out.
    /// </summary>
    [Theory]
    [InlineData(3, 14_847, 3_734, 0.08)]
    [InlineData(10, 41_177, 31_465, 0.03)]
    [InlineData(30, 168_579, 167_405, 0.01)]
    public void Roughly_matches_FWDs_illustration_at_4_percent(int year, double policyValue, double surrender, double valueTolerance)
    {
        var row = PolicyMath.Project(Terms(), Fwd, 4m, 31).Single(r => r.Year == year);

        Assert.InRange((double)row.SurrenderValue, surrender * 0.99, surrender * 1.01);
        Assert.InRange((double)row.AccountValue, policyValue * (1 - valueTolerance), policyValue * (1 + valueTolerance));
    }

    [Fact]
    public void Illustration_promo_booster_explains_the_policy_value_gap()
    {
        // With 60% in year 1 the model reproduces FWD's year-1 and year-3 policy values to within S$15.
        var promo = Fwd with { StartUpBonusPercent = [60, 30, 30] };
        var rows = PolicyMath.Project(Terms(), promo, 4m, 3);
        Assert.InRange(rows[0].AccountValue, 5_719m - 15, 5_719m + 15);
        Assert.InRange(rows[2].AccountValue, 14_847m - 15, 14_847m + 15);
    }

    /// <summary>The bank's text for an FWD premium, as the UOB app shows it (policy number made up).</summary>
    private const string FwdDebit = "Inward DR - GIRO 12345678-20261021 OTHR 12345678 FWD SINGAPORE PTE.";

    [Theory]
    [InlineData("12345678")]
    [InlineData("FWD SINGAPORE")]
    public void FWD_GIRO_debit_is_found_by_its_policy_number_or_name(string bankText)
    {
        var fwd = new Holding { Id = 2, AssetClass = AssetClass.Policy, ContributionMatch = bankText, ContributionAmount = 300m };
        var debit = new Transaction { Id = 1, Merchant = FwdDebit, Amount = 300m, OccurredAtUtc = new DateTime(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc) };

        Assert.Single(Portfolio.MatchContributions([fwd], [debit], "SGD"));
        Assert.Contains(MerchantText.Normalize(bankText), MerchantText.Normalize(FwdDebit));   // the budget rule matches too
    }

    [Fact]
    public void FWD_debits_taken_9_days_before_the_due_date_count_for_that_month()
    {
        // UOB takes the 21 Oct premium on 12 Oct; earlier months on about the same lead.
        var debits = new[] { new DateOnly(2026, 8, 12), new DateOnly(2026, 9, 12), new DateOnly(2026, 10, 12) };
        Assert.Empty(PolicyMath.MissedPremiums(Start, debits, new DateOnly(2026, 11, 10), 30));
        // Drop September's: it is flagged, not taken as October's.
        Assert.Equal([new DateOnly(2026, 9, 21)],
            PolicyMath.MissedPremiums(Start, [debits[0], debits[2]], new DateOnly(2026, 11, 10), 30));
    }

    [Fact]
    public void Portfolio_measures_an_ILPs_gain_on_surrender_value_but_keeps_its_policy_value()
    {
        var ilp = new Holding { Id = 1, Name = "HSBC", AssetClass = AssetClass.Policy, Units = 1 };
        var etf = new Holding { Id = 2, Name = "ETF", AssetClass = AssetClass.Etf, Units = 10, AverageCost = 100m, LastPrice = 110m,
            LastPriceAtUtc = DateTime.UtcNow };
        var summary = Portfolio.Summarise([ilp, etf], [], DateTime.UtcNow,
            new Dictionary<int, (decimal, decimal)> { [1] = (16_000m, 26_618.63m) },
            new Dictionary<int, decimal> { [1] = 532.37m });

        var row = summary.Holdings.Single(h => h.Id == 1);
        Assert.Equal(26_618.63m, row.MarketValueBase);           // still what the accounts hold
        Assert.Equal(532.37m, row.SurrenderValueBase);
        Assert.Equal(532.37m - 16_000m, row.UnrealisedPnlBase);  // what surrendering would leave you with
        Assert.Equal(26_618.63m + 1_100m, summary.TotalValueBase);
        Assert.Equal(532.37m - 16_000m + 100m, summary.UnrealisedPnlBase);
        // An ILP with no statement yet, and any other holding, keep value − cost.
        Assert.Equal(100m, summary.Holdings.Single(h => h.Id == 2).UnrealisedPnlBase);
    }

    [Fact]
    public void Combined_view_adds_up_both_policies_each_at_its_own_statement_date()
    {
        var today = new DateOnly(2026, 10, 12);
        var hsbc = new PolicyTerms { HoldingId = 1, CommencementDate = new DateOnly(2024, 3, 8), MonthlyPremium = 500m,
            InitialPeriodMonths = 60, MinimumInvestmentYears = 30 };
        var total = PolicyMath.Combine(
        [
            (1, "HSBC Life Wealth Accelerate", hsbc, PolicyPlans.HsbcWealthAccelerate(),
                new PolicyValuation { HoldingId = 1, AsOf = new DateOnly(2026, 9, 30), InitialUnits = 14_500m }),
            (2, "FWD Invest First Summit", Terms(), Fwd, Statement(2026, 9, 30, 4_000m, 0m)),
        ], today);

        Assert.Equal(800m, total.MonthlyPremium);
        Assert.Equal(16_000m + 4_500m, total.PremiumsPaid);              // 32 + 15 premiums to today
        Assert.Equal(18_500m, total.PolicyValue);
        Assert.Equal(290m + 0m, total.SurrenderValue);                    // 2% of 14,500; FWD year 2 keeps it all
        Assert.Equal((15_500m - 290m) + (4_500m - 0m), total.Gap);        // premiums to each statement date
        Assert.Equal(0, total.WithoutStatement);
    }

    [Fact]
    public void Combined_view_leaves_out_values_of_a_policy_with_no_statement_and_premiums_of_one_paid_up()
    {
        var total = PolicyMath.Combine([(2, "FWD", Terms(), Fwd, null)], new DateOnly(2056, 1, 1));
        Assert.Equal(0m, total.MonthlyPremium);                           // past the 30-year term
        Assert.Equal(108_000m, total.PremiumsPaid);
        Assert.Equal(0m, total.PolicyValue);
        Assert.Equal(1, total.WithoutStatement);
    }

    [Fact]
    public void FWD_schedule_is_stored_as_data_and_read_back_unchanged()
    {
        var back = PolicySchedule.Read(Fwd.Write());
        Assert.Equal(Fwd.AccountValueBonuses, back.AccountValueBonuses);
        Assert.Equal(Fwd.AccumulationUnitsFees, back.AccumulationUnitsFees);
        Assert.Equal(Fwd.PremiumCharges, back.PremiumCharges);
        Assert.Equal(25, back.WithdrawalsFromMonth);
        Assert.Null(PolicyMath.Invalid(back));
        Assert.Contains("\"PolicyYearEnd\"", Fwd.Write());   // readable names, not numbers
    }

    [Fact]
    public void An_HSBC_schedule_saved_before_the_FWD_settings_existed_reads_and_projects_the_same()
    {
        // Strip every setting added for FWD, as in a schedule saved by the first version.
        var json = JsonNode.Parse(PolicyPlans.HsbcWealthAccelerate().Write())!.AsObject();
        foreach (var key in new[] { "premiumCharges", "withdrawalChargeUntil", "withdrawalsFromMonth", "topUpChargePercent" })
            json.Remove(key);
        foreach (var list in new[] { "initialUnitsFees", "accumulationUnitsFees", "accountValueBonuses" })
            foreach (var rate in json[list]!.AsArray())
                foreach (var key in new[] { "until", "capPerYear", "timing", "base", "adjustment" })
                    rate!.AsObject().Remove(key);
        var old = PolicySchedule.Read(json.ToJsonString());

        var hsbc = new PolicyTerms { CommencementDate = new DateOnly(2024, 3, 8), MonthlyPremium = 500m,
            InitialPeriodMonths = 60, MinimumInvestmentYears = 30 };
        Assert.Equal(PolicyMath.Project(hsbc, PolicyPlans.HsbcWealthAccelerate(), 4m, 31),
            PolicyMath.Project(hsbc, old, 4m, 31));
    }

    [Fact]
    public void Edited_FWD_schedule_is_checked()
    {
        Assert.NotNull(PolicyMath.Invalid(Fwd with { AccountValueBonuses =
            [new("Loyalty Bonus", 4, 10, 0.6m, Adjustment: BonusAdjustment.PremiumsPaidLessWithdrawals)] }));  // monthly can't be scaled
        Assert.NotNull(PolicyMath.Invalid(Fwd with { AccumulationUnitsFees = [new("Charge", 1, null, 1.5m, CapPerYear: -1)] }));
        Assert.NotNull(PolicyMath.Invalid(Fwd with { PremiumCharges = [new("Shortfall", PremiumChargeKind.Shortfall, 9m)] }));
    }
}

public class FwdPolicySchemaUpgradeTests
{
    [Fact]
    public async Task Adds_the_premium_term_to_a_policy_table_from_the_first_version()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE "Transactions" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Transactions" PRIMARY KEY AUTOINCREMENT,
                  "Amount" TEXT NOT NULL, "Merchant" TEXT NOT NULL);
                CREATE TABLE "PolicyTerms" ("Id" INTEGER NOT NULL CONSTRAINT "PK_PolicyTerms" PRIMARY KEY AUTOINCREMENT,
                  "HoldingId" INTEGER NOT NULL, "CommencementDate" TEXT NOT NULL, "MonthlyPremium" TEXT NOT NULL,
                  "InitialPeriodMonths" INTEGER NOT NULL, "MinimumInvestmentYears" INTEGER NOT NULL, "ScheduleJson" TEXT NOT NULL);
                INSERT INTO "PolicyTerms" ("HoldingId", "CommencementDate", "MonthlyPremium", "InitialPeriodMonths",
                  "MinimumInvestmentYears", "ScheduleJson") VALUES (1, '2024-03-08', '500.0', 60, 30, '{}');
                """;
            cmd.ExecuteNonQuery();
        }

        using var db = new BudgetDbContext(new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(conn).Options);
        await SchemaUpgrade.ApplyAsync(db);
        await SchemaUpgrade.ApplyAsync(db);

        var hsbc = await db.PolicyTerms.SingleAsync();
        Assert.Null(hsbc.PremiumTermYears);
        Assert.Equal(500m, hsbc.MonthlyPremium);
    }
}
