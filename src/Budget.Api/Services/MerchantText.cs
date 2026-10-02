using System.Text.RegularExpressions;

namespace Budget.Api.Services;

public static partial class MerchantText
{
    /// <summary>Upper-case, collapse whitespace, strip punctuation noise. Keeps / . & for patterns like BUS/MRT, BOOKING.COM.</summary>
    public static string Normalize(string? merchant)
    {
        if (string.IsNullOrWhiteSpace(merchant)) return "";
        var upper = merchant.ToUpperInvariant();
        upper = Noise().Replace(upper, " ");
        return Spaces().Replace(upper, " ").Trim();
    }

    /// <summary>
    /// A reusable rule pattern from one merchant string: drops store numbers,
    /// reference codes and branch suffixes. "GRAB* A-5XYZ123 SINGAPORE" → "GRAB".
    /// "TOAST BOX - TAMPINES 1 #01-23" → "TOAST BOX".
    /// </summary>
    public static string SuggestPattern(string? merchant)
    {
        var norm = Normalize(merchant);
        // Cut at the first separator that usually starts a branch/reference.
        // A bare hyphen is kept so names like "7-ELEVEN" survive.
        var cut = new[] { norm.IndexOf('*'), norm.IndexOf(" -", StringComparison.Ordinal), norm.IndexOf(" #", StringComparison.Ordinal) }
            .Where(i => i > 0).DefaultIfEmpty(-1).Min();
        if (cut > 0) norm = norm[..cut].Trim();

        var tokens = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile((t, i) => (i == 0 || !t.Any(char.IsDigit)) && t is not ("SINGAPORE" or "SG" or "SGP"))
            .Take(2) // brand names rarely need more; branch names ("JEWEL CHANGI") would over-fit
            .ToList();

        return tokens.Count == 0 ? Normalize(merchant) : string.Join(' ', tokens);
    }

    /// <summary>Loose match used for de-duplication across sources.</summary>
    public static bool LooksLikeSameMerchant(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return true; // one source didn't give a merchant
        if (na.Contains(nb) || nb.Contains(na)) return true;

        var ta = na.Split(' ').Where(t => t.Length >= 4).ToHashSet();
        return nb.Split(' ').Any(t => t.Length >= 4 && ta.Contains(t));
    }

    [GeneratedRegex(@"[^A-Z0-9/.&*#\- ]")]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
