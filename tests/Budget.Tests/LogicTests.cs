using Budget.Api.Services;
using Budget.Web.Services;

namespace Budget.Tests;

public class AmountParserTests
{
    [Theory]
    [InlineData("S$12.50", 12.50, "SGD")]
    [InlineData("$8", 8, "SGD")]
    [InlineData("SGD 1,234.56", 1234.56, "SGD")]
    [InlineData("12,50", 12.50, "SGD")]
    [InlineData("1,234", 1234, "SGD")]
    [InlineData("-4.20", 4.20, "SGD")]
    [InlineData("US$19.99", 19.99, "USD")]
    [InlineData("RM 45.00", 45.00, "MYR")]
    [InlineData("¥1,200", 1200, "JPY")]
    [InlineData("1.234,56 €", 1234.56, "EUR")]
    public void Parses_common_formats(string raw, decimal expected, string currency)
    {
        Assert.True(AmountParser.TryParse(raw, "SGD", out var amount, out var cur));
        Assert.Equal(expected, amount);
        Assert.Equal(currency, cur);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("S$0.00")]
    public void Rejects_unusable_input(string? raw) =>
        Assert.False(AmountParser.TryParse(raw, "SGD", out _, out _));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("0", true)]
    [InlineData("S$0.00", true)]
    [InlineData("S$1.20", false)]
    [InlineData("abc", false)]
    public void Spots_a_tap_with_no_fare_yet(string? raw, bool expected) =>
        Assert.Equal(expected, AmountParser.IsBlankOrZero(raw));
}

public class MerchantTextTests
{
    [Theory]
    [InlineData("GRAB* A-5XYZ123 SINGAPORE", "GRAB")]
    [InlineData("TOAST BOX - TAMPINES 1 #01-23", "TOAST BOX")]
    [InlineData("7-ELEVEN TAMPINES", "7-ELEVEN TAMPINES")]
    [InlineData("STARBUCKS JEWEL CHANGI", "STARBUCKS JEWEL")]
    [InlineData("Cold Storage 1234 SG", "COLD STORAGE")]
    public void Suggests_reusable_patterns(string merchant, string expected) =>
        Assert.Equal(expected, MerchantText.SuggestPattern(merchant));

    [Theory]
    [InlineData("Starbucks", "STARBUCKS COFFEE JEWEL", true)]
    [InlineData("NTUC FairPrice", "FAIRPRICE XTRA", true)]
    [InlineData("", "ANYTHING", true)]
    [InlineData("Starbucks", "Toast Box", false)]
    public void Merchant_similarity(string a, string b, bool same) =>
        Assert.Equal(same, MerchantText.LooksLikeSameMerchant(a, b));
}

public class TransitTests
{
    [Theory]
    [InlineData("BUS/MRT 123456789", true)]
    [InlineData("SimplyGo", true)]
    [InlineData("SBS Transit", true)]
    [InlineData("SMRT Trains", true)]
    [InlineData("Grab", false)]
    [InlineData("Busy Bee Cafe", false)]
    [InlineData(null, false)]
    public void Recognises_public_transport(string? merchant, bool expected) =>
        Assert.Equal(expected, MerchantText.IsTransit(merchant));
}

public class PayPeriodTests
{
    [Fact]
    public void Date_after_payday_is_in_that_months_period()
    {
        var (s, e) = PayPeriod.NominalFor(new DateOnly(2026, 9, 30), 25);
        Assert.Equal(new DateOnly(2026, 9, 25), s);
        Assert.Equal(new DateOnly(2026, 10, 25), e);
    }

    [Fact]
    public void Date_before_payday_is_in_previous_period()
    {
        var (s, e) = PayPeriod.NominalFor(new DateOnly(2026, 10, 3), 25);
        Assert.Equal(new DateOnly(2026, 9, 25), s);
        Assert.Equal(new DateOnly(2026, 10, 25), e);
    }

    [Fact]
    public void Payday_31_clamps_to_short_months()
    {
        var (s, e) = PayPeriod.NominalFor(new DateOnly(2026, 2, 28), 31);
        Assert.Equal(new DateOnly(2026, 2, 28), s);
        Assert.Equal(new DateOnly(2026, 3, 31), e);
    }

    [Fact]
    public void Early_salary_opens_upcoming_period_on_the_day_it_lands()
    {
        // 25 Oct 2026 is a Sunday; salary lands Friday 23rd.
        var (s, e) = PayPeriod.ForSalary(new DateOnly(2026, 10, 23), 25);
        Assert.Equal(new DateOnly(2026, 10, 23), s);
        Assert.Equal(new DateOnly(2026, 11, 25), e);
    }

    [Fact]
    public void Late_salary_still_belongs_to_the_cycle_that_began_on_payday()
    {
        var (s, e) = PayPeriod.ForSalary(new DateOnly(2026, 10, 27), 25);
        Assert.Equal(new DateOnly(2026, 10, 25), s);
        Assert.Equal(new DateOnly(2026, 11, 25), e);
    }

    [Fact]
    public void Year_boundary()
    {
        var (s, e) = PayPeriod.ForSalary(new DateOnly(2026, 12, 24), 25);
        Assert.Equal(new DateOnly(2026, 12, 24), s);
        Assert.Equal(new DateOnly(2027, 1, 25), e);
    }
}

public class PayslipTests
{
    [Theory]
    [InlineData(500, 0)]                // no employee share up to S$500
    [InlineData(600, 60)]               // phase-in: 0.6 × (600 − 500)
    [InlineData(750, 150)]
    [InlineData(5000, 1000)]            // 20%
    [InlineData(4567.89, 913)]          // cents dropped: 913.578
    [InlineData(12000, 1600)]           // capped at the S$8,000 ceiling
    public void Employee_cpf_up_to_55(decimal wage, decimal expected) =>
        Assert.Equal(expected, Payslip.EmployeeCpf(wage, CpfAgeBand.UpTo55));

    [Theory]
    [InlineData(CpfAgeBand.Over55To60, 900)]
    [InlineData(CpfAgeBand.Over60To65, 625)]
    [InlineData(CpfAgeBand.Over65To70, 375)]
    [InlineData(CpfAgeBand.Over70, 250)]
    [InlineData(CpfAgeBand.NoCpf, 0)]
    public void Employee_cpf_by_age_band(CpfAgeBand band, decimal expected) =>
        Assert.Equal(expected, Payslip.EmployeeCpf(5000, band));

    [Theory]
    [InlineData(ShgFund.Cdac, 2000, 0.50)]
    [InlineData(ShgFund.Cdac, 2000.01, 1.00)]
    [InlineData(ShgFund.Cdac, 9000, 3.00)]
    [InlineData(ShgFund.Mbmf, 3500, 15.00)]
    [InlineData(ShgFund.Sinda, 4500, 7)]
    [InlineData(ShgFund.Sinda, 20000, 30)]
    [InlineData(ShgFund.Ecf, 1200, 4)]
    [InlineData(ShgFund.None, 5000, 0)]
    public void Shg_bands(ShgFund fund, decimal wage, decimal expected) =>
        Assert.Equal(expected, Payslip.ShgContribution(wage, fund));

    [Fact]
    public void Take_home_is_base_less_cpf_and_shg()
    {
        var b = Payslip.From(4000, CpfAgeBand.UpTo55, ShgFund.Cdac);
        Assert.Equal(800, b.EmployeeCpf);
        Assert.Equal(1.50m, b.Shg);   // CDAC band above S$3,500 to S$5,000
        Assert.Equal(3198.50m, b.TakeHome);
        Assert.Equal(680, b.EmployerCpf);
        Assert.Equal(1480, b.TotalCpf);
    }

    [Theory]
    [InlineData(40, CpfAgeBand.UpTo55, 0, 0)]               // nothing up to S$50
    [InlineData(250, CpfAgeBand.UpTo55, 43, 43)]            // employer only: 42.50 rounds up
    [InlineData(600, CpfAgeBand.UpTo55, 162, 102)]          // 17% × 600 + 0.6 × 100
    [InlineData(5000, CpfAgeBand.UpTo55, 1850, 850)]        // 37%
    [InlineData(4567.89, CpfAgeBand.UpTo55, 1690, 777)]     // total 1690.12 rounds; employee 913 cents dropped
    [InlineData(12000, CpfAgeBand.UpTo55, 2960, 1360)]      // capped at the S$8,000 ceiling
    [InlineData(5000, CpfAgeBand.Over55To60, 1700, 800)]    // 34%
    [InlineData(5000, CpfAgeBand.Over60To65, 1250, 625)]    // 25%
    [InlineData(5000, CpfAgeBand.Over65To70, 825, 450)]     // 16.5%
    [InlineData(5000, CpfAgeBand.Over70, 625, 375)]         // 12.5%
    [InlineData(5000, CpfAgeBand.NoCpf, 0, 0)]
    public void Total_and_employer_cpf(decimal wage, CpfAgeBand band, decimal total, decimal employer)
    {
        Assert.Equal(total, Payslip.TotalCpf(wage, band));
        Assert.Equal(employer, Payslip.EmployerCpf(wage, band));
    }

    [Theory]
    [InlineData("1971-03-15", "2026-03-25", CpfAgeBand.UpTo55)]      // turns 55 this month: new rate from next month
    [InlineData("1971-03-15", "2026-04-01", CpfAgeBand.Over55To60)]
    [InlineData("1971-04-01", "2026-04-30", CpfAgeBand.UpTo55)]      // birthday on the 1st is still "this month"
    [InlineData("1971-04-01", "2026-05-01", CpfAgeBand.Over55To60)]
    [InlineData("1966-01-10", "2026-02-25", CpfAgeBand.Over60To65)]
    [InlineData("1961-06-30", "2026-07-25", CpfAgeBand.Over65To70)]
    [InlineData("1950-01-01", "2026-06-25", CpfAgeBand.Over70)]
    [InlineData("1990-12-31", "2026-06-25", CpfAgeBand.UpTo55)]
    [InlineData("1972-02-29", "2027-02-28", CpfAgeBand.UpTo55)]      // leap-day birthday
    [InlineData("1972-02-29", "2027-03-01", CpfAgeBand.Over55To60)]
    public void Age_band_from_date_of_birth(string birth, string pay, CpfAgeBand expected) =>
        Assert.Equal(expected, Payslip.AgeBandOn(DateOnly.Parse(birth), DateOnly.Parse(pay)));

    [Theory]
    [InlineData("1990-06-15", "2026-10-06", "2045-07-01", CpfAgeBand.Over55To60)]
    [InlineData("1971-03-15", "2026-03-31", "2026-04-01", CpfAgeBand.Over55To60)]   // turns 55 this month
    [InlineData("1971-03-15", "2026-04-01", "2031-04-01", CpfAgeBand.Over60To65)]   // the day it changed: next one
    [InlineData("1958-12-20", "2026-10-06", "2029-01-01", CpfAgeBand.Over70)]       // December birthday: January next year
    public void Next_age_band_change(string birth, string today, string from, CpfAgeBand band)
    {
        var next = Payslip.NextBandChange(DateOnly.Parse(birth), DateOnly.Parse(today));
        Assert.Equal((DateOnly.Parse(from), band), next);
        // Agrees with the band worked out for a payslip on that day.
        Assert.Equal(band, Payslip.AgeBandOn(DateOnly.Parse(birth), DateOnly.Parse(from)));
    }

    [Fact]
    public void No_band_change_after_70() =>
        Assert.Null(Payslip.NextBandChange(new DateOnly(1950, 1, 1), new DateOnly(2026, 10, 6)));

    [Theory]
    [InlineData("1990-10-06", "2026-10-06", 36)]
    [InlineData("1990-10-07", "2026-10-06", 35)]
    [InlineData("2000-02-29", "2026-02-27", 25)]
    [InlineData("2000-02-29", "2026-02-28", 26)]    // leap-day birthday counts from 28 Feb, as in AgeBandOn
    public void Age_in_completed_years(string birth, string today, int expected) =>
        Assert.Equal(expected, Payslip.AgeOn(DateOnly.Parse(birth), DateOnly.Parse(today)));
}