using System.Text;
using Budget.Api.Data;
using Budget.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Endpoints;

public record SettingsUpdate(int? PayDay, decimal? NeedsPct, decimal? WantsPct, decimal? SavingsPct);
public record CategoryUpsert(string Name, Bucket Bucket, bool? IsArchived);
public record RuleCreate(string Pattern, int CategoryId, int? Priority);
public record HoldingUpsert(string Symbol, string Name, AssetClass AssetClass, string? Platform,
    decimal Units, decimal AverageCost, string? Currency, decimal? LastPrice, decimal? FxToBase,
    bool? AutoPrice = null, string? ContributionMatch = null, decimal? ContributionAmount = null, int? ContributionAccountId = null);
public record PriceUpdate(decimal Price, decimal? FxToBase);

public static class BudgetEndpoints
{
    public static void MapBudgetEndpoints(this RouteGroupBuilder api)
    {
        // ---- Budget ----
        api.MapGet("/budget/summary", async (string? date, BudgetService svc, Clock clock, CancellationToken ct) =>
        {
            var d = clock.Today;
            if (!string.IsNullOrWhiteSpace(date) && !DateOnly.TryParse(date, out d))
                return Results.BadRequest(new { message = "date must be yyyy-MM-dd." });
            return Results.Ok(await svc.SummaryAsync(d, ct));
        });

        api.MapGet("/budget/history", async (int? take, BudgetService svc, CancellationToken ct) =>
            Results.Ok(await svc.HistoryAsync(Math.Clamp(take ?? 12, 1, 36), ct)));

        api.MapGet("/budget/periods", async (BudgetDbContext db, CancellationToken ct) =>
            (await db.BudgetPeriods.AsNoTracking().ToListAsync(ct))
                .OrderByDescending(p => p.StartDate)
                .Select(p => new
                {
                    p.Id, p.StartDate, p.EndDate, p.TakeHomeIncome, p.NeedsPct, p.WantsPct, p.SavingsPct,
                    needs = p.NeedsBudget, wants = p.WantsBudget, savings = p.SavingsBudget
                }));

        // ---- Settings ----
        api.MapGet("/settings", (BudgetService svc, CancellationToken ct) => svc.GetSettingsAsync(ct));

        api.MapPut("/settings", async (SettingsUpdate body, BudgetDbContext db, CancellationToken ct) =>
        {
            var s = await db.Settings.SingleAsync(x => x.Id == 1, ct);
            if (body.PayDay is int pd)
            {
                if (pd is < 1 or > 31) return Results.BadRequest(new { message = "PayDay must be 1–31." });
                s.PayDay = pd;
            }
            var needs = body.NeedsPct ?? s.NeedsPct;
            var wants = body.WantsPct ?? s.WantsPct;
            var savings = body.SavingsPct ?? s.SavingsPct;
            if (needs < 0 || wants < 0 || savings < 0 || needs + wants + savings != 100)
                return Results.BadRequest(new { message = $"Split must add up to 100 (got {needs + wants + savings})." });
            (s.NeedsPct, s.WantsPct, s.SavingsPct) = (needs, wants, savings);
            await db.SaveChangesAsync(ct);
            return Results.Ok(s);
        });

        // ---- Categories ----
        api.MapGet("/categories", async (BudgetDbContext db, CancellationToken ct) =>
            (await db.Categories.AsNoTracking().ToListAsync(ct)).OrderBy(c => c.Bucket).ThenBy(c => c.Name));

        api.MapPost("/categories", async (CategoryUpsert body, BudgetDbContext db, CancellationToken ct) =>
        {
            var name = body.Name?.Trim();
            if (string.IsNullOrEmpty(name)) return Results.BadRequest(new { message = "Name is required." });
            if (await db.Categories.AnyAsync(c => c.Name == name, ct))
                return Results.Conflict(new { message = $"'{name}' already exists." });
            var c = new Category { Name = name, Bucket = body.Bucket };
            db.Categories.Add(c);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/categories/{c.Id}", c);
        });

        // Moving a category between buckets (e.g. Dining → Needs) re-shapes every budget that uses it.
        api.MapPut("/categories/{id:int}", async (int id, CategoryUpsert body, BudgetDbContext db, CancellationToken ct) =>
        {
            var c = await db.Categories.FindAsync([id], ct);
            if (c is null) return Results.NotFound();
            if (!string.IsNullOrWhiteSpace(body.Name)) c.Name = body.Name.Trim();
            c.Bucket = body.Bucket;
            if (body.IsArchived is bool a) c.IsArchived = a;
            await db.SaveChangesAsync(ct);
            return Results.Ok(c);
        });

        // ---- Merchant rules ----
        api.MapGet("/rules", async (BudgetDbContext db, CancellationToken ct) =>
            (await db.MerchantRules.AsNoTracking().Include(r => r.Category).ToListAsync(ct))
                .OrderBy(r => r.Priority).ThenBy(r => r.Pattern)
                .Select(r => new { r.Id, r.Pattern, r.Priority, r.LearnedFromUser, r.CategoryId, category = r.Category!.Name }));

        api.MapPost("/rules", async (RuleCreate body, BudgetDbContext db, Categorizer cat, CancellationToken ct) =>
        {
            if (MerchantText.Normalize(body.Pattern).Length < 3)
                return Results.BadRequest(new { message = "Pattern must be at least 3 characters." });
            if (!await db.Categories.AnyAsync(c => c.Id == body.CategoryId, ct))
                return Results.BadRequest(new { message = $"Category {body.CategoryId} doesn't exist." });
            var rule = await cat.LearnAsync(body.Pattern, body.CategoryId, ct);
            if (body.Priority is int p) rule.Priority = p;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { rule.Id, rule.Pattern, rule.Priority, rule.CategoryId });
        });

        api.MapDelete("/rules/{id:int}", async (int id, BudgetDbContext db, CancellationToken ct) =>
            await db.MerchantRules.Where(r => r.Id == id).ExecuteDeleteAsync(ct) == 0 ? Results.NotFound() : Results.NoContent());

        // ---- Investments ----
        api.MapGet("/holdings", async (BudgetDbContext db, BudgetService budgets, Clock clock, CancellationToken ct) =>
        {
            var holdings = await db.Holdings.AsNoTracking().ToListAsync(ct);
            var baseCcy = (await budgets.GetSettingsAsync(ct)).BaseCurrency;
            return Portfolio.Summarise(holdings, await ContributionsAsync(db, holdings, baseCcy, ct), clock.UtcNow,
                await PolicyEndpoints.FiguresAsync(db, clock.Today, ct));
        });

        // Fetches prices for holdings set to AutoPrice and exchange rates for foreign ones.
        // The Invest screen calls this each time it opens; recent prices are skipped unless force=true.
        api.MapPost("/holdings/refresh", async (bool? force, PriceRefresher refresher, CancellationToken ct) =>
            Results.Ok(await refresher.RefreshAsync(force ?? false, ct)));

        api.MapPost("/holdings", async (HoldingUpsert body, BudgetDbContext db, Clock clock, CancellationToken ct) =>
        {
            if (Invalid(body) is { } problem) return Results.BadRequest(new { message = problem });
            var h = new Holding();
            Apply(h, body, clock);
            db.Holdings.Add(h);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/holdings/{h.Id}", h);
        });

        api.MapPut("/holdings/{id:int}", async (int id, HoldingUpsert body, BudgetDbContext db, Clock clock, CancellationToken ct) =>
        {
            var h = await db.Holdings.FindAsync([id], ct);
            if (h is null) return Results.NotFound();
            if (Invalid(body) is { } problem) return Results.BadRequest(new { message = problem });
            Apply(h, body, clock);
            await db.SaveChangesAsync(ct);
            return Results.Ok(h);
        });

        api.MapPut("/holdings/{id:int}/price", async (int id, PriceUpdate body, BudgetDbContext db, Clock clock, CancellationToken ct) =>
        {
            var h = await db.Holdings.FindAsync([id], ct);
            if (h is null) return Results.NotFound();
            if (body.Price < 0) return Results.BadRequest(new { message = "Price can't be negative." });
            h.LastPrice = body.Price;
            h.LastPriceAtUtc = clock.UtcNow;
            if (body.FxToBase is decimal fx && fx > 0) h.FxToBase = fx;
            await db.SaveChangesAsync(ct);
            return Results.Ok(h);
        });

        api.MapDelete("/holdings/{id:int}", async (int id, BudgetDbContext db, CancellationToken ct) =>
        {
            if (await db.Holdings.Where(h => h.Id == id).ExecuteDeleteAsync(ct) == 0) return Results.NotFound();
            // An ILP's terms and statements go with it.
            await db.PolicyTerms.Where(t => t.HoldingId == id).ExecuteDeleteAsync(ct);
            await db.PolicyValuations.Where(v => v.HoldingId == id).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        // ---- Export ----
        api.MapGet("/export/transactions.csv", async (BudgetDbContext db, CancellationToken ct) =>
        {
            var rows = await db.Transactions.AsNoTracking().Include(t => t.Category).Include(t => t.Account)
                .OrderBy(t => t.OccurredAtUtc).ToListAsync(ct);
            var sb = new StringBuilder("Date,Amount,Currency,Type,Merchant,Category,Bucket,Account,Card,Source,Notes\n");
            foreach (var t in rows.Select(TransactionEndpoints.ToDto))
                sb.AppendLine(string.Join(',',
                    t.OccurredAt.ToString("yyyy-MM-dd HH:mm"), t.Amount.ToString("0.00"), t.Currency,
                    t.IsIncome ? "Income" : "Spend", Csv(t.Merchant), Csv(t.Category), t.Bucket ?? "",
                    Csv(t.Account), Csv(t.Card), t.Source, Csv(t.Notes)));
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "transactions.csv");
        });
    }

    private static string? Invalid(HoldingUpsert b)
    {
        if (string.IsNullOrWhiteSpace(b.Symbol)) return "Symbol is required.";
        if (b.Units < 0 || b.AverageCost < 0) return "Units and cost can't be negative.";
        if (b.ContributionAmount is <= 0) return "The monthly amount must be more than zero, or left empty.";
        if (b.ContributionMatch is { Length: > 100 }) return "Keep the bank text under 100 characters.";
        if (b.ContributionMatch is { } m && !string.IsNullOrWhiteSpace(m) && m.Trim().Length < 3)
            return "Use at least 3 letters of the bank text, such as FWD, so other payments don't match.";
        return null;
    }

    private static void Apply(Holding h, HoldingUpsert b, Clock clock)
    {
        var now = clock.UtcNow;
        h.Symbol = b.Symbol.Trim().ToUpperInvariant();
        h.Name = b.Name?.Trim() ?? h.Symbol;
        h.AssetClass = b.AssetClass;
        h.Platform = b.Platform;
        // New figures take in every contribution made so far; only later ones are added on top.
        if (h.Id == 0 || h.Units != b.Units || h.AverageCost != b.AverageCost || h.FiguresAsOfUtc is null)
            h.FiguresAsOfUtc = now;
        h.Units = b.Units;
        h.AverageCost = b.AverageCost;
        h.Currency = string.IsNullOrWhiteSpace(b.Currency) ? "SGD" : b.Currency.ToUpperInvariant();
        if (b.FxToBase is decimal fx && fx > 0) h.FxToBase = fx;
        // Only a changed price counts as a new one, so saving a name doesn't make an old balance look current.
        if (b.LastPrice is decimal p && (p != h.LastPrice || h.LastPriceAtUtc is null))
        {
            h.LastPrice = p;
            h.LastPriceAtUtc = now;
        }
        if (b.AutoPrice is bool auto)
        {
            if (auto != h.AutoPrice) h.PriceError = null;
            h.AutoPrice = auto;
        }
        if (b.ContributionMatch is not null)
        {
            var match = b.ContributionMatch.Trim();
            h.ContributionMatch = match.Length == 0 ? null : match;
            h.ContributionAmount = match.Length == 0 ? null : b.ContributionAmount;
            h.ContributionAccountId = match.Length == 0 ? null : b.ContributionAccountId;
        }
    }

    /// <summary>Bank entries that paid into a holding with a contribution rule (see Portfolio.MatchContributions).</summary>
    public static async Task<IReadOnlyList<Contribution>> ContributionsAsync(
        BudgetDbContext db, List<Holding> holdings, string baseCurrency, CancellationToken ct)
    {
        var texts = holdings.Where(h => !string.IsNullOrWhiteSpace(h.ContributionMatch))
            .Select(h => Portfolio.Normalise(h.ContributionMatch!)).Distinct().ToList();
        if (texts.Count == 0) return [];

        // A rough filter in SQL on the first word of each match; the exact rule runs after loading.
        var candidates = new Dictionary<int, Transaction>();
        foreach (var word in texts.Select(t => t.Split(' ')[0]).Distinct())
        {
            foreach (var t in await db.Transactions.AsNoTracking()
                         .Where(t => !t.IsIncome && t.Merchant.ToUpper().Contains(word)).ToListAsync(ct))
                candidates[t.Id] = t;
        }
        return Portfolio.MatchContributions(holdings, candidates.Values.OrderBy(t => t.OccurredAtUtc), baseCurrency);
    }

    // Also neutralises spreadsheet formula injection (=, +, -, @) since merchant text comes from outside.
    private static string Csv(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s[0] is '=' or '+' or '-' or '@') s = "'" + s;
        return s.IndexOfAny([',', '"', '\n']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
