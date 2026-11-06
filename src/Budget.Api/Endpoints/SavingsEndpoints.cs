using Budget.Api.Services;

namespace Budget.Api.Endpoints;

public record TargetsSave(decimal EmergencyTarget, decimal? ParentsTarget);

/// <summary>
/// AlreadySaved: money you'd saved before (moving it doesn't count in this pay period's 50/30/20).
/// Date: the day it moved (blank = now).
/// </summary>
public record TransferSave(int FromAccountId, int ToAccountId, decimal Amount, DateOnly? Date, bool AlreadySaved, string? Note);

public record TickSave(DateOnly? PeriodDate);

public static class SavingsEndpoints
{
    public static void MapSavingsEndpoints(this RouteGroupBuilder api)
    {
        // The Safety Fund: target, ready vs locked, every pocket with its interest. Other pockets are listed apart.
        api.MapGet("/savings", (SafetyFund svc, CancellationToken ct) => svc.ViewAsync(null, ct));

        api.MapPut("/savings/targets", (TargetsSave body, SafetyFund svc, CancellationToken ct) =>
            Run(async () => { await svc.SetTargetsAsync(body.EmergencyTarget, body.ParentsTarget, ct); return await svc.ViewAsync(null, ct); }));

        api.MapPost("/savings/pockets", (PocketSave body, SafetyFund svc, CancellationToken ct) =>
            Run(() => svc.AddPocketAsync(body, null, ct)));

        api.MapPut("/savings/pockets/{accountId:int}", async (int accountId, PocketSave body, SafetyFund svc, CancellationToken ct) =>
        {
            try
            {
                var p = await svc.UpdatePocketAsync(accountId, body, null, ct);
                return p is null ? Results.NotFound() : Results.Ok(p);
            }
            catch (SavingsException e) { return Results.BadRequest(new { message = e.Message }); }
        });

        // Money moved between your own accounts: two linked entries, never spending.
        api.MapPost("/savings/transfers", (TransferSave body, SavingsTransfers transfers, CancellationToken ct) =>
            Run(async () =>
            {
                var (moneyOut, moneyIn) = await transfers.RecordAsync(body.FromAccountId, body.ToAccountId, body.Amount,
                    transfers.WhenUtc(body.Date), body.AlreadySaved, body.Note, ct);
                return new { outId = moneyOut.Id, inId = moneyIn.Id };
            }));

        // ---- Planned transfers each pay period ----
        api.MapGet("/savings/planned", (SafetyFund svc, CancellationToken ct) => svc.PlannedAsync(ct));

        api.MapPost("/savings/planned", (PlannedSave body, SafetyFund svc, CancellationToken ct) =>
            Run(async () => { await svc.SavePlannedAsync(null, body, ct); return await svc.PlannedAsync(ct); }));

        api.MapPut("/savings/planned/{id:int}", (int id, PlannedSave body, SafetyFund svc, CancellationToken ct) =>
            Run(async () => { await svc.SavePlannedAsync(id, body, ct); return await svc.PlannedAsync(ct); }));

        api.MapDelete("/savings/planned/{id:int}", async (int id, SafetyFund svc, CancellationToken ct) =>
            await svc.DeletePlannedAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        api.MapGet("/savings/planned/period", (DateOnly? date, SafetyFund svc, Clock clock, CancellationToken ct) =>
            svc.ForPeriodAsync(date ?? clock.Today, ct));

        api.MapPost("/savings/planned/{id:int}/tick", (int id, TickSave body, SafetyFund svc, Clock clock, CancellationToken ct) =>
            Run(() => svc.TickAsync(id, body.PeriodDate ?? clock.Today, ct)));

        api.MapDelete("/savings/planned/{id:int}/tick", (int id, DateOnly? date, SafetyFund svc, Clock clock, CancellationToken ct) =>
            Run(() => svc.UntickAsync(id, date ?? clock.Today, ct)));

        // ---- Dashboard reminders ----
        api.MapGet("/savings/reminders", (SafetyFund svc, CancellationToken ct) => svc.RemindersAsync(null, ct));

        api.MapPost("/savings/reminders/{id:int}/done", async (int id, SafetyFund svc, CancellationToken ct) =>
            await svc.ReminderDoneAsync(id, null, ct) ? Results.Ok(await svc.RemindersAsync(null, ct)) : Results.NotFound());
    }

    private static async Task<IResult> Run<T>(Func<Task<T>> work)
    {
        try { return Results.Ok(await work()); }
        catch (SavingsException e) { return Results.BadRequest(new { message = e.Message }); }
    }
}
