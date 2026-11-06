using Budget.Api.Data;
using Budget.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Budget.Tests;

public class TransferRuleTests
{
    private static PocketTerms Pocket(PocketPurpose purpose, PocketType type = PocketType.Saving) =>
        new() { Purpose = purpose, Type = type };

    [Fact]
    public void Pay_into_a_safety_fund_pocket_is_savings() =>
        Assert.Equal(Bucket.Savings, TransferRules.OutSide(null, Pocket(PocketPurpose.SafetyFund), alreadySaved: false));

    [Fact]
    public void Pay_into_a_pocket_outside_the_safety_fund_is_only_moved() =>
        Assert.Equal(Bucket.Transfer, TransferRules.OutSide(null, Pocket(PocketPurpose.Other), alreadySaved: false));

    [Fact]
    public void Money_already_saved_is_only_moved() =>
        Assert.Equal(Bucket.Transfer, TransferRules.OutSide(null, Pocket(PocketPurpose.SafetyFund, PocketType.Boost), alreadySaved: true));

    [Fact]
    public void Saving_pocket_into_boost_pocket_is_not_counted_again() =>
        Assert.Equal(Bucket.Transfer, TransferRules.OutSide(
            Pocket(PocketPurpose.SafetyFund), Pocket(PocketPurpose.SafetyFund, PocketType.Boost), alreadySaved: false));

    [Fact]
    public void Into_an_ordinary_account_or_main_account_is_a_transfer()
    {
        Assert.Equal(Bucket.Transfer, TransferRules.OutSide(null, null, alreadySaved: false));
        Assert.Equal(Bucket.Transfer, TransferRules.OutSide(null, Pocket(PocketPurpose.Other, PocketType.Main), alreadySaved: false));
    }

    [Fact]
    public void Out_of_a_savings_pocket_is_never_counted_as_savings() =>
        Assert.Equal(Bucket.Transfer, TransferRules.OutSide(Pocket(PocketPurpose.SafetyFund), null, alreadySaved: false));

    [Theory]
    [InlineData(PocketPurpose.SafetyFund, false, "Safety Fund")]
    [InlineData(PocketPurpose.Other, false, "Transfer")]
    [InlineData(PocketPurpose.SafetyFund, true, "Transfer")]
    public void Files_the_money_out_side(PocketPurpose to, bool alreadySaved, string category) =>
        Assert.Equal(category, TransferRules.OutCategory(null, Pocket(to), alreadySaved));
}

public class PocketMathTests
{
    [Fact]
    public void Boost_pocket_matures_three_months_after_it_starts() =>
        Assert.Equal(new DateOnly(2027, 2, 6), PocketMath.Maturity(new DateOnly(2026, 11, 6), 3, null));

    [Fact]
    public void An_entered_maturity_date_wins() =>
        Assert.Equal(new DateOnly(2027, 2, 8), PocketMath.Maturity(new DateOnly(2026, 11, 6), 3, new DateOnly(2027, 2, 8)));

    [Fact]
    public void First_boost_interest_over_its_term()
    {
        // S$5,000 at 1.22% a year for the 92 days from 6 Nov 2026 to 6 Feb 2027: 5000 × 1.22% × 92/365.
        var interest = PocketMath.Accrued(0, [(new DateOnly(2026, 11, 6), 5000m)],
            new DateOnly(2026, 11, 6), new DateOnly(2027, 2, 6), 1.22m);
        Assert.Equal(15.38m, interest);
    }

    [Fact]
    public void Interest_follows_the_balance_day_by_day()
    {
        // 1,000 for 10 days, then 2,000 for 10 days, at 3.65% (0.01% a day).
        var interest = PocketMath.Accrued(1000m, [(new DateOnly(2027, 1, 11), 1000m)],
            new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 21), 3.65m);
        Assert.Equal(3.00m, interest);
    }

    [Fact]
    public void No_days_no_interest() =>
        Assert.Equal(0m, PocketMath.Accrued(5000m, [], new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 6), 1.22m));
}

/// <summary>Savings transfers against a real (in-memory SQLite) database: what moves, and what the budget sees.</summary>
public sealed class SavingsTransferTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly BudgetDbContext _db;
    private readonly BudgetService _budgets;
    private readonly TransactionService _tx;
    private readonly AccountService _accounts;
    private readonly SavingsTransfers _transfers;
    private readonly SafetyFund _fund;
    private readonly Clock _clock = new(new ConfigurationBuilder().Build());

    private static readonly DateOnly Nov6 = new(2026, 11, 6);
    private static readonly DateOnly Payday = new(2026, 12, 25);

    public SavingsTransferTests()
    {
        _conn.Open();
        _db = new BudgetDbContext(new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        Seed.EnsureSeededAsync(_db).GetAwaiter().GetResult();
        var categorizer = new Categorizer(_db);
        _budgets = new BudgetService(_db, _clock);
        _tx = new TransactionService(_db, categorizer, _budgets, _clock);
        _accounts = new AccountService(_db, categorizer, _budgets, _tx, _clock);
        _transfers = new SavingsTransfers(_db, _clock);
        _fund = new SafetyFund(_db, _clock, _budgets, _transfers);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private DateTime At(DateOnly day, int hour = 12) => _clock.LocalDateStartToUtc(day).AddHours(hour);

    private async Task<Account> BankAsync(string name, decimal balance)
    {
        var a = new Account { Name = name, Kind = AccountKind.Bank, AnchorBalance = balance, BalanceAsOfUtc = At(new DateOnly(2026, 10, 1), 0) };
        _db.Accounts.Add(a);
        await _db.SaveChangesAsync();
        return a;
    }

    private Task<PocketView> PocketAsync(string name, PocketType type, PocketPurpose purpose, decimal rate, DateOnly? start = null) =>
        _fund.AddPocketAsync(new PocketSave(name, "GXS Bank", type, purpose, rate, StartDate: start ?? Nov6), today: start ?? Nov6);

    /// <summary>A December pay period with S$5,000 take-home and some ordinary spending in it.</summary>
    private async Task<int> PayPeriodWithSpendingAsync(Account uob)
    {
        await _budgets.RecordSalaryAsync(5000m, At(Payday), null, uob.Id);
        var groceries = await _db.Categories.SingleAsync(c => c.Name == "Groceries");
        _db.Transactions.Add(new Transaction
        {
            OccurredAtUtc = At(Payday.AddDays(1)), Amount = 80m, Merchant = "FairPrice", AccountId = uob.Id,
            CategoryId = groceries.Id, CategoryConfirmed = true
        });
        await _db.SaveChangesAsync();
        // An Apple Pay tap, categorised the usual way.
        await _tx.IngestSpendAsync(7.80m, "SGD", "Starbucks", "iPhone", At(Payday.AddDays(2)), TransactionSource.ApplePayShortcut, null);
        return uob.Id;
    }

    private async Task<(decimal Needs, decimal Wants, decimal Savings, decimal Total, decimal Left)> BudgetAsync()
    {
        var s = await _budgets.SummaryAsync(Payday.AddDays(3));
        decimal Spent(string b) => s.Buckets.Single(x => x.Bucket == b).Spent;
        return (Spent("Needs"), Spent("Wants"), Spent("Savings"), s.TotalSpent, s.LeftToSpend);
    }

    [Fact]
    public async Task Payday_transfer_to_safety_fund_counts_as_savings_not_needs_or_wants()
    {
        var uob = await BankAsync("UOB One", 20000m);
        await PayPeriodWithSpendingAsync(uob);
        var safety = await PocketAsync("Safety fund", PocketType.Saving, PocketPurpose.SafetyFund, 1.2m);
        var before = await BudgetAsync();

        var (moneyOut, moneyIn) = await _transfers.RecordAsync(uob.Id, safety.AccountId, 300m, At(Payday.AddDays(1)), false, null);

        var after = await BudgetAsync();
        Assert.Equal(before.Needs, after.Needs);
        Assert.Equal(before.Wants, after.Wants);
        Assert.Equal(before.Savings + 300m, after.Savings);
        Assert.Equal("Safety Fund", (await _db.Categories.FindAsync(moneyOut.CategoryId))!.Name);
        Assert.Equal("Transfer", (await _db.Categories.FindAsync(moneyIn.CategoryId))!.Name);
        Assert.Equal(moneyIn.Id, moneyOut.PairId);
        Assert.Equal(moneyOut.Id, moneyIn.PairId);
        Assert.Equal(300m, await _accounts.BalanceAsync(await _db.Accounts.FindAsync(safety.AccountId) ?? throw new()));
    }

    [Fact]
    public async Task Existing_savings_moved_to_first_boost_counts_toward_safety_fund_only()
    {
        var uob = await BankAsync("UOB One", 20000m);
        await PayPeriodWithSpendingAsync(uob);
        var before = await BudgetAsync();
        var uobBefore = await _accounts.BalanceAsync(uob);

        // Today's move, but dated inside the pay period so the budget would see it if it counted.
        var boost = await _fund.AddPocketAsync(new PocketSave("First Boost", "GXS Bank", PocketType.Boost, PocketPurpose.SafetyFund,
            0.88m, BonusRatePct: 0.34m, StartDate: Payday.AddDays(1), TermMonths: 3, PromoCashback: 20m,
            FundFromAccountId: uob.Id, FundAmount: 5000m, FundAlreadySaved: true), today: Payday.AddDays(1));

        var after = await BudgetAsync();
        Assert.Equal(before, after); // Needs, Wants, Savings, total and what's left all unchanged
        Assert.Equal(uobBefore - 5000m, await _accounts.BalanceAsync(uob));
        Assert.Equal(5000m, boost.Balance);

        var view = await _fund.ViewAsync(Payday.AddDays(1));
        Assert.Equal(5000m, view.Locked);
        Assert.Equal(0m, view.Ready);
        Assert.Equal(12000m, view.Target);
    }

    [Fact]
    public async Task Saving_pocket_into_boost_pocket_changes_ready_to_locked_and_nothing_else()
    {
        var uob = await BankAsync("UOB One", 20000m);
        await PayPeriodWithSpendingAsync(uob);
        var safety = await PocketAsync("Safety fund", PocketType.Saving, PocketPurpose.SafetyFund, 1.2m);
        var boost = await PocketAsync("Boost 2", PocketType.Boost, PocketPurpose.SafetyFund, 1.3m);
        await _transfers.RecordAsync(uob.Id, safety.AccountId, 1200m, At(Payday.AddDays(1)), false, null);
        var before = await BudgetAsync();

        await _transfers.RecordAsync(safety.AccountId, boost.AccountId, 1200m, At(Payday.AddDays(2)), false, null);

        Assert.Equal(before, await BudgetAsync());
        var view = await _fund.ViewAsync(Payday.AddDays(3));
        Assert.Equal(0m, view.Ready);
        Assert.Equal(1200m, view.Locked);
        Assert.Equal(1200m, view.Total);
    }

    [Fact]
    public async Task A_pocket_outside_the_safety_fund_is_not_savings_and_not_in_the_fund()
    {
        var uob = await BankAsync("UOB One", 20000m);
        await PayPeriodWithSpendingAsync(uob);
        var other = await _fund.AddPocketAsync(new PocketSave("Holiday", "GXS Bank", PocketType.Saving, PocketPurpose.Other, 1.2m,
            StartDate: Nov6, GoalAmount: 2000m), today: Nov6);
        var before = await BudgetAsync();

        var (moneyOut, _) = await _transfers.RecordAsync(uob.Id, other.AccountId, 100m, At(Payday.AddDays(1)), false, null);

        Assert.Equal(before, await BudgetAsync());
        Assert.Equal("Transfer", (await _db.Categories.FindAsync(moneyOut.CategoryId))!.Name);
        var view = await _fund.ViewAsync(Payday.AddDays(1));
        Assert.Equal(0m, view.Total);
        Assert.Equal(100m, Assert.Single(view.Other).Balance);
    }

    [Fact]
    public async Task Upgrade_turns_old_travel_pockets_into_other_pockets()
    {
        var pocket = await PocketAsync("Travel", PocketType.Saving, PocketPurpose.SafetyFund, 1.2m);
        // A pocket saved as Travel (purpose 2) before Travel pockets were removed.
        await _db.Database.ExecuteSqlRawAsync($"UPDATE \"PocketTerms\" SET \"Purpose\" = 2 WHERE \"AccountId\" = {pocket.AccountId}");

        await SchemaUpgrade.ApplyAsync(_db);

        Assert.Equal(PocketPurpose.Other, (await _db.PocketTerms.AsNoTracking().SingleAsync()).Purpose);
        Assert.Equal("Travel", Assert.Single((await _fund.ViewAsync(Nov6)).Other).Name);
    }

    [Fact]
    public async Task Apple_pay_taps_are_still_spending_next_to_transfers()
    {
        var uob = await BankAsync("UOB One", 20000m);
        await PayPeriodWithSpendingAsync(uob);
        var safety = await PocketAsync("Safety fund", PocketType.Saving, PocketPurpose.SafetyFund, 1.2m);
        await _transfers.RecordAsync(uob.Id, safety.AccountId, 300m, At(Payday.AddDays(2)), false, null);

        var tap = await _tx.IngestSpendAsync(300m, "SGD", "Uniqlo", "iPhone", At(Payday.AddDays(2)).AddMinutes(5),
            TransactionSource.ApplePayShortcut, null);

        Assert.False(tap.WasDuplicate);
        var b = await BudgetAsync();
        Assert.Equal(7.80m + 300m, b.Wants);
        Assert.Equal(300m, b.Savings);
    }

    [Fact]
    public async Task Planned_transfers_show_from_their_start_and_tick_off_once_per_pay_period()
    {
        var uob = await BankAsync("UOB One", 20000m);
        await PayPeriodWithSpendingAsync(uob);
        var safety = await PocketAsync("Safety fund", PocketType.Saving, PocketPurpose.SafetyFund, 1.2m);
        var boost = await PocketAsync("Boost 2", PocketType.Boost, PocketPurpose.SafetyFund, 1.3m);
        var startsOn = new DateOnly(2026, 12, 1);
        var p1 = await _fund.SavePlannedAsync(null, new PlannedSave("Safety fund", 300m, uob.Id, safety.AccountId, startsOn));
        await _fund.SavePlannedAsync(null, new PlannedSave("Boost top-up", 100m, uob.Id, boost.AccountId, startsOn));

        // The November pay period started before the plans do.
        Assert.Empty((await _fund.ForPeriodAsync(new DateOnly(2026, 11, 26))).Items);

        var plan = await _fund.ForPeriodAsync(Payday);
        Assert.Equal(400m, plan.Planned);
        Assert.Equal(0m, plan.Done);

        plan = await _fund.TickAsync(p1.Id, Payday);
        Assert.Equal(300m, plan.Done);
        Assert.True(plan.Items.Single(i => i.Id == p1.Id).Done);
        Assert.Equal(300m, (await BudgetAsync()).Savings);
        await Assert.ThrowsAsync<SavingsException>(() => _fund.TickAsync(p1.Id, Payday));

        plan = await _fund.UntickAsync(p1.Id, Payday);
        Assert.Equal(0m, plan.Done);
        Assert.Equal(0m, (await BudgetAsync()).Savings);
        Assert.Equal(0m, (await _fund.ViewAsync(Payday)).Ready);
    }

    [Fact]
    public async Task Deleting_one_side_of_a_transfer_removes_both_and_unticks_the_plan()
    {
        var uob = await BankAsync("UOB One", 20000m);
        await PayPeriodWithSpendingAsync(uob);
        var safety = await PocketAsync("Safety fund", PocketType.Saving, PocketPurpose.SafetyFund, 1.2m);
        var p = await _fund.SavePlannedAsync(null, new PlannedSave("Safety fund", 300m, uob.Id, safety.AccountId, new DateOnly(2026, 12, 1)));
        var plan = await _fund.TickAsync(p.Id, Payday);
        var outId = plan.Items.Single().TransactionId!.Value;
        var inId = (await _db.Transactions.FindAsync(outId))!.PairId!.Value;

        var moneyIn = await _db.Transactions.FindAsync(inId);
        await SavingsTransfers.RemovePairAsync(_db, moneyIn!);
        await _db.SaveChangesAsync();

        Assert.False(await _db.Transactions.AnyAsync(t => t.Id == outId || t.Id == inId));
        Assert.False((await _fund.ForPeriodAsync(Payday)).Items.Single().Done);
        Assert.Equal(0m, (await BudgetAsync()).Savings);
    }

    [Fact]
    public async Task First_boost_shows_interest_so_far_and_at_maturity()
    {
        var uob = await BankAsync("UOB One", 20000m);
        await _fund.AddPocketAsync(new PocketSave("First Boost", "GXS Bank", PocketType.Boost, PocketPurpose.SafetyFund,
            0.88m, BonusRatePct: 0.34m, StartDate: Nov6, TermMonths: 3, PromoCashback: 20m,
            FundFromAccountId: uob.Id, FundAmount: 5000m), today: Nov6);

        var onDayOne = Assert.Single((await _fund.ViewAsync(Nov6)).Safety);
        Assert.Equal(new DateOnly(2027, 2, 6), onDayOne.MaturityDate);
        Assert.Equal(1.22m, onDayOne.TotalRatePct);
        Assert.Equal(0m, onDayOne.EarnedSoFar);
        Assert.Equal(15.38m, onDayOne.InterestAtMaturity);
        Assert.Equal(5035.38m, onDayOne.TotalAtMaturity); // balance + interest + S$20 cashback
        Assert.True(onDayOne.Locked);
        Assert.Equal(92, onDayOne.DaysToMaturity);

        var tenDaysIn = Assert.Single((await _fund.ViewAsync(new DateOnly(2026, 11, 16))).Safety);
        Assert.Equal(1.67m, tenDaysIn.EarnedSoFar); // 5000 × 1.22% × 10/365

        var matured = await _fund.ViewAsync(new DateOnly(2027, 2, 7));
        Assert.False(Assert.Single(matured.Safety).Locked);
        Assert.Equal(5000m, matured.Ready);
    }

    [Fact]
    public async Task Parents_amount_adds_to_the_target()
    {
        Assert.Null((await _fund.ViewAsync(Nov6)).ParentsTarget);
        await _fund.SetTargetsAsync(12000m, 3000m);
        Assert.Equal(15000m, (await _fund.ViewAsync(Nov6)).Target);
        await _fund.SetTargetsAsync(12000m, null);
        Assert.Equal(12000m, (await _fund.ViewAsync(Nov6)).Target);
    }

    [Fact]
    public async Task Reminders_for_maturity_and_every_four_months()
    {
        var uob = await BankAsync("UOB One", 20000m);
        await _fund.AddPocketAsync(new PocketSave("First Boost", "GXS Bank", PocketType.Boost, PocketPurpose.SafetyFund,
            0.88m, BonusRatePct: 0.34m, StartDate: Nov6, TermMonths: 3, FundFromAccountId: uob.Id, FundAmount: 5000m), today: Nov6);
        var safety = await PocketAsync("Safety fund", PocketType.Saving, PocketPurpose.SafetyFund, 1.2m);

        var maturity = Assert.Single(await _fund.RemindersAsync(Nov6));
        Assert.Equal("First Boost matures on 6 Feb 2027: move it into a longer Boost Pocket or SSB.", maturity.Text);
        Assert.False(maturity.Soon);
        Assert.True((await _fund.RemindersAsync(new DateOnly(2027, 1, 25))).Single().Soon);

        // The first S$300 into the Safety fund pocket starts the 4-monthly reminder.
        await _transfers.RecordAsync(uob.Id, safety.AccountId, 300m, At(Payday), false, null);
        await _transfers.RecordAsync(uob.Id, safety.AccountId, 300m, At(Payday.AddMonths(1)), false, null);
        var repeat = Assert.Single(await _fund.RemindersAsync(Payday), r => r.Kind == "repeat");
        Assert.Equal("Move the Safety fund Saving Pocket into a new Boost Pocket.", repeat.Text);
        Assert.Equal(new DateOnly(2027, 4, 25), repeat.Due);

        await _fund.ReminderDoneAsync(repeat.ReminderId!.Value, new DateOnly(2027, 4, 26));
        Assert.Equal(new DateOnly(2027, 8, 25),
            (await _fund.RemindersAsync(new DateOnly(2027, 4, 26))).Single(r => r.Kind == "repeat").Due);
    }

    [Fact]
    public async Task Pockets_show_on_accounts_with_their_bank_and_type()
    {
        await PocketAsync("First Boost", PocketType.Boost, PocketPurpose.SafetyFund, 0.88m);
        var summary = await _accounts.SummaryAsync(false);
        Assert.Equal("GXS Bank · Boost Pocket", summary.Accounts.Single(a => a.Name == "First Boost").Pocket);
    }

    [Fact]
    public async Task Refuses_a_transfer_to_itself_or_of_nothing()
    {
        var uob = await BankAsync("UOB One", 20000m);
        await Assert.ThrowsAsync<SavingsException>(() => _transfers.RecordAsync(uob.Id, uob.Id, 10m, At(Nov6), false, null));
        var safety = await PocketAsync("Safety fund", PocketType.Saving, PocketPurpose.SafetyFund, 1.2m);
        await Assert.ThrowsAsync<SavingsException>(() => _transfers.RecordAsync(uob.Id, safety.AccountId, 0m, At(Nov6), false, null));
    }
}
