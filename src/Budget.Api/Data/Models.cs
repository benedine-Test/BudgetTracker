namespace Budget.Api.Data;

/// <summary>Which 50/30/20 bucket a category rolls up into.</summary>
public enum Bucket
{
    Needs = 0,     // bills, groceries, transport, insurance
    Wants = 1,     // dining, shopping, entertainment
    Savings = 2,   // investments, emergency fund, extra loan repayment
    Income = 3,    // salary, bonus, refunds — never counted as spend
    Transfer = 4   // moving money between own accounts — ignored in budgets
}

public enum TransactionSource
{
    Manual = 0,
    ApplePayShortcut = 1,
    EmailAlert = 2,
    StatementImport = 3,
    AndroidNotification = 4
}

public enum AccountKind
{
    Bank = 0,        // savings / current account
    CreditCard = 1,  // balance goes negative as you spend: that's what you owe
    Cash = 2         // wallet, prepaid
}

/// <summary>
/// A bank account, card or wallet. Its balance is a known starting point (what the bank
/// showed at <see cref="BalanceAsOfUtc"/>) plus every linked transaction after it, so a
/// missed transaction shows up as a gap when you compare against the bank, and importing
/// the statement fills it.
/// </summary>
public class Account
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public AccountKind Kind { get; set; }
    public string Currency { get; set; } = "SGD";

    /// <summary>What the bank said the balance was at <see cref="BalanceAsOfUtc"/>. Negative on a card = owed.</summary>
    public decimal AnchorBalance { get; set; }
    public DateTime BalanceAsOfUtc { get; set; }

    /// <summary>
    /// Comma-separated card names as Apple Pay / bank alerts report them ("DBS Altitude, PayLah").
    /// Taps on a matching card are filed under this account automatically.
    /// </summary>
    public string? CardNames { get; set; }

    public bool IsArchived { get; set; }
    public DateTime? LastImportAtUtc { get; set; }

    /// <summary>
    /// Credit cards only: the day of the month the statement closes (1–31; short months use their
    /// last day). Spending up to and including that day is on that statement. Null = not tracked.
    /// Separate from the budget, which runs payday to payday.
    /// </summary>
    public int? StatementDay { get; set; }
}

/// <summary>
/// One statement import, kept so it can be undone: the entries it added carry its id, and it
/// remembers which existing entries it filed and the balance it replaced.
/// </summary>
public class ImportBatch
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? FileName { get; set; }
    public int Added { get; set; }
    /// <summary>Existing entries this import filed under the account (comma-separated ids), unfiled again on undo.</summary>
    public string? LinkedIds { get; set; }
    /// <summary>Set when the import reset the balance: what it was before, restored on undo.</summary>
    public bool ResetBalance { get; set; }
    public decimal PreviousAnchorBalance { get; set; }
    public DateTime PreviousBalanceAsOfUtc { get; set; }
    /// <summary>The balance date this import set; if the balance was changed again since, undo leaves it.</summary>
    public DateTime? SetBalanceAsOfUtc { get; set; }
    public decimal? SetAnchorBalance { get; set; }
    /// <summary>
    /// Entries on other accounts this import re-filed as Transfer (the other side of a card bill payment),
    /// as "id:previousCategoryId" pairs, previous empty for none. Put back on undo.
    /// </summary>
    public string? Recategorised { get; set; }
    public DateTime? PreviousLastImportAtUtc { get; set; }
    public DateTime? UndoneAtUtc { get; set; }
}

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public Bucket Bucket { get; set; }
    public bool IsArchived { get; set; }
}

/// <summary>
/// "If merchant text contains Pattern, assign Category". Lower Priority wins,
/// so put specific patterns (GRABFOOD) ahead of general ones (GRAB).
/// </summary>
public class MerchantRule
{
    public int Id { get; set; }
    public string Pattern { get; set; } = "";
    public int CategoryId { get; set; }
    public Category? Category { get; set; }
    public int Priority { get; set; } = 100;
    public bool LearnedFromUser { get; set; }
}

public class Transaction
{
    public int Id { get; set; }

    /// <summary>Stored in UTC. SQLite can't compare DateTimeOffset in SQL, so we keep plain UTC DateTime.</summary>
    public DateTime OccurredAtUtc { get; set; }

    /// <summary>Always positive. Direction comes from IsIncome.</summary>
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "SGD";
    public bool IsIncome { get; set; }

    public string Merchant { get; set; } = "";
    public string? CardName { get; set; }
    public string? Notes { get; set; }

    /// <summary>Which account the money moved in. Null = not known yet (doesn't affect any balance).</summary>
    public int? AccountId { get; set; }
    public Account? Account { get; set; }

    /// <summary>The statement import that added this entry, if any (see ImportBatch).</summary>
    public int? ImportBatchId { get; set; }

    public TransactionSource Source { get; set; }
    /// <summary>Other sources that reported the same transaction and were merged into this one.</summary>
    public string? MergedSources { get; set; }

    public int? CategoryId { get; set; }
    public Category? Category { get; set; }
    /// <summary>True once the user has confirmed or corrected the category.</summary>
    public bool CategoryConfirmed { get; set; }

    /// <summary>
    /// On a salary worked out from base pay: the CPF paid on it, employee and employer together.
    /// It is added to the CPF holdings (see Portfolio.SalaryCpf); deleting the salary takes it back out.
    /// </summary>
    public decimal? CpfContribution { get; set; }
    /// <summary>
    /// The other side of a move between your own accounts recorded by <see cref="Services.SavingsTransfers"/>
    /// (money out of one, money into the other). Deleting either side deletes both.
    /// </summary>
    public int? PairId { get; set; }
    /// <summary>
    /// A bill paid for others too: the part that was yours. Only this counts toward the budget;
    /// the rest is owed back until repayments (<see cref="RepaysId"/>) cover it or it's written off.
    /// Null = all of it was yours.
    /// </summary>
    public decimal? MyShare { get; set; }
    /// <summary>Who owes the rest of a shared bill ("Alex", "Team lunch"). Optional.</summary>
    public string? SharedWith { get; set; }
    /// <summary>On money in: the shared bill this pays back.</summary>
    public int? RepaysId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>What this entry costs you: your share of a shared bill, else all of it.</summary>
    public decimal CountedAmount => MyShare ?? Amount;
}

/// <summary>
/// One budget per pay period. Created when salary is recorded, using the split
/// percentages from Settings at that moment (so changing settings later doesn't
/// rewrite history).
/// </summary>
public class BudgetPeriod
{
    public int Id { get; set; }
    public DateOnly StartDate { get; set; }   // inclusive, local (Asia/Singapore)
    public DateOnly EndDate { get; set; }     // exclusive; pulled earlier if next salary lands early
    /// <summary>The payday this cycle runs up to, never trimmed. Identifies "the same cycle" for a second salary credit.</summary>
    public DateOnly CycleEnd { get; set; }
    public decimal TakeHomeIncome { get; set; }
    public decimal NeedsPct { get; set; }
    public decimal WantsPct { get; set; }
    public decimal SavingsPct { get; set; }

    public decimal NeedsBudget => Math.Round(TakeHomeIncome * NeedsPct / 100m, 2);
    public decimal WantsBudget => Math.Round(TakeHomeIncome * WantsPct / 100m, 2);
    public decimal SavingsBudget => Math.Round(TakeHomeIncome * SavingsPct / 100m, 2);
}

/// <summary>Single-row settings table.</summary>
public class AppSettings
{
    public int Id { get; set; } = 1;
    /// <summary>Day of month salary usually lands. Budget periods run payday → next payday.</summary>
    public int PayDay { get; set; } = 25;
    public decimal NeedsPct { get; set; } = 50;
    public decimal WantsPct { get; set; } = 30;
    public decimal SavingsPct { get; set; } = 20;
    public string TimeZoneId { get; set; } = "Asia/Singapore";
    public string BaseCurrency { get; set; } = "SGD";
    /// <summary>From the Profile page. Sets the CPF age band for each salary (see Payslip.AgeBandOn).</summary>
    public DateOnly? BirthDate { get; set; }
    /// <summary>Safety Fund target: the emergency part.</summary>
    public decimal EmergencyTarget { get; set; } = 12000m;
    /// <summary>Safety Fund target: the rainy-day money kept for parents. Null = not set yet.</summary>
    public decimal? ParentsTarget { get; set; }
}

public enum AssetClass
{
    Stock = 0,
    Etf = 1,
    Bond = 2,      // incl. SSB, T-bills
    Crypto = 3,
    Cash = 4,      // fixed deposits, HYSA
    Cpf = 5,       // OA / SA / MA balances
    Other = 6,
    Policy = 7     // investment-linked insurance (ILP): premiums paid vs the insurer's stated value
}

public class Holding
{
    public int Id { get; set; }
    public string Symbol { get; set; } = "";       // e.g. "ES3.SI", "VWRA.L", "CPF-OA"
    public string Name { get; set; } = "";
    public AssetClass AssetClass { get; set; }
    public string? Platform { get; set; }           // e.g. "IBKR", "Endowus", "CPF"
    public decimal Units { get; set; }
    public decimal AverageCost { get; set; }        // per unit, in Currency
    public string Currency { get; set; } = "SGD";
    public decimal? LastPrice { get; set; }         // per unit, in Currency
    public DateTime? LastPriceAtUtc { get; set; }
    /// <summary>FX rate Currency → base currency. 1 for SGD holdings.</summary>
    public decimal FxToBase { get; set; } = 1m;

    /// <summary>Fetch the price by <see cref="Symbol"/> (a Yahoo Finance ticker) instead of typing it.</summary>
    public bool AutoPrice { get; set; }
    /// <summary>Why the last automatic price fetch failed, shown until one succeeds.</summary>
    public string? PriceError { get; set; }

    /// <summary>
    /// When <see cref="Units"/> and <see cref="AverageCost"/> were last entered. Contributions
    /// matched after this are added on top (see <see cref="ContributionMatch"/>); earlier ones
    /// are assumed to be in the figures already.
    /// </summary>
    public DateTime? FiguresAsOfUtc { get; set; }

    /// <summary>
    /// Text a bank entry contains when it pays into this holding ("FWD" for a monthly GIRO
    /// premium). Matching spends count as money put in. Null = no regular contribution.
    /// </summary>
    public string? ContributionMatch { get; set; }
    /// <summary>Only entries of exactly this amount match, when set.</summary>
    public decimal? ContributionAmount { get; set; }
    /// <summary>Only entries from this account match, when set.</summary>
    public int? ContributionAccountId { get; set; }

    /// <summary>
    /// Units = 1 and a typed-in price means the price is the balance (CPF, cash, an ILP's value):
    /// entering a new balance takes in every contribution made before it.
    /// </summary>
    public bool IsBalance => !AutoPrice && Units == 1;
}

/// <summary>
/// The contract terms of an investment-linked policy (ILP), attached to its <see cref="Holding"/>.
/// The fixed facts are columns; the fee, bonus and surrender-charge tables are kept as data in
/// <see cref="ScheduleJson"/> (a <see cref="PolicySchedule"/>) so a wrong figure can be corrected
/// without touching the maths.
/// </summary>
public class PolicyTerms
{
    public int Id { get; set; }
    public int HoldingId { get; set; }
    /// <summary>Day one of policy year 1. Each later policy year starts on this date's anniversary.</summary>
    public DateOnly CommencementDate { get; set; }
    /// <summary>Monthly regular premium, due on the commencement date's day of the month.</summary>
    public decimal MonthlyPremium { get; set; }
    /// <summary>Initial Contribution Period: premiums in these first months go to the Initial Units Account.</summary>
    public int InitialPeriodMonths { get; set; }
    /// <summary>Minimum Investment Period, in policy years. 0 = the plan has none.</summary>
    public int MinimumInvestmentYears { get; set; }
    /// <summary>Years premiums are paid for; none are due after it. Null = beyond any projection (e.g. to age 99).</summary>
    public int? PremiumTermYears { get; set; }
    public string ScheduleJson { get; set; } = "{}";
}

/// <summary>Account values as an insurer's statement gave them on a date. The app never estimates these.</summary>
public class PolicyValuation
{
    public int Id { get; set; }
    public int HoldingId { get; set; }
    public DateOnly AsOf { get; set; }
    /// <summary>Initial Units Account value.</summary>
    public decimal InitialUnits { get; set; }
    /// <summary>Accumulation Units Account value.</summary>
    public decimal AccumulationUnits { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A detected subscription marked "not a subscription": charges from this merchant pattern at about
/// this amount (see Recurring.Similar) are no longer offered. Removing the row brings it back.
/// </summary>
public class DismissedRecurring
{
    public int Id { get; set; }
    public string Pattern { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public enum GoalKind
{
    /// <summary>Spend at most Amount on a category (or a whole bucket) each pay period.</summary>
    Limit = 0,
    /// <summary>Put at least Amount into Savings each pay period.</summary>
    Save = 1
}

/// <summary>
/// A per-pay-period target the person chose, by hand or from a suggestion on the Ask screen.
/// A Limit goal covers <see cref="CategoryId"/> when set, else the whole <see cref="Bucket"/>.
/// </summary>
public class Goal
{
    public int Id { get; set; }
    public GoalKind Kind { get; set; }
    public string Name { get; set; } = "";
    public int? CategoryId { get; set; }
    public Bucket? Bucket { get; set; }
    /// <summary>The limit, or the amount to save, per pay period.</summary>
    public decimal Amount { get; set; }
    /// <summary>Why: "So I can send my parents $400 a month".</summary>
    public string? Reason { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public enum PocketType
{
    /// <summary>The bank's main account.</summary>
    Main = 0,
    /// <summary>Savings you can take out any time.</summary>
    Saving = 1,
    /// <summary>Locked for a fixed term at a higher rate.</summary>
    Boost = 2
}

/// <summary>What a pocket's money is for. Only Safety Fund pockets count toward the Safety Fund.</summary>
public enum PocketPurpose
{
    Other = 0,
    SafetyFund = 1,
    Travel = 2
}

/// <summary>
/// A savings pocket (GXS Main Account, Saving Pocket, Boost Pocket) is an ordinary <see cref="Account"/>,
/// so its balance, entries and the bank total work as for any account. This row holds its pocket details.
/// Linked to the account by id only, like PolicyTerms to its holding; removed with the account.
/// </summary>
public class PocketTerms
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    /// <summary>"GXS Bank".</summary>
    public string Bank { get; set; } = "";
    public PocketType Type { get; set; }
    public PocketPurpose Purpose { get; set; }
    /// <summary>Base interest, % a year.</summary>
    public decimal InterestRatePct { get; set; }
    /// <summary>Boost Pockets: bonus interest on top of the base, % a year.</summary>
    public decimal? BonusRatePct { get; set; }
    /// <summary>When interest starts (a Boost Pocket's start date).</summary>
    public DateOnly? StartDate { get; set; }
    public int? TermMonths { get; set; }
    /// <summary>Boost Pockets: the day it matures. Start + term by default; can be corrected.</summary>
    public DateOnly? MaturityDate { get; set; }
    /// <summary>A promo cash amount paid at maturity.</summary>
    public decimal? PromoCashback { get; set; }
    /// <summary>A pocket's own goal (the Travel pocket's). Null = none.</summary>
    public decimal? GoalAmount { get; set; }

    public decimal TotalRatePct => InterestRatePct + (BonusRatePct ?? 0);
}

/// <summary>A move to make every pay period ("S$300 to Safety fund"), ticked off once done.</summary>
public class PlannedTransfer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }
    public int FromAccountId { get; set; }
    public int ToAccountId { get; set; }
    /// <summary>Shown in pay periods that start on or after this day.</summary>
    public DateOnly StartsOn { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>A planned transfer done in one pay period, with the money-out entry it recorded.</summary>
public class PlannedTransferTick
{
    public int Id { get; set; }
    public int PlannedTransferId { get; set; }
    /// <summary>Start of the pay period it was done for.</summary>
    public DateOnly PeriodStart { get; set; }
    public int TransactionId { get; set; }
    public DateTime DoneAtUtc { get; set; }
}

/// <summary>A dashboard reminder that comes back every <see cref="RepeatMonths"/> once marked done.</summary>
public class Reminder
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public DateOnly NextDue { get; set; }
    public int RepeatMonths { get; set; }
    /// <summary>The pocket it is about, if any.</summary>
    public int? AccountId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// What you've told the app about one income year's tax: the things it can't see in your entries.
/// One row per calendar year of income (2026 income = YA2027). Salary comes from your entries unless
/// <see cref="EmploymentIncomeOverride"/> is set (e.g. from the IR8A once your employer sends it).
/// </summary>
public class TaxPlan
{
    public int Id { get; set; }
    public int Year { get; set; }
    /// <summary>The year's employment income before CPF, typed in. Null = worked out from salary entries.</summary>
    public decimal? EmploymentIncomeOverride { get; set; }
    /// <summary>Monthly base pay for the months not paid yet. Null = the same as the latest salary.</summary>
    public decimal? ExpectedMonthlyPay { get; set; }
    /// <summary>Bonus and AWS still to come this year, before CPF.</summary>
    public decimal ExpectedBonus { get; set; }
    /// <summary>Rent, freelance and other taxable income, after expenses.</summary>
    public decimal OtherIncome { get; set; }
    public decimal SrsContributed { get; set; }
    public decimal CpfTopUpSelf { get; set; }
    public decimal CpfTopUpFamily { get; set; }
    public decimal Donations { get; set; }
    public int ParentsLivingWith { get; set; }
    public int ParentsNotLivingWith { get; set; }
    public decimal NsmanRelief { get; set; }
    public decimal OtherReliefs { get; set; }
    /// <summary>Not a citizen or PR: no CPF, higher SRS cap.</summary>
    public bool Foreigner { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
