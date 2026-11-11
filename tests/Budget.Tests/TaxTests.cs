using System.Text.Json;
using Budget.Api.Data;
using Budget.Api.Services;
using Budget.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Budget.Tests;

public class IncomeTaxTests
{
    private static TaxInputs Inputs(decimal employment, decimal cpf = 0, int? age = 30) =>
        new(2026, employment, 0, cpf, age, false, 0, 0, 0, 0, 0, 0, 0, 0);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(20_000, 0)]
    [InlineData(30_000, 200)]
    [InlineData(40_000, 550)]
    [InlineData(50_000, 1_250)]
    [InlineData(80_000, 3_350)]
    [InlineData(100_000, 5_650)]
    [InlineData(200_000, 21_150)]
    [InlineData(1_200_000, 247_150)]
    public void Resident_rates_from_YA2024(decimal chargeable, decimal tax) =>
        Assert.Equal(tax, IncomeTax.TaxOn(chargeable));

    [Theory]
    [InlineData(19_999, 0)]
    [InlineData(56_600, 7)]
    [InlineData(80_000, 11.5)]
    [InlineData(2_000_000, 24)]
    public void Marginal_rate(decimal chargeable, decimal rate) => Assert.Equal(rate, IncomeTax.MarginalRatePct(chargeable));

    [Fact]
    public void Salary_of_6000_a_month()
    {
        // 72,000 less CPF relief 14,400 and earned income relief 1,000 = 56,600 chargeable.
        var r = IncomeTax.Estimate(Inputs(72_000, 14_400));
        Assert.Equal(56_600m, r.ChargeableIncome);
        Assert.Equal(1_712m, r.Tax);
        Assert.Equal(2027, r.AssessmentYear);
        Assert.Equal(0m, r.Rebate); // no YA2027 rebate known
        Assert.NotNull(r.RebateNote);
    }

    [Fact]
    public void Known_rebate_is_applied_and_capped()
    {
        var r = IncomeTax.Estimate(Inputs(72_000, 14_400) with { IncomeYear = 2024 }); // YA2025: 60%, cap 200
        Assert.Equal(200m, r.Rebate);
        Assert.Equal(1_512m, r.Tax);
    }

    [Theory]
    [InlineData(null, 1_000)]
    [InlineData(54, 1_000)]
    [InlineData(55, 6_000)]
    [InlineData(62, 8_000)]
    public void Earned_income_relief_by_age(int? age, decimal relief) =>
        Assert.Equal(relief, IncomeTax.EarnedIncomeRelief(age, 50_000));

    [Fact]
    public void Reliefs_are_capped_at_80000()
    {
        var r = IncomeTax.Estimate(Inputs(300_000, 20_400) with
        {
            SrsContributed = 15_300, CpfTopUpSelf = 8_000, CpfTopUpFamily = 8_000, ParentsLivingWith = 2, OtherReliefs = 20_000
        });
        Assert.True(r.ReliefsBeforeCap > 80_000m);
        Assert.Equal(80_000m, r.ReliefsClaimed);
        Assert.Equal(220_000m, r.ChargeableIncome);
    }

    [Fact]
    public void SRS_and_top_ups_are_capped()
    {
        var r = IncomeTax.Estimate(Inputs(100_000) with { SrsContributed = 50_000, CpfTopUpSelf = 20_000 });
        Assert.Equal(15_300m, r.Reliefs.Single(l => l.Name.StartsWith("SRS")).Amount);
        Assert.Equal(8_000m, r.Reliefs.Single(l => l.Name.EndsWith("yourself")).Amount);
    }

    [Fact]
    public void Foreigners_get_no_CPF_relief_and_a_higher_SRS_cap()
    {
        var r = IncomeTax.Estimate(Inputs(100_000, 14_400) with { Foreigner = true, SrsContributed = 40_000, CpfTopUpSelf = 8_000 });
        Assert.DoesNotContain(r.Reliefs, l => l.Name.StartsWith("CPF"));
        Assert.Equal(35_700m, r.Reliefs.Single(l => l.Name.StartsWith("SRS")).Amount);
    }

    [Fact]
    public void Donations_are_deducted_at_250_percent_in_2026()
    {
        var r = IncomeTax.Estimate(Inputs(72_000, 14_400) with { Donations = 1_000 });
        Assert.Equal(2_500m, r.DonationDeduction);
        Assert.Equal(54_100m, r.ChargeableIncome);
    }

    [Fact]
    public void Full_SRS_saves_7_percent_of_it()
    {
        var tips = IncomeTax.Tips(Inputs(72_000, 14_400));
        var srs = tips.Single(t => t.Key == "srs");
        Assert.Equal(15_300m, srs.Room);
        Assert.Equal(1_071m, srs.Saves); // 15,300 × 7%: still above 40,000 after it
        Assert.Equal(70m, srs.SavesPer1000);
        Assert.Equal("srs", tips[0].Key); // biggest saving first
    }

    [Fact]
    public void Saving_stops_where_tax_does()
    {
        // 33,000 chargeable: only 13,000 is above the tax-free 20,000, so a full SRS saves the tax, not 15,300 × rate.
        var i = Inputs(34_000);
        var now = IncomeTax.Estimate(i).Tax;
        var srs = IncomeTax.Tips(i).Single(t => t.Key == "srs");
        Assert.Equal(now, srs.Saves);
    }

    [Fact]
    public void Tips_shrink_with_what_is_already_done()
    {
        var tips = IncomeTax.Tips(Inputs(72_000, 14_400) with { SrsContributed = 15_300, CpfTopUpSelf = 3_000 });
        Assert.DoesNotContain(tips, t => t.Key == "srs");
        Assert.Equal(5_000m, tips.Single(t => t.Key == "cpf-self").Room);
    }

    [Fact]
    public void Nothing_to_suggest_without_tax() => Assert.Empty(IncomeTax.Tips(Inputs(21_000))); // 20,000 chargeable after earned income relief

    [Fact]
    public void Parent_relief_tip_only_while_not_claimed_for_two()
    {
        Assert.Contains(IncomeTax.Tips(Inputs(72_000, 14_400)), t => t.Key == "parents");
        Assert.DoesNotContain(IncomeTax.Tips(Inputs(72_000, 14_400) with { ParentsLivingWith = 2 }), t => t.Key == "parents");
    }

    [Fact]
    public void A_donation_costs_more_than_it_saves()
    {
        var tip = IncomeTax.Tips(Inputs(72_000, 14_400)).Single(t => t.Key == "donate");
        Assert.Equal(175m, tip.Saves); // 2,500 × 7%
        Assert.True(tip.Saves < 1_000m);
    }
}

public class TaxPlannerTests
{
    // Take-home 4,800 = base 6,000 less 20% CPF, paid on the 25th of each month. Working base pay back
    // from take-home pay can land a dollar low (Payslip.BasePayFromTakeHome), hence the ranges.
    private static List<(DateOnly, decimal)> Paid(int months) =>
        Enumerable.Range(1, months).Select(m => (new DateOnly(2026, m, 25), 4_800m)).ToList();

    [Fact]
    public void Salary_entries_plus_the_months_still_to_come()
    {
        var v = TaxPlanner.Build(2026, new DateOnly(2026, 11, 10), TaxPlanner.Empty, null, Paid(10));
        Assert.Equal(10, v.Income.SalaryEntries);
        Assert.InRange(v.Income.PaidSoFar, 59_990m, 60_000m);
        Assert.Equal(2, v.Income.MonthsLeft);
        Assert.InRange(v.Income.MonthlyPay, 5_999m, 6_000m);
        Assert.InRange(v.Income.EmploymentIncome, 71_988m, 72_000m);
        Assert.InRange(v.Income.EmployeeCpf, 14_388m, 14_400m);
        Assert.InRange(v.Result.Tax, 1_711m, 1_712m);
        Assert.Equal(51, v.DaysToAct);
        Assert.NotEmpty(v.Tips);
    }

    [Fact]
    public void A_bonus_pays_CPF_only_up_to_the_annual_ceiling()
    {
        // Base 8,000 (take-home 6,400): 12 months of 8,000 = 96,000 of ordinary wages, so only 6,000 of a
        // 12,000 bonus is under the 102,000 ceiling and pays CPF (1,200).
        var paid = Enumerable.Range(1, 10).Select(m => (new DateOnly(2026, m, 25), 6_400m)).ToList();
        var plan = TaxPlanner.Empty with { ExpectedBonus = 12_000 };
        var v = TaxPlanner.Build(2026, new DateOnly(2026, 11, 10), plan, null, paid);
        Assert.InRange(v.Income.EmploymentIncome, 107_988m, 108_000m);
        Assert.InRange(v.Income.EmployeeCpf, 20_388m, 20_402m);
        Assert.InRange(v.Result.Tax, 4_108m, 4_112m); // 86,600 chargeable: 3,350 + 11.5% of 6,600
    }

    [Fact]
    public void A_typed_in_figure_wins_over_the_entries()
    {
        var plan = TaxPlanner.Empty with { EmploymentIncomeOverride = 72_000 };
        var v = TaxPlanner.Build(2026, new DateOnly(2026, 11, 10), plan, null, Paid(3));
        Assert.True(v.Income.FromOverride);
        Assert.Equal(72_000m, v.Income.EmploymentIncome);
        Assert.Equal(14_400m, v.Income.EmployeeCpf);
    }

    [Fact]
    public void After_31_December_nothing_more_counts()
    {
        var v = TaxPlanner.Build(2026, new DateOnly(2027, 1, 5), TaxPlanner.Empty, null, Paid(12));
        Assert.Equal(0, v.Income.MonthsLeft);
        Assert.True(v.DaysToAct < 0);
        Assert.Empty(v.Tips);
        Assert.Contains(v.Notes, n => n.Contains("past 31 Dec"));
    }

    [Fact]
    public void Earned_income_relief_follows_the_birth_date()
    {
        var v = TaxPlanner.Build(2026, new DateOnly(2026, 11, 10), TaxPlanner.Empty, new DateOnly(1970, 3, 1), Paid(10));
        Assert.Contains(v.Result.Reliefs, l => l.Name == "Earned income relief" && l.Amount == 6_000m);
        Assert.DoesNotContain(v.Notes, n => n.Contains("date of birth"));
    }

    [Fact]
    public void Monthly_pay_counts_the_whole_year_without_any_entries()
    {
        // 4,600 a month from January, typed in, in November with nothing recorded yet: 12 months, not 2.
        var plan = TaxPlanner.Empty with { ExpectedMonthlyPay = 4_600 };
        var v = TaxPlanner.Build(2026, new DateOnly(2026, 11, 10), plan, null, []);
        Assert.Equal(12, v.Income.MonthsLeft);
        Assert.Equal(55_200m, v.Income.EmploymentIncome);
        Assert.Equal(12 * 920m, v.Income.EmployeeCpf);
        Assert.Contains(v.Notes, n => n.Contains("10 earlier month(s)"));
    }

    [Fact]
    public void Months_missing_between_entries_are_filled_too()
    {
        // Entries for Jan, Feb and Oct only: the 9 other months are counted at the monthly pay.
        var paid = new List<(DateOnly, decimal)> { (new(2026, 1, 25), 4_800m), (new(2026, 2, 25), 4_800m), (new(2026, 10, 25), 4_800m) };
        var plan = TaxPlanner.Empty with { ExpectedMonthlyPay = 6_000 };
        var v = TaxPlanner.Build(2026, new DateOnly(2026, 11, 10), plan, null, paid);
        Assert.Equal(3, v.Income.SalaryEntries);
        Assert.Equal(9, v.Income.MonthsLeft);
        Assert.InRange(v.Income.EmploymentIncome, 71_997m, 72_000m);
        Assert.Contains(v.Notes, n => n.Contains("7 earlier month(s)"));
    }

    [Fact]
    public void A_past_year_counts_only_its_entries()
    {
        var plan = TaxPlanner.Empty with { ExpectedMonthlyPay = 4_600 };
        var v = TaxPlanner.Build(2026, new DateOnly(2027, 1, 5), plan, null, Paid(3));
        Assert.Equal(0, v.Income.MonthsLeft);
        Assert.InRange(v.Income.EmploymentIncome, 17_997m, 18_000m);
    }

    [Fact]
    public void No_salary_yet_asks_for_monthly_pay()
    {
        var v = TaxPlanner.Build(2026, new DateOnly(2026, 11, 10), TaxPlanner.Empty, null, []);
        Assert.Equal(0m, v.Income.EmploymentIncome);
        Assert.Contains(v.Notes, n => n.Contains("monthly base pay"));
    }
}

public sealed class TaxPlannerDbTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly BudgetDbContext _db;
    private readonly Clock _clock = new(new ConfigurationBuilder().Build());
    private readonly TaxPlanner _tax;

    public TaxPlannerDbTests()
    {
        _conn.Open();
        _db = new BudgetDbContext(new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        Seed.EnsureSeededAsync(_db).GetAwaiter().GetResult();
        _tax = new TaxPlanner(_db, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task Reads_salaries_and_saves_the_plan()
    {
        var year = _clock.Today.Year;
        var budgets = new BudgetService(_db, _clock);
        await budgets.RecordSalaryAsync(4_800m, _clock.LocalDateStartToUtc(new DateOnly(year, 1, 25)).AddHours(9), null);
        await budgets.RecordSalaryAsync(4_800m, _clock.LocalDateStartToUtc(new DateOnly(year - 1, 12, 25)).AddHours(9), null);

        var v = await _tax.ViewAsync(year);
        Assert.Equal(1, v.Income.SalaryEntries); // last year's salary isn't counted
        Assert.InRange(v.Income.PaidSoFar, 5_999m, 6_000m);

        var saved = await _tax.SaveAsync(year, TaxPlanner.Empty with { SrsContributed = 5_000, ParentsLivingWith = 1 });
        Assert.Equal(5_000m, saved.Plan.SrsContributed);
        Assert.Contains(saved.Result.Reliefs, l => l.Name == "Parent relief" && l.Amount == 9_000m);

        var again = await _tax.SaveAsync(year, saved.Plan with { SrsContributed = 6_000 });
        Assert.Equal(6_000m, again.Plan.SrsContributed);
        Assert.Equal(1, await _db.TaxPlans.CountAsync());
    }

    [Fact]
    public async Task Rejects_bad_figures()
    {
        await Assert.ThrowsAsync<TaxException>(() => _tax.SaveAsync(_clock.Today.Year, TaxPlanner.Empty with { SrsContributed = -1 }));
        await Assert.ThrowsAsync<TaxException>(() =>
            _tax.SaveAsync(_clock.Today.Year, TaxPlanner.Empty with { ParentsLivingWith = 2, ParentsNotLivingWith = 1 }));
    }

    [Fact]
    public async Task Ask_can_look_up_the_estimate()
    {
        var categorizer = new Categorizer(_db);
        var budgets = new BudgetService(_db, _clock);
        var tx = new TransactionService(_db, categorizer, budgets, _clock);
        var ai = new MoneyAssistant(_db, budgets, new AccountService(_db, categorizer, budgets, tx, _clock),
            new Subscriptions(_db, _clock), new GoalService(_db, budgets, _clock), _clock,
            new ConfigurationBuilder().Build(), NullLogger<MoneyAssistant>.Instance);
        var (output, label, failed) = await ai.RunToolAsync("get_income_tax", new Dictionary<string, JsonElement>());
        Assert.False(failed);
        Assert.StartsWith("Income tax for", label);
        Assert.Contains("assessmentYear", output);
    }
}
