using System.Text.RegularExpressions;
using Budget.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Budget.Api.Services;

/// <summary>Thrown when a card payment can't be logged; the message is for the person.</summary>
public class CardPaymentException(string message) : Exception(message);

/// <summary>
/// A card bill paid from a bank account is two entries: money out of the bank, money into the card.
/// The card's statement only shows its side weeks later, so until then the card still looks owed in
/// full. This logs the card side from the bank side, so what's owed drops the day the bill is paid.
/// When the card statement comes in, its payment line matches the logged entry instead of doubling it.
/// </summary>
public partial class CardPayments(BudgetDbContext db, Clock clock)
{
    /// <summary>The card-side entry already in the app for a payment out of the bank, if any.</summary>
    public async Task<Transaction?> OnCardAsync(Transaction paid, CancellationToken ct = default)
    {
        var cardIds = await db.Accounts.AsNoTracking().Where(a => a.Kind == AccountKind.CreditCard)
            .Select(a => a.Id).ToListAsync(ct);
        if (cardIds.Count == 0) return null;

        var day = clock.ToLocalDate(paid.OccurredAtUtc);
        var from = clock.LocalDateStartToUtc(day.AddDays(-StatementMatcher.DaysBefore));
        var to = clock.LocalDateStartToUtc(day.AddDays(StatementMatcher.DaysBefore + 1));
        // Amount is checked after loading: SQLite can't compare decimals in SQL.
        var candidates = await db.Transactions.Include(t => t.Account)
            .Where(t => t.Id != paid.Id && t.AccountId != null && cardIds.Contains(t.AccountId.Value) && t.IsIncome
                        && t.OccurredAtUtc >= from && t.OccurredAtUtc < to)
            .ToListAsync(ct);
        return candidates.Where(t => t.Amount == paid.Amount)
            .OrderBy(t => Math.Abs(clock.ToLocalDate(t.OccurredAtUtc).DayNumber - day.DayNumber))
            .FirstOrDefault();
    }

    /// <summary>
    /// Logs a payment out of the bank as paid into <paramref name="cardId"/>: both sides are filed as
    /// Transfer, and the card's balance goes up by the amount.
    /// </summary>
    public async Task<Transaction> LogAsync(int paidId, int cardId, CancellationToken ct = default)
    {
        var paid = await db.Transactions.Include(t => t.Account).FirstOrDefaultAsync(t => t.Id == paidId, ct)
                   ?? throw new CardPaymentException("That entry no longer exists.");
        if (paid.IsIncome) throw new CardPaymentException("Only money going out can pay a card bill.");
        if (paid.Account?.Kind == AccountKind.CreditCard)
            throw new CardPaymentException("This entry is on a card. Open the payment on the bank account it came from.");
        var card = await db.Accounts.FirstOrDefaultAsync(a => a.Id == cardId && a.Kind == AccountKind.CreditCard, ct)
                   ?? throw new CardPaymentException("Pick one of your credit cards.");
        if (await OnCardAsync(paid, ct) is { } there)
            throw new CardPaymentException(
                $"Already on {there.Account?.Name}: {TransactionService.Money(there.Amount, there.Currency)} on {clock.ToLocalDate(there.OccurredAtUtc):d MMM}.");

        var transfer = await TransferIdAsync(ct);
        paid.CategoryId = transfer;
        paid.CategoryConfirmed = true;
        var onCard = CardSide(paid, card, transfer, TransactionSource.Manual);
        db.Transactions.Add(onCard);
        await db.SaveChangesAsync(ct);
        return onCard;
    }

    public async Task<int?> TransferIdAsync(CancellationToken ct = default) =>
        (await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Name == "Transfer", ct))?.Id;

    /// <summary>The money arriving on the card, dated the day it left the bank.</summary>
    public static Transaction CardSide(Transaction paid, Account card, int? transferId, TransactionSource source) => new()
    {
        OccurredAtUtc = paid.OccurredAtUtc,
        Amount = paid.Amount,
        Currency = paid.Currency,
        IsIncome = true,
        Merchant = $"Bill paid from {paid.Account?.Name ?? "the bank"}",
        Notes = paid.Merchant,
        Source = source,
        AccountId = card.Id,
        CategoryId = transferId,
        CategoryConfirmed = true
    };

    private static readonly (string Bank, string[] Words)[] Banks =
    [
        ("DBS", ["DBS", "DBSC", "POSB"]), // DBSC is how DBS/POSB name their own cards on a bill payment
        ("UOB", ["UOB"]),
        ("OCBC", ["OCBC"]),
        ("CITI", ["CITI", "CITIBANK"]),
        ("HSBC", ["HSBC"]),
        ("SCB", ["SCB", "STANCHART"]),
        ("AMEX", ["AMEX"]),
        ("MAYBANK", ["MAYBANK"])
    ];

    /// <summary>
    /// Which card a bill payment's bank text pays, or null when it can't tell. The card number's last
    /// four digits in the card's name or card names decide first ("BILL DBSC-4119…3939" → "yuu 3939");
    /// then the bank, when exactly one card is from it ("DBSC" → the one DBS card); then, when the
    /// text names no bank and there's only one card in the app, that card.
    /// </summary>
    public static Account? GuessCard(string description, IEnumerable<Account> accounts)
    {
        var cards = accounts.Where(a => a.Kind == AccountKind.CreditCard && !a.IsArchived).ToList();
        if (cards.Count == 0) return null;

        var text = MerchantText.Normalize(description);
        var numbers = Digits().Matches(text).Select(m => m.Value).Where(n => n.Length >= 12).ToList();
        var byNumber = cards.Where(c => Digits().Matches($"{c.Name} {c.CardNames}")
            .Any(d => d.Value.Length == 4 && numbers.Any(n => n.EndsWith(d.Value, StringComparison.Ordinal)))).ToList();
        if (byNumber.Count == 1) return byNumber[0];

        var banks = BanksIn(text);
        if (banks.Count == 1)
        {
            var fromBank = cards.Where(c => BanksIn(MerchantText.Normalize($"{c.Name} {c.CardNames}")).Contains(banks[0])).ToList();
            return fromBank.Count == 1 ? fromBank[0] : null;
        }
        // Names another bank's card (or several): could be a card that isn't in the app.
        return cards.Count == 1 && banks.Count == 0 ? cards[0] : null;
    }

    private static List<string> BanksIn(string normalized)
    {
        var words = normalized.Split(new[] { ' ', '-', ':', '/', ',', '.' }, StringSplitOptions.RemoveEmptyEntries);
        return Banks.Where(b => words.Any(w => b.Words.Contains(w))).Select(b => b.Bank).ToList();
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
