using System.Globalization;
using System.Text;
using ExcelDataReader;
using ExcelDataReader.Exceptions;

namespace Budget.Api.Services;

/// <summary>
/// Reads a transaction history downloaded as Excel (.xlsx, or the older .xls). Each sheet is turned
/// into rows of text and read exactly like a CSV, so column detection and day-first dates are the
/// same. The first sheet that has transactions is used.
/// </summary>
public static class ExcelStatementReader
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public const string PasswordProblem = "This Excel file is password-protected. Enter the password to open it.";
    public const string WrongPasswordProblem = "That password didn't open the Excel file. Check it and try again.";

    static ExcelStatementReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // .xls text

    public static ParsedStatement Read(byte[] file, string? password, bool positiveIsSpend, ColumnMap? overrides = null)
    {
        List<List<List<string>>> sheets;
        try
        {
            sheets = ReadSheets(file, password);
        }
        catch (InvalidPasswordException)
        {
            return Fail(string.IsNullOrEmpty(password) ? PasswordProblem : WrongPasswordProblem);
        }
        catch (HeaderException)
        {
            // Some banks' ".xls" is really a text or web page file with an Excel name.
            var text = Encoding.UTF8.GetString(file).TrimStart('﻿', ' ', '\r', '\n', '\t');
            if (text.StartsWith('<'))
                return Fail("This file is a web page saved with an Excel name. Open it in Excel and save it as .xlsx or CSV.");
            return StatementParser.Parse(text, positiveIsSpend, overrides);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Fail("Couldn't open that Excel file. Try downloading it from your bank again.");
        }

        ParsedStatement? firstProblem = null;
        foreach (var sheet in sheets.Where(s => s.Count > 0))
        {
            var parsed = StatementParser.ParseLines(sheet, positiveIsSpend, overrides);
            if (parsed.Problem is null) return parsed;
            // Prefer a problem that came with headings, so the person can point at the columns.
            if (firstProblem is null || (firstProblem.Headers.Count == 0 && parsed.Headers.Count > 0)) firstProblem = parsed;
        }
        return firstProblem ?? Fail("The file is empty.");
    }

    private static List<List<List<string>>> ReadSheets(byte[] file, string? password)
    {
        var config = new ExcelReaderConfiguration();
        if (!string.IsNullOrEmpty(password)) config.Password = password;
        using var stream = new MemoryStream(file, writable: false);
        using var reader = ExcelReaderFactory.CreateReader(stream, config);

        var sheets = new List<List<List<string>>>();
        do
        {
            var rows = new List<List<string>>();
            while (reader.Read())
            {
                var row = new List<string>(reader.FieldCount);
                for (var c = 0; c < reader.FieldCount; c++) row.Add(CellText(reader.GetValue(c)));
                if (row.Any(s => s.Trim().Length > 0)) rows.Add(row);
            }
            sheets.Add(rows);
        } while (reader.NextResult());
        return sheets;
    }

    /// <summary>
    /// Excel stores dates and amounts as numbers. Write them in forms the CSV reader can't misread:
    /// dates as yyyy-MM-dd (never month-first), amounts with a plain decimal point.
    /// </summary>
    public static string CellText(object? value) => value switch
    {
        null => "",
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        double n when Math.Abs(n) < 1e15 => ((decimal)n).ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static ParsedStatement Fail(string message) => new([], new ColumnMap(), [], 0, null, null, message);
}
