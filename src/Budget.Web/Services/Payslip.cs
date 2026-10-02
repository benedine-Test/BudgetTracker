namespace Budget.Web.Services;

/// <summary>Age on the payslip month, as CPF bands it. NoCpf = foreigner (no CPF at all).</summary>
public enum CpfAgeBand { UpTo55, Over55To60, Over60To65, Over65To70, Over70, NoCpf }

/// <summary>Self-help group fund deducted with CPF. Opt-out-able, so it's a choice, not a rule.</summary>
public enum ShgFund { None, Cdac, Ecf, Mbmf, Sinda }

public record PayslipBreakdown(decimal BasePay, decimal EmployeeCpf, decimal CpfRatePct, decimal Shg, decimal TakeHome);

/// <summary>
/// Works out take-home pay from monthly base pay, Singapore rules from 1 Jan 2026.
/// Covers Singapore citizens and PRs from their third year (full rates); first- and
/// second-year PRs pay graduated rates that aren't modelled here.
/// Runs on the phone so the figures update as you type.
/// </summary>
public static class Payslip
{
    /// <summary>CPF is only charged on ordinary wages up to this each month.</summary>
    public const decimal OrdinaryWageCeiling = 8000m;

    /// <summary>Employee share (%) for wages above S$750.</summary>
    public static decimal EmployeeRatePct(CpfAgeBand band) => band switch
    {
        CpfAgeBand.UpTo55 => 20m,
        CpfAgeBand.Over55To60 => 18m,
        CpfAgeBand.Over60To65 => 12.5m,
        CpfAgeBand.Over65To70 => 7.5m,
        CpfAgeBand.Over70 => 5m,
        _ => 0m,
    };

    /// <summary>
    /// Employee CPF for the month. Nothing up to S$500; phased in between S$500 and S$750
    /// (at 3× the rate on the excess); full rate above, capped at the wage ceiling.
    /// CPF drops the cents from the employee share.
    /// </summary>
    public static decimal EmployeeCpf(decimal wage, CpfAgeBand band)
    {
        var rate = EmployeeRatePct(band) / 100m;
        if (rate == 0 || wage <= 500m) return 0m;
        var share = wage <= 750m
            ? 3m * rate * (wage - 500m)
            : rate * Math.Min(wage, OrdinaryWageCeiling);
        return Math.Floor(share);
    }

    /// <summary>Monthly SHG deduction for the fund, banded on total wages.</summary>
    public static decimal ShgContribution(decimal wage, ShgFund fund) => fund switch
    {
        ShgFund.Cdac => wage switch
        {
            <= 2000m => 0.50m,
            <= 3500m => 1.00m,
            <= 5000m => 1.50m,
            <= 7500m => 2.00m,
            _ => 3.00m,
        },
        ShgFund.Ecf => wage switch
        {
            <= 1000m => 2m,
            <= 1500m => 4m,
            <= 2500m => 6m,
            <= 4000m => 9m,
            <= 7000m => 12m,
            <= 10000m => 16m,
            _ => 20m,
        },
        ShgFund.Mbmf => wage switch
        {
            <= 1000m => 3.00m,
            <= 2000m => 4.50m,
            <= 3000m => 6.50m,
            <= 4000m => 15.00m,
            <= 6000m => 19.50m,
            <= 8000m => 22.00m,
            <= 10000m => 24.00m,
            _ => 26.00m,
        },
        ShgFund.Sinda => wage switch
        {
            <= 1000m => 1m,
            <= 1500m => 3m,
            <= 2500m => 5m,
            <= 4500m => 7m,
            <= 7500m => 9m,
            <= 10000m => 12m,
            <= 15000m => 18m,
            _ => 30m,
        },
        _ => 0m,
    };

    public static PayslipBreakdown From(decimal basePay, CpfAgeBand band, ShgFund fund)
    {
        if (basePay <= 0) return new(basePay, 0, EmployeeRatePct(band), 0, 0);
        var cpf = EmployeeCpf(basePay, band);
        var shg = ShgContribution(basePay, fund);
        return new(basePay, cpf, EmployeeRatePct(band), shg, basePay - cpf - shg);
    }

    public static string ShgName(ShgFund fund) => fund switch
    {
        ShgFund.Cdac => "CDAC",
        ShgFund.Ecf => "ECF",
        ShgFund.Mbmf => "MBMF",
        ShgFund.Sinda => "SINDA",
        _ => "None",
    };
}
