using Budget.Api.Services;

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
