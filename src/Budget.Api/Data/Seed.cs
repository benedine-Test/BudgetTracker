using Budget.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Data;

public static class Seed
{
    /// <summary>Bus, MRT/LRT and other fares on the card ("BUS/MRT", SimplyGo, SBS Transit...).</summary>
    public const string PublicTransport = "Bus & Train";
    /// <summary>Taxis, Grab/Gojek rides and running your own car (fuel).</summary>
    public const string PrivateTransport = "Taxi & Car";
    /// <summary>The single category both of the above replaced.</summary>
    private const string OldTransport = "Transport";

    private static readonly (string Name, Bucket Bucket)[] Categories =
    [
        // Needs
        ("Housing", Bucket.Needs),
        ("Utilities", Bucket.Needs),
        ("Groceries", Bucket.Needs),
        (PublicTransport, Bucket.Needs),
        (PrivateTransport, Bucket.Needs),
        ("Telco & Internet", Bucket.Needs),
        ("Insurance", Bucket.Needs),
        ("Healthcare", Bucket.Needs),
        ("Childcare & Education", Bucket.Needs),
        ("Loan Repayment", Bucket.Needs),
        // Wants
        ("Dining & Food Delivery", Bucket.Wants),
        ("Shopping", Bucket.Wants),
        ("Entertainment", Bucket.Wants),
        ("Subscriptions", Bucket.Wants),
        ("Travel", Bucket.Wants),
        ("Personal Care", Bucket.Wants),
        ("Gifts & Donations", Bucket.Wants),
        // Savings
        ("Investments", Bucket.Savings),
        ("Emergency Fund", Bucket.Savings),
        ("Savings Goals", Bucket.Savings),
        // Income / transfers
        ("Salary", Bucket.Income),
        ("Other Income", Bucket.Income),
        ("Refund", Bucket.Income),
        ("Paid Back", Bucket.Income), // a friend repaying their part of a bill you paid
        ("Transfer", Bucket.Transfer),
    ];

    // Upper-case "contains" patterns for common Singapore merchants.
    // Lower priority number = checked first. Specific before general.
    private static readonly (string Pattern, string Category, int Priority)[] Rules =
    [
        ("GRABFOOD", "Dining & Food Delivery", 10),
        ("GRAB FOOD", "Dining & Food Delivery", 10),
        ("GRABMART", "Groceries", 10),
        ("FOODPANDA", "Dining & Food Delivery", 20),
        ("DELIVEROO", "Dining & Food Delivery", 20),
        ("GRAB", PrivateTransport, 50),
        ("GOJEK", PrivateTransport, 50),
        ("TADA", PrivateTransport, 50),
        ("COMFORTDELGRO", PrivateTransport, 30),
        ("BUS/MRT", PublicTransport, 20),
        ("SIMPLYGO", PublicTransport, 20),
        ("TRANSITLINK", PublicTransport, 20),
        ("SMRT", PublicTransport, 30),
        ("SBS TRANSIT", PublicTransport, 30),
        ("SHELL", PrivateTransport, 40),
        ("ESSO", PrivateTransport, 40),
        ("CALTEX", PrivateTransport, 40),
        ("SPC ", PrivateTransport, 40),
        ("FAIRPRICE", "Groceries", 20),
        ("NTUC", "Groceries", 30),
        ("COLD STORAGE", "Groceries", 20),
        ("SHENG SIONG", "Groceries", 20),
        ("GIANT", "Groceries", 40),
        ("DON DON DONKI", "Groceries", 20),
        ("SP SERVICES", "Utilities", 20),
        ("SP DIGITAL", "Utilities", 20),
        ("SP GROUP", "Utilities", 20),
        ("TOWN COUNCIL", "Housing", 20),
        ("SINGTEL", "Telco & Internet", 20),
        ("STARHUB", "Telco & Internet", 20),
        ("CIRCLES", "Telco & Internet", 30),
        ("SIMBA", "Telco & Internet", 30),
        ("NETFLIX", "Subscriptions", 20),
        ("SPOTIFY", "Subscriptions", 20),
        ("DISNEY", "Subscriptions", 30),
        ("APPLE.COM/BILL", "Subscriptions", 20),
        ("YOUTUBE", "Subscriptions", 30),
        ("SHOPEE", "Shopping", 20),
        ("LAZADA", "Shopping", 20),
        ("AMAZON", "Shopping", 30),
        ("TAOBAO", "Shopping", 30),
        ("UNIQLO", "Shopping", 30),
        ("IKEA", "Shopping", 30),
        ("STARBUCKS", "Dining & Food Delivery", 30),
        ("MCDONALD", "Dining & Food Delivery", 30),
        ("TOAST BOX", "Dining & Food Delivery", 30),
        ("YA KUN", "Dining & Food Delivery", 30),
        ("KOPITIAM", "Dining & Food Delivery", 30),
        ("KOUFU", "Dining & Food Delivery", 30),
        ("GUARDIAN", "Personal Care", 30),
        ("WATSONS", "Personal Care", 30),
        ("GOLDEN VILLAGE", "Entertainment", 30),
        ("SHAW THEATRES", "Entertainment", 30),
        ("CATHAY", "Entertainment", 40),
        ("SINGAPORE AIRLINES", "Travel", 30),
        ("SCOOT", "Travel", 30),
        ("AGODA", "Travel", 30),
        ("BOOKING.COM", "Travel", 30),
        ("KLOOK", "Travel", 30),
        ("POLYCLINIC", "Healthcare", 30),
        ("CLINIC", "Healthcare", 60),
        ("PHARMACY", "Healthcare", 60),
    ];

    public static async Task EnsureSeededAsync(BudgetDbContext db)
    {
        if (!await db.Settings.AnyAsync())
            db.Settings.Add(new AppSettings());

        if (!await db.Categories.AnyAsync())
        {
            db.Categories.AddRange(Categories.Select(c => new Category { Name = c.Name, Bucket = c.Bucket }));
            await db.SaveChangesAsync();

            var byName = await db.Categories.ToDictionaryAsync(c => c.Name);
            db.MerchantRules.AddRange(Rules.Select(r => new MerchantRule
            {
                Pattern = r.Pattern,
                CategoryId = byName[r.Category].Id,
                Priority = r.Priority
            }));
        }
        else
        {
            // Added after the first release, so databases seeded before it don't have it yet.
            if (!await db.Categories.AnyAsync(c => c.Name == SharedBills.RepaymentCategory))
                db.Categories.Add(new Category { Name = SharedBills.RepaymentCategory, Bucket = Bucket.Income });

            await SplitTransportAsync(db);
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Databases from before the split have one "Transport" category. It's renamed to
    /// <see cref="PrivateTransport"/> (so its id, and anything pointing at it, stays valid), and
    /// the bus/train rules and spending move to a new <see cref="PublicTransport"/> category.
    /// </summary>
    private static async Task SplitTransportAsync(BudgetDbContext db)
    {
        var old = await db.Categories.FirstOrDefaultAsync(c => c.Name == OldTransport);
        if (old is null || await db.Categories.AnyAsync(c => c.Name == PublicTransport)) return;

        old.Name = PrivateTransport;
        var transit = new Category { Name = PublicTransport, Bucket = old.Bucket, IsArchived = old.IsArchived };
        db.Categories.Add(transit);
        await db.SaveChangesAsync();

        foreach (var rule in await db.MerchantRules.Where(r => r.CategoryId == old.Id).ToListAsync())
            if (MerchantText.IsTransit(rule.Pattern))
                rule.CategoryId = transit.Id;

        // A seeded rule the old database never had.
        if (!await db.MerchantRules.AnyAsync(r => r.Pattern == "TRANSITLINK"))
            db.MerchantRules.Add(new MerchantRule { Pattern = "TRANSITLINK", CategoryId = transit.Id, Priority = 20 });
        if (!await db.MerchantRules.AnyAsync(r => r.Pattern == "COMFORTDELGRO"))
            db.MerchantRules.Add(new MerchantRule { Pattern = "COMFORTDELGRO", CategoryId = old.Id, Priority = 30 });

        foreach (var tx in await db.Transactions.Where(t => t.CategoryId == old.Id).ToListAsync())
            if (MerchantText.IsTransit(tx.Merchant))
                tx.CategoryId = transit.Id;
    }
}
