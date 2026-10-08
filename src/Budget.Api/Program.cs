using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Budget.Api.Data;
using Budget.Api.Endpoints;
using Budget.Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---- Database: SQLite locally, SQL Server when deployed ----
var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var conn = builder.Configuration.GetConnectionString("Budget") ?? "Data Source=budget.db";
builder.Services.AddDbContext<BudgetDbContext>(o =>
{
    if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)) o.UseSqlServer(conn);
    else o.UseSqlite(conn);
});

builder.Services.AddSingleton<Clock>();
builder.Services.AddScoped<Categorizer>();
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<TransactionService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<PriceRefresher>();
builder.Services.AddHttpClient<IQuoteSource, YahooQuoteSource>(c =>
{
    c.BaseAddress = new Uri("https://query1.finance.yahoo.com/");
    c.Timeout = TimeSpan.FromSeconds(10);
    // Yahoo turns away requests without a browser-like user agent.
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; BudgetTracker/1.0)");
});

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ---- API key: required. The server refuses to start without one. ----
var apiKey = builder.Configuration["Auth:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length < 24)
    throw new InvalidOperationException(
        "Set Auth:ApiKey (env var Auth__ApiKey) to a random string of at least 24 characters. " +
        "Generate one with: openssl rand -base64 32");
var apiKeyBytes = Encoding.UTF8.GetBytes(apiKey);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BudgetDbContext>();
    // EnsureCreated is fine for v1. Switch to EF migrations before the schema needs to change on a live DB.
    await db.Database.EnsureCreatedAsync();
    await SchemaUpgrade.ApplyAsync(db);
    await Seed.EnsureSeededAsync(db);
}

// The app screen (Budget.Web) is served from this same site, so one publish deploys both.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var api = app.MapGroup("/api").AddEndpointFilter(async (ctx, next) =>
{
    var header = ctx.HttpContext.Request.Headers["X-Api-Key"].ToString();
    var ok = header.Length > 0 &&
             CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), apiKeyBytes);
    return ok ? await next(ctx) : Results.Unauthorized();
});

api.MapTransactionEndpoints();
api.MapBudgetEndpoints();
api.MapAccountEndpoints();
api.MapPolicyEndpoints();

// Unknown /api paths are a 404, not the app page.
app.Map("/api/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program; // for integration tests
