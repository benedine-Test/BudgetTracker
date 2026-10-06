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
    StatementImport = 3
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

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
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
