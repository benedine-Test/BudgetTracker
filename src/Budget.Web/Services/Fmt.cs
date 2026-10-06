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
        _ => "Entered by hand"
    };

    public static string AssetClass(string value) => value switch
    {
        "Etf" => "ETF",
        "Bond" => "Bond / T-bill",
        "Cash" => "Cash / deposit",
        "Cpf" => "CPF",
        "Policy" => "Insurance plan (ILP)",
        _ => value
    };
}
