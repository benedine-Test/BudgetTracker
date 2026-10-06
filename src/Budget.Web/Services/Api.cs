using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Budget.Web.Services;

// ---- Shapes returned by the server (kept in step with Budget.Api) ----

public record BucketStatus(string Bucket, decimal Budget, decimal Spent, decimal Remaining,
    decimal PercentUsed, string Status, decimal DailyAllowance);

public record CategoryTotal(int? CategoryId, string Category, string Bucket, decimal Total, int Count);

public record BudgetSummary(DateOnly PeriodStart, DateOnly PeriodEndExclusive, int DaysLeft, bool SalaryRecorded,
    decimal TakeHomeIncome, List<BucketStatus> Buckets, decimal UncategorisedSpend, int UncategorisedCount,
    decimal TotalSpent, decimal LeftToSpend, List<CategoryTotal> TopCategories);

public record TransactionDto(int Id, DateTimeOffset OccurredAt, decimal Amount, string Currency, bool IsIncome,
    string Merchant, string? Card, string? Notes, string Source, string? MergedSources,
    int? CategoryId, string? Category, string? Bucket, bool CategoryConfirmed, int? AccountId, string? Account);

public record AccountDto(int Id, string Name, string Kind, string Currency, decimal Balance,
    decimal AnchorBalance, DateTimeOffset BalanceAsOf, string? CardNames, bool IsArchived, DateTimeOffset? LastImportAt);

public record AccountsSummary(decimal InAccounts, decimal OwedOnCards, decimal Net, string Currency,
    int UnassignedSinceOldest, List<AccountDto> Accounts);

public record ColumnMap(string? Date, List<string>? Description, string? Debit, string? Credit,
    string? Amount, string? Direction, string? Balance);

public record ImportPreviewRow(int Index, DateOnly Date, string Description, decimal Amount, bool IsCredit, decimal? Balance,
    string Status, int? MatchId, string? MatchText, bool SuggestSalary, string? Category);

public record ImportPreview(List<string> Headers, ColumnMap Columns, bool PositiveIsSpend, List<ImportPreviewRow> Rows,
    int SkippedLines, decimal? StatementBalance, DateOnly? StatementBalanceDate, decimal? AppBalanceThatDayAfterImport,
    string? Problem, string? Warning, bool NeedsPassword);

public record ImportRowIn(DateOnly Date, string Description, decimal Amount, bool IsCredit, bool IsSalary);

public record ImportResult(int Added, int Linked, int SalariesRecorded, decimal Balance, string Message, int BatchId);

public record ImportBatchView(int Id, DateTimeOffset CreatedAt, string? FileName, int Added, int Linked,
    bool ResetBalance, DateTimeOffset? UndoneAt, bool CanUndo);

public record CategoryDto(int Id, string Name, string Bucket, bool IsArchived);

public record SettingsDto(int PayDay, decimal NeedsPct, decimal WantsPct, decimal SavingsPct);

public record HoldingValue(int Id, string Symbol, string Name, string AssetClass, string? Platform,
    decimal Units, decimal AverageCost, string Currency, decimal FxToBase, decimal? LastPrice, DateTime? LastPriceAtUtc,
    decimal CostBase, decimal MarketValueBase, decimal UnrealisedPnlBase, decimal? UnrealisedPnlPct, bool PriceIsStale);

public record AllocationSlice(string AssetClass, decimal ValueBase, decimal Percent);

public record PortfolioSummary(decimal TotalCostBase, decimal TotalValueBase, decimal UnrealisedPnlBase,
    decimal? UnrealisedPnlPct, List<AllocationSlice> Allocation, List<HoldingValue> Holdings);

public record CategoriseResult(TransactionDto Transaction, string? LearnedPattern, int ReFiled);

public record HoldingSave(string Symbol, string Name, string AssetClass, string? Platform,
    decimal Units, decimal AverageCost, string Currency, decimal? LastPrice, decimal FxToBase);

/// <summary>A problem worth showing to the person using the app, in plain words.</summary>
public class ApiException(string message) : Exception(message);

public class Api(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private record ErrorBody(string? Message);

    // ---- Budget ----
    public Task<BudgetSummary> Summary(DateOnly? date = null) =>
        Get<BudgetSummary>("api/budget/summary" + (date is { } d ? "?date=" + Fmt.Iso(d) : ""));

    public Task<List<BudgetSummary>> History() => Get<List<BudgetSummary>>("api/budget/history?take=12");

    public Task<SettingsDto> Settings() => Get<SettingsDto>("api/settings");

    public Task SaveSettings(SettingsDto s) => Send(HttpMethod.Put, "api/settings", s);

    public Task RecordIncome(decimal amount, string kind, string? date, string? note, int? accountId) =>
        Send(HttpMethod.Post, "api/income", new { amount, kind, date, note, accountId });

    // ---- Transactions ----
    public Task<List<TransactionDto>> Transactions(string? filter, string? from, string? to, string? search, int? accountId = null)
    {
        var parts = new List<string> { "take=300" };
        if (accountId is int aid) parts.Add($"accountId={aid}");
        switch (filter)
        {
            case "sort": parts.Add("uncategorised=true"); break;
            case "needs" or "wants" or "savings" or "income": parts.Add($"bucket={filter}"); break;
        }
        if (!string.IsNullOrWhiteSpace(from)) parts.Add($"from={Uri.EscapeDataString(from)}");
        if (!string.IsNullOrWhiteSpace(to)) parts.Add($"to={Uri.EscapeDataString(to)}");
        if (!string.IsNullOrWhiteSpace(search)) parts.Add($"q={Uri.EscapeDataString(search.Trim())}");
        return Get<List<TransactionDto>>("api/transactions?" + string.Join('&', parts));
    }

    public Task AddSpend(decimal amount, string merchant, int? categoryId, string date, string? notes, int? accountId) =>
        Send(HttpMethod.Post, "api/transactions", new { amount, merchant, categoryId, date, notes, accountId });

    /// <summary>Fix an entry's amount, description or date (ISO with offset).</summary>
    public async Task<TransactionDto> UpdateTransaction(int id, decimal amount, string merchant, string date)
    {
        using var r = await Send(HttpMethod.Put, $"api/transactions/{id}", new { amount, merchant, date });
        return (await r.Content.ReadFromJsonAsync<TransactionDto>(Json))!;
    }

    /// <summary>accountId 0 takes the entry off its account.</summary>
    public async Task<TransactionDto> MoveToAccount(int id, int accountId)
    {
        using var r = await Send(HttpMethod.Put, $"api/transactions/{id}", new { accountId });
        return (await r.Content.ReadFromJsonAsync<TransactionDto>(Json))!;
    }

    public async Task<CategoriseResult> Categorise(int id, int categoryId, bool learnRule)
    {
        using var r = await Send(HttpMethod.Post, $"api/transactions/{id}/categorise",
            new { categoryId, learnRule, applyToSimilar = learnRule });
        return (await r.Content.ReadFromJsonAsync<CategoriseResult>(Json))!;
    }

    public Task DeleteTransaction(int id) => Send(HttpMethod.Delete, $"api/transactions/{id}");

    public async Task<byte[]> ExportCsv()
    {
        using var r = await Send(HttpMethod.Get, "api/export/transactions.csv");
        return await r.Content.ReadAsByteArrayAsync();
    }

    // ---- Accounts ----
    public Task<AccountsSummary> Accounts(bool archived = false) =>
        Get<AccountsSummary>("api/accounts" + (archived ? "?archived=true" : ""));

    public Task AddAccount(string name, string kind, decimal balance, string? cardNames) =>
        Send(HttpMethod.Post, "api/accounts", new { name, kind, balance, cardNames });

    public Task UpdateAccount(int id, string? name, string? kind, string? cardNames, bool? isArchived) =>
        Send(HttpMethod.Put, $"api/accounts/{id}", new { name, kind, cardNames, isArchived });

    /// <summary>"The bank shows this now" — the balance counts on from here.</summary>
    public Task SetBalance(int id, decimal balance) =>
        Send(HttpMethod.Put, $"api/accounts/{id}/balance", new { balance });

    public Task DeleteAccount(int id) => Send(HttpMethod.Delete, $"api/accounts/{id}");

    public Task<ImportPreview> PreviewImport(int id, string csv, bool? positiveIsSpend, ColumnMap? columns) =>
        Post<ImportPreview>($"api/accounts/{id}/import/preview", new { csv, positiveIsSpend, columns });

    public Task<ImportPreview> PreviewPdf(int id, string pdfBase64, string? password, bool? positiveIsSpend) =>
        Post<ImportPreview>($"api/accounts/{id}/import/preview", new { pdf = pdfBase64, password, positiveIsSpend });

    public Task<ImportResult> CommitImport(int id, List<ImportRowIn> add, List<int> link, decimal? statementBalance,
        DateOnly? statementBalanceDate, string? fileName) =>
        Post<ImportResult>($"api/accounts/{id}/import", new { add, link, statementBalance, statementBalanceDate, fileName });

    public Task<List<ImportBatchView>> Imports(int id) => Get<List<ImportBatchView>>($"api/accounts/{id}/imports");

    public async Task<ImportResult> UndoImport(int id, int batchId)
    {
        using var r = await Send(HttpMethod.Delete, $"api/accounts/{id}/imports/{batchId}");
        return (await r.Content.ReadFromJsonAsync<ImportResult>(Json))!;
    }

    // ---- Categories ----
    public Task<List<CategoryDto>> Categories() => Get<List<CategoryDto>>("api/categories");

    public Task AddCategory(string name, string bucket) =>
        Send(HttpMethod.Post, "api/categories", new { name, bucket });

    public Task MoveCategory(CategoryDto c, string bucket) =>
        Send(HttpMethod.Put, $"api/categories/{c.Id}", new { name = c.Name, bucket });

    // ---- Investments ----
    public Task<PortfolioSummary> Portfolio() => Get<PortfolioSummary>("api/holdings");

    public Task SaveHolding(int? id, HoldingSave h) =>
        id is null ? Send(HttpMethod.Post, "api/holdings", h) : Send(HttpMethod.Put, $"api/holdings/{id}", h);

    public Task DeleteHolding(int id) => Send(HttpMethod.Delete, $"api/holdings/{id}");

    /// <summary>Checks a key before it is saved on the phone.</summary>
    public async Task<bool> KeyWorks(string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "api/settings");
        req.Headers.TryAddWithoutValidation(ApiKeyHandler.Header, key);
        try
        {
            using var r = await http.SendAsync(req);
            if (r.StatusCode == HttpStatusCode.Unauthorized) return false;
            if (r.IsSuccessStatusCode) return true;
            throw new ApiException($"The server answered with an error ({(int)r.StatusCode}). Try again in a minute.");
        }
        catch (HttpRequestException)
        {
            throw new ApiException("Can't reach the server. Check your connection and try again.");
        }
    }

    // ---- Plumbing ----
    private async Task<T> Get<T>(string url)
    {
        using var r = await Send(HttpMethod.Get, url);
        return (await r.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private async Task<T> Post<T>(string url, object body)
    {
        using var r = await Send(HttpMethod.Post, url, body);
        return (await r.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null)
    {
        HttpResponseMessage r;
        try
        {
            var req = new HttpRequestMessage(method, url);
            if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
            r = await http.SendAsync(req);
        }
        catch (HttpRequestException)
        {
            throw new ApiException("Can't reach the server. Check your connection and try again.");
        }

        if (r.IsSuccessStatusCode) return r;

        using (r)
        {
            if (r.StatusCode == HttpStatusCode.Unauthorized)
                throw new ApiException("The saved key was rejected. Enter it again.");

            string? message = null;
            try { message = (await r.Content.ReadFromJsonAsync<ErrorBody>(Json))?.Message; }
            catch (JsonException) { /* not a JSON error body */ }
            throw new ApiException(message ?? $"That didn't work ({(int)r.StatusCode}). Try again.");
        }
    }
}
