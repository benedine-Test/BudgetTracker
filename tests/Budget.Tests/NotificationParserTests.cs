using Budget.Api.Services;

namespace Budget.Tests;

public class NotificationParserTests
{
    [Fact]
    public void Reads_a_bank_alert()
    {
        var p = NotificationParser.Parse("UOB TMRW", "Transaction alert",
            "A transaction of SGD 12.34 was made with your UOB Card ending 1234 at STARBUCKS on 30/10/26. If unauthorised, call us.", "SGD");

        Assert.NotNull(p);
        Assert.Equal(12.34m, p.Amount);
        Assert.Equal("SGD", p.Currency);
        Assert.Equal("STARBUCKS", p.Merchant);
        Assert.Equal("UOB TMRW 1234", p.Card);
    }

    [Fact]
    public void Reads_a_wallet_style_notification_with_the_shop_in_the_title()
    {
        var p = NotificationParser.Parse("Google Wallet", "Starbucks", "S$5.80 with Visa ••5678", "SGD");

        Assert.NotNull(p);
        Assert.Equal(5.80m, p.Amount);
        Assert.Equal("Starbucks", p.Merchant);
        Assert.Equal("Google Wallet 5678", p.Card);
    }

    [Theory]
    [InlineData("Paid S$8.90 at AMAZON.SG.", "AMAZON.SG")]
    [InlineData("You spent US$19.99 at NETFLIX.COM using card x9999", "NETFLIX.COM")]
    [InlineData("12.50 SGD at Toast Box, Tampines", "Toast Box")]
    public void Finds_the_merchant_after_at(string text, string merchant) =>
        Assert.Equal(merchant, NotificationParser.Parse("Bank", "Card alert", text, "SGD")?.Merchant);

    [Fact]
    public void Falls_back_to_unknown_when_the_title_is_generic() =>
        Assert.Equal(NotificationParser.UnknownMerchant,
            NotificationParser.Parse("Bank", "Card transaction", "S$3.00 charged", "SGD")?.Merchant);

    [Theory]
    [InlineData("Your OTP for S$50.00 at SHOPEE is 123456")]
    [InlineData("Transaction of SGD 20.00 at GRAB was declined")]
    [InlineData("Refund of S$15.00 from LAZADA")]
    [InlineData("Get 10% cashback this weekend!")]
    public void Ignores_what_isnt_a_purchase(string text) =>
        Assert.Null(NotificationParser.Parse("Bank", "UOB", text, "SGD"));

    [Fact]
    public void An_unknown_merchant_still_merges_with_the_same_purchase_from_another_source() =>
        Assert.True(MerchantText.LooksLikeSameMerchant(NotificationParser.UnknownMerchant, "Starbucks"));
}
