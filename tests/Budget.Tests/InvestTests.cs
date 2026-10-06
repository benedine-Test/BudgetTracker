using System.Net;
using Budget.Api.Data;
using Budget.Api.Endpoints;
using Budget.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Budget.Tests;

public class ContributionTests
{
    private static readonly DateTime Day1 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Transaction Spend(int id, string merchant, decimal amount, int day, int? accountId = 1, bool income = false) =>
        new() { Id = id, Merchant = merchant, Amount = amount, OccurredAtUtc = Day1.AddDays(day - 1), AccountId = accountId, IsIncome = income };

    [Fact]
    public void Monthly_premium_matches_on_text_amount_and_account()
    {
        var fwd = new Holding { Id = 1, ContributionMatch = "fwd", ContributionAmount = 300m, ContributionAccountId = 1 };
        var tx = new[]
        {
            Spend(1, "GIRO FWD SINGAPORE PTE LTD", 300m, 5),
            Spend(2, "GIRO  FWD SINGAPORE", 120m, 5),         // a different FWD policy
            Spend(3, "GIRO FWD SINGAPORE", 300m, 6, accountId: 2), // from another account
            Spend(4, "FWD REFUND", 300m, 7, income: true),
            Spend(5, "NTUC FAIRPRICE", 300m, 8),
        };

        var c = Portfolio.MatchContributions([fwd], tx, "SGD");

        Assert.Equal([1], c.Select(x => x.TransactionId));
    }

    [Fact]
    public void An_entry_goes_to_the_most_specific_match_only()
    {
        var general = new Holding { Id = 1, ContributionMatch = "ENDOWUS" };
        var specific = new Holding { Id = 2, ContributionMatch = "Endowus  CPF" };

        var c = Portfolio.MatchContributions([general, specific],
            [Spend(1, "ENDOWUS CPF TOPUP", 500m, 3), Spend(2, "ENDOWUS CASH SMART", 200m, 3)], "SGD");

        Assert.Equal(2, c.Single(x => x.TransactionId == 1).HoldingId);
        Assert.Equal(1, c.Single(x => x.TransactionId == 2).HoldingId);
    }

    [Fact]
    public void Policy_cost_grows_with_each_premium_and_a_new_value_takes_in_earlier_ones()
    {
        // Paid S$3,600 so far, worth S$3,300 per the insurer on day 1.
        var ilp = new Holding
        {
            Id = 1, Symbol = "FWD", AssetClass = AssetClass.Policy, Units = 1, AverageCost = 3600m,
            LastPrice = 3300m, LastPriceAtUtc = Day1, FiguresAsOfUtc = Day1, ContributionMatch = "FWD"
        };
        var paid = new List<Contribution> { new(1, 1, Day1.AddDays(-25), 300m), new(1, 2, Day1.AddDays(4), 300m) };

        var h = Portfolio.Summarise([ilp], paid, Day1.AddDays(5)).Holdings.Single();
        Assert.Equal(3900m, h.CostBase);           // the day-5 premium is new; the earlier one is in the S$3,600
        Assert.Equal(3600m, h.MarketValueBase);    // counted at cost until the insurer says otherwise
        Assert.Equal(300m, h.NotInFiguresBase);
        Assert.Equal(2, h.ContributionCount);
        Assert.Equal(600m, h.ContributedBase);

        // Day 10: new value from the FWD app. It already includes the day-5 premium.
        ilp.LastPrice = 3550m;
        ilp.LastPriceAtUtc = Day1.AddDays(9);
        h = Portfolio.Summarise([ilp], paid, Day1.AddDays(10)).Holdings.Single();
        Assert.Equal(3900m, h.CostBase);
        Assert.Equal(3550m, h.MarketValueBase);
        Assert.Equal(-350m, h.UnrealisedPnlBase);
    }

    [Fact]
    public void Regular_savings_into_an_etf_count_at_cost_until_units_are_updated()
    {
        var etf = new Holding
        {
            Id = 1, Symbol = "ES3.SI", AssetClass = AssetClass.Etf, Units = 100, AverageCost = 3.50m,
            LastPrice = 4m, LastPriceAtUtc = Day1.AddDays(20), FiguresAsOfUtc = Day1, AutoPrice = true
        };
        var paid = new List<Contribution> { new(1, 1, Day1.AddDays(5), 100m) };

        var h = Portfolio.Summarise([etf], paid, Day1.AddDays(20)).Holdings.Single();

        // A fresh automatic price doesn't mean the units include the new money.
        Assert.Equal(450m, h.CostBase);
        Assert.Equal(500m, h.MarketValueBase);
        Assert.Equal(100m, h.NotInFiguresBase);
    }

    [Theory]
    [InlineData(612.5, "GBp", "GBP", 6.125)]
    [InlineData(120.4, "USD", "USD", 120.4)]
    [InlineData(3.9, "SGD", "SGD", 3.9)]
    public void Quote_converts_pence_and_passes_matching_currency(decimal price, string quoted, string holding, decimal expected) =>
        Assert.Equal(expected, PriceRefresher.InHoldingCurrency(new Quote(price, quoted), holding));

    [Fact]
    public void Quote_in_another_currency_is_refused() =>
        Assert.Null(PriceRefresher.InHoldingCurrency(new Quote(100m, "EUR"), "USD"));
}

public sealed class PriceRefreshTests : IDisposable
{
    private sealed class FakeQuotes : IQuoteSource
    {
        public Dictionary<string, Quote> Prices { get; } = new();
        public List<string> Asked { get; } = [];

        public Task<Quote> GetAsync(string symbol, CancellationToken ct)
        {
            Asked.Add(symbol);
            return Prices.TryGetValue(symbol, out var q)
                ? Task.FromResult(q)
                : throw new QuoteException($"Yahoo Finance doesn't know \"{symbol}\".");
        }
    }

    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly BudgetDbContext _db;
    private readonly FakeQuotes _quotes = new();
    private readonly Clock _clock = new(new ConfigurationBuilder().Build());
    private readonly PriceRefresher _refresher;

    public PriceRefreshTests()
    {
        _conn.Open();
        _db = new BudgetDbContext(new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        Seed.EnsureSeededAsync(_db).GetAwaiter().GetResult();
        _refresher = new PriceRefresher(_db, _quotes, new BudgetService(_db, _clock), _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task Fetches_prices_and_exchange_rates_and_keeps_the_last_good_price_on_failure()
    {
        _db.Holdings.AddRange(
            new Holding { Symbol = "ES3.SI", AutoPrice = true, Units = 100, AverageCost = 3.5m, Currency = "SGD" },
            new Holding { Symbol = "VWRA.L", AutoPrice = true, Units = 10, AverageCost = 120m, Currency = "USD", FxToBase = 1.30m },
            new Holding { Symbol = "ES3", AutoPrice = true, Units = 1, AverageCost = 3m, LastPrice = 3.8m, Currency = "SGD" },
            new Holding { Symbol = "CPF", AutoPrice = false, Units = 1, AverageCost = 50000m, Currency = "SGD" });
        await _db.SaveChangesAsync();
        _quotes.Prices["ES3.SI"] = new Quote(3.95m, "SGD");
        _quotes.Prices["VWRA.L"] = new Quote(141.2m, "USD");
        _quotes.Prices["USDSGD=X"] = new Quote(1.2875m, "SGD");

        var r = await _refresher.RefreshAsync(force: false);

        Assert.Equal(2, r.Updated);
        Assert.Equal(1, r.Failed);
        var h = await _db.Holdings.AsNoTracking().ToDictionaryAsync(x => x.Symbol);
        Assert.Equal(3.95m, h["ES3.SI"].LastPrice);
        Assert.Equal(141.2m, h["VWRA.L"].LastPrice);
        Assert.Equal(1.2875m, h["VWRA.L"].FxToBase);
        Assert.Equal(3.8m, h["ES3"].LastPrice);                // old price kept
        Assert.Contains("doesn't know", h["ES3"].PriceError);
        Assert.Null(h["CPF"].LastPrice);                       // typed-in balances aren't fetched
        Assert.DoesNotContain("CPF", _quotes.Asked);
    }

    [Fact]
    public async Task Recent_prices_are_not_fetched_again_unless_forced()
    {
        _db.Holdings.Add(new Holding { Symbol = "ES3.SI", AutoPrice = true, Units = 1, AverageCost = 3m, Currency = "SGD" });
        await _db.SaveChangesAsync();
        _quotes.Prices["ES3.SI"] = new Quote(3.95m, "SGD");

        await _refresher.RefreshAsync(force: false);
        var second = await _refresher.RefreshAsync(force: false);
        Assert.Equal(1, second.Skipped);
        Assert.Single(_quotes.Asked);

        await _refresher.RefreshAsync(force: true);
        Assert.Equal(2, _quotes.Asked.Count);
    }

    [Fact]
    public async Task Wrong_currency_is_reported_not_stored()
    {
        _db.Holdings.Add(new Holding { Symbol = "SAP.DE", AutoPrice = true, Units = 1, AverageCost = 200m, Currency = "USD", FxToBase = 1.3m });
        await _db.SaveChangesAsync();
        _quotes.Prices["SAP.DE"] = new Quote(230m, "EUR");

        await _refresher.RefreshAsync(force: false);

        var h = await _db.Holdings.AsNoTracking().SingleAsync();
        Assert.Null(h.LastPrice);
        Assert.Contains("in EUR", h.PriceError);
    }

    [Fact]
    public async Task Contributions_are_found_in_the_database()
    {
        var dbs = new Account { Name = "DBS Savings", Kind = AccountKind.Bank, BalanceAsOfUtc = DateTime.UtcNow.AddDays(-90) };
        _db.Accounts.Add(dbs);
        await _db.SaveChangesAsync();
        _db.Transactions.AddRange(
            new Transaction { Merchant = "GIRO Fwd Singapore Pte", Amount = 300m, OccurredAtUtc = DateTime.UtcNow.AddDays(-60), AccountId = dbs.Id },
            new Transaction { Merchant = "GIRO FWD SINGAPORE PTE", Amount = 300m, OccurredAtUtc = DateTime.UtcNow.AddDays(-30), AccountId = dbs.Id },
            new Transaction { Merchant = "Starbucks", Amount = 7.8m, OccurredAtUtc = DateTime.UtcNow.AddDays(-30), AccountId = dbs.Id });
        _db.Holdings.Add(new Holding { Symbol = "FWD", AssetClass = AssetClass.Policy, Units = 1, ContributionMatch = "fwd singapore", ContributionAccountId = dbs.Id });
        await _db.SaveChangesAsync();

        var holdings = await _db.Holdings.AsNoTracking().ToListAsync();
        var c = await BudgetEndpoints.ContributionsAsync(_db, holdings, "SGD", default);

        Assert.Equal(2, c.Count);
        Assert.Equal(600m, c.Sum(x => x.Amount));
    }
}

public class YahooQuoteSourceTests
{
    private sealed class Canned(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? Requested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static (YahooQuoteSource, Canned) Source(HttpStatusCode status, string body)
    {
        var handler = new Canned(status, body);
        return (new YahooQuoteSource(new HttpClient(handler) { BaseAddress = new Uri("https://query1.finance.yahoo.com/") }), handler);
    }

    [Fact]
    public async Task Reads_price_and_currency_from_the_chart_response()
    {
        var (yahoo, handler) = Source(HttpStatusCode.OK, """
            {"chart":{"result":[{"meta":{"currency":"GBp","symbol":"VUSA.L","exchangeName":"LSE",
              "regularMarketPrice":9012.5,"previousClose":8990.0},"timestamp":[1791273600],
              "indicators":{"quote":[{"close":[9012.5]}]}}],"error":null}}
            """);

        var q = await yahoo.GetAsync("USDSGD=X", default);

        Assert.Equal(new Quote(9012.5m, "GBp"), q);
        Assert.Equal("/v8/finance/chart/USDSGD%3DX", handler.Requested!.AbsolutePath);
    }

    [Fact]
    public async Task Unknown_ticker_says_so()
    {
        var (yahoo, _) = Source(HttpStatusCode.NotFound,
            """{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found, symbol may be delisted"}}}""");

        var e = await Assert.ThrowsAsync<QuoteException>(() => yahoo.GetAsync("ES3", default));
        Assert.Contains("ES3.SI", e.Message);
    }

    [Fact]
    public async Task Empty_result_is_a_quote_problem_not_a_crash()
    {
        var (yahoo, _) = Source(HttpStatusCode.OK, """{"chart":{"result":null,"error":null}}""");
        await Assert.ThrowsAsync<QuoteException>(() => yahoo.GetAsync("XYZ", default));
    }
}

public class HoldingSchemaUpgradeTests
{
    [Fact]
    public async Task Adds_price_and_contribution_columns_to_existing_holdings()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE "Transactions" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Transactions" PRIMARY KEY AUTOINCREMENT,
                  "Amount" TEXT NOT NULL, "Merchant" TEXT NOT NULL);
                CREATE TABLE "Holdings" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Holdings" PRIMARY KEY AUTOINCREMENT,
                  "Symbol" TEXT NOT NULL, "AssetClass" INTEGER NOT NULL);
                INSERT INTO "Holdings" ("Symbol", "AssetClass") VALUES ('ES3.SI', 1), ('CPF', 5);
                """;
            cmd.ExecuteNonQuery();
        }

        using var db = new BudgetDbContext(new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(conn).Options);
        await SchemaUpgrade.ApplyAsync(db);
        await SchemaUpgrade.ApplyAsync(db); // second run is a no-op

        using var check = conn.CreateCommand();
        check.CommandText = """SELECT "AutoPrice" FROM "Holdings" ORDER BY "Id" """;
        using (var r = check.ExecuteReader())
        {
            Assert.True(r.Read()); Assert.Equal(1L, r.GetInt64(0));
            Assert.True(r.Read()); Assert.Equal(0L, r.GetInt64(0));
        }
        check.CommandText = """SELECT COUNT(*) FROM "Holdings" WHERE "FiguresAsOfUtc" IS NULL""";
        Assert.Equal(0L, check.ExecuteScalar());
        check.CommandText = """SELECT COUNT(*) FROM pragma_table_info('Holdings') WHERE name LIKE 'Contribution%'""";
        Assert.Equal(3L, check.ExecuteScalar());
    }
}
