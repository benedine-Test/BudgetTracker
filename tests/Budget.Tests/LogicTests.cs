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
    }
}