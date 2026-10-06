using System.Net;
using System.Text.Json;
using Budget.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Services;

/// <summary>A price as the exchange quotes it. Currency is the exchange's code, e.g. "GBp" for pence.</summary>
public record Quote(decimal Price, string Currency);

/// <summary>Thrown with a message fit to show next to the holding.</summary>
public class QuoteException(string message) : Exception(message);

public interface IQuoteSource
{
    /// <summary>Latest price for a ticker, or a <see cref="QuoteException"/> saying why not.</summary>
    Task<Quote> GetAsync(string symbol, CancellationToken ct);
}

/// <summary>
/// Yahoo Finance's chart endpoint. No key needed, and it covers SGX (ES3.SI), LSE (VWRA.L),
/// US shares, crypto (BTC-USD) and FX (USDSGD=X). It is unofficial, so it can change or
/// refuse without notice: failures are kept on the holding and the last good price stays.
/// </summary>
public class YahooQuoteSource(HttpClient http) : IQuoteSource
{
    public async Task<Quote> GetAsync(string symbol, CancellationToken ct)
    {
        HttpResponseMessage r;
        try
        {
            r = await http.GetAsync($"v8/finance/chart/{Uri.EscapeDataString(symbol)}?range=1d&interval=1d", ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new QuoteException("Couldn't reach Yahoo Finance for a price. It will try again later.");
        }

        using (r)
        {
            if (r.StatusCode == HttpStatusCode.NotFound)
                throw new QuoteException($"Yahoo Finance doesn't know \"{symbol}\". Use its ticker there, such as ES3.SI or VWRA.L.");
            if (!r.IsSuccessStatusCode)
                throw new QuoteException($"Yahoo Finance refused the price request ({(int)r.StatusCode}). It will try again later.");

            try
            {
                using var doc = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var meta = doc.RootElement.GetProperty("chart").GetProperty("result")[0].GetProperty("meta");
                var price = meta.GetProperty("regularMarketPrice").GetDecimal();
                var currency = meta.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
                if (price <= 0) throw new QuoteException($"Yahoo Finance has no price for \"{symbol}\" right now.");
                return new Quote(price, currency);
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or FormatException)
            {
                throw new QuoteException($"Yahoo Finance has no price for \"{symbol}\".");
            }
        }
    }
}

public record PriceRefreshResult(int Updated, int Failed, int Skipped, string Message);

/// <summary>Brings automatic prices and every foreign holding's exchange rate up to date.</summary>
public class PriceRefresher(BudgetDbContext db, IQuoteSource quotes, BudgetService budgets, Clock clock)
{
    /// <summary>A price fetched more recently than this is left alone unless forced.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(15);

    public async Task<PriceRefreshResult> RefreshAsync(bool force, CancellationToken ct = default)
    {
        var baseCcy = (await budgets.GetSettingsAsync(ct)).BaseCurrency.ToUpperInvariant();
        var now = clock.UtcNow;
        var holdings = await db.Holdings.ToListAsync(ct);

        int updated = 0, failed = 0, skipped = 0;
        var due = holdings.Where(h => h.AutoPrice && !string.IsNullOrWhiteSpace(h.Symbol)).ToList();
        foreach (var group in due.GroupBy(h => h.Symbol.Trim().ToUpperInvariant()))
        {
            if (!force && group.All(h => h.PriceError is null && now - h.LastPriceAtUtc < FreshFor))
            {
                skipped += group.Count();
                continue;
            }

            Quote? quote = null;
            string? problem = null;
            try { quote = await quotes.GetAsync(group.Key, ct); }
            catch (QuoteException e) { problem = e.Message; }

            foreach (var h in group)
            {
                var price = quote is null ? null : InHoldingCurrency(quote, h.Currency);
                if (price is decimal p)
                {
                    h.LastPrice = p;
                    h.LastPriceAtUtc = now;
                    h.PriceError = null;
                    updated++;
                }
                else
                {
                    h.PriceError = problem ?? $"Yahoo Finance prices {group.Key} in {quote!.Currency}, but this holding is in {h.Currency}. Change its currency to match.";
                    failed++;
                }
            }
        }

        // Exchange rates for every foreign holding, typed-in prices included.
        foreach (var group in holdings.Where(h => !h.Currency.Equals(baseCcy, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(h => h.Currency.ToUpperInvariant()))
        {
            try
            {
                var fx = await quotes.GetAsync($"{group.Key}{baseCcy}=X", ct);
                foreach (var h in group) h.FxToBase = fx.Price;
            }
            catch (QuoteException)
            {
                // Keep the last rate. The price error (if any) already tells the person something is off.
            }
        }

        await db.SaveChangesAsync(ct);

        var message = failed == 0
            ? updated == 0 ? "Prices are up to date." : $"Updated {updated} price{(updated == 1 ? "" : "s")}."
            : $"Updated {updated}, couldn't get {failed}. See the holding for why.";
        return new PriceRefreshResult(updated, failed, skipped, message);
    }

    /// <summary>
    /// The quote in the holding's currency, or null if they don't match. London quotes many
    /// shares in pence ("GBp"), so a GBP holding takes those divided by 100.
    /// </summary>
    public static decimal? InHoldingCurrency(Quote q, string holdingCurrency)
    {
        if (string.IsNullOrEmpty(q.Currency) || q.Currency.Equals(holdingCurrency, StringComparison.OrdinalIgnoreCase) && q.Currency != "GBp")
            return q.Price;
        if (q.Currency == "GBp" && holdingCurrency.Equals("GBP", StringComparison.OrdinalIgnoreCase))
            return Math.Round(q.Price / 100m, 6);
        return null;
    }
}
