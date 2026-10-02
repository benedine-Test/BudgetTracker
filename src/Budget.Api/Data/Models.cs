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
    Other = 6
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
}
