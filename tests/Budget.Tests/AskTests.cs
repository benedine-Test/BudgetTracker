using System.Net;
using System.Text;
using System.Text.Json;
using Budget.Api.Data;
using Budget.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Budget.Tests;

public sealed class AskTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly BudgetDbContext _db;
    private readonly BudgetService _budgets;
    private readonly MoneyAssistant _ai;
    private readonly Clock _clock = new(new ConfigurationBuilder().Build());
    private readonly FakeClaude _claude = new();

    public AskTests()
    {
        _conn.Open();
        _db = new BudgetDbContext(new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        Seed.EnsureSeededAsync(_db).GetAwaiter().GetResult();
        var categorizer = new Categorizer(_db);
        _budgets = new BudgetService(_db, _clock);
        var tx = new TransactionService(_db, categorizer, _budgets, _clock);
        var accounts = new AccountService(_db, categorizer, _budgets, tx, _clock);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Anthropic:ApiKey"] = "test-key" }).Build();
        _ai = new MoneyAssistant(_db, _budgets, accounts, new Subscriptions(_db, _clock), _clock, config,
            NullLogger<MoneyAssistant>.Instance) { Http = new HttpClient(_claude) };
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private static Dictionary<string, JsonElement> Input(object o) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;

    private async Task SpendAsync(string merchant, decimal amount, int daysAgo, string category)
    {
        var cat = await _db.Categories.SingleAsync(c => c.Name == category);
        _db.Transactions.Add(new Transaction
        {
            OccurredAtUtc = _clock.LocalDateStartToUtc(_clock.Today.AddDays(-daysAgo)).AddHours(12),
            Amount = amount, Merchant = merchant, CategoryId = cat.Id, Source = TransactionSource.Manual
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Find_transactions_filters_by_text_and_totals_every_match()
    {
        await SpendAsync("GRAB *RIDE", 12.50m, 1, _db.Categories.First(c => c.Bucket == Bucket.Needs).Name);
        await SpendAsync("GRAB *RIDE", 7.50m, 2, _db.Categories.First(c => c.Bucket == Bucket.Needs).Name);
        await SpendAsync("NETFLIX", 19.98m, 3, _db.Categories.First(c => c.Bucket == Bucket.Wants).Name);

        var (output, label, failed) = await _ai.RunToolAsync("find_transactions", Input(new { text = "grab", limit = 1 }));

        Assert.False(failed);
        Assert.Equal("Spending entries", label);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(2, doc.RootElement.GetProperty("matches").GetInt32());
        Assert.Equal(20.00m, doc.RootElement.GetProperty("totalSpent").GetDecimal());
        Assert.Equal(1, doc.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Bad_tool_input_is_reported_back_as_an_error_not_thrown()
    {
        var (output, _, failed) = await _ai.RunToolAsync("find_transactions", Input(new { from = "last week" }));
        Assert.True(failed);
        Assert.Contains("from must be a date", output);

        (_, _, failed) = await _ai.RunToolAsync("delete_everything", Input(new { }));
        Assert.True(failed);
    }

    [Fact]
    public async Task Budget_tool_returns_the_pay_period()
    {
        await _budgets.RecordSalaryAsync(5000m, _clock.LocalDateStartToUtc(_clock.Today).AddHours(9), null);
        var (output, _, failed) = await _ai.RunToolAsync("get_budget", Input(new { }));
        Assert.False(failed);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(5000m, doc.RootElement.GetProperty("takeHomeIncome").GetDecimal());
    }

    [Fact]
    public async Task Ask_runs_the_tool_Claude_asks_for_then_returns_its_answer()
    {
        _claude.Replies.Enqueue("""
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5",
             "content":[{"type":"tool_use","id":"tu_1","name":"get_accounts","input":{}}],
             "stop_reason":"tool_use","stop_sequence":null,
             "usage":{"input_tokens":10,"output_tokens":5}}
            """);
        _claude.Replies.Enqueue("""
            {"id":"msg_2","type":"message","role":"assistant","model":"claude-opus-5-5",
             "content":[{"type":"text","text":"Yes, you can afford it."}],
             "stop_reason":"end_turn","stop_sequence":null,
             "usage":{"input_tokens":10,"output_tokens":5}}
            """);

        var answer = await _ai.AskAsync(new AskRequest("Can I send my parents $400 a month?",
            [new AskTurn("user", "hi"), new AskTurn("assistant", "Hello!")]));

        Assert.Equal("Yes, you can afford it.", answer.Answer);
        Assert.Equal(["Account balances"], answer.Looked);
        Assert.Equal(2, _claude.Requests.Count);

        using var first = JsonDocument.Parse(_claude.Requests[0]);
        var root = first.RootElement;
        Assert.Equal("claude-opus-5-5", root.GetProperty("model").GetString());
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());
        Assert.Equal("medium", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal(7, root.GetProperty("tools").GetArrayLength());
        var msgs = root.GetProperty("messages");
        Assert.Equal(3, msgs.GetArrayLength());
        Assert.Contains("$400", msgs[2].GetProperty("content").ToString());

        // The second request carries the tool call and its result.
        using var second = JsonDocument.Parse(_claude.Requests[1]);
        var last = second.RootElement.GetProperty("messages").EnumerateArray().Last();
        Assert.Equal("tool_result", last.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("tu_1", last.GetProperty("content")[0].GetProperty("tool_use_id").GetString());
    }

    [Fact]
    public async Task Ask_without_a_key_explains_how_to_set_it_up()
    {
        var categorizer = new Categorizer(_db);
        var tx = new TransactionService(_db, categorizer, _budgets, _clock);
        var noKey = new MoneyAssistant(_db, _budgets, new AccountService(_db, categorizer, _budgets, tx, _clock),
            new Subscriptions(_db, _clock), _clock, new ConfigurationBuilder().Build(), NullLogger<MoneyAssistant>.Instance);

        Assert.False(noKey.IsConfigured);
        var e = await Assert.ThrowsAsync<AskException>(() => noKey.AskAsync(new AskRequest("hi", null)));
        Assert.Contains("Anthropic__ApiKey", e.Message);
    }

    private sealed class FakeClaude : HttpMessageHandler
    {
        public Queue<string> Replies { get; } = new();
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Replies.Dequeue(), Encoding.UTF8, "application/json")
            };
        }
    }
}
