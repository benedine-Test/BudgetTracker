using System.Text.RegularExpressions;

namespace Budget.Api.Services;

public record ParsedNotification(decimal Amount, string Currency, string Merchant, string? Card);

/// <summary>
/// Reads a payment out of an Android notification (Google Wallet or a bank app), as
/// forwarded raw by MacroDroid/Tasker. Parsing lives here, not on the phone, so a
/// change in wording is fixed once on the server.
/// "A transaction of SGD 12.34 was made with your UOB Card ending 1234 at STARBUCKS on 30/10/26."
/// Title "Starbucks", text "S$5.80 with Visa ••1234" (Google Wallet style).
/// </summary>
public static partial class NotificationParser
{
    public const string UnknownMerchant = "Unknown merchant";

    /// <summary>Null when the notification isn't a completed purchase (promo, OTP, declined, refund…).</summary>
    public static ParsedNotification? Parse(string? app, string? title, string? text, string baseCurrency)
    {
        var all = $"{title}\n{text}";
        if (NotAPurchase().IsMatch(all)) return null;

        var money = Money().Match(all);
        if (!money.Success || !AmountParser.TryParse(money.Value, baseCurrency, out var amount, out var currency))
            return null;

        return new ParsedNotification(amount, currency, MerchantFrom(title, all), CardFrom(app, all));
    }

    private static string MerchantFrom(string? title, string all)
    {
        var at = AtMerchant().Match(all);
        if (at.Success) return at.Groups[1].Value.Trim();

        // Google Wallet puts the shop in the title and the amount in the text.
        var t = title?.Trim() ?? "";
        if (t.Length is > 0 and <= 40 && !Money().IsMatch(t) && !GenericTitle().IsMatch(t)) return t;
        return UnknownMerchant;
    }

    /// <summary>"UOB TMRW 1234": the app plus the card's last four, so an account listing "1234" picks it up.</summary>
    private static string? CardFrom(string? app, string all)
    {
        var last4 = LastFour().Match(all);
        var card = $"{app?.Trim()} {(last4.Success ? last4.Groups[1].Value : "")}".Trim();
        return card.Length == 0 ? null : card;
    }

    [GeneratedRegex(@"\b(declined|unsuccessful|not successful|failed|OTP|one[- ]time (password|pin|code)|refund(ed)?|revers(al|ed)|credited)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex NotAPurchase();

    [GeneratedRegex(@"(US\$|HK\$|A\$|S\$|SGD|USD|MYR|RM|EUR|GBP|JPY|HKD|AUD|[$€£¥])\s?\d[\d,]*(\.\d{1,2})?|\d[\d,]*(\.\d{1,2})?\s?(SGD|USD|MYR|EUR|GBP|JPY|HKD|AUD)\b")]
    private static partial Regex Money();

    // Stops before the date/card part that usually follows, or at a sentence end ("AMAZON.SG" keeps its dot).
    [GeneratedRegex(@"\bat\s+(.+?)(?=\s+(on|using|with|via|for|from|if)\b|[,;\n]|\.(\s|$)|$)", RegexOptions.IgnoreCase)]
    private static partial Regex AtMerchant();

    [GeneratedRegex(@"\b(alert|transaction|payment|purchase|card|notification|wallet|bank)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GenericTitle();

    [GeneratedRegex(@"(?:ending(?:\s+(?:in|with))?|ends?\s+(?:in|with)|[x*•·.…]{2,}|…)\s*(\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex LastFour();
}
