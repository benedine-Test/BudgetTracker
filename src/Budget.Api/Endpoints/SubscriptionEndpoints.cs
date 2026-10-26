using Budget.Api.Services;

namespace Budget.Api.Endpoints;

/// <summary>Pattern and amount as the subscription list gave them.</summary>
public record RecurringDismiss(string Pattern, decimal Amount);

public static class SubscriptionEndpoints
{
    public static void MapSubscriptionEndpoints(this RouteGroupBuilder api)
    {
        // Subscriptions and regular bills found in the spending: monthly cost, next charge, price changes.
        api.MapGet("/subscriptions", async (Subscriptions svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(ct)));

        api.MapPost("/subscriptions/dismiss", async (RecurringDismiss body, Subscriptions svc, CancellationToken ct) =>
        {
            var pattern = body.Pattern?.Trim() ?? "";
            if (pattern.Length is 0 or > 100) return Results.BadRequest(new { message = "Pattern must be 1–100 characters." });
            if (body.Amount <= 0) return Results.BadRequest(new { message = "Amount must be more than zero." });
            await svc.DismissAsync(pattern, body.Amount, ct);
            return Results.Ok(await svc.ListAsync(ct));
        });

        api.MapGet("/subscriptions/dismissed", async (Subscriptions svc, CancellationToken ct) =>
            Results.Ok(await svc.DismissedAsync(ct)));

        api.MapDelete("/subscriptions/dismissed/{id:int}", async (int id, Subscriptions svc, CancellationToken ct) =>
            await svc.RestoreAsync(id, ct) ? Results.NoContent() : Results.NotFound());
    }
}
