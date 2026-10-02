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
    public DbSet<RecurringItem> RecurringItems => Set<RecurringItem>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Category>().HasIndex(c => c.Name).IsUnique();

        b.Entity<MerchantRule>().HasIndex(r => r.Pattern).IsUnique();

        b.Entity<Transaction>(e =>
        {
            e.Property(t => t.Amount).HasPrecision(18, 2);
            e.Property(t => t.Merchant).HasMaxLength(200);
            e.HasIndex(t => t.OccurredAtUtc);
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
        });

        b.Entity<Holding>(e =>
        {
            e.Property(h => h.Units).HasPrecision(18, 6);
            e.Property(h => h.AverageCost).HasPrecision(18, 6);
            e.Property(h => h.LastPrice).HasPrecision(18, 6);
            e.Property(h => h.FxToBase).HasPrecision(18, 6);
        });

        b.Entity<RecurringItem>(e =>
        {
            e.Property(r => r.Amount).HasPrecision(18, 2);
            e.Property(r => r.Name).HasMaxLength(200);
            e.HasIndex(r => r.NextDueDate);
        });
    }
}
