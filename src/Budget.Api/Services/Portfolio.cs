using Budget.Api.Data;

namespace Budget.Api.Services;

public record HoldingValue(
    int Id, string Symbol, string Name, string AssetClass, string? Platform,
    decimal Units, decimal AverageCost, string Currency, decimal FxToBase, decimal? LastPrice, DateTime? LastPriceAtUtc,
    decimal CostBase, decimal MarketValueBase, decimal UnrealisedPnlBase, decimal? UnrealisedPnlPct,
    bool PriceIsStale, bool AutoPrice, string? PriceError,
    string? ContributionMatch, decimal? ContributionAmount, int? ContributionAccountId,
    int ContributionCount, decimal ContributedBase, DateTime? LastContributionAtUtc, decimal NotInFiguresBase);

public record AllocationSlice(string AssetClass, decimal ValueBase, decimal Percent);

public record PortfolioSummary(
    decimal TotalCostBase, decimal TotalValueBase, decimal UnrealisedPnlBase, decimal? UnrealisedPnlPct,
    IReadOnlyList<AllocationSlice> Allocation, IReadOnlyList<HoldingValue> Holdings);

/// <summary>A bank entry that paid into a holding.</summary>
public record Contribution(int HoldingId, int TransactionId, DateTime OccurredAtUtc, decimal Amount);

public static class Portfolio
{
    /// <summary>Price older than this is flagged so you don't trust an old number.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    /// <summary>
    /// Which spends paid into which holding: the entry's merchant text contains the holding's
    /// <see cref="Holding.ContributionMatch"/>, and its amount and account agree when those are set.
    /// An entry that fits two holdings goes to the one with the longer match text.
    /// Only base-currency spends count, so a contribution is always in base currency.
    /// </summary>
    public static IReadOnlyList<Contribution> MatchContributions(
        IEnumerable<Holding> holdings, IEnumerable<Transaction> transactions, string baseCurrency)
    {
        var rules = holdings
            .Where(h => !string.IsNullOrWhiteSpace(h.ContributionMatch))
            .Select(h => (Holding: h, Text: Normalise(h.ContributionMatch!)))
            .OrderByDescending(r => r.Text.Length).ThenBy(r => r.Holding.Id)
            .ToList();
        if (rules.Count == 0) return [];

        var result = new List<Contribution>();
        foreach (var t in transactions)
        {
            if (t.IsIncome || t.Amount <= 0 || !t.Currency.Equals(baseCurrency, StringComparison.OrdinalIgnoreCase)) continue;
            var merchant = Normalise(t.Merchant);
            foreach (var (h, text) in rules)
            {
                if (!merchant.Contains(text, StringComparison.Ordinal)) continue;
                if (h.ContributionAmount is decimal amount && t.Amount != amount) continue;
                if (h.ContributionAccountId is int accountId && t.AccountId != accountId) continue;
                result.Add(new Contribution(h.Id, t.Id, t.OccurredAtUtc, t.Amount));
                break;
            }
        }
        return result;
    }

    /// <summary>Upper case, single spaces: bank text varies in both.</summary>
    public static string Normalise(string text) =>
        string.Join(' ', text.ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static PortfolioSummary Summarise(IEnumerable<Holding> holdings, DateTime nowUtc) =>
        Summarise(holdings, [], nowUtc);

    public static PortfolioSummary Summarise(IEnumerable<Holding> holdings, IReadOnlyList<Contribution> contributions, DateTime nowUtc)
    {
        var byHolding = contributions.ToLookup(c => c.HoldingId);
        var rows = holdings.Select(h =>
        {
            var paid = byHolding[h.Id].ToList();
            // Money paid in since the figures were last entered isn't in them yet, so it is added
            // at cost. A typed-in balance (CPF, an ILP's value) already includes what was paid before it.
            var notInCost = paid.Where(c => h.FiguresAsOfUtc is null || c.OccurredAtUtc > h.FiguresAsOfUtc).Sum(c => c.Amount);
            var valueFrom = h.IsBalance && h.LastPriceAtUtc > h.FiguresAsOfUtc ? h.LastPriceAtUtc : h.FiguresAsOfUtc;
            var notInValue = paid.Where(c => valueFrom is null || c.OccurredAtUtc > valueFrom).Sum(c => c.Amount);

            var cost = Math.Round(h.Units * h.AverageCost * h.FxToBase + notInCost, 2);
            // No price yet → value at cost so totals aren't wildly wrong.
            var value = Math.Round(h.Units * (h.LastPrice ?? h.AverageCost) * h.FxToBase + notInValue, 2);
            var pnl = value - cost;
            decimal? pnlPct = cost == 0 ? null : Math.Round(pnl / cost * 100, 2);
            var stale = h.LastPriceAtUtc is null || nowUtc - h.LastPriceAtUtc > StaleAfter;
            return new HoldingValue(h.Id, h.Symbol, h.Name, h.AssetClass.ToString(), h.Platform, h.Units, h.AverageCost, h.Currency, h.FxToBase,
                h.LastPrice, h.LastPriceAtUtc, cost, value, pnl, pnlPct, stale, h.AutoPrice, h.PriceError,
                h.ContributionMatch, h.ContributionAmount, h.ContributionAccountId,
                paid.Count, paid.Sum(c => c.Amount), paid.Count == 0 ? null : paid.Max(c => c.OccurredAtUtc), notInCost);
        }).OrderByDescending(r => r.MarketValueBase).ToList();

        var totalCost = rows.Sum(r => r.CostBase);
        var totalValue = rows.Sum(r => r.MarketValueBase);
        var allocation = rows.GroupBy(r => r.AssetClass)
            .Select(g => new AllocationSlice(g.Key, g.Sum(r => r.MarketValueBase),
                totalValue == 0 ? 0 : Math.Round(g.Sum(r => r.MarketValueBase) / totalValue * 100, 1)))
            .OrderByDescending(a => a.ValueBase)
            .ToList();

        return new PortfolioSummary(totalCost, totalValue, totalValue - totalCost,
            totalCost == 0 ? null : Math.Round((totalValue - totalCost) / totalCost * 100, 2),
            allocation, rows);
    }
}
