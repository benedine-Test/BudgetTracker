using Budget.Api.Data;
using Budget.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Budget.Tests;

public sealed class TransportSplitTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<BudgetDbContext> _options;

    public TransportSplitTests()
    {
        _conn.Open();
        _options = new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(_conn).Options;
        using var db = new BudgetDbContext(_options);
        db.Database.EnsureCreated();
        Seed.EnsureSeededAsync(db).GetAwaiter().GetResult();
    }

    public void Dispose() => _conn.Dispose();

    [Theory]
    [InlineData("BUS/MRT 123456789", Seed.PublicTransport)]
    [InlineData("SimplyGo", Seed.PublicTransport)]
    [InlineData("SBS Transit", Seed.PublicTransport)]
    [InlineData("GRAB* RIDE", Seed.PrivateTransport)]
    [InlineData("Gojek", Seed.PrivateTransport)]
    [InlineData("ComfortDelGro Taxi", Seed.PrivateTransport)]
    [InlineData("Shell Bukit Timah", Seed.PrivateTransport)]
    [InlineData("GrabFood", "Dining & Food Delivery")]
    public async Task Fresh_database_puts_bus_and_train_apart_from_taxis(string merchant, string expected)
    {
        using var db = new BudgetDbContext(_options);
        var id = await new Categorizer(db).MatchAsync(merchant);
        Assert.Equal(expected, (await db.Categories.SingleAsync(c => c.Id == id)).Name);
    }

    [Fact]
    public async Task Existing_transport_category_is_split_with_its_spending()
    {
        using (var cmd = _conn.CreateCommand())
        {
            // As a database from before the split: one "Transport" holding every rule and trip.
            cmd.CommandText = """
                DELETE FROM "MerchantRules" WHERE "Pattern" IN ('TRANSITLINK', 'COMFORTDELGRO');
                UPDATE "MerchantRules" SET "CategoryId" = (SELECT "Id" FROM "Categories" WHERE "Name" = 'Taxi & Car')
                    WHERE "CategoryId" = (SELECT "Id" FROM "Categories" WHERE "Name" = 'Bus & Train');
                DELETE FROM "Categories" WHERE "Name" = 'Bus & Train';
                UPDATE "Categories" SET "Name" = 'Transport' WHERE "Name" = 'Taxi & Car';
                """;
            cmd.ExecuteNonQuery();
        }

        using (var db = new BudgetDbContext(_options))
        {
            var transport = await db.Categories.SingleAsync(c => c.Name == "Transport");
            db.Transactions.AddRange(
                new Transaction { Amount = 3.2m, Merchant = "BUS/MRT 123456", CategoryId = transport.Id, OccurredAtUtc = DateTime.UtcNow },
                new Transaction { Amount = 18m, Merchant = "GRAB* RIDE", CategoryId = transport.Id, OccurredAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        using (var db = new BudgetDbContext(_options))
        {
            await Seed.EnsureSeededAsync(db);
            await Seed.EnsureSeededAsync(db); // second run is a no-op
        }

        using (var db = new BudgetDbContext(_options))
        {
            Assert.False(await db.Categories.AnyAsync(c => c.Name == "Transport"));
            Assert.Equal(1, await db.Categories.CountAsync(c => c.Name == Seed.PublicTransport));

            var txs = await db.Transactions.Include(t => t.Category).ToListAsync();
            Assert.Equal(Seed.PublicTransport, txs.Single(t => t.Merchant.StartsWith("BUS")).Category!.Name);
            Assert.Equal(Seed.PrivateTransport, txs.Single(t => t.Merchant.StartsWith("GRAB")).Category!.Name);

            var categorizer = new Categorizer(db);
            var bus = await db.Categories.SingleAsync(c => c.Name == Seed.PublicTransport);
            Assert.Equal(bus.Id, await categorizer.MatchAsync("SimplyGo"));
            Assert.Equal(bus.Id, await categorizer.MatchAsync("TransitLink"));
            Assert.NotEqual(bus.Id, await categorizer.MatchAsync("Grab"));
        }
    }
}
