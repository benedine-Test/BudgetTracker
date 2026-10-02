using Budget.Api.Data;
using Budget.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Endpoints;

public record RecurringUpsert(string Name, decimal Amount, int CategoryId, RecurringFrequency Frequency,
    int? Interval, string StartDate, string? EndDate, string? Notes, bool? IsActive);

public record RecurringDto(int Id, string Name, decimal Amount, int CategoryId, string? Category, string? Bucket,
    string Frequency, int Interval, DateOnly StartDate, DateOnly? EndDate, DateOnly NextDueDate,
    DateOnly? LastPostedDate, bool IsActive, string? Notes, decimal MonthlyEquivalent);

public static class RecurringEndpoints
{
    public static RecurringDto ToDto(RecurringItem r) => new(
        r.Id, r.Name, r.Amount, r.CategoryId, r.Category?.Name, r.Category?.Bucket.ToString(),
        r.Frequency.ToString(), r.Interval, r.StartDate, r.EndDate, r.NextDueDate, r.LastPostedDate,
        r.IsActive, r.Notes, Recurrence.MonthlyEquivalent(r.Amount, r.Frequency, r.Interval));

    public static void MapRecurringEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/recurring", async (BudgetDbContext db, RecurringService svc, CancellationToken ct) =>
        {
            await svc.PostDueAsync(ct);
            var rows = await db.RecurringItems.AsNoTracking().Include(r => r.Category).ToListAsync(ct);
            return rows.OrderByDescending(r => r.IsActive).ThenBy(r => r.NextDueDate).ThenBy(r => r.Name).Select(ToDto);
        });

        // Starts from the first scheduled date that is today or later. Past dates are not back-filled.
        api.MapPost("/recurring", async (RecurringUpsert body, BudgetDbContext db, RecurringService svc, CancellationToken ct) =>
        {
            var item = new RecurringItem();
            if (await ApplyAsync(item, body, db, ct) is { } error) return error;
            item.NextDueDate = svc.NextDue(item);
            db.RecurringItems.Add(item);
            await db.SaveChangesAsync(ct);
            await svc.PostDueAsync(ct); // due today → appears straight away
            await db.Entry(item).ReloadAsync(ct);
            await db.Entry(item).Reference(r => r.Category).LoadAsync(ct);
            return Results.Created($"/api/recurring/{item.Id}", ToDto(item));
        });

        // Editing never re-posts or changes entries already posted; it only shapes future ones.
        api.MapPut("/recurring/{id:int}", async (int id, RecurringUpsert body, BudgetDbContext db, RecurringService svc, CancellationToken ct) =>
        {
            var item = await db.RecurringItems.FindAsync([id], ct);
            if (item is null) return Results.NotFound();
            if (await ApplyAsync(item, body, db, ct) is { } error) return error;
            item.NextDueDate = svc.NextDue(item);
            await db.SaveChangesAsync(ct);
            await svc.PostDueAsync(ct);
            await db.Entry(item).ReloadAsync(ct);
            await db.Entry(item).Reference(r => r.Category).LoadAsync(ct);
            return Results.Ok(ToDto(item));
        });

        // Stops future entries. Ones already posted stay in Spending (delete them there if wanted).
        api.MapDelete("/recurring/{id:int}", async (int id, BudgetDbContext db, CancellationToken ct) =>
            await db.RecurringItems.Where(r => r.Id == id).ExecuteDeleteAsync(ct) == 0 ? Results.NotFound() : Results.NoContent());
    }

    private static async Task<IResult?> ApplyAsync(RecurringItem item, RecurringUpsert b, BudgetDbContext db, CancellationToken ct)
    {
        var name = b.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return Results.BadRequest(new { message = "Name is required." });
        if (b.Amount <= 0) return Results.BadRequest(new { message = "Amount must be positive." });
        var interval = b.Interval ?? 1;
        if (interval is < 1 or > 52) return Results.BadRequest(new { message = "Repeat every 1–52." });
        if (!DateOnly.TryParse(b.StartDate, out var start))
            return Results.BadRequest(new { message = "startDate must be yyyy-MM-dd." });
        DateOnly? end = null;
        if (!string.IsNullOrWhiteSpace(b.EndDate))
        {
            if (!DateOnly.TryParse(b.EndDate, out var e)) return Results.BadRequest(new { message = "endDate must be yyyy-MM-dd." });
            if (e < start) return Results.BadRequest(new { message = "The end date is before the first date." });
            end = e;
        }
        var cat = await db.Categories.FindAsync([b.CategoryId], ct);
        if (cat is null) return Results.BadRequest(new { message = $"Category {b.CategoryId} doesn't exist." });
        if (cat.Bucket is Bucket.Income)
            return Results.BadRequest(new { message = "Recurring items are for money going out. Record income in Settings." });

        item.Name = name.Length > 200 ? name[..200] : name;
        item.Amount = Math.Round(b.Amount, 2);
        item.CategoryId = cat.Id;
        item.Frequency = b.Frequency;
        item.Interval = interval;
        item.StartDate = start;
        item.EndDate = end;
        item.Notes = string.IsNullOrWhiteSpace(b.Notes) ? null : b.Notes.Trim();
        if (b.IsActive is bool active) item.IsActive = active;
        return null;
    }
}
