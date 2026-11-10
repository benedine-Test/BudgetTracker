using Budget.Api.Services;
using Budget.Web.Services;

namespace Budget.Api.Endpoints;

public static class TaxEndpoints
{
    public static void MapTaxEndpoints(this RouteGroupBuilder api)
    {
        // A year's income tax estimate and what would bring it down. Year = the year the income was earned.
        api.MapGet("/tax", (int? year, TaxPlanner tax, Clock clock, CancellationToken ct) =>
            tax.ViewAsync(year ?? clock.Today.Year, ct));

        api.MapPut("/tax/{year:int}", async (int year, TaxPlanDto body, TaxPlanner tax, CancellationToken ct) =>
        {
            try { return Results.Ok(await tax.SaveAsync(year, body, ct)); }
            catch (TaxException e) { return Results.BadRequest(new { message = e.Message }); }
        });
    }
}
