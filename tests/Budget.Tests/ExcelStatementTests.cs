using System.IO.Compression;
using System.Security;
using System.Text;
using Budget.Api.Services;

namespace Budget.Tests;

public class ExcelStatementTests
{
    /// <summary>A real date cell: Excel stores it as a day number with a date format.</summary>
    private sealed record XlDate(DateOnly Date);

    /// <summary>Builds a minimal .xlsx: each sheet is rows of string / number / XlDate / null cells.</summary>
    private static byte[] Xlsx(params object?[][][] sheets)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }

            var ids = Enumerable.Range(1, sheets.Length).ToList();
            Add("[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">""" +
                """<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>""" +
                """<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>""" +
                """<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""" +
                string.Concat(ids.Select(i => $"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""")) +
                "</Types>");
            Add("_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""" +
                """<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add("xl/workbook.xml",
                """<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>""" +
                string.Concat(ids.Select(i => $"""<sheet name="Sheet{i}" sheetId="{i}" r:id="rId{i}"/>""")) + "</sheets></workbook>");
            Add("xl/_rels/workbook.xml.rels",
                """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""" +
                string.Concat(ids.Select(i => $"""<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i}.xml"/>""")) +
                $"""<Relationship Id="rId{sheets.Length + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            // Style 1 = built-in format 14, a short date.
            Add("xl/styles.xml",
                """<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""" +
                """<fonts count="1"><font/></fonts><fills count="1"><fill/></fills><borders count="1"><border/></borders>""" +
                """<cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/></cellXfs></styleSheet>""");

            for (var s = 0; s < sheets.Length; s++)
            {
                var xml = new StringBuilder("""<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
                for (var r = 0; r < sheets[s].Length; r++)
                {
                    xml.Append($"<row r=\"{r + 1}\">");
                    for (var c = 0; c < sheets[s][r].Length; c++)
                    {
                        var at = $"{(char)('A' + c)}{r + 1}";
                        switch (sheets[s][r][c])
                        {
                            case null: break;
                            case string text: xml.Append($"<c r=\"{at}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(text)}</t></is></c>"); break;
                            case XlDate d: xml.Append($"<c r=\"{at}\" s=\"1\"><v>{d.Date.DayNumber - new DateOnly(1899, 12, 30).DayNumber}</v></c>"); break;
                            case IFormattable n: xml.Append($"<c r=\"{at}\"><v>{n.ToString(null, System.Globalization.CultureInfo.InvariantCulture)}</v></c>"); break;
                        }
                    }
                    xml.Append("</row>");
                }
                Add($"xl/worksheets/sheet{s + 1}.xml", xml.Append("</sheetData></worksheet>").ToString());
            }
        }
        return buffer.ToArray();
    }

    private static XlDate D(int year, int month, int day) => new(new DateOnly(year, month, day));

    [Fact]
    public void Reads_a_bank_sheet_with_real_date_cells_and_number_amounts()
    {
        var file = Xlsx([
            ["POSB Savings Account 123-45678-9"],
            ["Transaction history 01 Oct 2026 to 05 Oct 2026"],
            [],
            ["Transaction Date", "Reference", "Description", "Withdrawals (SGD)", "Deposits (SGD)", "Balance (SGD)"],
            [D(2026, 10, 1), "POS", "NTUC FAIRPRICE TAMPINES", 45.3, null, 1954.7],
            [D(2026, 10, 3), "ICT", "SALARY ACME PTE LTD", null, 4200, 6154.7],
            [D(2026, 10, 5), "POS", "BUS/MRT 123456", 3.86, null, 6150.84],
        ]);

        var parsed = ExcelStatementReader.Read(file, null, positiveIsSpend: false);

        Assert.Null(parsed.Problem);
        Assert.Equal(3, parsed.Rows.Count);
        Assert.Equal(new DateOnly(2026, 10, 3), parsed.Rows[1].Date);   // 3 Oct, not 10 Mar
        Assert.Equal(("NTUC FAIRPRICE TAMPINES", 45.3m, false), (parsed.Rows[0].Description, parsed.Rows[0].Amount, parsed.Rows[0].IsCredit));
        Assert.Equal((4200m, true), (parsed.Rows[1].Amount, parsed.Rows[1].IsCredit));
        Assert.Equal(6150.84m, parsed.ClosingBalance);
        Assert.Equal(new DateOnly(2026, 10, 5), parsed.ClosingDate);
    }

    [Fact]
    public void Skips_a_summary_sheet_and_reads_the_one_with_transactions()
    {
        var file = Xlsx(
            [["Card summary"], ["Credit limit", 10000], ["Outstanding", 812.4]],
            [
                ["Date", "Description", "Amount"],
                ["02/10/2026", "GRAB *GRABFOOD", "23.50"],
                ["04/10/2026", "PAYMENT - THANK YOU", "-500.00"],
            ]);

        var parsed = ExcelStatementReader.Read(file, null, positiveIsSpend: true);

        Assert.Null(parsed.Problem);
        Assert.Equal(2, parsed.Rows.Count);
        Assert.Equal(new DateOnly(2026, 10, 2), parsed.Rows[0].Date);   // text dates are day-first too
        Assert.False(parsed.Rows[0].IsCredit);
        Assert.True(parsed.Rows[1].IsCredit);
    }

    [Fact]
    public void Gives_back_the_headings_when_it_cannot_tell_the_amount_column()
    {
        var file = Xlsx([
            ["Date", "Details", "Value"],
            [D(2026, 10, 1), "SHOP", 5],
        ]);

        var parsed = ExcelStatementReader.Read(file, null, positiveIsSpend: true);
        Assert.NotNull(parsed.Problem);

        var mapped = ExcelStatementReader.Read(file, null, positiveIsSpend: true, new ColumnMap(Date: "Date", Amount: "Value"));
        Assert.Null(mapped.Problem);
        Assert.Equal(5m, Assert.Single(mapped.Rows).Amount);
    }

    [Fact]
    public void A_text_file_with_an_excel_name_is_read_as_text()
    {
        var file = Encoding.UTF8.GetBytes("Date\tDescription\tDebit\tCredit\n01/10/2026\tCOFFEE\t4.50\t\n");

        var parsed = ExcelStatementReader.Read(file, null, positiveIsSpend: false);

        Assert.Null(parsed.Problem);
        Assert.Equal(4.5m, Assert.Single(parsed.Rows).Amount);
    }

    [Fact]
    public void A_web_page_with_an_excel_name_says_what_to_do()
    {
        var parsed = ExcelStatementReader.Read(Encoding.UTF8.GetBytes("<html><table><tr><td>Date</td></tr></table></html>"), null, false);
        Assert.Contains("web page", parsed.Problem);
    }

    [Fact]
    public void A_broken_file_is_a_problem_not_a_crash()
    {
        var parsed = ExcelStatementReader.Read([(byte)'P', (byte)'K', 3, 4, 0, 0, 1, 2, 3], null, false);
        Assert.NotNull(parsed.Problem);
        Assert.Empty(parsed.Rows);
    }

    [Theory]
    [InlineData(0.30000000000000004, "0.3")]
    [InlineData(-1234.5, "-1234.5")]
    [InlineData(4200d, "4200")]
    public void Numbers_come_out_without_floating_point_noise(double value, string expected) =>
        Assert.Equal(expected, ExcelStatementReader.CellText(value));
}
