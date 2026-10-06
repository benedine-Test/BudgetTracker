using Budget.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Services;

public record AccountView(
    int Id, string Name, string Kind, string Currency, decimal Balance,
    decimal AnchorBalance, DateTimeOffset BalanceAsOf, string? CardNames, bool IsArchived,
    DateTimeOffset? LastImportAt);

public record AccountsSummary(
    decimal InAccounts, decimal OwedOnCards, decimal Net, string Currency,
    int UnassignedSinceOldest, IReadOnlyList<AccountView> Accounts);

public record ImportPreviewRow(
    int Index, DateOnly Date, string Description, decimal Amount, bool IsCredit, decimal? Balance,
    string Status,               // "new" | "matched" | "check"
    int? MatchId, string? MatchText, bool SuggestSalary, string? Category);

public record ImportPreview(
    IReadOnlyList<string> Headers, ColumnMap Columns, bool PositiveIsSpend,
    IReadOnlyList<ImportPreviewRow> Rows, int SkippedLines,
    decimal? StatementBalance, DateOnly? StatementBalanceDate,
    decimal? AppBalanceThatDayAfterImport, string? Problem,
    string? Warning = null, bool NeedsPassword = false);

public record ImportRowIn(DateOnly Date, string Description, decimal Amount, bool IsCredit, bool IsSalary);

public record ImportResult(int Added, int Linked, int SalariesRecorded, decimal Balance, string Message, int BatchId = 0);

public record ImportBatchView(int Id, DateTimeOffset CreatedAt, string? FileName, int Added, int Linked,
    bool ResetBalance, DateTimeOffset? UndoneAt, bool CanUndo);

/// <summary>Thrown for an undo that can't be done safely; the message is for the person.</summary>
public class ImportUndoException(string message) : Exception(message);

public class AccountService(BudgetDbContext db, Categorizer categorizer, BudgetService budgets,
    TransactionService transactions, Clock clock)
{
    private static readonly TimeSpan Sgt = TimeSpan.FromHours(8);

    public async Task<AccountsSummary> SummaryAsync(bool includeArchived, CancellationToken ct = default)
    {
        var settings = await budgets.GetSettingsAsync(ct);
        var accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.Kind).ThenBy(a => a.Name).ToListAsync(ct);
        var ids = accounts.Select(a => a.Id).ToList();
        var linked = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId != null && ids.Contains(t.AccountId.Value))
            .Select(t => new { t.AccountId, t.OccurredAtUtc, t.Amount, t.IsIncome })
            .ToListAsync(ct);

        // Spending with no account since the oldest anchor: a balance can't include it until it's filed.
        var oldestAnchor = accounts.Where(a => !a.IsArchived).Select(a => (DateTime?)a.BalanceAsOfUtc).Min();
        // Amount is checked after loading: SQLite can't compare decimals in SQL. (Pending S$0 taps don't count.)
        var unassigned = oldestAnchor is null ? 0 : (await db.Transactions
            .Where(t => t.AccountId == null && t.OccurredAtUtc >= oldestAnchor)
            .Select(t => t.Amount).ToListAsync(ct)).Count(amt => amt != 0);

        var views = new List<AccountView>();
        foreach (var a in accounts.Where(a => includeArchived || !a.IsArchived))
        {
            var mine = linked.Where(t => t.AccountId == a.Id)
                .Select(t => (t.OccurredAtUtc, t.IsIncome ? t.Amount : -t.Amount));
            var balance = AccountMath.BalanceAt(a.AnchorBalance, a.BalanceAsOfUtc, mine, DateTime.MaxValue);
            views.Add(ToView(a, balance));
        }

        var active = views.Where(v => !v.IsArchived && v.Currency == settings.BaseCurrency).ToList();
        var inAccounts = active.Where(v => v.Kind != nameof(AccountKind.CreditCard)).Sum(v => v.Balance);
        var owed = -active.Where(v => v.Kind == nameof(AccountKind.CreditCard)).Sum(v => v.Balance);
        return new AccountsSummary(inAccounts, owed, inAccounts - owed, settings.BaseCurrency, unassigned, views);
    }

    public async Task<decimal> BalanceAsync(Account a, CancellationToken ct = default)
    {
        var txs = await db.Transactions.AsNoTracking().Where(t => t.AccountId == a.Id)
            .Select(t => new { t.OccurredAtUtc, t.Amount, t.IsIncome }).ToListAsync(ct);
        return AccountMath.BalanceAt(a.AnchorBalance, a.BalanceAsOfUtc,
            txs.Select(t => (t.OccurredAtUtc, t.IsIncome ? t.Amount : -t.Amount)), DateTime.MaxValue);
    }

    public static AccountView ToView(Account a, decimal balance) => new(
        a.Id, a.Name, a.Kind.ToString(), a.Currency, balance, a.AnchorBalance,
        Local(a.BalanceAsOfUtc), a.CardNames, a.IsArchived,
        a.LastImportAtUtc is { } li ? Local(li) : null);

    private static DateTimeOffset Local(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToOffset(Sgt);

    // ---- Card names → account ----

    public static IEnumerable<string> CardTokens(string? cardNames) =>
        (cardNames ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(MerchantText.Normalize).Where(t => t.Length > 0);

    /// <summary>The account a card name belongs to ("DBS Altitude Visa" → the account listing "Altitude").</summary>
    public static Account? ForCard(IEnumerable<Account> accounts, string? cardName)
    {
        var card = MerchantText.Normalize(cardName);
        if (card.Length == 0) return null;
        return accounts.Where(a => !a.IsArchived)
            .Select(a => (a, best: CardTokens(a.CardNames).Where(t => card == t || card.Contains(t)).Select(t => t.Length).DefaultIfEmpty(0).Max()))
            .Where(x => x.best > 0)
            .OrderByDescending(x => x.best)
            .Select(x => x.a)
            .FirstOrDefault();
    }

    /// <summary>After an account's card names change, file the existing taps that use those cards.</summary>
    public async Task<int> LinkExistingByCardAsync(Account account, CancellationToken ct = default)
    {
        if (!CardTokens(account.CardNames).Any()) return 0;
        var accounts = await db.Accounts.AsNoTracking().Where(a => !a.IsArchived && a.CardNames != null).ToListAsync(ct);
        var loose = await db.Transactions.Where(t => t.AccountId == null && t.CardName != null).ToListAsync(ct);
        var n = 0;
        foreach (var t in loose.Where(t => ForCard(accounts, t.CardName)?.Id == account.Id))
        {
            t.AccountId = account.Id;
            n++;
        }
        return n;
    }

    // ---- Statement import ----

    /// <param name="positiveBalanceIsOwed">How to read the statement's balance; defaults to "it's a card".</param>
    public async Task<ImportPreview> PreviewAsync(Account account, ParsedStatement parsed, bool positiveIsSpend,
        CancellationToken ct = default, bool? positiveBalanceIsOwed = null)
    {
        if (parsed.Problem is not null)
            return new ImportPreview(parsed.Headers, parsed.Columns, positiveIsSpend, [], parsed.SkippedLines, null, null, null, parsed.Problem,
                NeedsPassword: parsed.Problem is PdfStatementReader.PasswordProblem or PdfStatementReader.WrongPasswordProblem);

        var rows = parsed.Rows;
        var from = clock.LocalDateStartToUtc(rows.Min(r => r.Date).AddDays(-StatementMatcher.DaysAfter - 1));
        var to = clock.LocalDateStartToUtc(rows.Max(r => r.Date).AddDays(StatementMatcher.DaysBefore + 1));

        var existing = await db.Transactions.AsNoTracking().Include(t => t.Category)
            .Where(t => t.OccurredAtUtc >= from && t.OccurredAtUtc < to && (t.AccountId == null || t.AccountId == account.Id))
            .ToListAsync(ct);
        existing = existing.Where(t => t.Amount > 0).ToList(); // pending bus/MRT taps are never a match
        var known = existing.Select(t => new KnownTx(t.Id, clock.ToLocalDate(t.OccurredAtUtc), t.Amount, t.IsIncome, t.Merchant, t.AccountId)).ToList();
        var matches = StatementMatcher.Match(rows, known, account.Id);
        var byId = existing.ToDictionary(t => t.Id);

        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var salaries = existing.Where(t => t.IsIncome && t.Category?.Name == "Salary").ToList();

        var preview = new List<ImportPreviewRow>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (matches[i] is int id)
            {
                var m = byId[id];
                preview.Add(new ImportPreviewRow(i, r.Date, r.Description, r.Amount, r.IsCredit, r.Balance, "matched", id,
                    $"{m.Merchant} · {clock.ToLocalDate(m.OccurredAtUtc):d MMM}", false, m.Category?.Name));
                continue;
            }

            var category = await GuessCategoryAsync(r, ct);
            var salary = r.IsCredit && (StatementMatcher.LooksLikeSalary(r.Description) ||
                                        (category is int c && categories[c] == "Salary"));
            // Salary entered by hand from a payslip can differ by a few cents from what the bank paid,
            // so it won't match exactly. Flag it rather than doubling this month's income.
            var nearSalary = salary ? salaries.FirstOrDefault(s =>
                Math.Abs(clock.ToLocalDate(s.OccurredAtUtc).DayNumber - r.Date.DayNumber) <= 7) : null;
            preview.Add(nearSalary is not null
                ? new ImportPreviewRow(i, r.Date, r.Description, r.Amount, r.IsCredit, r.Balance, "check", nearSalary.Id,
                    $"Salary {TransactionService.Money(nearSalary.Amount, nearSalary.Currency)} already recorded on {clock.ToLocalDate(nearSalary.OccurredAtUtc):d MMM}",
                    false, "Salary")
                : new ImportPreviewRow(i, r.Date, r.Description, r.Amount, r.IsCredit, r.Balance, "new", null, null,
                    salary, salary ? "Salary" : category is int cc ? categories[cc] : null));
        }

        // What the app will say the balance was on the statement's last day, once the missing rows are in.
        decimal? appThen = null;
        if (parsed.ClosingDate is { } closeDay)
        {
            var linkedNow = await db.Transactions.AsNoTracking().Where(t => t.AccountId == account.Id)
                .Select(t => new { t.Id, t.OccurredAtUtc, t.Amount, t.IsIncome }).ToListAsync(ct);
            var flows = linkedNow.Select(t => (t.OccurredAtUtc, t.IsIncome ? t.Amount : -t.Amount)).ToList();
            var linkedIds = linkedNow.Select(t => t.Id).ToHashSet();
            flows.AddRange(preview.Where(p => p.Status == "matched" && !linkedIds.Contains(p.MatchId!.Value))
                .Select(p => byId[p.MatchId!.Value]).Select(t => (t.OccurredAtUtc, t.IsIncome ? t.Amount : -t.Amount)));
            flows.AddRange(preview.Where(p => p.Status == "new")
                .Select(p => (RowTimeUtc(p.Date), p.IsCredit ? p.Amount : -p.Amount)));
            appThen = AccountMath.BalanceAt(account.AnchorBalance, account.BalanceAsOfUtc, flows, EndOfDayUtc(closeDay));
        }

        decimal? statementBalance = parsed.ClosingBalance is { } cb
            ? AccountMath.FromStatement(cb, positiveBalanceIsOwed ?? account.Kind == AccountKind.CreditCard)
            : null;
        return new ImportPreview(parsed.Headers, parsed.Columns, positiveIsSpend, preview, parsed.SkippedLines,
            statementBalance, parsed.ClosingDate, appThen, null, parsed.Warning);
    }

    public async Task<ImportResult> CommitAsync(Account account, IReadOnlyList<ImportRowIn> add, IReadOnlyList<int> link,
        decimal? statementBalance, DateOnly? statementBalanceDate, CancellationToken ct = default, string? fileName = null)
    {
        var tracked = await db.Accounts.SingleAsync(a => a.Id == account.Id, ct);
        var batch = new ImportBatch
        {
            AccountId = account.Id,
            CreatedAtUtc = clock.UtcNow,
            FileName = fileName is { Length: > 200 } ? fileName[..200] : fileName,
            PreviousAnchorBalance = tracked.AnchorBalance,
            PreviousBalanceAsOfUtc = tracked.BalanceAsOfUtc,
            PreviousLastImportAtUtc = tracked.LastImportAtUtc
        };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(ct);

        var linked = 0;
        var linkedIds = new List<int>();
        if (link.Count > 0)
        {
            var toLink = await db.Transactions.Where(t => link.Contains(t.Id)).ToListAsync(ct);
            foreach (var t in toLink.Where(t => t.AccountId is null || t.AccountId == account.Id))
            {
                if (t.AccountId is null)
                {
                    linked++;
                    linkedIds.Add(t.Id);
                }
                t.AccountId = account.Id;
                var tag = nameof(TransactionSource.StatementImport);
                if (t.Source != TransactionSource.StatementImport && (t.MergedSources is null || !t.MergedSources.Contains(tag)))
                    t.MergedSources = string.IsNullOrEmpty(t.MergedSources) ? tag : $"{t.MergedSources},{tag}";
            }
            await db.SaveChangesAsync(ct);
        }

        var added = 0;
        var salaries = 0;
        var settings = await budgets.GetSettingsAsync(ct);
        foreach (var r in add.OrderBy(r => r.Date))
        {
            var when = RowTimeUtc(r.Date);
            var description = r.Description.Trim();
            if (r.IsCredit && r.IsSalary)
            {
                var (salaryTx, _) = await budgets.RecordSalaryAsync(r.Amount, when, description, account.Id, TransactionSource.StatementImport, ct);
                salaryTx.ImportBatchId = batch.Id;
                await db.SaveChangesAsync(ct);
                salaries++;
                added++;
                continue;
            }

            var row = new StatementRow(0, r.Date, description, r.Amount, r.IsCredit, null);
            var tx = new Transaction
            {
                OccurredAtUtc = when,
                Amount = Math.Round(r.Amount, 2),
                Currency = account.Currency,
                IsIncome = r.IsCredit,
                Merchant = description.Length > 200 ? description[..200] : description,
                Source = TransactionSource.StatementImport,
                AccountId = account.Id,
                ImportBatchId = batch.Id,
                CategoryId = await GuessCategoryAsync(row, ct),
                Notes = "From statement"
            };
            db.Transactions.Add(tx);
            await transactions.AbsorbPendingFaresAsync(tx, ct);
            await db.SaveChangesAsync(ct);
            added++;
        }

        tracked.LastImportAtUtc = batch.CreatedAtUtc;
        if (statementBalance is { } bal && statementBalanceDate is { } day)
        {
            // The statement is the truth: start counting again from its closing balance.
            tracked.AnchorBalance = bal;
            tracked.BalanceAsOfUtc = EndOfDayUtc(day);
            batch.ResetBalance = true;
            batch.SetBalanceAsOfUtc = tracked.BalanceAsOfUtc;
            batch.SetAnchorBalance = tracked.AnchorBalance;
        }
        batch.Added = added;
        batch.LinkedIds = linkedIds.Count == 0 ? null : string.Join(',', linkedIds);
        await db.SaveChangesAsync(ct);

        var balance = await BalanceAsync(tracked, ct);
        var parts = new List<string>();
        parts.Add(added == 0 ? "Nothing was missing" : $"Added {added} missing {(added == 1 ? "entry" : "entries")}");
        if (linked > 0) parts.Add($"filed {linked} existing under {account.Name}");
        if (salaries > 0) parts.Add("salary recorded");
        return new ImportResult(added, linked, salaries, balance,
            $"{string.Join(", ", parts)}. Balance now {TransactionService.Money(balance, account.Currency)}.", batch.Id);
    }

    public async Task<IReadOnlyList<ImportBatchView>> ImportsAsync(int accountId, CancellationToken ct = default)
    {
        var batches = await db.ImportBatches.AsNoTracking().Where(b => b.AccountId == accountId)
            .OrderByDescending(b => b.Id).Take(20).ToListAsync(ct);
        var latestLive = batches.FirstOrDefault(b => b.UndoneAtUtc is null)?.Id;
        return batches.Select(b => new ImportBatchView(b.Id, Local(b.CreatedAtUtc), b.FileName, b.Added,
            b.LinkedIds?.Split(',').Length ?? 0, b.ResetBalance,
            b.UndoneAtUtc is { } u ? Local(u) : null, b.Id == latestLive)).ToList();
    }

    /// <summary>
    /// Takes a statement import back out: removes the entries it added (and the budget a salary
    /// line opened), unfiles the entries it filed, and puts the balance back the way it was.
    /// Only the latest import of an account can be undone, so undos never tangle with each other.
    /// Bus/MRT taps that an imported fare replaced stay gone; they were S$0 placeholders.
    /// </summary>
    public async Task<ImportResult> UndoAsync(Account account, int batchId, CancellationToken ct = default)
    {
        var batch = await db.ImportBatches.FirstOrDefaultAsync(b => b.Id == batchId && b.AccountId == account.Id, ct)
                    ?? throw new ImportUndoException("That import isn't on this account.");
        if (batch.UndoneAtUtc is not null) throw new ImportUndoException("That import was already undone.");
        var newer = await db.ImportBatches.AnyAsync(b => b.AccountId == account.Id && b.Id > batch.Id && b.UndoneAtUtc == null, ct);
        if (newer) throw new ImportUndoException("Undo the newer import first — only the latest one can be undone.");

        var added = await db.Transactions.Include(t => t.Category).Where(t => t.ImportBatchId == batch.Id).ToListAsync(ct);
        foreach (var t in added.Where(t => t.IsIncome && t.Category?.Name == "Salary"))
            await budgets.ReverseSalaryAsync(t, ct);
        db.Transactions.RemoveRange(added);

        var ids = (batch.LinkedIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        if (ids.Count > 0)
        {
            var tag = nameof(TransactionSource.StatementImport);
            foreach (var t in await db.Transactions.Where(t => ids.Contains(t.Id) && t.AccountId == account.Id).ToListAsync(ct))
            {
                t.AccountId = null;
                var rest = (t.MergedSources ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Where(s => s != tag).ToList();
                t.MergedSources = rest.Count == 0 ? null : string.Join(',', rest);
            }
        }

        var tracked = await db.Accounts.SingleAsync(a => a.Id == account.Id, ct);
        var keptManualBalance = false;
        if (batch.ResetBalance)
        {
            if (tracked.BalanceAsOfUtc == batch.SetBalanceAsOfUtc && tracked.AnchorBalance == batch.SetAnchorBalance)
            {
                tracked.AnchorBalance = batch.PreviousAnchorBalance;
                tracked.BalanceAsOfUtc = batch.PreviousBalanceAsOfUtc;
            }
            else keptManualBalance = true; // set by hand after the import: that's newer information
        }
        tracked.LastImportAtUtc = batch.PreviousLastImportAtUtc;
        batch.UndoneAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        var balance = await BalanceAsync(tracked, ct);
        var message = $"Import undone: removed {added.Count} {(added.Count == 1 ? "entry" : "entries")}. " +
                      $"Balance now {TransactionService.Money(balance, tracked.Currency)}." +
                      (keptManualBalance ? " The balance you set by hand after it was kept." : "");
        return new ImportResult(-added.Count, -ids.Count, 0, balance, message, batch.Id);
    }

    /// <summary>
    /// Category for a statement line. Card bill payments are Transfers (your own money moving);
    /// money in only takes an income or transfer category (else Other Income) so a refund isn't filed as shopping.
    /// </summary>
    private async Task<int?> GuessCategoryAsync(StatementRow r, CancellationToken ct)
    {
        if (StatementMatcher.LooksLikeCardPayment(r.Description))
            return (await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Name == "Transfer", ct))?.Id;

        var match = await categorizer.MatchAsync(r.Description, ct);
        if (!r.IsCredit) return match;
        if (match is int id && await db.Categories.AnyAsync(c => c.Id == id && (c.Bucket == Bucket.Income || c.Bucket == Bucket.Transfer), ct))
            return id;
        // Money in can't be sorted on the Spending screen, so it shouldn't wait there as "needs a category".
        return (await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Name == "Other Income", ct))?.Id;
    }

    /// <summary>Statements give a day, not a time. Midday keeps it on that day in any time zone handling.</summary>
    private DateTime RowTimeUtc(DateOnly day) => clock.LocalDateStartToUtc(day).AddHours(12);

    /// <summary>The first moment after that day: a balance "as of" a day includes all of it.</summary>
    private DateTime EndOfDayUtc(DateOnly day) => clock.LocalDateStartToUtc(day.AddDays(1));
}
