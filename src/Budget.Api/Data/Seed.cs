using Budget.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Data;

public static class Seed
{
    private static readonly (string Name, Bucket Bucket)[] Categories =
    [
        // Needs
        ("Housing", Bucket.Needs),
        ("Utilities", Bucket.Needs),
        ("Groceries", Bucket.Needs),
        ("Transport", Bucket.Needs),
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
        ("GRAB", "Transport", 50),
        ("GOJEK", "Transport", 50),
        ("TADA", "Transport", 50),
        ("BUS/MRT", "Transport", 20),
        ("SIMPLYGO", "Transport", 20),
        ("SMRT", "Transport", 30),
        ("SBS TRANSIT", "Transport", 30),
        ("SHELL", "Transport", 40),
        ("ESSO", "Transport", 40),
        ("CALTEX", "Transport", 40),
        ("SPC ", "Transport", 40),
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
        // Added after the first release, so databases seeded before it don't have it yet.
        else if (!await db.Categories.AnyAsync(c => c.Name == SharedBills.RepaymentCategory))
            db.Categories.Add(new Category { Name = SharedBills.RepaymentCategory, Bucket = Bucket.Income });

        await db.SaveChangesAsync();
    }
}
