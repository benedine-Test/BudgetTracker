using Budget.Api.Data;

namespace Budget.Api.Services;

public record HoldingValue(
    int Id, string Symbol, string Name, string AssetClass, string? Platform,
    decimal Units, decimal AverageCost, string Currency, decimal FxToBase, decimal? LastPrice, DateTime? LastPriceAtUtc,
    decimal CostBase, decimal MarketValueBase, decimal UnrealisedPnlBase, decimal? UnrealisedPnlPct,
    bool PriceIsStale);

public record AllocationSlice(string AssetClass, decimal ValueBase, decimal Percent);

public record PortfolioSummary(
    decimal TotalCostBase, decimal TotalValueBase, decimal UnrealisedPnlBase, decimal? UnrealisedPnlPct,
    IReadOnlyList<AllocationSlice> Allocation, IReadOnlyList<HoldingValue> Holdings);

public static class Portfolio
{
    /// <summary>Price older than this is flagged so you don't trust an old number.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    public static PortfolioSummary Summarise(IEnumerable<Holding> holdings, DateTime nowUtc)
    {
        var rows = holdings.Select(h =>
        {
            var cost = Math.Round(h.Units * h.AverageCost * h.FxToBase, 2);
            // No price yet → value at cost so totals aren't wildly wrong.
            var value = Math.Round(h.Units * (h.LastPrice ?? h.AverageCost) * h.FxToBase, 2);
            var pnl = value - cost;
            decimal? pnlPct = cost == 0 ? null : Math.Round(pnl / cost * 100, 2);
            var stale = h.LastPriceAtUtc is null || nowUtc - h.LastPriceAtUtc > StaleAfter;
            return new HoldingValue(h.Id, h.Symbol, h.Name, h.AssetClass.ToString(), h.Platform, h.Units, h.AverageCost, h.Currency, h.FxToBase,
                h.LastPrice, h.LastPriceAtUtc, cost, value, pnl, pnlPct, stale);
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
