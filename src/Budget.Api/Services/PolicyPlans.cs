namespace Budget.Api.Services;

/// <summary>
/// Starting terms for known plans, copied onto a policy when it is set up. After that the
/// policy's own copy is what counts, and can be corrected on the policy screen.
/// </summary>
public static class PolicyPlans
{
    public const string HsbcWealthAccelerateName = "HSBC Life Wealth Accelerate";

    /// <summary>Terms of an HSBC Life Wealth Accelerate regular-premium policy, from the policy contract.</summary>
    public static PolicySchedule HsbcWealthAccelerate() => new(
        StartUpBonusPercent: [30, 40, 40, 40, 50],
        InitialUnitsFees: [new("Account Maintenance Fee", 1, 30, 3.4m)],
        AccumulationUnitsFees: [new("Investment Management Fee", 1, null, 1.0m)],
        AccountValueBonuses:
        [
            new("Power-up Bonus", 15, 30, 1.25m),
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
        ]);

    public const int HsbcWealthAccelerateInitialMonths = 60;
    public const int HsbcWealthAccelerateMinimumYears = 30;
}
