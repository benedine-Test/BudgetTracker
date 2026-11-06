using Budget.Api.Services;

namespace Budget.Api.Endpoints;

public record GoalUpdate(decimal Amount, string? Name);

public static class GoalEndpoints
{
    public static void MapGoalEndpoints(this RouteGroupBuilder api)
    {
        // Every goal with how it's going this pay period and in the last few.
        api.MapGet("/goals", async (GoalService goals, CancellationToken ct) => Results.Ok(await goals.ListAsync(ct)));

        api.MapPost("/goals", async (GoalDraft body, GoalService goals, CancellationToken ct) =>
        {
            try
            {
                var g = await goals.CreateAsync(body, ct);
                return Results.Created($"/api/goals/{g.Id}", await goals.ListAsync(ct));
            }
            catch (GoalException e)
            {
                return Results.BadRequest(new { message = e.Message });
            }
        });

        api.MapPut("/goals/{id:int}", async (int id, GoalUpdate body, GoalService goals, CancellationToken ct) =>
        {
            try
            {
                return await goals.UpdateAsync(id, body.Amount, body.Name, ct) is null
                    ? Results.NotFound() : Results.Ok(await goals.ListAsync(ct));
            }
            catch (GoalException e)
            {
                return Results.BadRequest(new { message = e.Message });
            }
        });

        api.MapDelete("/goals/{id:int}", async (int id, GoalService goals, CancellationToken ct) =>
            await goals.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());
    }
}
