using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Budget.Api.Data;
using Budget.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Services;

/// <summary>One earlier turn of the conversation, as the Ask screen shows it. Role: "user" | "assistant".</summary>
public record AskTurn(string Role, string Text);

public record AskRequest(string Question, List<AskTurn>? History);

/// <param name="Looked">What the assistant checked to answer ("This pay period's budget"), shown under the answer.</param>
/// <param name="Goals">Goals it suggests. Nothing is saved until the person taps Save on one.</param>
public record AskAnswer(string Answer, IReadOnlyList<string> Looked, IReadOnlyList<GoalDraft>? Goals = null);

/// <summary>Thrown for a problem the person should see as it is (no key set, the AI service is down).</summary>
public class AskException(string message, int status = 503) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Answers questions about the money in this app ("can I afford to send my parents $400 a month?") by
/// letting Claude look things up through read-only tools: budgets, spending, accounts, subscriptions,
/// investments. Nothing here can change data.
/// </summary>
public class MoneyAssistant(BudgetDbContext db, BudgetService budgets, AccountService accounts,
    Subscriptions subscriptions, GoalService goals, Clock clock, IConfiguration config, ILogger<MoneyAssistant> log)
{
    public const string Model = "claude-opus-5-5";
    /// <summary>Tool rounds per question. A question needs two or three; this stops a runaway loop.</summary>
    private const int MaxRounds = 8;
    private const int MaxHistoryTurns = 12;
    public const int MaxQuestionLength = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private const string SystemPrompt = """
        You are the money advisor inside a personal budget app used by one person in Singapore. They ask
        about their own money: whether they can afford something, where their money goes, how a pay period
        is going, what they owe, how their investments are doing.

        How the app works:
        - Money is in SGD unless a row says otherwise.
        - Budgets run payday to payday, not calendar months. Each pay period splits take-home pay into
          Needs, Wants and Savings buckets (by default 50/30/20). Spending in Transfer (moving money between
          their own accounts, paying a card bill) and Income categories never counts as spending.
        - Card balances are negative when money is owed. "Net" on accounts is bank and cash less card debt.
        - Subscriptions are detected from repeating charges; "cancelled" ones are not counted in totals.
        - Investments include CPF, which they cannot spend; don't count it as available money.

        How to answer:
        - Always look at their real data with the tools before answering. Never invent figures. If the data
          needed isn't there (no salary recorded, few months of history), say what's missing and what you
          would need.
        - For "can I afford X every month" questions, compare their typical take-home pay against typical
          spending over several recent pay periods (not just the current, unfinished one), plus regular
          bills and subscriptions, and say how much room is left before and after X. Note which bucket X
          would fall in (money given to family regularly is usually a Need).
        - Lead with the answer (yes / no / yes if ...) and the one or two numbers behind it, then a short
          explanation. Keep it brief: it is read on a phone. Write plain text: short paragraphs, or lines
          starting with "- " for a list. No markdown (no **bold**, # headings or tables).
        - Say plainly when something is an estimate, and when the current pay period is not over yet.
        - Goals: they may have saved goals (get_goals): a spending limit per pay period on a category or
          bucket, or an amount to save per pay period. Mention how they're doing on any goal that matters
          to the question.
        - When they ask for goals, ways to cut spending, or how to afford something, suggest 1 to 3 goals
          with suggest_goal, each built from their real numbers: start from what they actually spent in
          that category over the last few finished pay periods, and set a cut they could keep (often 10 to
          25 percent, never below their usual Needs). Prefer the categories where a cut frees the most
          money. Say in the answer what each goal frees up per pay period and what that adds up to. Don't
          suggest a goal they already have; suggest a new amount for it instead. Suggested goals appear
          under your answer with a Save button; they're only saved if the person taps it.
        - Apart from suggesting goals, you can only read data. If they ask you to change something
          (record, move, delete), tell them where in the app to do it.
        - You give budgeting guidance, not regulated financial advice; for big decisions (loans,
          insurance, investing large sums) suggest they check with a licensed adviser.
        """;

    private static readonly List<BetaTool> Tools =
    [
        Tool("get_budget",
            "Budget for the pay period containing a date: take-home pay, budget/spent/remaining per bucket " +
            "(Needs, Wants, Savings), days left, total spent, left to spend, and the top spending categories.",
            new()
            {
                ["date"] = Prop("string", "Any day in the pay period, yyyy-MM-dd. Omit for the current period."),
            }),
        Tool("get_budget_history",
            "The same budget summary for each recorded pay period, newest first. Use it to see typical income " +
            "and spending over several months.",
            new()
            {
                ["periods"] = Prop("integer", "How many pay periods, 1-12. Default 6."),
            }),
        Tool("get_accounts",
            "Every bank account, card and wallet with its balance now; total in the bank, owed on cards, net, " +
            "and what friends still owe on shared bills.",
            new()),
        Tool("get_subscriptions",
            "Regular charges detected in spending (subscriptions and bills): each one's amount, how often, " +
            "monthly cost, next charge date and status, plus the monthly and yearly totals.",
            new()),
        Tool("get_investments",
            "Investments and CPF: total cost, value, gain or loss, mix by type, and each holding.",
            new()),
        Tool("find_transactions",
            "Search individual spending and income entries. Returns matching entries (newest first) and their " +
            "total. Combine filters as needed.",
            new()
            {
                ["from"] = Prop("string", "First day, yyyy-MM-dd."),
                ["to"] = Prop("string", "Last day (inclusive), yyyy-MM-dd."),
                ["text"] = Prop("string", "Words in the shop name or note, e.g. 'grab' or 'netflix'."),
                ["category"] = Prop("string", "Category name, e.g. 'Dining' or 'Groceries'."),
                ["bucket"] = Prop("string", "Needs, Wants, Savings, Income or Transfer."),
                ["kind"] = Prop("string", "'spend' for money out, 'income' for money in. Omit for both."),
                ["limit"] = Prop("integer", "Most entries to list, 1-200. Default 50. The total covers every match."),
            }),
        Tool("get_goals",
            "The goals they've saved, each with this pay period's progress (spent or saved so far, status, " +
            "what's left) and whether it was kept in the last three finished pay periods.",
            new()),
        Tool("suggest_goal",
            "Suggest one goal, shown to the person under your answer with a Save button (it is not saved " +
            "unless they tap it). Call once per goal. A 'limit' needs a category name, or a bucket for the " +
            "whole of Needs or Wants. A 'save' goal counts what goes into Savings categories.",
            new()
            {
                ["kind"] = Prop("string", "'limit' (spend at most amount per pay period) or 'save' (save at least amount)."),
                ["category"] = Prop("string", "For a limit: the category name exactly as get_categories lists it."),
                ["bucket"] = Prop("string", "For a limit on a whole bucket instead of a category: Needs or Wants."),
                ["amount"] = Prop("number", "SGD per pay period."),
                ["name"] = Prop("string", "Short label, e.g. 'Dining under S$300'."),
                ["reason"] = Prop("string", "One line on why, in their words, e.g. 'Frees S$80 a month for Mum and Dad'."),
            },
            ["kind", "amount", "name"]),
        Tool("get_categories",
            "Every spending category and which bucket it counts in, plus the pay day and budget split settings.",
            new()),
    ];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Tests swap in a fake Anthropic server here.</summary>
    public HttpClient? Http { get; set; }

    private string? ApiKey => config["Anthropic:ApiKey"] is { Length: > 0 } k ? k : config["ANTHROPIC_API_KEY"];

    public async Task<AskAnswer> AskAsync(AskRequest request, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new AskException("The AI helper isn't set up yet. Add an Anthropic API key to the server " +
                                   "(setting Anthropic__ApiKey) and try again.");

        var client = Http is null
            ? new AnthropicClient { ApiKey = ApiKey!, Timeout = TimeSpan.FromSeconds(90) }
            : new AnthropicClient { ApiKey = ApiKey!, HttpClient = Http, MaxRetries = 0 };
        var messages = new List<BetaMessageParam>();
        foreach (var turn in (request.History ?? []).TakeLast(MaxHistoryTurns))
        {
            if (string.IsNullOrWhiteSpace(turn.Text)) continue;
            messages.Add(turn.Role == "assistant"
                ? new BetaAssistantMessageParam { Content = turn.Text }
                : new BetaUserMessageParam { Content = turn.Text });
        }
        // Today's date goes with the question, not in the system prompt, so the cached prompt stays the same.
        messages.Add(new BetaUserMessageParam
        {
            Content = $"(Today is {clock.Today:yyyy-MM-dd}, {clock.Today.DayOfWeek}.)\n\n{request.Question.Trim()}"
        });

        var looked = new List<string>();
        var suggested = new List<GoalDraft>();
        for (var round = 0; round < MaxRounds; round++)
        {
            BetaMessage response;
            try
            {
                response = await client.Beta.Messages.Create(new MessageCreateParams
                {
                    Model = Model,
                    MaxTokens = 16000,
                    // Opus 5.5 always thinks; effort sets how hard. Medium is plenty for reading a few tables.
                    OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
                    // If a safety check declines the request, let the API retry it on a fallback model.
                    Betas = ["server-side-fallback-2026-07-01"],
                    Fallbacks = new Default(),
                    System = new List<BetaTextBlockParam>
                    {
                        new() { Text = SystemPrompt, CacheControl = new BetaCacheControlEphemeral() }
                    },
                    Tools = [.. Tools.Select(t => (BetaToolUnion)t)],
                    Messages = messages,
                }, ct);
            }
            catch (AnthropicApiException e)
            {
                log.LogWarning(e, "Claude request failed");
                throw new AskException("The AI helper couldn't answer just now. Try again in a minute.", 502);
            }

            if (response.StopReason == BetaStopReason.Refusal)
                return new AskAnswer("Sorry, I can't help with that one. Try asking about your budget, spending, " +
                                     "accounts or investments.", looked, suggested);

            var assistant = new List<BetaContentBlockParam>();
            var results = new List<BetaContentBlockParam>();
            var text = new List<string>();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var t))
                {
                    assistant.Add(new BetaTextBlockParam { Text = t.Text });
                    text.Add(t.Text);
                }
                else if (block.TryPickThinking(out var th))
                {
                    assistant.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                }
                else if (block.TryPickRedactedThinking(out var rt))
                {
                    assistant.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
                }
                else if (block.TryPickToolUse(out var use))
                {
                    assistant.Add(new BetaToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                    var (output, label, failed) = await RunToolAsync(use.Name, use.Input, suggested, ct);
                    if (!failed && label.Length > 0 && !looked.Contains(label)) looked.Add(label);
                    results.Add(new BetaToolResultBlockParam { ToolUseID = use.ID, Content = output, IsError = failed });
                }
            }

            if (results.Count == 0)
            {
                var answer = string.Join("\n\n", text).Trim();
                if (response.StopReason == BetaStopReason.MaxTokens && answer.Length == 0)
                    answer = "That needed a longer answer than I can give here. Try asking something narrower.";
                return new AskAnswer(answer.Length > 0 ? answer : "I couldn't work that out. Try rephrasing the question.", looked, suggested);
            }

            messages.Add(new BetaAssistantMessageParam { Content = assistant });
            messages.Add(new BetaUserMessageParam { Content = results });
        }

        return new AskAnswer("That took more looking up than I'm allowed for one question. " +
                             "Try asking something more specific.", looked, suggested);
    }

    /// <summary>
    /// Runs one lookup. Returns JSON for Claude, a label for the person, and whether it failed.
    /// The only tool that isn't a plain read, suggest_goal, adds to <paramref name="suggested"/> and saves nothing.
    /// </summary>
    public async Task<(string Output, string Label, bool Failed)> RunToolAsync(
        string name, IReadOnlyDictionary<string, JsonElement> input, List<GoalDraft>? suggested = null,
        CancellationToken ct = default)
    {
        try
        {
            switch (name)
            {
                case "get_budget":
                {
                    var date = Date(input, "date") ?? clock.Today;
                    return (Out(await budgets.SummaryAsync(date, ct)), "Pay period budget", false);
                }
                case "get_budget_history":
                {
                    var n = Math.Clamp(Int(input, "periods") ?? 6, 1, 12);
                    return (Out(await budgets.HistoryAsync(n, ct)), "Earlier pay periods", false);
                }
                case "get_accounts":
                    return (Out(await accounts.SummaryAsync(false, ct)), "Account balances", false);
                case "get_subscriptions":
                    return (Out(await subscriptions.ListAsync(ct)), "Subscriptions and bills", false);
                case "get_investments":
                {
                    var holdings = await db.Holdings.AsNoTracking().ToListAsync(ct);
                    var baseCcy = (await budgets.GetSettingsAsync(ct)).BaseCurrency;
                    var (figures, surrender) = await PolicyEndpoints.FiguresAsync(db, clock.Today, ct);
                    var summary = Portfolio.Summarise(holdings,
                        await BudgetEndpoints.ContributionsAsync(db, holdings, baseCcy, ct), clock.UtcNow, figures, surrender);
                    return (Out(summary), "Investments", false);
                }
                case "find_transactions":
                    return (Out(await FindAsync(input, ct)), "Spending entries", false);
                case "get_goals":
                    return (Out(await goals.ListAsync(ct)), "Your goals", false);
                case "suggest_goal":
                    return (await SuggestAsync(input, suggested), "", false); // not a lookup, so not listed as checked
                case "get_categories":
                {
                    var s = await budgets.GetSettingsAsync(ct);
                    var cats = await db.Categories.AsNoTracking().Where(c => !c.IsArchived)
                        .Select(c => new { c.Name, c.Bucket }).ToListAsync(ct);
                    return (Out(new { s.PayDay, s.NeedsPct, s.WantsPct, s.SavingsPct, s.BaseCurrency, categories = cats }),
                        "Categories and settings", false);
                }
                default:
                    return ($"No tool called {name}.", name, true);
            }
        }
        catch (ToolInputException e)
        {
            return (e.Message, name, true);
        }
    }

    private async Task<string> SuggestAsync(IReadOnlyDictionary<string, JsonElement> input, List<GoalDraft>? suggested)
    {
        if (suggested is { Count: >= 3 }) throw new ToolInputException("Three suggestions is the most per answer.");
        var kind = Str(input, "kind")?.ToLowerInvariant() switch
        {
            "limit" => "Limit",
            "save" => "Save",
            _ => throw new ToolInputException("kind must be 'limit' or 'save'."),
        };
        var amount = input.TryGetValue("amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDecimal()
            : throw new ToolInputException("amount must be a number.");

        int? categoryId = null;
        string? categoryName = null;
        if (kind == "Limit" && Str(input, "category") is { } catText)
        {
            var upper = catText.ToUpper();
            var cat = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Name.ToUpper() == upper)
                      ?? throw new ToolInputException($"There's no category called '{catText}'. Check get_categories.");
            (categoryId, categoryName) = (cat.Id, cat.Name);
        }
        var draft = new GoalDraft(kind, Str(input, "name"), categoryId, categoryId is null ? Str(input, "bucket") : null,
            Math.Round(amount, 2), Str(input, "reason"), categoryName);
        try
        {
            await goals.ValidateAsync(draft);
        }
        catch (GoalException e)
        {
            throw new ToolInputException(e.Message);
        }
        suggested?.Add(draft);
        return "Shown to them with a Save button. Not saved unless they tap it.";
    }

    private async Task<object> FindAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var query = db.Transactions.AsNoTracking().Include(t => t.Category).Include(t => t.Account).AsQueryable();
        if (Date(input, "from") is { } from)
        {
            var fromUtc = clock.LocalDateStartToUtc(from);
            query = query.Where(t => t.OccurredAtUtc >= fromUtc);
        }
        if (Date(input, "to") is { } to)
        {
            var toUtc = clock.LocalDateStartToUtc(to.AddDays(1));
            query = query.Where(t => t.OccurredAtUtc < toUtc);
        }
        if (Str(input, "text") is { } text)
        {
            var needle = text.ToUpper();
            query = query.Where(t => t.Merchant.ToUpper().Contains(needle) || (t.Notes != null && t.Notes.ToUpper().Contains(needle)));
        }
        if (Str(input, "category") is { } cat)
        {
            var c = cat.ToUpper();
            query = query.Where(t => t.Category != null && t.Category.Name.ToUpper() == c);
        }
        if (Str(input, "bucket") is { } bucketText)
        {
            if (!Enum.TryParse<Bucket>(bucketText, true, out var bucket))
                throw new ToolInputException("bucket must be Needs, Wants, Savings, Income or Transfer.");
            query = query.Where(t => t.Category != null && t.Category.Bucket == bucket);
        }
        switch (Str(input, "kind")?.ToLowerInvariant())
        {
            case null: break;
            case "spend": query = query.Where(t => !t.IsIncome); break;
            case "income": query = query.Where(t => t.IsIncome); break;
            default: throw new ToolInputException("kind must be 'spend' or 'income'.");
        }

        // Sums happen after loading: SQLite can't add up decimals in SQL.
        var all = await query.OrderByDescending(t => t.OccurredAtUtc).ThenByDescending(t => t.Id).ToListAsync(ct);
        var limit = Math.Clamp(Int(input, "limit") ?? 50, 1, 200);
        return new
        {
            matches = all.Count,
            totalSpent = all.Where(t => !t.IsIncome).Sum(t => t.CountedAmount),
            totalIncome = all.Where(t => t.IsIncome).Sum(t => t.Amount),
            shown = Math.Min(limit, all.Count),
            entries = all.Take(limit).Select(t => new
            {
                date = clock.ToLocalDate(t.OccurredAtUtc),
                t.Merchant,
                amount = t.Amount,
                counted = t.IsIncome ? (decimal?)null : t.CountedAmount,
                t.Currency,
                type = t.IsIncome ? "income" : "spend",
                category = t.Category?.Name,
                bucket = t.Category?.Bucket.ToString(),
                account = t.Account?.Name,
                t.Notes,
            }),
        };
    }

    // ---- Small helpers ----

    private class ToolInputException(string message) : Exception(message);

    private static string Out(object value) => JsonSerializer.Serialize(value, Json);

    private static string? Str(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim() : null;

    private static int? Int(IReadOnlyDictionary<string, JsonElement> input, string key)
    {
        if (!input.TryGetValue(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n)) return n;
        return null;
    }

    private static DateOnly? Date(IReadOnlyDictionary<string, JsonElement> input, string key)
    {
        if (Str(input, key) is not { } s) return null;
        return DateOnly.TryParseExact(s, "yyyy-MM-dd", out var d)
            ? d : throw new ToolInputException($"{key} must be a date like 2026-01-31.");
    }

    private static BetaTool Tool(string name, string description, Dictionary<string, JsonElement> properties,
        List<string>? required = null) => new()
    {
        Name = name,
        Description = description,
        InputSchema = new() { Properties = properties, Required = required },
    };

    private static JsonElement Prop(string type, string description) =>
        JsonSerializer.SerializeToElement(new { type, description });
}
