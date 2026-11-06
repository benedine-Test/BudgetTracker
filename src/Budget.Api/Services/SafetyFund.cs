using System.Globalization;
using Budget.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Services;

/// <summary>
/// One savings pocket. EarnedSoFar is interest up to yesterday; InterestAtMaturity (Boost Pockets) is the
/// whole term's interest if the balance stays as it is, and TotalAtMaturity adds the balance and any cashback.
/// YearAtThisRate is a year's interest on today's balance. All interest figures are estimates.
/// </summary>
public record PocketView(
    int AccountId, string Name, string Bank, string Type, string Purpose, decimal Balance, bool IsArchived,
    decimal InterestRatePct, decimal? BonusRatePct, decimal TotalRatePct,
    DateOnly? StartDate, int? TermMonths, DateOnly? MaturityDate, decimal? PromoCashback, decimal? GoalAmount,
    bool Locked, int? DaysToMaturity, decimal EarnedSoFar, decimal? InterestAtMaturity, decimal? TotalAtMaturity,
    decimal YearAtThisRate);

/// <param name="Ready">Safety Fund money you can take out today (Saving Pockets, matured Boost Pockets).</param>
/// <param name="Locked">Safety Fund money in Boost Pockets that haven't matured.</param>
public record SafetyFundView(
    string Currency, decimal EmergencyTarget, decimal? ParentsTarget, decimal Target,
    decimal Ready, decimal Locked, decimal Total, decimal PercentOfTarget,
    List<PocketView> Safety, List<PocketView> Travel, List<PocketView> Other);

/// <summary>Kind: "maturity" (a Boost Pocket's end date) | "repeat" (comes back once marked done).</summary>
public record ReminderView(string Kind, string Text, DateOnly Due, int DaysLeft, bool Soon, int? ReminderId, int? AccountId);

public record PlannedView(int Id, string Name, decimal Amount, int FromAccountId, string? From, int ToAccountId, string? To,
    DateOnly StartsOn, bool IsActive);

public record PlannedItem(int Id, string Name, decimal Amount, string? From, string? To, bool Done, int? TransactionId, DateOnly? DoneOn);

/// <summary>The planned transfers for one pay period and which are done.</summary>
public record PlannedForPeriod(DateOnly PeriodStart, DateOnly PeriodEndExclusive, decimal Planned, decimal Done, List<PlannedItem> Items);

public record PocketSave(
    string Name, string? Bank, PocketType Type, PocketPurpose Purpose, decimal InterestRatePct,
    decimal? BonusRatePct = null, DateOnly? StartDate = null, int? TermMonths = null, DateOnly? MaturityDate = null,
    decimal? PromoCashback = null, decimal? GoalAmount = null,
    decimal? OpeningBalance = null, int? FundFromAccountId = null, decimal? FundAmount = null, bool FundAlreadySaved = true);

public record PlannedSave(string Name, decimal Amount, int FromAccountId, int ToAccountId, DateOnly StartsOn, bool IsActive = true);

/// <summary>Interest arithmetic for pockets, kept apart so it can be checked on its own.</summary>
public static class PocketMath
{
    /// <summary>
    /// Simple daily interest from <paramref name="from"/> up to (not including) <paramref name="toExclusive"/>:
    /// each day earns its closing balance × rate ÷ 365. Days after the last known move keep the latest balance.
    /// </summary>
    public static decimal Accrued(decimal openingBalance, IEnumerable<(DateOnly Day, decimal Signed)> moves,
        DateOnly from, DateOnly toExclusive, decimal ratePct)
    {
        if (toExclusive <= from || ratePct == 0) return 0;
        var byDay = moves.Where(m => m.Day >= from).GroupBy(m => m.Day).ToDictionary(g => g.Key, g => g.Sum(m => m.Signed));
        var balance = openingBalance;
        decimal interest = 0;
        for (var d = from; d < toExclusive; d = d.AddDays(1))
        {
            if (byDay.TryGetValue(d, out var change)) balance += change;
            if (balance > 0) interest += balance * ratePct / 100m / 365m;
        }
        return Math.Round(interest, 2);
    }

    /// <summary>A Boost Pocket's end date: start plus the term, unless one was entered.</summary>
    public static DateOnly? Maturity(DateOnly? start, int? termMonths, DateOnly? entered) =>
        entered ?? (start is { } s && termMonths is > 0 ? s.AddMonths(termMonths.Value) : null);
}

public class SafetyFund(BudgetDbContext db, Clock clock, BudgetService budgets, SavingsTransfers transfers)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- The Safety Fund screen ----

    public async Task<SafetyFundView> ViewAsync(DateOnly? today = null, CancellationToken ct = default)
    {
        var day = today ?? clock.Today;
        var settings = await budgets.GetSettingsAsync(ct);
        var pockets = await PocketsAsync(day, ct);
        var active = pockets.Where(p => !p.IsArchived).ToList();

        var safety = active.Where(p => p.Purpose == nameof(PocketPurpose.SafetyFund)).ToList();
        var locked = safety.Where(p => p.Locked).Sum(p => p.Balance);
        var ready = safety.Where(p => !p.Locked).Sum(p => p.Balance);
        var target = settings.EmergencyTarget + (settings.ParentsTarget ?? 0);
        var total = ready + locked;
        var pct = target <= 0 ? 0 : Math.Round(total / target * 100m, 1);

        return new SafetyFundView(settings.BaseCurrency, settings.EmergencyTarget, settings.ParentsTarget, target,
            ready, locked, total, pct, safety,
            active.Where(p => p.Purpose == nameof(PocketPurpose.Travel)).ToList(),
            active.Where(p => p.Purpose == nameof(PocketPurpose.Other)).ToList());
    }

    public async Task<List<PocketView>> PocketsAsync(DateOnly today, CancellationToken ct = default)
    {
        var terms = await db.PocketTerms.AsNoTracking().ToListAsync(ct);
        if (terms.Count == 0) return [];
        var ids = terms.Select(t => t.AccountId).ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var txs = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId != null && ids.Contains(t.AccountId.Value))
            .Select(t => new { t.AccountId, t.OccurredAtUtc, t.Amount, t.IsIncome }).ToListAsync(ct);

        var result = new List<PocketView>();
        foreach (var p in terms)
        {
            if (!accounts.TryGetValue(p.AccountId, out var a)) continue;
            var mine = txs.Where(t => t.AccountId == a.Id)
                .Select(t => (AtUtc: t.OccurredAtUtc, Signed: t.IsIncome ? t.Amount : -t.Amount)).ToList();
            result.Add(View(a, p, mine, today));
        }
        return result.OrderBy(p => p.Bank).ThenBy(p => p.Type).ThenBy(p => p.Name).ToList();
    }

    private PocketView View(Account a, PocketTerms p, List<(DateTime AtUtc, decimal Signed)> txs, DateOnly today)
    {
        var balance = AccountMath.BalanceAt(a.AnchorBalance, a.BalanceAsOfUtc, txs, DateTime.MaxValue);
        var from = p.StartDate ?? clock.ToLocalDate(a.BalanceAsOfUtc);
        var opening = AccountMath.BalanceAt(a.AnchorBalance, a.BalanceAsOfUtc, txs, clock.LocalDateStartToUtc(from));
        var moves = txs.Select(t => (clock.ToLocalDate(t.AtUtc), t.Signed)).ToList();
        var rate = p.TotalRatePct;
        var maturity = p.Type == PocketType.Boost ? PocketMath.Maturity(p.StartDate, p.TermMonths, p.MaturityDate) : null;

        var soFarEnd = maturity is { } m && m < today ? m : today;
        var earned = PocketMath.Accrued(opening, moves, from, soFarEnd, rate);
        decimal? atMaturity = maturity is { } end ? PocketMath.Accrued(opening, moves, from, end, rate) : null;
        decimal? totalAtMaturity = atMaturity is { } i ? balance + i + (p.PromoCashback ?? 0) : null;
        var locked = p.Type == PocketType.Boost && (maturity is null || today < maturity);

        return new PocketView(a.Id, a.Name, p.Bank, p.Type.ToString(), p.Purpose.ToString(), balance, a.IsArchived,
            p.InterestRatePct, p.BonusRatePct, rate, p.StartDate, p.TermMonths, maturity, p.PromoCashback, p.GoalAmount,
            locked, maturity is { } md ? md.DayNumber - today.DayNumber : null,
            earned, atMaturity, totalAtMaturity, Math.Round(Math.Max(balance, 0) * rate / 100m, 2));
    }

    public async Task SetTargetsAsync(decimal emergency, decimal? parents, CancellationToken ct = default)
    {
        if (emergency < 0 || parents < 0) throw new SavingsException("Targets can't be below zero.");
        var s = await db.Settings.SingleAsync(x => x.Id == 1, ct);
        s.EmergencyTarget = Math.Round(emergency, 2);
        s.ParentsTarget = parents is { } v ? Math.Round(v, 2) : null;
        await db.SaveChangesAsync(ct);
    }

    // ---- Pockets ----

    /// <summary>
    /// Adds a pocket as a bank account with its details, optionally moving money in from another account.
    /// Moving money you'd already saved (the default here) counts toward the Safety Fund but not 50/30/20.
    /// </summary>
    public async Task<PocketView> AddPocketAsync(PocketSave body, DateOnly? today = null, CancellationToken ct = default)
    {
        var day = today ?? clock.Today;
        var name = Check(body);
        var settings = await budgets.GetSettingsAsync(ct);
        var start = body.StartDate ?? day;

        var account = new Account
        {
            Name = name,
            Kind = AccountKind.Bank,
            Currency = settings.BaseCurrency,
            AnchorBalance = Math.Round(body.OpeningBalance ?? 0, 2),
            // From the start of the pocket's first day, so a move into it dated that day is counted.
            BalanceAsOfUtc = clock.LocalDateStartToUtc(start < day ? start : day)
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);

        var terms = new PocketTerms { AccountId = account.Id };
        Apply(terms, body, start);
        db.PocketTerms.Add(terms);
        await db.SaveChangesAsync(ct);

        if (body.FundFromAccountId is int fromId && body.FundAmount is > 0)
            await transfers.RecordAsync(fromId, account.Id, body.FundAmount.Value, transfers.WhenUtc(start),
                body.FundAlreadySaved, null, ct);

        return (await PocketsAsync(day, ct)).Single(p => p.AccountId == account.Id);
    }

    public async Task<PocketView?> UpdatePocketAsync(int accountId, PocketSave body, DateOnly? today = null, CancellationToken ct = default)
    {
        var terms = await db.PocketTerms.FirstOrDefaultAsync(p => p.AccountId == accountId, ct);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (terms is null || account is null) return null;
        account.Name = Check(body);
        Apply(terms, body, body.StartDate);
        await db.SaveChangesAsync(ct);
        return (await PocketsAsync(today ?? clock.Today, ct)).Single(p => p.AccountId == accountId);
    }

    private static string Check(PocketSave body)
    {
        var name = body.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) throw new SavingsException("Give the pocket a name (under 100 characters).");
        if (body.InterestRatePct is < 0 or > 100 || body.BonusRatePct is < 0 or > 100)
            throw new SavingsException("Interest rates are % a year, between 0 and 100.");
        if (body.TermMonths is < 1 or > 120) throw new SavingsException("The term is in months, 1 to 120.");
        if (body.PromoCashback < 0 || body.GoalAmount < 0 || body.OpeningBalance < 0)
            throw new SavingsException("Amounts can't be below zero.");
        if (body.StartDate is { } s && body.MaturityDate is { } m && m <= s)
            throw new SavingsException("The maturity date must be after the start date.");
        return name;
    }

    private static void Apply(PocketTerms t, PocketSave body, DateOnly? start)
    {
        t.Bank = string.IsNullOrWhiteSpace(body.Bank) ? "GXS Bank" : body.Bank.Trim()[..Math.Min(body.Bank.Trim().Length, 100)];
        t.Type = body.Type;
        t.Purpose = body.Purpose;
        t.InterestRatePct = body.InterestRatePct;
        var boost = body.Type == PocketType.Boost;
        t.BonusRatePct = boost ? body.BonusRatePct : null;
        t.StartDate = start;
        t.TermMonths = boost ? body.TermMonths : null;
        t.MaturityDate = boost ? PocketMath.Maturity(start, body.TermMonths, body.MaturityDate) : null;
        t.PromoCashback = boost ? body.PromoCashback : null;
        t.GoalAmount = body.GoalAmount is > 0 ? body.GoalAmount : null;
    }

    // ---- Planned transfers ----

    public async Task<List<PlannedView>> PlannedAsync(CancellationToken ct = default)
    {
        var plans = await db.PlannedTransfers.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct);
        var names = await AccountNamesAsync(ct);
        return plans.Select(p => new PlannedView(p.Id, p.Name, p.Amount, p.FromAccountId, names.GetValueOrDefault(p.FromAccountId),
            p.ToAccountId, names.GetValueOrDefault(p.ToAccountId), p.StartsOn, p.IsActive)).ToList();
    }

    public async Task<PlannedTransfer> SavePlannedAsync(int? id, PlannedSave body, CancellationToken ct = default)
    {
        var name = body.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) throw new SavingsException("Give it a name (under 100 characters).");
        if (body.Amount <= 0) throw new SavingsException("Enter an amount above zero.");
        if (body.FromAccountId == body.ToAccountId) throw new SavingsException("Pick two different accounts.");
        var ids = new[] { body.FromAccountId, body.ToAccountId };
        if (await db.Accounts.CountAsync(a => ids.Contains(a.Id), ct) != 2) throw new SavingsException("Pick accounts that are in the app.");

        var p = id is int i ? await db.PlannedTransfers.FirstOrDefaultAsync(x => x.Id == i, ct)
                              ?? throw new SavingsException("That plan no longer exists.")
                            : new PlannedTransfer { CreatedAtUtc = clock.UtcNow };
        p.Name = name;
        p.Amount = Math.Round(body.Amount, 2);
        p.FromAccountId = body.FromAccountId;
        p.ToAccountId = body.ToAccountId;
        p.StartsOn = body.StartsOn;
        p.IsActive = body.IsActive;
        if (id is null) db.PlannedTransfers.Add(p);
        await db.SaveChangesAsync(ct);
        return p;
    }

    /// <summary>Removes a plan. Transfers already ticked off stay; they did happen.</summary>
    public async Task<bool> DeletePlannedAsync(int id, CancellationToken ct = default)
    {
        var p = await db.PlannedTransfers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return false;
        db.PlannedTransferTicks.RemoveRange(await db.PlannedTransferTicks.Where(k => k.PlannedTransferId == id).ToListAsync(ct));
        db.PlannedTransfers.Remove(p);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// The planned transfers for the pay period containing <paramref name="date"/>: active plans that start
    /// on or before the period does, plus any already done in it.
    /// </summary>
    public async Task<PlannedForPeriod> ForPeriodAsync(DateOnly date, CancellationToken ct = default)
    {
        var (period, _) = await budgets.PeriodForAsync(date, ct);
        var plans = await db.PlannedTransfers.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct);
        var ticks = await LiveTicksAsync(period.StartDate, ct);
        var names = await AccountNamesAsync(ct);

        var items = plans
            .Where(p => (p.IsActive && p.StartsOn <= period.StartDate) || ticks.ContainsKey(p.Id))
            .Select(p =>
            {
                var tick = ticks.GetValueOrDefault(p.Id);
                return new PlannedItem(p.Id, p.Name, tick?.Tx.Amount ?? p.Amount, names.GetValueOrDefault(p.FromAccountId),
                    names.GetValueOrDefault(p.ToAccountId), tick is not null, tick?.Tx.Id,
                    tick is null ? null : clock.ToLocalDate(tick.Tx.OccurredAtUtc));
            }).ToList();
        return new PlannedForPeriod(period.StartDate, period.EndDate,
            items.Sum(i => i.Amount), items.Where(i => i.Done).Sum(i => i.Amount), items);
    }

    /// <summary>Ticks a plan off for a pay period by recording the transfer, dated within that period.</summary>
    public async Task<PlannedForPeriod> TickAsync(int plannedId, DateOnly periodDate, CancellationToken ct = default)
    {
        var p = await db.PlannedTransfers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == plannedId, ct)
                ?? throw new SavingsException("That plan no longer exists.");
        var (period, _) = await budgets.PeriodForAsync(periodDate, ct);
        if ((await LiveTicksAsync(period.StartDate, ct)).ContainsKey(plannedId))
            throw new SavingsException("Already ticked off for this pay period.");
        // A tick whose entry was deleted elsewhere no longer counts; clear it so this one can be saved.
        db.PlannedTransferTicks.RemoveRange(await db.PlannedTransferTicks
            .Where(k => k.PlannedTransferId == plannedId && k.PeriodStart == period.StartDate).ToListAsync(ct));

        var today = clock.Today;
        var at = today >= period.StartDate && today < period.EndDate ? clock.UtcNow
            : transfers.WhenUtc(today < period.StartDate ? period.StartDate : period.EndDate.AddDays(-1));
        var (moneyOut, _) = await transfers.RecordAsync(p.FromAccountId, p.ToAccountId, p.Amount, at, false, p.Name, ct);
        db.PlannedTransferTicks.Add(new PlannedTransferTick
        {
            PlannedTransferId = p.Id, PeriodStart = period.StartDate, TransactionId = moneyOut.Id, DoneAtUtc = clock.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return await ForPeriodAsync(period.StartDate, ct);
    }

    /// <summary>Unticks a plan for a pay period: the transfer it recorded (both sides) is removed.</summary>
    public async Task<PlannedForPeriod> UntickAsync(int plannedId, DateOnly periodDate, CancellationToken ct = default)
    {
        var (period, _) = await budgets.PeriodForAsync(periodDate, ct);
        var tick = await db.PlannedTransferTicks
            .FirstOrDefaultAsync(k => k.PlannedTransferId == plannedId && k.PeriodStart == period.StartDate, ct);
        if (tick is not null)
        {
            db.PlannedTransferTicks.Remove(tick);
            if (await db.Transactions.FirstOrDefaultAsync(t => t.Id == tick.TransactionId, ct) is { } tx)
                await SavingsTransfers.RemovePairAsync(db, tx, ct);
            await db.SaveChangesAsync(ct);
        }
        return await ForPeriodAsync(period.StartDate, ct);
    }

    private record LiveTick(PlannedTransferTick Tick, Transaction Tx);

    /// <summary>Ticks for a period whose transfer is still in the app, by plan id.</summary>
    private async Task<Dictionary<int, LiveTick>> LiveTicksAsync(DateOnly periodStart, CancellationToken ct)
    {
        var ticks = await db.PlannedTransferTicks.AsNoTracking().Where(k => k.PeriodStart == periodStart).ToListAsync(ct);
        var txIds = ticks.Select(k => k.TransactionId).ToList();
        var txs = await db.Transactions.AsNoTracking().Where(t => txIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        return ticks.Where(k => txs.ContainsKey(k.TransactionId))
            .GroupBy(k => k.PlannedTransferId)
            .ToDictionary(g => g.Key, g => new LiveTick(g.First(), txs[g.First().TransactionId]));
    }

    private async Task<Dictionary<int, string>> AccountNamesAsync(CancellationToken ct) =>
        await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name, ct);

    // ---- Reminders ----

    /// <summary>
    /// Every Boost Pocket still holding money, by maturity date, and every repeating reminder.
    /// Soon = within 14 days (or past).
    /// </summary>
    public async Task<List<ReminderView>> RemindersAsync(DateOnly? today = null, CancellationToken ct = default)
    {
        var day = today ?? clock.Today;
        var list = new List<ReminderView>();
        foreach (var p in (await PocketsAsync(day, ct)).Where(p => !p.IsArchived && p.Type == nameof(PocketType.Boost)
                     && p.MaturityDate is not null && p.Balance > 0))
        {
            var due = p.MaturityDate!.Value;
            var verb = due < day ? "matured on" : "matures on";
            list.Add(new ReminderView("maturity",
                $"{p.Name} {verb} {due.ToString("d MMM yyyy", Inv)}: move it into a longer Boost Pocket or SSB.",
                due, due.DayNumber - day.DayNumber, due.DayNumber - day.DayNumber <= 14, null, p.AccountId));
        }

        var archived = await db.Accounts.AsNoTracking().Where(a => a.IsArchived).Select(a => a.Id).ToListAsync(ct);
        foreach (var r in await db.Reminders.AsNoTracking().ToListAsync(ct))
        {
            if (r.AccountId is int aid && archived.Contains(aid)) continue;
            list.Add(new ReminderView("repeat", r.Text, r.NextDue, r.NextDue.DayNumber - day.DayNumber,
                r.NextDue.DayNumber - day.DayNumber <= 14, r.Id, r.AccountId));
        }
        return list.OrderBy(r => r.Due).ToList();
    }

    /// <summary>Marks a repeating reminder done: it comes back its number of months after the date it was due.</summary>
    public async Task<bool> ReminderDoneAsync(int id, DateOnly? today = null, CancellationToken ct = default)
    {
        var r = await db.Reminders.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return false;
        var day = today ?? clock.Today;
        do r.NextDue = r.NextDue.AddMonths(Math.Max(1, r.RepeatMonths));
        while (r.NextDue <= day);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
