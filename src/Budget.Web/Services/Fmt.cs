using System.Globalization;

namespace Budget.Web.Services;

public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly TimeSpan Sgt = TimeSpan.FromHours(8);

    public static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(Sgt).DateTime);

    public static string Money(decimal value, string currency = "SGD")
    {
        var sign = value < 0 ? "−" : "";
        var body = Math.Abs(value).ToString("N2", Inv);
        return currency == "SGD" ? $"{sign}S${body}" : $"{sign}{currency} {body}";
    }

    public static string Signed(decimal value) => (value > 0 ? "+" : "") + Money(value);

    public static string Percent(decimal value) => value.ToString("0.#", Inv) + "%";

    /// <summary>Drops trailing zeros (50.0 → 50) so number boxes read cleanly.</summary>
    public static decimal Trim(decimal value) => value / 1.0000000000000000000000000000m;
    public static decimal? Trim(decimal? value) => value is { } v ? Trim(v) : null;

    /// <summary>"25 Sep – 24 Oct" (the end date passed in is the first day of the next period).</summary>
    public static string Range(DateOnly start, DateOnly endExclusive) =>
        $"{start.ToString("d MMM", Inv)} – {endExclusive.AddDays(-1).ToString("d MMM", Inv)}";

    public static string Day(DateOnly day)
    {
        var today = Today;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.ToString(day.Year == today.Year ? "ddd d MMM" : "ddd d MMM yyyy", Inv);
    }

    public static string When(DateTimeOffset when) => when.ToString("ddd d MMM, h:mm tt", Inv);

    public static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", Inv);

    public static string BucketClass(string? bucket) => bucket?.ToLowerInvariant() switch
    {
        "needs" => "needs",
        "wants" => "wants",
        "savings" => "savings",
        "income" => "income",
        _ => "other"
    };

    public static string Source(string source) => source switch
    {
        "ApplePayShortcut" => "Apple Pay",
        "EmailAlert" => "Bank alert",
        "StatementImport" => "Statement",
        "Recurring" => "Recurring",
        _ => "Entered by hand"
    };

    /// <summary>"Monthly on the 1st", "Every 2 weeks on Mon", "Yearly on 3 Mar".</summary>
    public static string Schedule(string frequency, int interval, DateOnly start) => frequency switch
    {
        "Weekly" => (interval == 1 ? "Weekly" : $"Every {interval} weeks") + $" on {start.ToString("ddd", Inv)}",
        "Yearly" => (interval == 1 ? "Yearly" : $"Every {interval} years") + $" on {start.ToString("d MMM", Inv)}",
        _ => (interval == 1 ? "Monthly" : $"Every {interval} months") + $" on the {Ordinal(start.Day)}"
    };

    private static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch
    {
        1 => "st",
        2 => "nd",
        3 => "rd",
        _ => "th"
    });

    public static string AssetClass(string value) => value switch
    {
        "Etf" => "ETF",
        "Bond" => "Bond / T-bill",
        "Cash" => "Cash / deposit",
        "Cpf" => "CPF",
        _ => value
    };
}
