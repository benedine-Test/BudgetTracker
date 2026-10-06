using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Budget.Api.Services;

/// <summary>One money movement from a bank statement. Amount is always positive; IsCredit = money in.</summary>
public record StatementRow(int Line, DateOnly Date, string Description, decimal Amount, bool IsCredit, decimal? Balance);

/// <summary>
/// Which statement columns hold what. Detected from the header row; any of them can be
/// overridden by name when a bank labels things oddly.
/// </summary>
public record ColumnMap(
    string? Date = null,
    IReadOnlyList<string>? Description = null,
    string? Debit = null,
    string? Credit = null,
    string? Amount = null,
    string? Direction = null,
    string? Balance = null);

public record ParsedStatement(
    IReadOnlyList<string> Headers,
    ColumnMap Columns,
    IReadOnlyList<StatementRow> Rows,
    int SkippedLines,
    decimal? ClosingBalance,
    DateOnly? ClosingDate,
    string? Problem);

/// <summary>
/// Reads the CSV "transaction history" download most banks offer (DBS/POSB, OCBC, UOB, Citi,
/// HSBC…). Every bank lays it out differently, so nothing is hard-coded per bank: it looks for
/// the header row, works out which column is the date / description / money out / money in /
/// balance, and reads dates day-first, as Singapore banks write them.
/// </summary>
public static partial class StatementParser
{
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <param name="positiveIsSpend">
    /// For a single signed "Amount" column: card statements usually show a purchase as a positive
    /// number, bank accounts as negative. Ignored when the file has separate debit/credit columns
    /// or marks each row DR/CR.
    /// </param>
    public static ParsedStatement Parse(string text, bool positiveIsSpend, ColumnMap? overrides = null)
    {
        text = text.TrimStart('﻿');
        if (text.StartsWith("PK", StringComparison.Ordinal) || text.StartsWith("ÐÏ", StringComparison.Ordinal))
            return Fail("This is an Excel file. Open it and save it as CSV, or download the CSV version from your bank.");
        if (text.StartsWith("%PDF", StringComparison.Ordinal))
            return Fail("This is a PDF. PDF statements can't be read yet — download the transaction history as CSV from your bank instead.");

        var lines = ReadCsv(text, DetectDelimiter(text));
        if (lines.Count == 0) return Fail("The file is empty.");

        var headerIndex = FindHeader(lines, overrides);
        if (headerIndex < 0)
            return Fail("Couldn't find the column headings (a date column and an amount, debit or credit column). " +
                        "Check it's the transaction history CSV, not a summary.");

        var headers = lines[headerIndex].Select(h => h.Trim()).ToList();
        var dataLines = lines.Skip(headerIndex + 1).ToList();
        var map = DetectColumns(headers, dataLines, overrides);

        if (map.Date is null) return Fail("Couldn't tell which column is the date.", headers, map);
        if (map.Amount is null && map.Debit is null && map.Credit is null)
            return Fail("Couldn't tell which column has the amounts.", headers, map);

        int Col(string? name) => name is null ? -1 : headers.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
        var dateCol = Col(map.Date);
        var descCols = (map.Description ?? []).Select(Col).Where(i => i >= 0).ToList();
        var debitCol = Col(map.Debit);
        var creditCol = Col(map.Credit);
        var amountCol = Col(map.Amount);
        var dirCol = Col(map.Direction);
        var balanceCol = Col(map.Balance);

        var rows = new List<StatementRow>();
        var skipped = 0;
        for (var i = 0; i < dataLines.Count; i++)
        {
            var cells = dataLines[i];
            string Cell(int c) => c >= 0 && c < cells.Count ? cells[c].Trim() : "";
            if (cells.All(string.IsNullOrWhiteSpace)) continue;

            // Summary lines ("Balance brought forward", totals) have no date: not a transaction.
            if (!TryParseDate(Cell(dateCol), out var date)) { skipped++; continue; }

            decimal amount;
            bool isCredit;
            if (debitCol >= 0 || creditCol >= 0)
            {
                var hasOut = TryParseMoney(Cell(debitCol), out var outAmt, out _) && outAmt != 0;
                var hasIn = TryParseMoney(Cell(creditCol), out var inAmt, out _) && inAmt != 0;
                if (hasOut) (amount, isCredit) = (Math.Abs(outAmt), false);
                else if (hasIn) (amount, isCredit) = (Math.Abs(inAmt), true);
                else { skipped++; continue; }
            }
            else
            {
                if (!TryParseMoney(Cell(amountCol), out var signed, out var marker) || signed == 0) { skipped++; continue; }
                amount = Math.Abs(signed);
                var dir = Cell(dirCol).ToUpperInvariant();
                if (dir.StartsWith('C')) isCredit = true;
                else if (dir.StartsWith('D')) isCredit = false;
                else if (marker is not null) isCredit = marker == "CR";
                else isCredit = positiveIsSpend ? signed < 0 : signed > 0;
            }

            var description = string.Join(' ', descCols.Select(Cell).Where(s => s.Length > 0));
            description = Spaces().Replace(description, " ").Trim();
            if (description.Length == 0) description = isCredit ? "Money in" : "Money out";
            if (description.Length > 200) description = description[..200];

            decimal? balance = TryParseMoney(Cell(balanceCol), out var bal, out _) ? bal : null;
            rows.Add(new StatementRow(headerIndex + i + 2, date, description, Math.Round(amount, 2), isCredit, balance));
        }

        if (rows.Count == 0)
            return Fail("Found the headings but no transactions under them. Check the dates and amounts are in the file.", headers, map);

        // Closing balance: the last row in time. Banks list newest-first or oldest-first.
        var newestFirst = rows[0].Date > rows[^1].Date;
        var latest = newestFirst ? rows[0] : rows[^1];
        var withBalance = (newestFirst ? rows : Enumerable.Reverse(rows)).FirstOrDefault(r => r.Balance is not null && r.Date == latest.Date);

        return new ParsedStatement(headers, map, rows, skipped, withBalance?.Balance, withBalance?.Date, null);

        static ParsedStatement Fail(string message, IReadOnlyList<string>? headers = null, ColumnMap? map = null) =>
            new(headers ?? [], map ?? new ColumnMap(), [], 0, null, null, message);
    }

    // ---- Columns ----

    private static string Key(string header) => Regex.Replace(header.ToLowerInvariant(), "[^a-z0-9/]+", " ").Trim();

    private static bool IsDateHeader(string k) => k.Contains("date");
    private static bool IsDebitHeader(string k) =>
        !IsDirectionHeader(k) && (k.Contains("debit") || k.Contains("withdraw") || k.Contains("money out") || k.Contains("paid out") || k == "dr");
    private static bool IsCreditHeader(string k) =>
        !IsDirectionHeader(k) && (k.Contains("credit") || k.Contains("deposit") || k.Contains("money in") || k.Contains("paid in") || k == "cr");
    private static bool IsDirectionHeader(string k) =>
        k is "dr/cr" or "cr/dr" or "debit/credit" or "credit/debit" or "d/c" or "c/d" or "type" or "transaction type";
    private static bool IsAmountHeader(string k) => k.Contains("amount") && !IsDebitHeader(k) && !IsCreditHeader(k);
    private static bool IsBalanceHeader(string k) => k.Contains("balance");
    private static bool IsDescriptionHeader(string k) =>
        !IsDateHeader(k) && !IsAmountHeader(k) &&
        (k.Contains("desc") || k.Contains("detail") || k.Contains("narrat") || k.Contains("particular") ||
         k.Contains("merchant") || k.Contains("payee") || k.Contains("ref") || k.Contains("remark") ||
         k.Contains("memo") || k == "transaction" || k == "transactions");

    private static int FindHeader(List<List<string>> lines, ColumnMap? overrides)
    {
        for (var i = 0; i < Math.Min(lines.Count, 40); i++)
        {
            var keys = lines[i].Select(Key).ToList();
            if (overrides?.Date is { } d && lines[i].Any(c => c.Trim().Equals(d, StringComparison.OrdinalIgnoreCase)))
                return i;
            if (keys.Any(IsDateHeader) &&
                keys.Any(k => IsAmountHeader(k) || IsDebitHeader(k) || IsCreditHeader(k)))
                return i;
        }
        return -1;
    }

    private static ColumnMap DetectColumns(List<string> headers, List<List<string>> data, ColumnMap? o)
    {
        var keys = headers.Select(Key).ToList();
        string? First(Func<string, bool> test) { var i = keys.FindIndex(k => test(k)); return i < 0 ? null : headers[i]; }

        // Prefer the transaction date over posting/value dates: it's when you actually paid.
        var date = First(k => IsDateHeader(k) && (k.Contains("transaction") || k.Contains("trans") || k.Contains("txn")))
                   ?? First(k => k == "date") ?? First(IsDateHeader);

        // A column whose values are all DR/CR is the direction marker, whatever it's called.
        string? direction = First(IsDirectionHeader);
        for (var c = 0; c < headers.Count && direction is null; c++)
        {
            var values = data.Select(r => c < r.Count ? r[c].Trim().ToUpperInvariant() : "").Where(v => v.Length > 0).Take(50).ToList();
            if (values.Count >= 2 && values.All(v => v is "DR" or "CR" or "D" or "C" or "DEBIT" or "CREDIT"))
                direction = headers[c];
        }

        var description = keys
            .Select((k, i) => (k, i))
            .Where(x => IsDescriptionHeader(x.k) && headers[x.i] != direction)
            .Select(x => x.i)
            .ToList();
        // Drop short code columns (e.g. "POS", "ICT") when there's a real description beside them.
        var meaningful = description.Where(i => data.Any(r => i < r.Count && r[i].Trim().Length > 4)).ToList();
        if (meaningful.Count > 0) description = meaningful;

        var detected = new ColumnMap(
            date,
            description.Select(i => headers[i]).ToList(),
            First(IsDebitHeader),
            First(IsCreditHeader),
            First(IsAmountHeader),
            direction,
            First(IsBalanceHeader));

        if (o is null) return detected;
        return new ColumnMap(
            Pick(o.Date) ?? detected.Date,
            o.Description is { Count: > 0 } ? o.Description.Select(Pick).OfType<string>().ToList() : detected.Description,
            Pick(o.Debit) ?? detected.Debit,
            Pick(o.Credit) ?? detected.Credit,
            Pick(o.Amount) ?? detected.Amount,
            Pick(o.Direction) ?? detected.Direction,
            Pick(o.Balance) ?? detected.Balance);

        // Only accept overrides that name a real column.
        string? Pick(string? name) => name is null ? null : headers.FirstOrDefault(h => h.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    // ---- Values ----

    private static readonly string[] DateFormats =
    [
        "d MMM yyyy", "d MMM yy", "d-MMM-yyyy", "d-MMM-yy", "d/MMM/yyyy", "d MMMM yyyy", "d-MMMM-yyyy",
        "d/M/yyyy", "d/M/yy", "d-M-yyyy", "d-M-yy", "d.M.yyyy",
        "yyyy-M-d", "yyyy/M/d", "yyyyMMdd",
        "MMM d yyyy", "MMM d, yyyy", "MMMM d, yyyy",
    ];

    /// <summary>Day-first, as Singapore banks write dates. "01/02/2026" is 1 February.</summary>
    public static bool TryParseDate(string raw, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim();
        var t = text.IndexOf('T');
        if (t >= 8 && char.IsDigit(text[t - 1])) text = text[..t];
        text = TimePart().Replace(text, "").Trim();
        text = Spaces().Replace(text, " ");
        if (!DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var dt)) return false;
        date = DateOnly.FromDateTime(dt);
        return date.Year is > 1990 and < 2200;
    }

    /// <summary>
    /// "1,234.56", "-12.50", "(12.50)", "S$ 12.50", "12.50 CR", "12.50DR". Marker is "CR"/"DR" when present.
    /// Blank is false; "0.00" is true with 0.
    /// </summary>
    public static bool TryParseMoney(string raw, out decimal value, out string? marker)
    {
        value = 0;
        marker = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim().ToUpperInvariant();

        if (text.EndsWith("CR", StringComparison.Ordinal)) marker = "CR";
        else if (text.EndsWith("DR", StringComparison.Ordinal)) marker = "DR";

        var negative = text.StartsWith('(') && text.EndsWith(')') || text.Contains('-') || text.Contains('−');
        var digits = MoneyChars().Replace(text, "");
        if (digits.Length == 0 || !digits.Any(char.IsDigit)) return false;
        if (!decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)) return false;
        value = negative ? -v : v;
        return true;
    }

    // ---- CSV ----

    private static char DetectDelimiter(string text)
    {
        var sample = text.Split('\n').Take(40).ToList();
        return new[] { ',', ';', '\t' }
            .OrderByDescending(d => sample.Count(l => l.Count(c => c == d) >= 2))
            .ThenByDescending(d => sample.Sum(l => l.Count(c => c == d)))
            .First();
    }

    /// <summary>RFC 4180-ish: quoted fields, doubled quotes, line breaks inside quotes.</summary>
    public static List<List<string>> ReadCsv(string text, char delimiter = ',')
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(c);
                continue;
            }

            if (c == '"' && cell.ToString().Trim().Length == 0) { cell.Clear(); quoted = true; }
            else if (c == delimiter) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString());
                cell.Clear();
                if (row.Any(s => s.Trim().Length > 0)) rows.Add(row);
                row = [];
            }
            else cell.Append(c);
        }
        row.Add(cell.ToString());
        if (row.Any(s => s.Trim().Length > 0)) rows.Add(row);
        return rows;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s*\d{1,2}:\d{2}(:\d{2})?(\.\d+)?\s*(AM|PM|am|pm)?\s*$")]
    private static partial Regex TimePart();

    [GeneratedRegex(@"[^0-9.]")]
    private static partial Regex MoneyChars();
}
