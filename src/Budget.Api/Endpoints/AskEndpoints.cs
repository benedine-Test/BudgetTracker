using Budget.Api.Services;

namespace Budget.Api.Endpoints;

public static class AskEndpoints
{
    public static void MapAskEndpoints(this RouteGroupBuilder api)
    {
        // Whether the AI helper has a key, so the Ask screen can explain instead of failing.
        api.MapGet("/ask/status", (MoneyAssistant ai) => Results.Ok(new { configured = ai.IsConfigured }));

        // A question about the money in the app, answered from the data. Read-only.
        api.MapPost("/ask", async (AskRequest body, MoneyAssistant ai, CancellationToken ct) =>
        {
            var q = body.Question?.Trim() ?? "";
            if (q.Length == 0) return Results.BadRequest(new { message = "Type a question first." });
            if (q.Length > MoneyAssistant.MaxQuestionLength)
                return Results.BadRequest(new { message = $"Keep the question under {MoneyAssistant.MaxQuestionLength} characters." });
            try
            {
                return Results.Ok(await ai.AskAsync(body with { Question = q }, ct));
            }
            catch (AskException e)
            {
                return Results.Json(new { message = e.Message }, statusCode: e.Status);
            }
        });
    }
}
