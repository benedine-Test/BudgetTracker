using Budget.Api.Data;
using Budget.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Endpoints;

/// <summary>Balance is what the bank shows now (or at AsOf, ISO date/time; blank = now).</summary>
public record AccountCreate(string Name, AccountKind Kind, string? Currency, decimal Balance, string? AsOf, string? CardNames);
public record AccountUpdate(string? Name, AccountKind? Kind, string? CardNames, bool? IsArchived);
public record BalanceSet(decimal Balance, string? AsOf);

/// <summary>
/// Send Csv (the file's text), Pdf or Excel (the file, base64; .xlsx or .xls) with its Password if it has one.
/// PositiveIsSpend overrides the default for amounts with no in/out marking (CSV and Excel: true for cards,
/// false for bank accounts; PDF: true). Columns overrides CSV and Excel column detection by heading name.
/// </summary>
public record ImportPreviewRequest(string? Csv, bool? PositiveIsSpend, ColumnMap? Columns, string? Pdf = null, string? Password = null,
    string? Excel = null);

/// <summary>
/// Add = statement lines to create. Link = existing entries the statement confirmed, filed under
/// this account. StatementBalance/Date (already in the app's sign) resets the balance to the bank's.
/// </summary>
public record ImportCommitRequest(List<ImportRowIn>? Add, List<int>? Link, decimal? StatementBalance, DateOnly? StatementBalanceDate,
    string? FileName = null);

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/accounts", async (bool? archived, AccountService svc, CancellationToken ct) =>
            Results.Ok(await svc.SummaryAsync(archived == true, ct)));

        api.MapPost("/accounts", async (AccountCreate body, BudgetDbContext db, AccountService svc, Clock clock, CancellationToken ct) =>
        {
            var name = body.Name?.Trim();
            if (string.IsNullOrEmpty(name)) return Results.BadRequest(new { message = "Give the account a name." });
            if (name.Length > 100) return Results.BadRequest(new { message = "Keep the name under 100 characters." });
            var currency = string.IsNullOrWhiteSpace(body.Currency) ? (await db.Settings.SingleAsync(ct)).BaseCurrency : body.Currency.Trim().ToUpperInvariant();
            if (currency.Length != 3) return Results.BadRequest(new { message = "Currency must be a 3-letter code like SGD." });

            var a = new Account
            {
                Name = name,
                Kind = body.Kind,
                Currency = currency,
                AnchorBalance = Math.Round(body.Balance, 2),
                BalanceAsOfUtc = clock.ParseToUtc(body.AsOf),
                CardNames = CleanCards(body.CardNames)
            };
            db.Accounts.Add(a);
            await db.SaveChangesAsync(ct);
            await svc.LinkExistingByCardAsync(a, ct);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/accounts/{a.Id}", AccountService.ToView(a, await svc.BalanceAsync(a, ct)));
        });

        api.MapPut("/accounts/{id:int}", async (int id, AccountUpdate body, BudgetDbContext db, AccountService svc, CancellationToken ct) =>
        {
            var a = await db.Accounts.FindAsync([id], ct);
            if (a is null) return Results.NotFound();
            if (body.Name is not null)
            {
                var name = body.Name.Trim();
                if (name.Length is 0 or > 100) return Results.BadRequest(new { message = "Give the account a name (under 100 characters)." });
                a.Name = name;
            }
            if (body.Kind is AccountKind k) a.Kind = k;
            if (body.IsArchived is bool arch) a.IsArchived = arch;
            var linked = 0;
            if (body.CardNames is not null)
            {
                a.CardNames = CleanCards(body.CardNames);
                await db.SaveChangesAsync(ct);
                linked = await svc.LinkExistingByCardAsync(a, ct);
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { account = AccountService.ToView(a, await svc.BalanceAsync(a, ct)), linked });
        });

        // "My bank app says S$X right now": the balance starts counting again from here.
        api.MapPut("/accounts/{id:int}/balance", async (int id, BalanceSet body, BudgetDbContext db, AccountService svc, Clock clock, CancellationToken ct) =>
        {
            var a = await db.Accounts.FindAsync([id], ct);
            if (a is null) return Results.NotFound();
            a.AnchorBalance = Math.Round(body.Balance, 2);
            a.BalanceAsOfUtc = clock.ParseToUtc(body.AsOf);
            await db.SaveChangesAsync(ct);
            return Results.Ok(AccountService.ToView(a, await svc.BalanceAsync(a, ct)));
        });

        // Entries stay; they just lose the link. Archive instead to keep the history grouped.
        api.MapDelete("/accounts/{id:int}", async (int id, BudgetDbContext db, CancellationToken ct) =>
        {
            var a = await db.Accounts.FindAsync([id], ct);
            if (a is null) return Results.NotFound();
            await db.Transactions.Where(t => t.AccountId == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.AccountId, (int?)null), ct);
            db.Accounts.Remove(a);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Step 1: read the statement and show what's already in the app and what's missing. Saves nothing.
        api.MapPost("/accounts/{id:int}/import/preview", async (int id, ImportPreviewRequest body, BudgetDbContext db,
            AccountService svc, CancellationToken ct) =>
        {
            var a = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null) return Results.NotFound();
            if (!string.IsNullOrWhiteSpace(body.Pdf))
            {
                byte[] bytes;
                try { bytes = Convert.FromBase64String(body.Pdf); }
                catch (FormatException) { return Results.BadRequest(new { message = "That PDF didn't arrive intact. Try again." }); }
                if (bytes.Length > PdfStatementReader.MaxBytes)
                    return Results.BadRequest(new { message = "That PDF is too big. Statements are usually well under 4 MB — is it the right file?" });

                var spend = body.PositiveIsSpend ?? true;
                return Results.Ok(await svc.PreviewAsync(a, PdfStatementReader.Read(bytes, body.Password, spend), spend, ct));
            }

            var positiveIsSpend = body.PositiveIsSpend ?? a.Kind == AccountKind.CreditCard;
            ParsedStatement parsed;
            if (!string.IsNullOrWhiteSpace(body.Excel))
            {
                byte[] bytes;
                try { bytes = Convert.FromBase64String(body.Excel); }
                catch (FormatException) { return Results.BadRequest(new { message = "That Excel file didn't arrive intact. Try again." }); }
                if (bytes.Length > ExcelStatementReader.MaxBytes)
                    return Results.BadRequest(new { message = "That file is too big. Download a shorter date range (a few months at a time)." });
                parsed = ExcelStatementReader.Read(bytes, body.Password, positiveIsSpend, body.Columns);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(body.Csv)) return Results.BadRequest(new { message = "The file is empty." });
                if (body.Csv.Length > StatementParser.MaxBytes)
                    return Results.BadRequest(new { message = "That file is too big. Download a shorter date range (a few months at a time)." });
                parsed = StatementParser.Parse(body.Csv, positiveIsSpend, body.Columns);
            }
            // In a CSV or sheet, a card's balance is shown the same way round as its amounts, so "swap" flips both.
            return Results.Ok(await svc.PreviewAsync(a, parsed, positiveIsSpend, ct, positiveBalanceIsOwed: positiveIsSpend));
        });

        // Step 2: add the lines the person ticked.
        api.MapPost("/accounts/{id:int}/import", async (int id, ImportCommitRequest body, BudgetDbContext db,
            AccountService svc, CancellationToken ct) =>
        {
            var a = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null) return Results.NotFound();
            var add = body.Add ?? [];
            if (add.Count > 5000) return Results.BadRequest(new { message = "Too many lines at once. Import a shorter date range." });
            if (add.Any(r => r.Amount <= 0)) return Results.BadRequest(new { message = "Every line needs an amount above zero." });
            if (add.Any(r => string.IsNullOrWhiteSpace(r.Description))) return Results.BadRequest(new { message = "Every line needs a description." });
            if ((body.StatementBalance is null) != (body.StatementBalanceDate is null))
                return Results.BadRequest(new { message = "Send the statement balance together with its date." });

            return Results.Ok(await svc.CommitAsync(a, add, body.Link ?? [], body.StatementBalance, body.StatementBalanceDate, ct, body.FileName));
        });

        // Past imports, newest first. Only the latest one that's still in can be undone.
        api.MapGet("/accounts/{id:int}/imports", async (int id, BudgetDbContext db, AccountService svc, CancellationToken ct) =>
            await db.Accounts.AnyAsync(a => a.Id == id, ct) ? Results.Ok(await svc.ImportsAsync(id, ct)) : Results.NotFound());

        // Undo a test import (or a wrong one): its entries go, the balance goes back.
        api.MapDelete("/accounts/{id:int}/imports/{batchId:int}", async (int id, int batchId, BudgetDbContext db,
            AccountService svc, CancellationToken ct) =>
        {
            var a = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null) return Results.NotFound();
            try { return Results.Ok(await svc.UndoAsync(a, batchId, ct)); }
            catch (ImportUndoException e) { return Results.Conflict(new { message = e.Message }); }
        });
    }

    private static string? CleanCards(string? cards)
    {
        var parts = (cards ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var joined = string.Join(", ", parts.Distinct(StringComparer.OrdinalIgnoreCase));
        return joined.Length == 0 ? null : joined.Length > 500 ? joined[..500] : joined;
    }
}
