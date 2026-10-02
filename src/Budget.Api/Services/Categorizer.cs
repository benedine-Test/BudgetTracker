using Budget.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Services;

public class Categorizer(BudgetDbContext db)
{
    /// <summary>Returns the category id of the first matching rule, or null.</summary>
    public async Task<int?> MatchAsync(string merchant, CancellationToken ct = default)
    {
        var norm = MerchantText.Normalize(merchant);
        if (norm.Length == 0) return null;

        var rules = await db.MerchantRules.AsNoTracking().ToListAsync(ct);
        return rules
            .OrderBy(r => r.Priority)
            .ThenByDescending(r => r.Pattern.Length)
            .FirstOrDefault(r => norm.Contains(r.Pattern, StringComparison.Ordinal))
            ?.CategoryId;
    }

    /// <summary>
    /// Save a user correction as a rule so the same merchant is right next time.
    /// User rules get priority 5 so they beat the seeded ones.
    /// </summary>
    public async Task<MerchantRule> LearnAsync(string pattern, int categoryId, CancellationToken ct = default)
    {
        pattern = MerchantText.Normalize(pattern);
        var existing = await db.MerchantRules.FirstOrDefaultAsync(r => r.Pattern == pattern, ct);
        if (existing is null)
        {
            existing = new MerchantRule { Pattern = pattern };
            db.MerchantRules.Add(existing);
        }

        existing.CategoryId = categoryId;
        existing.Priority = 5;
        existing.LearnedFromUser = true;
        return existing;
    }
}
