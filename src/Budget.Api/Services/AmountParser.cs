using System.Globalization;
using System.Text.RegularExpressions;

namespace Budget.Api.Services;

/// <summary>
/// Parses amounts the way iPhone Shortcuts and bank emails hand them over:
/// "S$12.50", "SGD 1,234.56", "$8", "12,50", "-4.20", "US$19.99".
/// </summary>
public static partial class AmountParser
{
    private static readonly (string Token, string Currency)[] CurrencyTokens =
    [
        ("US$", "USD"), ("USD", "USD"),
        ("S$", "SGD"), ("SGD", "SGD"),
        ("RM", "MYR"), ("MYR", "MYR"),
        ("€", "EUR"), ("EUR", "EUR"),
        ("£", "GBP"), ("GBP", "GBP"),
        ("¥", "JPY"), ("JPY", "JPY"),
        ("A$", "AUD"), ("AUD", "AUD"),
        ("HK$", "HKD"), ("HKD", "HKD"),
    ];

    public static bool TryParse(string? raw, string defaultCurrency, out decimal amount, out string currency)
    {
        amount = 0;
        currency = defaultCurrency;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim().ToUpperInvariant();

        // Longest/most specific tokens are listed first (US$ before $-less matches).
        foreach (var (token, code) in CurrencyTokens)
        {
            if (text.Contains(token, StringComparison.Ordinal))
            {
                currency = code;
                break;
            }
        }

        var numeric = NumericChars().Replace(text, "");
        if (numeric.Length == 0) return false;

        var hasDot = numeric.Contains('.');
        var hasComma = numeric.Contains(',');
        if (hasDot && hasComma)
        {
            // Whichever separator comes last is the decimal separator.
            if (numeric.LastIndexOf(',') > numeric.LastIndexOf('.'))
                numeric = numeric.Replace(".", "").Replace(',', '.');
            else
                numeric = numeric.Replace(",", "");
        }
        else if (hasComma)
        {
            // "12,50" → decimal comma. "1,234" → thousands separator.
            var afterComma = numeric.Length - numeric.LastIndexOf(',') - 1;
            numeric = afterComma == 2 && numeric.Count(c => c == ',') == 1
                ? numeric.Replace(',', '.')
                : numeric.Replace(",", "");
        }

        if (!decimal.TryParse(numeric, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value))
            return false;

        amount = Math.Round(Math.Abs(value), 2);
        return amount > 0;
    }

    /// <summary>
    /// True for "", "0", "S$0.00"... What a bus/MRT tap reports: the fare isn't known
    /// until the trip ends and is charged later. "abc" is not zero, it's junk.
    /// </summary>
    public static bool IsBlankOrZero(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var digits = raw.Where(char.IsDigit).ToList();
        return digits.Count > 0 && digits.All(c => c == '0');
    }

    [GeneratedRegex(@"[^0-9.,\-]")]
    private static partial Regex NumericChars();
}
