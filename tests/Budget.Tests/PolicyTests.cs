using Budget.Api.Data;
using Budget.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Budget.Tests;

public class PolicyMathTests
{
    private static readonly DateOnly Start = new(2024, 3, 8);

    private static PolicyTerms Terms(decimal premium = 500m) => new()
    {
        HoldingId = 1, CommencementDate = Start, MonthlyPremium = premium,
        InitialPeriodMonths = 60, MinimumInvestmentYears = 30,
    };

    private static readonly PolicySchedule Hsbc = PolicyPlans.HsbcWealthAccelerate();

    /// <summary>The HSBC schedule with fees and bonuses off, to check one piece of the maths at a time.</summary>
    private static PolicySchedule Bare(decimal fundCharge = 0) =>
        Hsbc with { InitialUnitsFees = [], AccumulationUnitsFees = [], AccountValueBonuses = [], FundChargePercent = fundCharge };

    private static PolicyValuation Statement(int y, int m, int d, decimal iua, decimal aua) =>
        new() { HoldingId = 1, AsOf = new DateOnly(y, m, d), InitialUnits = iua, AccumulationUnits = aua };

    [Theory]
    [InlineData("2024-03-07", 0)]
    [InlineData("2024-03-08", 1)]
    [InlineData("2025-03-07", 1)]
    [InlineData("2025-03-08", 2)]
    [InlineData("2026-03-07", 2)]
    [InlineData("2026-03-08", 3)]
    [InlineData("2026-10-08", 3)]
    [InlineData("2054-03-07", 30)]
    [InlineData("2054-03-08", 31)]
    public void Policy_year_changes_on_8_March(string date, int year) =>
        Assert.Equal(year, PolicyMath.PolicyYear(Start, DateOnly.Parse(date)));

    [Theory]
    [InlineData("2024-03-07", 0)]
    [InlineData("2024-03-08", 1)]
    [InlineData("2024-04-07", 1)]
    [InlineData("2024-04-08", 2)]
    [InlineData("2026-10-07", 31)]
    [InlineData("2026-10-08", 32)]
    public void A_premium_counts_from_its_due_date(string date, int premiums) =>
        Assert.Equal(premiums, PolicyMath.PremiumsDue(Start, DateOnly.Parse(date)));

    [Fact]
    public void Surrender_value_takes_the_charge_for_the_statement_years_policy_year()
    {
        // 7 Mar 2026 is the last day of year 2 (99%); 8 Mar 2026 is the first of year 3 (98%).
        var lastDay = PolicyMath.Position(Terms(), Hsbc, Statement(2026, 3, 7, 10_000m, 0m));
        Assert.Equal(2, lastDay.PolicyYear);
        Assert.Equal(99m, lastDay.EarlyEncashmentPercent);
        Assert.Equal(9_900m, lastDay.EarlyEncashmentCharge);
        Assert.Equal(100m, lastDay.SurrenderValue);
        Assert.Equal(12_000m, lastDay.PremiumsPaid);   // 24 premiums
        Assert.Equal(11_900m, lastDay.Gap);

        var firstDay = PolicyMath.Position(Terms(), Hsbc, Statement(2026, 3, 8, 10_000m, 0m));
        Assert.Equal(3, firstDay.PolicyYear);
        Assert.Equal(200m, firstDay.SurrenderValue);
        Assert.Equal(12_500m, firstDay.PremiumsPaid);  // the 25th premium is due that day
        Assert.Equal(12_300m, firstDay.Gap);
    }

    [Fact]
    public void Surrender_charge_is_taken_from_the_initial_units_only()
    {
        // Year 12 → 13 is the first big step down: 89% → 75%.
        var year12 = PolicyMath.Position(Terms(), Hsbc, Statement(2036, 3, 7, 20_000m, 5_000m));
        var year13 = PolicyMath.Position(Terms(), Hsbc, Statement(2036, 3, 8, 20_000m, 5_000m));

        Assert.Equal(25_000m, year12.AccountValue);
        Assert.Equal(7_200m, year12.SurrenderValue);   // 25,000 − 89% × 20,000
        Assert.Equal(10_000m, year13.SurrenderValue);  // 25,000 − 75% × 20,000
    }

    [Fact]
    public void No_surrender_charge_after_the_minimum_investment_period()
    {
        var year30 = PolicyMath.Position(Terms(), Hsbc, Statement(2054, 3, 7, 50_000m, 150_000m));
        var year31 = PolicyMath.Position(Terms(), Hsbc, Statement(2054, 3, 8, 50_000m, 150_000m));

        Assert.Equal(196_000m, year30.SurrenderValue); // 8% of the IUA
        Assert.Equal(0m, year31.EarlyEncashmentPercent);
        Assert.Equal(200_000m, year31.SurrenderValue);
    }

    [Fact]
    public void Premiums_go_to_the_initial_units_with_start_up_bonus_for_60_months_then_to_accumulation()
    {
        // Gross return equal to the fund charge: no growth, so only premiums and bonus move the accounts.
        var rows = PolicyMath.Project(Terms(), Bare(fundCharge: 1.3m), grossReturnPercent: 1.3m, toYear: 6);

        Assert.Equal(6, rows.Count);
        Assert.Equal(7_800m, rows[0].InitialUnits);        // 12 × 500 × 130%
        Assert.Equal(16_200m, rows[1].InitialUnits);       // + 12 × 500 × 140%
        Assert.Equal(42_000m, rows[4].InitialUnits);       // years 1–5: 30, 40, 40, 40, 50%
        Assert.Equal(0m, rows[4].AccumulationUnits);
        Assert.Equal(42_000m, rows[5].InitialUnits);       // year 6: no more into the IUA
        Assert.Equal(6_000m, rows[5].AccumulationUnits);
        Assert.Equal(36_000m, rows[5].PremiumsPaid);
        Assert.Equal(new DateOnly(2025, 3, 7), rows[0].EndsOn);
    }

    [Fact]
    public void Growth_is_the_gross_return_less_fund_charges_compounded_monthly()
    {
        var terms = Terms(premium: 0m);
        var rows = PolicyMath.Project(terms, Bare(fundCharge: 1.3m), 9.3m, toYear: 2, from: Statement(2024, 3, 7, 10_000m, 0m));

        Assert.Equal(10_800m, rows[0].InitialUnits);       // 8% net for a year
        Assert.Equal(11_664m, rows[1].InitialUnits);
    }

    [Fact]
    public void Account_maintenance_fee_comes_off_the_initial_units_monthly_and_stops_after_the_minimum_period()
    {
        var schedule = Bare() with { InitialUnitsFees = [new("Account Maintenance Fee", 1, 30, 3.4m)] };
        var rows = PolicyMath.Project(Terms(premium: 0m), schedule, 0m, toYear: 31, from: Statement(2024, 3, 7, 10_000m, 0m));

        var keep = 1m - 3.4m / 1200m;
        var year1 = 10_000m;
        for (var i = 0; i < 12; i++) year1 *= keep;
        Assert.Equal(Math.Round(year1, 2), rows[0].InitialUnits);
        Assert.Equal(rows[29].InitialUnits, rows[30].InitialUnits); // year 31: no fee
        Assert.True(rows[29].InitialUnits < rows[28].InitialUnits);
    }

    [Fact]
    public void Power_up_bonus_starts_in_year_15_and_loyalty_bonus_after_the_minimum_period()
    {
        var schedule = Bare() with { AccountValueBonuses = Hsbc.AccountValueBonuses };
        // From the last day of year 14 with no more premiums, so only bonuses move the value.
        var rows = PolicyMath.Project(Terms(premium: 0m), schedule, 0m, toYear: 31, from: Statement(2038, 3, 7, 0m, 100_000m));

        Assert.Equal(15, rows[0].Year);
        var powerUp = 100_000m;
        for (var i = 0; i < 12; i++) powerUp *= 1 + 1.25m / 1200m;
        Assert.Equal(Math.Round(powerUp, 2), rows[0].AccumulationUnits);

        var year30 = rows.Single(r => r.Year == 30).AccumulationUnits;
        var year31 = rows.Single(r => r.Year == 31).AccumulationUnits;
        var loyalty = year30;
        for (var i = 0; i < 12; i++) loyalty *= 1 + 1.10m / 1200m;
        Assert.Equal(Math.Round(loyalty, 0), Math.Round(year31, 0)); // year 30's value is rounded to cents
    }

    [Fact]
    public void Projected_surrender_value_uses_each_years_charge()
    {
        var rows = PolicyMath.Project(Terms(), Hsbc, 4m, toYear: 31);

        Assert.Equal(31, rows.Count);
        Assert.Equal(186_000m, rows[^1].PremiumsPaid);
        foreach (var r in rows)
        {
            Assert.Equal(Hsbc.EarlyEncashmentIn(r.Year), r.EarlyEncashmentPercent);
            Assert.Equal(Math.Round(r.AccountValue - r.InitialUnits * r.EarlyEncashmentPercent / 100m, 2), r.SurrenderValue, 1);
        }
        Assert.Equal(0m, rows[0].SurrenderValue);              // 100% charge in year 1, all in the IUA
        Assert.Equal(rows[^1].AccountValue, rows[^1].SurrenderValue);
    }

    [Fact]
    public void A_higher_return_projects_higher_every_year()
    {
        var low = PolicyMath.Project(Terms(), Hsbc, 4m, 31);
        var high = PolicyMath.Project(Terms(), Hsbc, 8m, 31);
        Assert.All(low.Zip(high), p => Assert.True(p.Second.AccountValue > p.First.AccountValue));
    }

    [Fact]
    public void Projection_from_a_statement_starts_at_the_next_premium()
    {
        // 5 Oct 2026: 31 premiums paid, the 32nd (8 Oct) is the next one in, still year 3.
        var rows = PolicyMath.Project(Terms(), Hsbc, 4m, 31, Statement(2026, 10, 5, 9_000m, 0m));

        Assert.Equal(3, rows[0].Year);
        Assert.Equal(18_000m, rows[0].PremiumsPaid);
        Assert.Equal(29, rows.Count);
    }

    [Fact]
    public void Portfolio_line_is_premiums_at_cost_until_a_statement_then_its_value_plus_later_premiums()
    {
        var today = new DateOnly(2026, 10, 8);
        Assert.Equal((16_000m, 16_000m), PolicyMath.Figures(Terms(), null, today));
        // A statement on 1 Sep: the 8 Sep and 8 Oct premiums are added at cost.
        Assert.Equal((16_000m, 10_500m), PolicyMath.Figures(Terms(), Statement(2026, 9, 1, 9_000m, 500m), today));
    }

    [Fact]
    public void A_month_with_no_bank_debit_near_its_due_date_is_flagged()
    {
        // Debits land a few days early (2 Oct for 8 Oct). September's is missing.
        var debits = new[] { new DateOnly(2026, 7, 3), new DateOnly(2026, 8, 2), new DateOnly(2026, 10, 2) };

        var missed = PolicyMath.MissedPremiums(Start, debits, new DateOnly(2026, 10, 8));

        Assert.Equal([new DateOnly(2026, 9, 8)], missed);
    }

    [Fact]
    public void Months_before_the_first_debit_in_the_app_and_the_latest_month_are_not_flagged()
    {
        // Only the latest premium imported; earlier months are before the app's bank history.
        Assert.Empty(PolicyMath.MissedPremiums(Start, [new DateOnly(2026, 9, 2)], new DateOnly(2026, 10, 8)));
        // 8 Oct premium not imported yet on 10 Oct: too soon to call it missed.
        Assert.Empty(PolicyMath.MissedPremiums(Start, [new DateOnly(2026, 9, 2)], new DateOnly(2026, 10, 10)));
        Assert.Empty(PolicyMath.MissedPremiums(Start, [], new DateOnly(2026, 10, 8)));
    }

    [Fact]
    public void Schedule_is_stored_as_data_and_read_back_unchanged()
    {
        var back = PolicySchedule.Read(Hsbc.Write());

        Assert.Equal(Hsbc.EarlyEncashmentPercent, back.EarlyEncashmentPercent);
        Assert.Equal(Hsbc.AccountValueBonuses, back.AccountValueBonuses);
        Assert.Equal(Hsbc.Funds, back.Funds);
        Assert.Equal(1.30m, back.FundChargePercent);
        Assert.Null(PolicyMath.Invalid(back));
    }

    [Fact]
    public void Edited_schedule_is_checked()
    {
        Assert.NotNull(PolicyMath.Invalid(Hsbc with { EarlyEncashmentPercent = [100, 120] }));
        Assert.NotNull(PolicyMath.Invalid(Hsbc with { AccountValueBonuses = [new("Power-up Bonus", 15, 10, 1.25m)] }));
        Assert.NotNull(PolicyMath.Invalid(Hsbc with { Funds = [new("One fund", "USD", 90)] }));
    }
}

public class PolicySchemaUpgradeTests
{
    [Fact]
    public async Task Adds_policy_tables_to_an_existing_database()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE "Transactions" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Transactions" PRIMARY KEY AUTOINCREMENT,
                  "Amount" TEXT NOT NULL, "Merchant" TEXT NOT NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        using var db = new BudgetDbContext(new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(conn).Options);
        await SchemaUpgrade.ApplyAsync(db);
        await SchemaUpgrade.ApplyAsync(db); // second run is a no-op

        db.PolicyTerms.Add(new PolicyTerms { HoldingId = 1, CommencementDate = new DateOnly(2024, 3, 8), MonthlyPremium = 500m,
            InitialPeriodMonths = 60, MinimumInvestmentYears = 30, ScheduleJson = PolicyPlans.HsbcWealthAccelerate().Write() });
        db.PolicyValuations.Add(new PolicyValuation { HoldingId = 1, AsOf = new DateOnly(2026, 9, 30), InitialUnits = 14_321.5m, AccumulationUnits = 0m });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var v = await db.PolicyValuations.SingleAsync();
        Assert.Equal(new DateOnly(2026, 9, 30), v.AsOf);
        Assert.Equal(14_321.5m, v.InitialUnits);
        var t = await db.PolicyTerms.SingleAsync();
        Assert.Equal(98m, PolicySchedule.Read(t.ScheduleJson).EarlyEncashmentIn(3));
    }
}
