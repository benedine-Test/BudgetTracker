namespace Budget.Web.Services;

/// <summary>Age on the payslip month, as CPF bands it. NoCpf = foreigner (no CPF at all).</summary>
public enum CpfAgeBand { UpTo55, Over55To60, Over60To65, Over65To70, Over70, NoCpf }

/// <summary>Self-help group fund deducted with CPF. Opt-out-able, so it's a choice, not a rule.</summary>
public enum ShgFund { None, Cdac, Ecf, Mbmf, Sinda }

public record PayslipBreakdown(decimal BasePay, decimal EmployeeCpf, decimal CpfRatePct, decimal Shg, decimal TakeHome, decimal EmployerCpf)
{
    /// <summary>What goes into the CPF accounts this month: your share plus your employer's.</summary>
    public decimal TotalCpf => EmployeeCpf + EmployerCpf;
}

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

    /// <summary>Employer share (%) on top of your pay.</summary>
    public static decimal EmployerRatePct(CpfAgeBand band) => band switch
    {
        CpfAgeBand.UpTo55 => 17m,
        CpfAgeBand.Over55To60 => 16m,
        CpfAgeBand.Over60To65 => 12.5m,
        CpfAgeBand.Over65To70 => 9m,
        CpfAgeBand.Over70 => 7.5m,
        _ => 0m,
    };

    /// <summary>
    /// The age band for pay in the month of <paramref name="payDate"/>. CPF moves you to the
    /// next band from the first day of the month after the birthday, not on the day itself.
    /// </summary>
    public static CpfAgeBand AgeBandOn(DateOnly birthDate, DateOnly payDate)
    {
        var monthStart = new DateOnly(payDate.Year, payDate.Month, 1);
        bool Past(int age) => birthDate.AddYears(age) < monthStart;
        return Past(70) ? CpfAgeBand.Over70
            : Past(65) ? CpfAgeBand.Over65To70
            : Past(60) ? CpfAgeBand.Over60To65
            : Past(55) ? CpfAgeBand.Over55To60
            : CpfAgeBand.UpTo55;
    }

    /// <summary>The next change of age band after <paramref name="today"/>, and what it changes to. Null once above 70.</summary>
    public static (DateOnly From, CpfAgeBand Band)? NextBandChange(DateOnly birthDate, DateOnly today)
    {
        foreach (var (age, band) in new[]
                 {
                     (55, CpfAgeBand.Over55To60), (60, CpfAgeBand.Over60To65),
                     (65, CpfAgeBand.Over65To70), (70, CpfAgeBand.Over70),
                 })
        {
            var birthday = birthDate.AddYears(age);
            var from = new DateOnly(birthday.Year, birthday.Month, 1).AddMonths(1);
            if (from > today) return (from, band);
        }
        return null;
    }

    /// <summary>Completed years on <paramref name="today"/>.</summary>
    public static int AgeOn(DateOnly birthDate, DateOnly today)
    {
        var age = today.Year - birthDate.Year;
        return birthDate.AddYears(age) > today ? age - 1 : age;
    }

    public static string BandName(CpfAgeBand band) => band switch
    {
        CpfAgeBand.UpTo55 => "55 and below",
        CpfAgeBand.Over55To60 => "above 55 to 60",
        CpfAgeBand.Over60To65 => "above 60 to 65",
        CpfAgeBand.Over65To70 => "above 65 to 70",
        CpfAgeBand.Over70 => "above 70",
        _ => "no CPF",
    };

    /// <summary>
    /// Total CPF for the month, employer and employee together, rounded to the nearest dollar.
    /// Nothing up to S$50; employer only up to S$500; employee share phased in up to S$750.
    /// </summary>
    public static decimal TotalCpf(decimal wage, CpfAgeBand band)
    {
        var employer = EmployerRatePct(band) / 100m;
        var employee = EmployeeRatePct(band) / 100m;
        if (employer == 0 || wage <= 50m) return 0m;
        var total = wage <= 500m ? employer * wage
            : wage <= 750m ? employer * wage + 3m * employee * (wage - 500m)
            : (employer + employee) * Math.Min(wage, OrdinaryWageCeiling);
        return Math.Round(total, 0, MidpointRounding.AwayFromZero);
    }

    /// <summary>Employer CPF: the rounded total less the employee's share.</summary>
    public static decimal EmployerCpf(decimal wage, CpfAgeBand band) => TotalCpf(wage, band) - EmployeeCpf(wage, band);

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
        if (basePay <= 0) return new(basePay, 0, EmployeeRatePct(band), 0, 0, 0);
        var cpf = EmployeeCpf(basePay, band);
        var shg = ShgContribution(basePay, fund);
        return new(basePay, cpf, EmployeeRatePct(band), shg, basePay - cpf - shg, EmployerCpf(basePay, band));
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