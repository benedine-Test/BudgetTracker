using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace Budget.Api.Services;

/// <summary>
/// Reads the transactions out of a bank or card PDF statement. Every bank lays its PDF out
/// differently, so instead of a template per bank it works from what all of them share:
/// a transaction is a line that starts with a date and ends with amounts, under column
/// headings like Withdrawal / Deposit / Balance. Where there's a running balance, every line
/// is checked against it, so a misread line is reported instead of quietly going in wrong.
/// Scanned (image-only) PDFs have no text to read and are refused.
/// </summary>
public static partial class PdfStatementReader
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public const string PasswordProblem = "This PDF is password-protected. Enter the password your bank gave you for its statements.";
    public const string WrongPasswordProblem = "That password didn't open the PDF. Check it and try again.";

    /// <summary>A word on the page and where it sits (x grows to the right).</summary>
    public record Word(string Text, double Left, double Right);

    /// <summary>One printed line, words left to right.</summary>
    public record Line(int Page, IReadOnlyList<Word> Words)
    {
        public string Text => string.Join(' ', Words.Select(w => w.Text));
    }

    public static ParsedStatement Read(byte[] pdf, string? password, bool positiveIsSpend)
    {
        List<Line> lines;
        try
        {
            var options = new ParsingOptions { UseLenientParsing = true };
            if (!string.IsNullOrEmpty(password)) options.Password = password;
            using var doc = PdfDocument.Open(pdf, options);
            lines = [];
            foreach (var page in doc.GetPages())
                lines.AddRange(GroupLines(page.Number,
                    page.GetWords().Select(w => (w.Text, w.BoundingBox.Left, w.BoundingBox.Right, w.BoundingBox.Bottom))));
        }
        catch (PdfDocumentEncryptedException)
        {
            return Fail(string.IsNullOrEmpty(password) ? PasswordProblem : WrongPasswordProblem);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Fail("Couldn't open that PDF. Try downloading it from your bank again.");
        }

        if (lines.Count == 0 || lines.Sum(l => l.Words.Count) < 10)
            return Fail("This PDF has no readable text — it's probably a scan. Download the statement from your bank's website instead of scanning it.");

        return ReadLines(lines, positiveIsSpend);
    }

    /// <summary>Words that sit on the same baseline (within a couple of points) form one line.</summary>
    public static IEnumerable<Line> GroupLines(int page, IEnumerable<(string Text, double Left, double Right, double Bottom)> words)
    {
        const double tolerance = 2.5;
        var lines = new List<(double Y, List<Word> Words)>();
        foreach (var w in words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderByDescending(w => w.Bottom))
        {
            var line = lines.Count > 0 && Math.Abs(lines[^1].Y - w.Bottom) <= tolerance ? lines[^1] : default;
            if (line.Words is null)
            {
                line = (w.Bottom, []);
                lines.Add(line);
            }
            line.Words.Add(new Word(w.Text.Trim(), w.Left, w.Right));
        }
        return lines.Select(l => new Line(page, l.Words.OrderBy(w => w.Left).ToList()));
    }

    private enum Col { Out, In, Amount, Balance }

    /// <summary>The text-level work, separate from PDF opening so it can be tested with plain lines.</summary>
    public static ParsedStatement ReadLines(IReadOnlyList<Line> lines, bool positiveIsSpend)
    {
        var statementDate = FindStatementDate(lines);
        var rows = new List<StatementRow>();
        var balanceMismatches = 0;
        decimal? previousBalance = null;
        Dictionary<Col, double>? columns = null;
        var usedColumns = false;
        var usedChain = false;
        (int Row, double DescLeft, double DescRight, int Extra)? open = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (FindColumns(line) is { } found)
            {
                columns = found;
                open = null;
                continue;
            }

            var words = line.Words;
            var upper = line.Text.ToUpperInvariant();

            // "Balance brought forward 1,234.56" seeds the running balance; it isn't a transaction.
            if (OpeningWords().IsMatch(upper))
            {
                if (TrailingMoney(words) is { Count: > 0 } m) previousBalance = m[^1].Value;
                open = null;
                continue;
            }
            if (SummaryWords().IsMatch(upper)) { open = null; continue; }

            var dateWords = LeadingDate(words, statementDate, out var date);
            var money = TrailingMoney(words);
            if (dateWords == 0 || money.Count == 0)
            {
                // A description that wraps onto the next line(s): keep it with the transaction above,
                // as long as it sits in the description column and isn't a page header/footer.
                if (open is { } o && dateWords == 0 && money.Count == 0 && o.Extra < 2
                    && words[0].Left >= o.DescLeft - 3 && words[^1].Right <= o.DescRight + 3)
                {
                    var r = rows[o.Row];
                    var more = string.Join(' ', words.Select(w => w.Text));
                    rows[o.Row] = r with { Description = Trim200($"{r.Description} {more}") };
                    open = o with { Extra = o.Extra + 1 };
                }
                else open = null;
                continue;
            }

            // A second date right after the first (card statements: transaction date, then posting date).
            var skip = dateWords + LeadingDate(words.Skip(dateWords).ToList(), statementDate, out _);
            var descWords = words.Skip(skip).Take(words.Count - skip - money.Sum(m => m.Words)).ToList();
            var description = Trim200(string.Join(' ', descWords.Select(w => w.Text)));

            decimal amount;
            bool isCredit;
            decimal? balance = null;
            var assigned = columns is null ? null : AssignColumns(money, columns);
            if (assigned is not null && (assigned.ContainsKey(Col.Out) || assigned.ContainsKey(Col.In) || assigned.ContainsKey(Col.Amount)))
            {
                usedColumns = true;
                balance = assigned.TryGetValue(Col.Balance, out var b) ? b.Value : null;
                if (assigned.TryGetValue(Col.Out, out var o)) (amount, isCredit) = (Math.Abs(o.Value), false);
                else if (assigned.TryGetValue(Col.In, out var inn)) (amount, isCredit) = (Math.Abs(inn.Value), true);
                else
                {
                    var a = assigned[Col.Amount];
                    amount = Math.Abs(a.Value);
                    isCredit = Direction(a, positiveIsSpend);
                }
            }
            else if (money.Count >= 2 && previousBalance is not null)
            {
                // No headings found: the last figure is the balance, the one before it the amount,
                // and whether the balance went up or down says which way the money moved.
                usedChain = true;
                var a = money[^2];
                balance = money[^1].Value;
                amount = Math.Abs(a.Value);
                isCredit = balance.Value - previousBalance.Value == amount
                           || (balance.Value - previousBalance.Value != -amount && Direction(a, positiveIsSpend));
            }
            else
            {
                // Card statements: one amount, "CR" on payments and refunds.
                var a = money[^1];
                amount = Math.Abs(a.Value);
                isCredit = Direction(a, positiveIsSpend);
            }

            if (amount == 0) { open = null; continue; }

            if (balance is { } bal && previousBalance is { } prev && prev + (isCredit ? amount : -amount) != bal)
                balanceMismatches++;
            if (balance is not null) previousBalance = balance;

            if (description.Length == 0) description = isCredit ? "Money in" : "Money out";
            rows.Add(new StatementRow(i + 1, date, description, Math.Round(amount, 2), isCredit, balance));
            var descLeft = descWords.Count > 0 ? descWords[0].Left : words[skip - 1].Right;
            var descRight = money.Count > 0 ? words[words.Count - money.Sum(m => m.Words)].Left : descLeft + 200;
            open = (rows.Count - 1, descLeft, descRight, 0);
        }

        if (rows.Count == 0)
            return Fail("Couldn't find any transactions in this PDF. If it's a summary or a scan, download the detailed statement instead.");

        var newestFirst = rows[0].Date > rows[^1].Date;
        var latest = newestFirst ? rows[0] : rows[^1];
        var closing = (newestFirst ? rows : Enumerable.Reverse(rows)).FirstOrDefault(r => r.Balance is not null && r.Date == latest.Date);

        string? warning = null;
        if (balanceMismatches > 0)
            warning = $"{balanceMismatches} {(balanceMismatches == 1 ? "line doesn't" : "lines don't")} add up with the running balance on the statement. " +
                      "Check the amounts and money in/out before adding.";
        else if (!usedColumns && !usedChain)
            warning = "Couldn't see a balance column, so money in is only spotted where the statement marks it CR. Check the + and − before adding.";

        var map = new ColumnMap(Amount: usedColumns || usedChain ? null : "Amount");
        return new ParsedStatement([], map, rows, 0, closing?.Balance, closing?.Date, null) { Warning = warning };

        static string Trim200(string s) => s.Length > 200 ? s[..200] : s;
    }

    private static ParsedStatement Fail(string message) => new([], new ColumnMap(), [], 0, null, null, message);

    private static bool Direction(Money m, bool positiveIsSpend) =>
        m.Marker is not null ? m.Marker == "CR" : positiveIsSpend ? m.Value < 0 : m.Value > 0;

    // ---- Column headings ----

    private static Dictionary<Col, double>? FindColumns(Line line)
    {
        var cols = new Dictionary<Col, double>();
        var hasDate = false;
        foreach (var w in line.Words)
        {
            var t = w.Text.ToUpperInvariant().Trim('(', ')', ':');
            if (t.StartsWith("DATE")) hasDate = true;
            else if (t.StartsWith("WITHDRAW") || t is "DEBIT" or "DEBITS" or "DR" || t.StartsWith("PAYMENTS")) cols.TryAdd(Col.Out, w.Right);
            else if (t.StartsWith("DEPOSIT") || t is "CREDIT" or "CREDITS" or "CR" || t.StartsWith("RECEIPTS")) cols.TryAdd(Col.In, w.Right);
            else if (t.StartsWith("BALANCE")) cols.TryAdd(Col.Balance, w.Right);
            else if (t.StartsWith("AMOUNT")) cols.TryAdd(Col.Amount, w.Right);
        }
        var moneyCols = cols.Keys.Count(k => k != Col.Balance);
        return hasDate && moneyCols >= 1 && line.Words.Count <= 14 ? cols : null;
    }

    /// <summary>Each figure goes to the heading whose right edge it lines up with (amounts are right-aligned).</summary>
    private static Dictionary<Col, Money>? AssignColumns(IReadOnlyList<Money> money, Dictionary<Col, double> cols)
    {
        var result = new Dictionary<Col, Money>();
        foreach (var m in money)
        {
            var best = cols.OrderBy(c => Math.Abs(c.Value - m.Right)).First();
            // A figure far from every heading is probably part of the description (e.g. "USD 15.99").
            if (Math.Abs(best.Value - m.Right) > 60) continue;
            result[best.Key] = m;
        }
        return result.Count == 0 ? null : result;
    }

    // ---- Money ----

    public record Money(decimal Value, string? Marker, double Right, int Words);

    /// <summary>The amounts at the end of a line, left to right. "CR"/"DR" can be its own word.</summary>
    private static List<Money> TrailingMoney(IReadOnlyList<Word> words)
    {
        var found = new List<Money>();
        var i = words.Count - 1;
        while (i >= 0 && found.Count < 4)
        {
            string? marker = null;
            var used = 0;
            var text = words[i].Text;
            if (text.ToUpperInvariant() is "CR" or "DR" && i > 0)
            {
                marker = text.ToUpperInvariant();
                used = 1;
                i--;
                text = words[i].Text;
            }
            if (!MoneyToken().IsMatch(text)) { i += used; break; }
            StatementParser.TryParseMoney(text, out var value, out var inline);
            found.Insert(0, new Money(value, marker ?? inline, words[i].Right, used + 1));
            i--;
        }
        return found;
    }

    // ---- Dates ----

    /// <summary>
    /// How many words at the start of the line make up a date (0 = none). Dates without a year
    /// ("01 OCT") take the statement's year, stepping back one for December lines on a January statement.
    /// </summary>
    private static int LeadingDate(IReadOnlyList<Word> words, DateOnly? statementDate, out DateOnly date)
    {
        date = default;
        for (var n = Math.Min(3, words.Count); n >= 1; n--)
        {
            var text = string.Join(' ', words.Take(n).Select(w => w.Text));
            // "01 OCT 03 OCT" must not read as 1 Oct 2003: a full date has to be near the statement's.
            if (StatementParser.TryParseDate(text, out date)
                && (statementDate is not { } near || (date <= near.AddDays(31) && date >= near.AddDays(-400))))
                return n;
            if (DayMonth(text) is { } dm && statementDate is { } sd)
            {
                var year = sd.Year;
                if (!DateOnly.TryParse($"{year}-{dm.Month:00}-{dm.Day:00}", CultureInfo.InvariantCulture, out date)) continue;
                if (date > sd.AddDays(31)) date = date.AddYears(-1);
                return n;
            }
        }
        return 0;
    }

    private static (int Day, int Month)? DayMonth(string text)
    {
        var m = DayMonthName().Match(text);
        if (m.Success && DateTime.TryParseExact(m.Groups[2].Value[..3], "MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var mon))
            return (int.Parse(m.Groups[1].Value), mon.Month);
        m = DayMonthNumber().Match(text);
        if (m.Success && int.Parse(m.Groups[2].Value) is >= 1 and <= 12 and var month)
            return (int.Parse(m.Groups[1].Value), month);
        return null;
    }

    /// <summary>
    /// The statement date: the latest full date with a 4-digit year, preferring lines that say
    /// "statement" or "as at". Two-digit years are ignored here: "28 DEC 30 DEC" isn't 2030.
    /// </summary>
    private static DateOnly? FindStatementDate(IEnumerable<Line> lines)
    {
        DateOnly? latest = null, labelled = null;
        foreach (var line in lines)
        {
            var w = line.Words;
            var isLabel = StatementLabel().IsMatch(line.Text.ToUpperInvariant());
            for (var i = 0; i < w.Count; i++)
                for (var n = 1; n <= 3 && i + n <= w.Count; n++)
                {
                    var text = string.Join(' ', w.Skip(i).Take(n).Select(x => x.Text));
                    if (!FourDigitYear().IsMatch(text) || !StatementParser.TryParseDate(text, out var d)) continue;
                    if (latest is null || d > latest) latest = d;
                    if (isLabel && (labelled is null || d > labelled)) labelled = d;
                }
        }
        return labelled ?? latest;
    }

    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex FourDigitYear();

    [GeneratedRegex(@"STATEMENT|AS AT|AS OF|PERIOD")]
    private static partial Regex StatementLabel();

    [GeneratedRegex(@"^\(?-?(S\$|\$)?-?\d{1,3}(,\d{3})*(\.\d{2})\)?-?(CR|DR)?$|^\(?-?\d+\.\d{2}\)?-?(CR|DR)?$", RegexOptions.IgnoreCase)]
    private static partial Regex MoneyToken();

    [GeneratedRegex(@"^(\d{1,2})[ \-/]?([A-Za-z]{3,9})$")]
    private static partial Regex DayMonthName();

    [GeneratedRegex(@"^(\d{1,2})/(\d{1,2})$")]
    private static partial Regex DayMonthNumber();

    [GeneratedRegex(@"BALANCE (B/F|BROUGHT FORWARD|BROUGHT FWD)|OPENING BALANCE|PREVIOUS (STATEMENT )?BALANCE|BALANCE FROM PREVIOUS")]
    private static partial Regex OpeningWords();

    [GeneratedRegex(@"BALANCE (C/F|CARRIED FORWARD|CARRIED FWD)|CLOSING BALANCE|NEW BALANCE|\bSUB-?TOTAL\b|\bTOTAL\b|MINIMUM PAYMENT|CREDIT LIMIT")]
    private static partial Regex SummaryWords();
}
