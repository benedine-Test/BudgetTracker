using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Data;

public class BudgetDbContext(DbContextOptions<BudgetDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<MerchantRule> MerchantRules => Set<MerchantRule>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<BudgetPeriod> BudgetPeriods => Set<BudgetPeriod>();
    public DbSet<AppSettings> Settings => Set<AppSettings>();
    public DbSet<Holding> Holdings => Set<Holding>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<PolicyTerms> PolicyTerms => Set<PolicyTerms>();
    public DbSet<PolicyValuation> PolicyValuations => Set<PolicyValuation>();
    public DbSet<DismissedRecurring> DismissedRecurring => Set<DismissedRecurring>();
    public DbSet<Goal> Goals => Set<Goal>();
    public DbSet<PocketTerms> PocketTerms => Set<PocketTerms>();
    public DbSet<PlannedTransfer> PlannedTransfers => Set<PlannedTransfer>();
    public DbSet<PlannedTransferTick> PlannedTransferTicks => Set<PlannedTransferTick>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<TaxPlan> TaxPlans => Set<TaxPlan>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Category>().HasIndex(c => c.Name).IsUnique();

        b.Entity<MerchantRule>().HasIndex(r => r.Pattern).IsUnique();

        b.Entity<Transaction>(e =>
        {
            e.Property(t => t.Amount).HasPrecision(18, 2);
            e.Property(t => t.CpfContribution).HasPrecision(18, 2);
            e.Property(t => t.Merchant).HasMaxLength(200);
            e.HasIndex(t => t.OccurredAtUtc);
            // Deleting an account keeps its history, just unlinked.
            e.HasOne(t => t.Account).WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(t => t.ImportBatchId);
            e.Property(t => t.MyShare).HasPrecision(18, 2);
            e.Property(t => t.SharedWith).HasMaxLength(100);
            e.HasIndex(t => t.RepaysId);
            e.HasIndex(t => t.PairId);
            e.Ignore(t => t.CountedAmount);
        });

        b.Entity<Account>(e =>
        {
            e.Property(a => a.Name).HasMaxLength(100);
            e.Property(a => a.Currency).HasMaxLength(3);
            e.Property(a => a.CardNames).HasMaxLength(500);
            e.Property(a => a.AnchorBalance).HasPrecision(18, 2);
        });

        b.Entity<ImportBatch>(e =>
        {
            e.Property(i => i.FileName).HasMaxLength(200);
            e.Property(i => i.PreviousAnchorBalance).HasPrecision(18, 2);
            e.Property(i => i.SetAnchorBalance).HasPrecision(18, 2);
            e.HasIndex(i => i.AccountId);
        });

        b.Entity<BudgetPeriod>(e =>
        {
            e.Property(p => p.TakeHomeIncome).HasPrecision(18, 2);
            e.Property(p => p.NeedsPct).HasPrecision(5, 2);
            e.Property(p => p.WantsPct).HasPrecision(5, 2);
            e.Property(p => p.SavingsPct).HasPrecision(5, 2);
            e.HasIndex(p => p.StartDate).IsUnique();
        });

        b.Entity<AppSettings>(e =>
        {
            e.Property(s => s.NeedsPct).HasPrecision(5, 2);
            e.Property(s => s.WantsPct).HasPrecision(5, 2);
            e.Property(s => s.SavingsPct).HasPrecision(5, 2);
            e.Property(s => s.EmergencyTarget).HasPrecision(18, 2);
            e.Property(s => s.ParentsTarget).HasPrecision(18, 2);
        });

        b.Entity<Holding>(e =>
        {
            e.Property(h => h.Units).HasPrecision(18, 6);
            e.Property(h => h.AverageCost).HasPrecision(18, 6);
            e.Property(h => h.LastPrice).HasPrecision(18, 6);
            e.Property(h => h.FxToBase).HasPrecision(18, 6);
            e.Property(h => h.PriceError).HasMaxLength(300);
            e.Property(h => h.ContributionMatch).HasMaxLength(100);
            e.Property(h => h.ContributionAmount).HasPrecision(18, 2);
            e.Ignore(h => h.IsBalance);
        });

        // Linked to a holding by id only, like ImportBatch to its account; removed with the holding.
        b.Entity<PolicyTerms>(e =>
        {
            e.Property(t => t.MonthlyPremium).HasPrecision(18, 2);
            e.HasIndex(t => t.HoldingId).IsUnique();
        });

        b.Entity<PolicyValuation>(e =>
        {
            e.Property(v => v.InitialUnits).HasPrecision(18, 2);
            e.Property(v => v.AccumulationUnits).HasPrecision(18, 2);
            e.HasIndex(v => v.HoldingId);
        });

        b.Entity<DismissedRecurring>(e =>
        {
            e.Property(d => d.Pattern).HasMaxLength(100);
            e.Property(d => d.Amount).HasPrecision(18, 2);
            e.HasIndex(d => d.Pattern);
        });

        // Linked to a category by id only; a goal whose category is deleted shows as "category removed".
        b.Entity<Goal>(e =>
        {
            e.Property(g => g.Name).HasMaxLength(100);
            e.Property(g => g.Amount).HasPrecision(18, 2);
            e.Property(g => g.Reason).HasMaxLength(300);
        });

        // Savings pockets, planned transfers and reminders: linked by id only, like the tables above.
        b.Entity<PocketTerms>(e =>
        {
            e.Property(p => p.Bank).HasMaxLength(100);
            e.Property(p => p.InterestRatePct).HasPrecision(7, 4);
            e.Property(p => p.BonusRatePct).HasPrecision(7, 4);
            e.Property(p => p.PromoCashback).HasPrecision(18, 2);
            e.Property(p => p.GoalAmount).HasPrecision(18, 2);
            e.HasIndex(p => p.AccountId).IsUnique();
            e.Ignore(p => p.TotalRatePct);
        });

        b.Entity<PlannedTransfer>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(100);
            e.Property(p => p.Amount).HasPrecision(18, 2);
        });

        b.Entity<PlannedTransferTick>(e =>
            e.HasIndex(t => new { t.PlannedTransferId, t.PeriodStart }).IsUnique());

        b.Entity<Reminder>(e => e.Property(r => r.Text).HasMaxLength(300));

        b.Entity<TaxPlan>(e =>
        {
            e.HasIndex(t => t.Year).IsUnique();
            e.Property(t => t.EmploymentIncomeOverride).HasPrecision(18, 2);
            e.Property(t => t.ExpectedMonthlyPay).HasPrecision(18, 2);
            e.Property(t => t.ExpectedBonus).HasPrecision(18, 2);
            e.Property(t => t.OtherIncome).HasPrecision(18, 2);
            e.Property(t => t.SrsContributed).HasPrecision(18, 2);
            e.Property(t => t.CpfTopUpSelf).HasPrecision(18, 2);
            e.Property(t => t.CpfTopUpFamily).HasPrecision(18, 2);
            e.Property(t => t.Donations).HasPrecision(18, 2);
            e.Property(t => t.NsmanRelief).HasPrecision(18, 2);
            e.Property(t => t.OtherReliefs).HasPrecision(18, 2);
        });
    }
}
