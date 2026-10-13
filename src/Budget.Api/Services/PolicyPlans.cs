namespace Budget.Api.Services;

/// <summary>A plan's standard terms, offered when a policy is set up.</summary>
public record PolicyPlan(string Key, string Name, int InitialPeriodMonths, int MinimumInvestmentYears, int? PremiumTermYears,
    PolicySchedule Schedule);

/// <summary>
/// Starting terms for known plans, copied onto a policy when it is set up. After that the
/// policy's own copy is what counts, and can be corrected on the policy screen.
/// </summary>
public static class PolicyPlans
{
    public const string HsbcWealthAccelerateName = "HSBC Life Wealth Accelerate";
    public const string FwdInvestFirstSummitName = "FWD Invest First Summit";

    public static IReadOnlyList<PolicyPlan> All() =>
    [
        new("hsbc-wealth-accelerate", HsbcWealthAccelerateName, HsbcWealthAccelerateInitialMonths,
            HsbcWealthAccelerateMinimumYears, null, HsbcWealthAccelerate()),
        new("fwd-invest-first-summit", FwdInvestFirstSummitName, 24, 0, 30, FwdInvestFirstSummit()),
    ];

    /// <summary>Terms of an HSBC Life Wealth Accelerate regular-premium policy, from the policy contract.</summary>
    public static PolicySchedule HsbcWealthAccelerate() => new(
        StartUpBonusPercent: [30, 40, 40, 40, 50],
        InitialUnitsFees: [new("Account Maintenance Fee", 1, 30, 3.4m, Until: PolicyPeriod.MinimumInvestmentPeriod)],
        AccumulationUnitsFees: [new("Investment Management Fee", 1, null, 1.0m)],
        AccountValueBonuses:
        [
            new("Power-up Bonus", 15, 30, 1.25m, Until: PolicyPeriod.MinimumInvestmentPeriod),
            new("Loyalty Bonus", 31, null, 1.10m),
        ],
        EarlyEncashmentPercent:
        [
            100, 99, 98, 97, 96, 95, 94, 93, 92, 91,
            90, 89, 75, 68, 58, 48, 40, 32, 26, 24,
            22, 20, 19, 18, 17, 16, 15, 13, 10, 8,
        ],
        FundChargePercent: 1.30m,
        PartialWithdrawalChargePercent: 7m,
        Funds:
        [
            new("BlackRock World Healthscience Fund", "USD", 30),
            new("JPM Greater China Fund", "USD", 30),
            new("BlackRock World Mining Fund (SGD Hedged)", "SGD", 20),
            new("Franklin Technology Fund", "USD", 20),
        ],
        WithdrawalChargeUntil: PolicyPeriod.MinimumInvestmentPeriod);

    public const int HsbcWealthAccelerateInitialMonths = 60;
    public const int HsbcWealthAccelerateMinimumYears = 30;

    /// <summary>
    /// Terms of an FWD Invest First Summit policy with a 30-year premium term and an annual premium under
    /// S$12,000 (reward band 1), from the policy contract. Bonus rates are non-guaranteed; FWD can change
    /// them with 30 days' notice. The accumulation charge cap is 0.7% × 30 years × the S$3,600 annual premium
    /// committed at the start, so it needs correcting if the premium is different.
    /// </summary>
    public static PolicySchedule FwdInvestFirstSummit() => new(
        // Booster Bonus, years 1–3. FWD's illustration also showed a promotional booster, not in the contract.
        StartUpBonusPercent: [30, 30, 30],
        InitialUnitsFees: [new("Initial account charge", 1, 30, 3.95m, Until: PolicyPeriod.PremiumTerm)],
        AccumulationUnitsFees: [new("Accumulation account charge", 1, null, 1.5m, CapPerYear: 756m)],
        AccountValueBonuses:
        [
            new("Loyalty Bonus", 4, 10, 0.6m, Timing: BonusTiming.PolicyYearEnd, Base: BonusBase.AccumulationAccount,
                Adjustment: BonusAdjustment.PremiumsPaidLessWithdrawals),
            new("Loyalty Bonus", 11, 30, 1.5m, Until: PolicyPeriod.PremiumTerm, Timing: BonusTiming.PolicyYearEnd,
                Base: BonusBase.AccumulationAccount, Adjustment: BonusAdjustment.PremiumsPaidLessWithdrawals),
            new("Perpetual Bonus", 31, null, 1.0m, Timing: BonusTiming.PolicyYearEnd, Base: BonusBase.AccumulationAccount),
        ],
        EarlyEncashmentPercent:
        [
            100, 100, 99, 99, 99, 99, 98, 97, 96, 95,
            91, 90, 88, 87, 78, 73, 69, 67, 63, 54,
            48, 46, 45, 44, 43, 42, 39, 31, 24, 15,
        ],
        FundChargePercent: 1.30m,
        PartialWithdrawalChargePercent: 0m,
        Funds:
        [
            new("Allianz Income and Growth Fund SGDH Dis", "SGD", 50),
            new("Fundsmith Equity Fund EUR Acc", "EUR", 50),
        ],
        PremiumCharges:
        [
            new("Premium shortfall charge", PremiumChargeKind.Shortfall, 9m, AfterMissedMonths: 24, Until: PolicyPeriod.PremiumTerm),
            new("Premium reduction charge", PremiumChargeKind.Reduction, 9m, FromYear: 3, ToYear: 8),
        ],
        WithdrawalsFromMonth: 25,
        TopUpChargePercent: 5m);
}
