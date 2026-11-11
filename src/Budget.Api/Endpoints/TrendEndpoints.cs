using System.Globalization;
using Budget.Api.Services;

namespace Budget.Api.Endpoints;

public static class TrendEndpoints
{
    public static void MapTrendEndpoints(this RouteGroupBuilder api)
    {
        // Trends screen: six months by bucket, a spending calendar and top shops for one month,
        // and this pay period's categories against your usual. month = "2026-11"; blank = this month.
        api.MapGet("/trends", async (string? month, TrendService svc, CancellationToken ct) =>
        {
            DateOnly? m = null;
            if (!string.IsNullOrWhiteSpace(month))
            {
                if (!DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    return Results.BadRequest(new { message = "Month must look like 2026-11." });
                m = d;
            }
            return Results.Ok(await svc.GetAsync(m, ct));
        });
    }
}
