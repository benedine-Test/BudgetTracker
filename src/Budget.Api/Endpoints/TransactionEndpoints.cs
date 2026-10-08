using Budget.Api.Data;
using Budget.Api.Services;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Endpoints;

// ---- Request / response shapes ----

/// <summary>
/// What the iPhone Shortcut posts. Every field is a string on purpose: Shortcuts
/// hands over "S$12.50" or "12.50" depending on how you wire it, and we'd rather
/// parse leniently than drop a transaction. Numbers are read as text too.
/// </summary>
public record ApplePayIngest(
    [property: JsonConverter(typeof(AnyAsText))] string? Amount,
    [property: JsonConverter(typeof(AnyAsText))] string? Merchant,
    [property: JsonConverter(typeof(AnyAsText))] string? Card,
    [property: JsonConverter(typeof(AnyAsText))] string? Date,
    [property: JsonConverter(typeof(AnyAsText))] string? Name);

public record SpendCreate(decimal Amount, string Merchant, string? Currency, string? Date, int? CategoryId, string? Notes, string? Card, int? AccountId);
// Kind: salary (default) | other | refund. Cpf: on a salary, the CPF paid on it (employee + employer), added to the CPF holdings.
public record IncomeCreate(decimal Amount, string? Date, string? Note, string? Kind, int? AccountId, decimal? Cpf = null);
/// <summary>AccountId 0 takes the entry off its account.</summary>
public record TransactionUpdate(decimal? Amount, string? Merchant, string? Date, string? Notes, int? CategoryId, int? AccountId);
public record Categorise(int CategoryId, bool LearnRule = true, string? Pattern = null, bool ApplyToSimilar = true);
/// <summary>MyShare null = the bill is all yours again.</summary>
public record ShareUpdate(decimal? MyShare, string? SharedWith);
/// <summary>Either an existing money-in entry (RepaymentId), or a new one to record (Amount, Date, AccountId, Note).</summary>
public record RepaymentIn(int? RepaymentId, decimal? Amount, string? Date, int? AccountId, string? Note);

/// <param name="Owed">On a shared bill: what's still owed back. Null when the bill isn't shared.</param>
public record TransactionDto(
    int Id, DateTimeOffset OccurredAt, decimal Amount, string Currency, bool IsIncome,
    string Merchant, string? Card, string? Notes, string Source, string? MergedSources,
    int? CategoryId, string? Category, string? Bucket, bool CategoryConfirmed, int? AccountId, string? Account,
    decimal? MyShare = null, string? SharedWith = null, decimal? Owed = null, decimal Repaid = 0, int? RepaysId = null);

public record OwedDto(TransactionDto Bill, decimal Owed, decimal Repaid);
public record OwedList(decimal Total, IReadOnlyList<OwedDto> Bills);

public static class TransactionEndpoints
{
    public static TransactionDto ToDto(Transaction t) => ToDto(t, 0);

    /// <param name="repaid">For a shared bill, the repayments linked to it so far.</param>
    public static TransactionDto ToDto(Transaction t, decimal repaid) => new(
        t.Id,
        new DateTimeOffset(DateTime.SpecifyKind(t.OccurredAtUtc, DateTimeKind.Utc)).ToOffset(TimeSpan.FromHours(8)),
        t.Amount, t.Currency, t.IsIncome, t.Merchant, t.CardName, t.Notes,
        t.Source.ToString(), t.MergedSources, t.CategoryId, t.Category?.Name, t.Category?.Bucket.ToString(),
        t.CategoryConfirmed, t.AccountId, t.Account?.Name,
        t.MyShare, t.SharedWith, t.MyShare is null ? null : SharedBills.Owed(t, repaid), repaid, t.RepaysId);

    private static async Task<TransactionDto> ToDtoAsync(BudgetDbContext db, Transaction t, CancellationToken ct) =>
        ToDto(t, t.MyShare is null ? 0 : (await SharedBills.RepaidAsync(db, [t.Id], ct)).GetValueOrDefault(t.Id));

    public static void MapTransactionEndpoints(this RouteGroupBuilder api)
    {
        // Called by the iPhone "Transaction" automation on every Apple Pay tap.
        api.MapPost("/ingest/apple-pay", (ApplePayIngest body, TransactionService svc, Clock clock,
            BudgetService budgets, CancellationToken ct) =>
            Ingest(body, TransactionSource.ApplePayShortcut, svc, clock, budgets, ct));

        // Called by whatever reads bank alert emails (Power Automate, Gmail Apps Script…)
        // after it has pulled amount / merchant / time out of the email body.
        api.MapPost("/ingest/card-alert", (ApplePayIngest body, TransactionService svc, Clock clock,
            BudgetService budgets, CancellationToken ct) =>
            Ingest(body, TransactionSource.EmailAlert, svc, clock, budgets, ct));

        api.MapPost("/transactions", async (SpendCreate body, BudgetDbContext db, Categorizer cat, Clock clock,
            BudgetService budgets, TransactionService svc, CancellationToken ct) =>
        {
            if (body.Amount <= 0) return Results.BadRequest(new { message = "Amount must be positive." });
            if (string.IsNullOrWhiteSpace(body.Merchant)) return Results.BadRequest(new { message = "Merchant is required." });
            if (body.CategoryId is int cid && !await db.Categories.AnyAsync(c => c.Id == cid, ct))
                return Results.BadRequest(new { message = $"Category {cid} doesn't exist." });
            if (body.AccountId is int aid && !await db.Accounts.AnyAsync(a => a.Id == aid, ct))
                return Results.BadRequest(new { message = $"Account {aid} doesn't exist." });

            var settings = await budgets.GetSettingsAsync(ct);
            var tx = new Transaction
            {
                OccurredAtUtc = clock.ParseToUtc(body.Date),
                Amount = Math.Round(body.Amount, 2),
                Currency = string.IsNullOrWhiteSpace(body.Currency) ? settings.BaseCurrency : body.Currency.ToUpperInvariant(),
                Merchant = body.Merchant.Trim(),
                CardName = body.Card,
                Notes = body.Notes,
                Source = TransactionSource.Manual,
                AccountId = body.AccountId,
                CategoryId = body.CategoryId ?? await cat.MatchAsync(body.Merchant, ct),
                CategoryConfirmed = body.CategoryId is not null
            };
            await svc.LinkByCardAsync(tx, ct);
            db.Transactions.Add(tx);
            await svc.AbsorbPendingFaresAsync(tx, ct);
            await db.SaveChangesAsync(ct);
            await LoadRefsAsync(db, tx, ct);
            return Results.Created($"/api/transactions/{tx.Id}", ToDto(tx));
        });

        api.MapPost("/income", async (IncomeCreate body, BudgetService budgets, BudgetDbContext db, Clock clock, CancellationToken ct) =>
        {
            if (body.Amount <= 0) return Results.BadRequest(new { message = "Amount must be positive." });
            if (body.Cpf is < 0) return Results.BadRequest(new { message = "CPF can't be negative." });
            if (body.AccountId is int aid && !await db.Accounts.AnyAsync(a => a.Id == aid, ct))
                return Results.BadRequest(new { message = $"Account {aid} doesn't exist." });
            var when = clock.ParseToUtc(body.Date);
            var kind = (body.Kind ?? "salary").ToLowerInvariant();

            if (kind == "salary")
            {
                var (tx, period) = await budgets.RecordSalaryAsync(Math.Round(body.Amount, 2), when, body.Note, body.AccountId, cpfContribution: body.Cpf, ct: ct);
                await LoadRefsAsync(db, tx, ct);
                return Results.Ok(new
                {
                    transaction = ToDto(tx),
                    period = new
                    {
                        period.StartDate, period.EndDate, period.TakeHomeIncome,
                        needs = period.NeedsBudget, wants = period.WantsBudget, savings = period.SavingsBudget
                    }
                });
            }

            var catName = kind == "refund" ? "Refund" : "Other Income";
            var cat = await db.Categories.SingleAsync(c => c.Name == catName, ct);
            var other = new Transaction
            {
                OccurredAtUtc = when, Amount = Math.Round(body.Amount, 2), IsIncome = true,
                Merchant = body.Note ?? catName, Source = TransactionSource.Manual,
                AccountId = body.AccountId, CategoryId = cat.Id, CategoryConfirmed = true
            };
            db.Transactions.Add(other);
            await db.SaveChangesAsync(ct);
            await LoadRefsAsync(db, other, ct);
            return Results.Ok(new { transaction = ToDto(other) });
        });

        api.MapGet("/transactions", async (BudgetDbContext db, Clock clock,
            string? from, string? to, int? categoryId, string? bucket, bool? uncategorised, string? q, int? take,
            int? accountId, bool? shared, CancellationToken ct) =>
        {
            var query = db.Transactions.AsNoTracking().Include(t => t.Category).Include(t => t.Account).AsQueryable();

            if (!string.IsNullOrWhiteSpace(from))
            {
                if (!DateOnly.TryParse(from, out var f)) return Results.BadRequest(new { message = "from must be yyyy-MM-dd." });
                var fromUtc = clock.LocalDateStartToUtc(f);
                query = query.Where(t => t.OccurredAtUtc >= fromUtc);
            }
            if (!string.IsNullOrWhiteSpace(to))
            {
                if (!DateOnly.TryParse(to, out var tt)) return Results.BadRequest(new { message = "to must be yyyy-MM-dd." });
                var toUtc = clock.LocalDateStartToUtc(tt.AddDays(1));
                query = query.Where(t => t.OccurredAtUtc < toUtc);
            }
            if (categoryId is int cid) query = query.Where(t => t.CategoryId == cid);
            if (accountId is int aid) query = aid == 0 ? query.Where(t => t.AccountId == null) : query.Where(t => t.AccountId == aid);
            if (uncategorised == true) query = query.Where(t => t.CategoryId == null);
            if (shared == true) query = query.Where(t => t.MyShare != null && !t.IsIncome);
            if (!string.IsNullOrWhiteSpace(bucket) && Enum.TryParse<Bucket>(bucket, true, out var b))
                query = query.Where(t => t.Category != null && t.Category.Bucket == b);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var needle = q.ToUpper();
                query = query.Where(t => t.Merchant.ToUpper().Contains(needle) || (t.Notes != null && t.Notes.ToUpper().Contains(needle)));
            }

            var rows = await query.OrderByDescending(t => t.OccurredAtUtc).ThenByDescending(t => t.Id)
                .Take(Math.Clamp(take ?? 100, 1, 1000)).ToListAsync(ct);
            var repaid = await SharedBills.RepaidAsync(db, rows.Where(t => t.MyShare != null).Select(t => t.Id), ct);
            return Results.Ok(rows.Select(t => ToDto(t, repaid.GetValueOrDefault(t.Id))));
        });

        api.MapGet("/transactions/{id:int}", async (int id, BudgetDbContext db, CancellationToken ct) =>
            await db.Transactions.Include(t => t.Category).Include(t => t.Account).FirstOrDefaultAsync(t => t.Id == id, ct) is { } t
                ? Results.Ok(await ToDtoAsync(db, t, ct)) : Results.NotFound());

        api.MapPut("/transactions/{id:int}", async (int id, TransactionUpdate body, BudgetDbContext db, Clock clock, CancellationToken ct) =>
        {
            var t = await db.Transactions.Include(x => x.Category).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();
            if (body.Amount is decimal neg && neg <= 0) return Results.BadRequest(new { message = "Amount must be above zero." });
            var newAmount = body.Amount is decimal a ? Math.Round(a, 2) : t.Amount;
            var newWhen = string.IsNullOrWhiteSpace(body.Date) ? t.OccurredAtUtc : clock.ParseToUtc(body.Date);
            // A salary also set its pay period's budget; changing it here would leave that budget wrong.
            if (t.IsIncome && t.Category?.Name == "Salary" && (newAmount != t.Amount || newWhen != t.OccurredAtUtc))
                return Results.BadRequest(new { message = "To change a salary, delete it and record it again so the budget updates too." });
            if (newAmount != t.Amount && t.MyShare is decimal mine)
            {
                var repaid = (await SharedBills.RepaidAsync(db, [t.Id], ct)).GetValueOrDefault(t.Id);
                if (newAmount < mine + repaid)
                    return Results.BadRequest(new { message = $"This bill is shared: your share and what's been paid back come to {TransactionService.Money(mine + repaid, t.Currency)}. Lower your share first." });
            }
            if (newAmount != t.Amount && t.RepaysId is int billId
                && await db.Transactions.AsNoTracking().FirstOrDefaultAsync(b => b.Id == billId, ct) is { MyShare: decimal billShare } bill)
            {
                var others = (await SharedBills.RepaidAsync(db, [billId], ct)).GetValueOrDefault(billId) - t.Amount;
                if (billShare + others + newAmount > bill.Amount)
                    return Results.BadRequest(new { message = "That's more than is owed on the bill it pays back. Unlink it first." });
            }
            t.Amount = newAmount;
            t.OccurredAtUtc = newWhen;
            if (body.Merchant is not null)
            {
                var name = body.Merchant.Trim();
                if (name.Length == 0) return Results.BadRequest(new { message = "The shop or description can't be empty." });
                t.Merchant = name.Length > 200 ? name[..200] : name;
            }
            if (body.Notes is not null) t.Notes = body.Notes;
            if (body.CategoryId is int cid)
            {
                if (!await db.Categories.AnyAsync(c => c.Id == cid, ct))
                    return Results.BadRequest(new { message = $"Category {cid} doesn't exist." });
                t.CategoryId = cid;
                t.CategoryConfirmed = true;
            }
            if (body.AccountId is int aid)
            {
                if (aid != 0 && !await db.Accounts.AnyAsync(a => a.Id == aid, ct))
                    return Results.BadRequest(new { message = $"Account {aid} doesn't exist." });
                t.AccountId = aid == 0 ? null : aid;
            }
            await db.SaveChangesAsync(ct);
            await LoadRefsAsync(db, t, ct);
            return Results.Ok(await ToDtoAsync(db, t, ct));
        });

        // ---- Paying for friends ----
        api.MapPut("/transactions/{id:int}/share", (int id, ShareUpdate body, SharedBills bills, BudgetDbContext db, CancellationToken ct) =>
            Shared(db, () => bills.SetShareAsync(id, body.MyShare, body.SharedWith, ct), ct));

        api.MapPost("/transactions/{id:int}/repayments", async (int id, RepaymentIn body, SharedBills bills, BudgetDbContext db,
            Clock clock, CancellationToken ct) =>
        {
            if (body.RepaymentId is int rid) return await Shared(db, () => bills.LinkAsync(id, rid, ct), ct);
            if (body.Amount is not decimal amount) return Results.BadRequest(new { message = "Enter how much was paid back." });
            return await Shared(db, () => bills.RecordAsync(id, amount, clock.ParseToUtc(body.Date), body.AccountId, body.Note, ct), ct);
        });

        // On a repayment: it no longer pays back the bill, and is plain Other Income again.
        api.MapDelete("/transactions/{id:int}/repays", (int id, SharedBills bills, BudgetDbContext db, CancellationToken ct) =>
            Shared(db, () => bills.UnlinkAsync(id, ct), ct));

        api.MapPost("/transactions/{id:int}/write-off", (int id, SharedBills bills, BudgetDbContext db, CancellationToken ct) =>
            Shared(db, () => bills.WriteOffAsync(id, ct), ct));

        // Bills with money still owed back; ?amount= puts the ones a repayment of that size fits first.
        api.MapGet("/owed", async (decimal? amount, SharedBills bills, BudgetService budgets, CancellationToken ct) =>
        {
            var settings = await budgets.GetSettingsAsync(ct);
            var open = await bills.OpenAsync(amount, ct);
            return Results.Ok(new OwedList(
                open.Where(o => o.Bill.Currency == settings.BaseCurrency).Sum(o => o.Owed),
                open.Select(o => new OwedDto(ToDto(o.Bill, o.Repaid), o.Owed, o.Repaid)).ToList()));
        });

        // Categorise + teach: fixes this one, saves a rule, and re-files similar unconfirmed ones.
        api.MapPost("/transactions/{id:int}/categorise", async (int id, Categorise body, BudgetDbContext db,
            Categorizer categorizer, CancellationToken ct) =>
        {
            var t = await db.Transactions.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();
            if (!await db.Categories.AnyAsync(c => c.Id == body.CategoryId, ct))
                return Results.BadRequest(new { message = $"Category {body.CategoryId} doesn't exist." });

            t.CategoryId = body.CategoryId;
            t.CategoryConfirmed = true;

            string? pattern = null;
            var reFiled = 0;
            if (body.LearnRule)
            {
                pattern = MerchantText.Normalize(body.Pattern ?? MerchantText.SuggestPattern(t.Merchant));
                if (pattern.Length >= 3)
                {
                    await categorizer.LearnAsync(pattern, body.CategoryId, ct);
                    if (body.ApplyToSimilar)
                    {
                        var others = await db.Transactions.Where(x => x.Id != id && !x.CategoryConfirmed && !x.IsIncome).ToListAsync(ct);
                        foreach (var o in others.Where(o => MerchantText.Normalize(o.Merchant).Contains(pattern, StringComparison.Ordinal)))
                        {
                            o.CategoryId = body.CategoryId;
                            reFiled++;
                        }
                    }
                }
                else pattern = null; // too short to be a safe rule
            }

            await db.SaveChangesAsync(ct);
            await LoadRefsAsync(db, t, ct);
            return Results.Ok(new { transaction = ToDto(t), learnedPattern = pattern, reFiled });
        });

        api.MapDelete("/transactions/{id:int}", async (int id, BudgetDbContext db, BudgetService budgets, CancellationToken ct) =>
        {
            var t = await db.Transactions.Include(x => x.Category).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();

            // A salary entry also fed a pay period's budget, so deleting it must take that back out.
            if (t.IsIncome && t.Category?.Name == "Salary")
                await budgets.ReverseSalaryAsync(t, ct);
            // A shared bill's repayments stay (the money did come in), as plain income.
            await SharedBills.DetachRepaymentsAsync(db, [t.Id], ct);

            db.Transactions.Remove(t);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static async Task<IResult> Ingest(ApplePayIngest body, TransactionSource source,
        TransactionService svc, Clock clock, BudgetService budgets, CancellationToken ct)
    {
        var merchant = FirstNonBlank(body.Merchant, body.Name) ?? "Unknown merchant";

        var settings = await budgets.GetSettingsAsync(ct);
        var readable = AmountParser.TryParse(body.Amount, settings.BaseCurrency, out var amount, out var currency);

        // Bus/MRT taps report S$0, nothing, or text like "Pending": the fare is worked out after
        // the trip and charged later. Keep the trip rather than reject it. Bank alerts with S$0 are noise.
        if (source == TransactionSource.ApplePayShortcut
            && (AmountParser.IsBlankOrZero(body.Amount) || (!readable && MerchantText.IsTransit(merchant))))
        {
            var trip = await svc.LogPendingFareAsync(merchant, body.Card, clock.ParseToUtc(body.Date), source, ct);
            return Results.Ok(new { message = trip.Message, duplicate = false, transaction = ToDto(trip.Transaction) });
        }

        if (!readable)
            return Results.BadRequest(new { message = $"Couldn't read amount '{body.Amount}'." });

        var result = await svc.IngestSpendAsync(amount, currency, merchant, body.Card,
            clock.ParseToUtc(body.Date), source, null, ct);

        return Results.Ok(new { message = result.Message, duplicate = result.WasDuplicate, transaction = ToDto(result.Transaction) });
    }

    private static async Task<IResult> Shared(BudgetDbContext db, Func<Task<Transaction>> change, CancellationToken ct)
    {
        try
        {
            var t = await change();
            await LoadRefsAsync(db, t, ct);
            return Results.Ok(await ToDtoAsync(db, t, ct));
        }
        catch (SharedBillException e)
        {
            return Results.BadRequest(new { message = e.Message });
        }
    }

    private static async Task LoadRefsAsync(BudgetDbContext db, Transaction t, CancellationToken ct)
    {
        await db.Entry(t).Reference(x => x.Category).LoadAsync(ct);
        await db.Entry(t).Reference(x => x.Account).LoadAsync(ct);
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
