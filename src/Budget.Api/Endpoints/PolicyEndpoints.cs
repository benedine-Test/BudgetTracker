using Budget.Api.Data;
using Budget.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Endpoints;

/// <summary>Contract terms of an ILP. A null schedule keeps the current one (or, the first time, the plan's standard terms).</summary>
public record PolicySetup(DateOnly CommencementDate, decimal MonthlyPremium, int InitialPeriodMonths, int MinimumInvestmentYears,
    PolicySchedule? Schedule = null, string? BankText = null);

public record ValuationAdd(DateOnly AsOf, decimal InitialUnits, decimal AccumulationUnits);

/// <summary>Where the policy stands today, by the calendar (no values: those only come from statements).</summary>
public record PolicyToday(DateOnly Date, int PolicyYear, decimal EarlyEncashmentPercent, int PremiumsDue, decimal PremiumsPaid,
    DateOnly NextYearStarts, decimal NextEarlyEncashmentPercent);

public record PolicyView(int HoldingId, string Name, bool Configured, DateOnly? CommencementDate, decimal MonthlyPremium,
    int InitialPeriodMonths, int MinimumInvestmentYears, PolicySchedule Schedule, string? BankText, string? PlanName,
    PolicyToday? Today, List<PolicyPosition> Statements, int BankPremiumsFound, List<DateOnly> MissedPremiums);

public record PolicySaved(PolicyView Policy, int ReFiled, int ConfirmedElsewhere, string? Category);

public record ProjectionView(decimal GrossReturnPercent, decimal NetReturnPercent, DateOnly? FromStatement, List<ProjectionYear> Years);

public static class PolicyEndpoints
{
    public static void MapPolicyEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/holdings/{id:int}/policy", async (int id, BudgetDbContext db, Clock clock, CancellationToken ct) =>
            await db.Holdings.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id, ct) is { AssetClass: AssetClass.Policy } h
                ? Results.Ok(await ViewAsync(db, clock, h, ct))
                : Results.NotFound());

        // Sets up or corrects the contract terms. With bank text, the premium's bank entries are filed under
        // Investments (Savings bucket) from now on, and unconfirmed ones already in the app are re-filed.
        api.MapPut("/holdings/{id:int}/policy", async (int id, PolicySetup body, BudgetDbContext db, Clock clock,
            Categorizer categorizer, CancellationToken ct) =>
        {
            var h = await db.Holdings.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (h is not { AssetClass: AssetClass.Policy }) return Results.NotFound();
            var terms = await db.PolicyTerms.FirstOrDefaultAsync(t => t.HoldingId == id, ct);
            var schedule = body.Schedule ?? (terms is null ? PolicyPlans.HsbcWealthAccelerate() : PolicySchedule.Read(terms.ScheduleJson));
            if (Invalid(body, schedule, clock.Today) is { } problem) return Results.BadRequest(new { message = problem });

            if (terms is null)
            {
                terms = new PolicyTerms { HoldingId = id };
                db.PolicyTerms.Add(terms);
            }
            terms.CommencementDate = body.CommencementDate;
            terms.MonthlyPremium = Math.Round(body.MonthlyPremium, 2);
            terms.InitialPeriodMonths = body.InitialPeriodMonths;
            terms.MinimumInvestmentYears = body.MinimumInvestmentYears;
            terms.ScheduleJson = schedule.Write();

            // The holding's own figures follow the policy, so the line still makes sense if the terms are removed.
            h.Units = 1;
            h.AutoPrice = false;
            h.PriceError = null;
            h.Currency = "SGD";
            h.FxToBase = 1;
            h.AverageCost = PolicyMath.PremiumsDue(terms.CommencementDate, clock.Today) * terms.MonthlyPremium;
            h.FiguresAsOfUtc = clock.UtcNow;

            int reFiled = 0, elsewhere = 0;
            string? category = null;
            if (body.BankText is not null)
            {
                var text = MerchantText.Normalize(body.BankText);
                h.ContributionMatch = text.Length == 0 ? null : text;
                h.ContributionAmount = text.Length == 0 ? null : terms.MonthlyPremium;
                if (text.Length > 0)
                {
                    var savings = await db.Categories.Where(c => c.Bucket == Bucket.Savings && !c.IsArchived)
                        .OrderBy(c => c.Name == "Investments" ? 0 : 1).ThenBy(c => c.Id).FirstOrDefaultAsync(ct);
                    if (savings is null)
                        return Results.BadRequest(new { message = "Add a category in the Savings bucket (such as Investments) first." });
                    category = savings.Name;
                    await categorizer.LearnAsync(text, savings.Id, ct);
                    var word = text.Split(' ')[0];
                    var matching = (await db.Transactions.Where(t => !t.IsIncome && t.Merchant.ToUpper().Contains(word)).ToListAsync(ct))
                        .Where(t => MerchantText.Normalize(t.Merchant).Contains(text, StringComparison.Ordinal)).ToList();
                    foreach (var t in matching.Where(t => t.CategoryId != savings.Id))
                    {
                        if (t.CategoryConfirmed) { elsewhere++; continue; }
                        t.CategoryId = savings.Id;
                        reFiled++;
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new PolicySaved(await ViewAsync(db, clock, h, ct), reFiled, elsewhere, category));
        });

        // A statement's figures. A second one for the same date replaces the first.
        api.MapPost("/holdings/{id:int}/policy/valuations", async (int id, ValuationAdd body, BudgetDbContext db, Clock clock, CancellationToken ct) =>
        {
            var h = await db.Holdings.FirstOrDefaultAsync(x => x.Id == id, ct);
            var terms = await db.PolicyTerms.AsNoTracking().FirstOrDefaultAsync(t => t.HoldingId == id, ct);
            if (h is null || terms is null) return Results.NotFound();
            if (body.AsOf < terms.CommencementDate) return Results.BadRequest(new { message = "The statement date is before the policy started." });
            if (body.AsOf > clock.Today) return Results.BadRequest(new { message = "The statement date can't be in the future." });
            if (body.InitialUnits < 0 || body.AccumulationUnits < 0) return Results.BadRequest(new { message = "Account values can't be negative." });

            var v = await db.PolicyValuations.FirstOrDefaultAsync(x => x.HoldingId == id && x.AsOf == body.AsOf, ct);
            if (v is null)
            {
                v = new PolicyValuation { HoldingId = id, AsOf = body.AsOf, CreatedAtUtc = clock.UtcNow };
                db.PolicyValuations.Add(v);
            }
            v.InitialUnits = Math.Round(body.InitialUnits, 2);
            v.AccumulationUnits = Math.Round(body.AccumulationUnits, 2);
            await db.SaveChangesAsync(ct);
            await SyncHoldingAsync(db, clock, h, ct);
            return Results.Ok(await ViewAsync(db, clock, h, ct));
        });

        api.MapDelete("/holdings/{id:int}/policy/valuations/{valuationId:int}", async (int id, int valuationId, BudgetDbContext db, Clock clock, CancellationToken ct) =>
        {
            var h = await db.Holdings.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (h is null) return Results.NotFound();
            if (await db.PolicyValuations.Where(v => v.Id == valuationId && v.HoldingId == id).ExecuteDeleteAsync(ct) == 0)
                return Results.NotFound();
            await SyncHoldingAsync(db, clock, h, ct);
            return Results.Ok(await ViewAsync(db, clock, h, ct));
        });

        // Account and surrender value per policy year to the year after the Minimum Investment Period,
        // once per gross return rate (default 4% and 8%). from=latest starts at the newest statement.
        api.MapGet("/holdings/{id:int}/policy/projection", async (int id, decimal[]? rate, string? from, BudgetDbContext db, CancellationToken ct) =>
        {
            var terms = await db.PolicyTerms.AsNoTracking().FirstOrDefaultAsync(t => t.HoldingId == id, ct);
            if (terms is null) return Results.NotFound();
            var rates = rate is { Length: > 0 } ? rate : [4m, 8m];
            if (rates.Length > 4 || rates.Any(r => r is < -50 or > 50))
                return Results.BadRequest(new { message = "Give up to 4 return rates, each between −50% and 50%." });

            PolicyValuation? start = null;
            if (string.Equals(from, "latest", StringComparison.OrdinalIgnoreCase))
            {
                start = await db.PolicyValuations.AsNoTracking().Where(v => v.HoldingId == id)
                    .OrderByDescending(v => v.AsOf).FirstOrDefaultAsync(ct);
                if (start is null) return Results.BadRequest(new { message = "Enter a statement first to project from it." });
            }

            var schedule = PolicySchedule.Read(terms.ScheduleJson);
            var toYear = terms.MinimumInvestmentYears + 1;
            return Results.Ok(rates.Select(r => new ProjectionView(r, r - schedule.FundChargePercent, start?.AsOf,
                PolicyMath.Project(terms, schedule, r, toYear, start).ToList())));
        });
    }

    private static string? Invalid(PolicySetup b, PolicySchedule schedule, DateOnly today)
    {
        if (b.CommencementDate > today) return "The start date can't be in the future.";
        if (b.CommencementDate.Year < 1950) return "Check the start date.";
        if (b.MonthlyPremium <= 0) return "The monthly premium must be more than zero.";
        if (b.InitialPeriodMonths is < 0 or > 600) return "The initial contribution period must be 0–600 months.";
        if (b.MinimumInvestmentYears is < 1 or > 99) return "The minimum investment period must be 1–99 years.";
        if (b.BankText is { } t && t.Trim().Length is > 0 and < 3) return "Use at least 3 letters of the bank text, such as HSBC LIFE.";
        if (b.BankText is { Length: > 100 }) return "Keep the bank text under 100 characters.";
        return PolicyMath.Invalid(schedule);
    }

    /// <summary>The holding's value is the newest statement's account value; with none, premiums at cost.</summary>
    private static async Task SyncHoldingAsync(BudgetDbContext db, Clock clock, Holding h, CancellationToken ct)
    {
        var latest = await db.PolicyValuations.AsNoTracking().Where(v => v.HoldingId == h.Id)
            .OrderByDescending(v => v.AsOf).FirstOrDefaultAsync(ct);
        h.LastPrice = latest is null ? null : latest.InitialUnits + latest.AccumulationUnits;
        h.LastPriceAtUtc = latest is null ? null : clock.LocalDateStartToUtc(latest.AsOf);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Cost and value for every policy holding with terms, for the portfolio (see Portfolio.Summarise).</summary>
    public static async Task<Dictionary<int, (decimal Cost, decimal Value)>> FiguresAsync(BudgetDbContext db, DateOnly today, CancellationToken ct)
    {
        var terms = await db.PolicyTerms.AsNoTracking().ToListAsync(ct);
        if (terms.Count == 0) return [];
        var valuations = (await db.PolicyValuations.AsNoTracking().ToListAsync(ct))
            .GroupBy(v => v.HoldingId).ToDictionary(g => g.Key, g => g.MaxBy(v => v.AsOf));
        return terms.ToDictionary(t => t.HoldingId, t => PolicyMath.Figures(t, valuations.GetValueOrDefault(t.HoldingId), today));
    }

    private static async Task<PolicyView> ViewAsync(BudgetDbContext db, Clock clock, Holding h, CancellationToken ct)
    {
        var terms = await db.PolicyTerms.AsNoTracking().FirstOrDefaultAsync(t => t.HoldingId == h.Id, ct);
        if (terms is null)
        {
            // Not set up yet: offer the standard terms to start from.
            return new PolicyView(h.Id, h.Name, false, null, 0, PolicyPlans.HsbcWealthAccelerateInitialMonths,
                PolicyPlans.HsbcWealthAccelerateMinimumYears, PolicyPlans.HsbcWealthAccelerate(), h.ContributionMatch,
                PolicyPlans.HsbcWealthAccelerateName, null, [], 0, []);
        }

        var schedule = PolicySchedule.Read(terms.ScheduleJson);
        var today = clock.Today;
        var year = PolicyMath.PolicyYear(terms.CommencementDate, today);
        var due = PolicyMath.PremiumsDue(terms.CommencementDate, today);
        var todayView = new PolicyToday(today, year, schedule.EarlyEncashmentIn(year), due, due * terms.MonthlyPremium,
            terms.CommencementDate.AddYears(year), schedule.EarlyEncashmentIn(year + 1));

        var statements = (await db.PolicyValuations.AsNoTracking().Where(v => v.HoldingId == h.Id).ToListAsync(ct))
            .OrderByDescending(v => v.AsOf).Select(v => PolicyMath.Position(terms, schedule, v)).ToList();

        var baseCcy = (await db.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, ct)).BaseCurrency;
        var debits = (await BudgetEndpoints.ContributionsAsync(db, [h], baseCcy, ct))
            .Select(c => clock.ToLocalDate(c.OccurredAtUtc)).ToList();

        return new PolicyView(h.Id, h.Name, true, terms.CommencementDate, terms.MonthlyPremium, terms.InitialPeriodMonths,
            terms.MinimumInvestmentYears, schedule, h.ContributionMatch, null, todayView, statements,
            debits.Count, PolicyMath.MissedPremiums(terms.CommencementDate, debits, today).ToList());
    }
}
