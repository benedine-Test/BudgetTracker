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

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Category>().HasIndex(c => c.Name).IsUnique();

        b.Entity<MerchantRule>().HasIndex(r => r.Pattern).IsUnique();

        b.Entity<Transaction>(e =>
        {
            e.Property(t => t.Amount).HasPrecision(18, 2);
            e.Property(t => t.Merchant).HasMaxLength(200);
            e.HasIndex(t => t.OccurredAtUtc);
            // Deleting an account keeps its history, just unlinked.
            e.HasOne(t => t.Account).WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Account>(e =>
        {
            e.Property(a => a.Name).HasMaxLength(100);
            e.Property(a => a.Currency).HasMaxLength(3);
            e.Property(a => a.CardNames).HasMaxLength(500);
            e.Property(a => a.AnchorBalance).HasPrecision(18, 2);
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
    }
}
