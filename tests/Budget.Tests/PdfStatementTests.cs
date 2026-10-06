using Budget.Api.Services;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Budget.Tests;

public class PdfStatementTests
{
    /// <summary>Builds a PDF from (page, y, x, text, rightAligned) the way a bank's would lay it out.</summary>
    private static byte[] Pdf(params (int Page, double Y, double X, string Text, bool Right)[] items)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var page in items.GroupBy(i => i.Page).OrderBy(g => g.Key))
        {
            var p = builder.AddPage(PageSize.A4);
            foreach (var (_, y, x, text, right) in page)
            {
                var width = right ? p.MeasureText(text, 9, new PdfPoint(0, 0), font).Max(l => l.GlyphRectangle.Right) : 0;
                p.AddText(text, 9, new PdfPoint(right ? x - width : x, y), font);
            }
        }
        return builder.Build();
    }

    // Column right edges for the bank layout.
    private const double Withdrawal = 400, Deposit = 480, Balance = 560;

    private static (int, double, double, string, bool)[] BankHeader(int page, double y) =>
    [
        (page, y, 40, "Date", false), (page, y, 110, "Description", false),
        (page, y, Withdrawal - 60, "Withdrawal", false), (page, y, Deposit - 45, "Deposit", false), (page, y, Balance - 40, "Balance", false),
    ];

    [Fact]
    public void Reads_a_bank_statement_with_columns_wrapped_lines_and_two_pages()
    {
        var items = new List<(int, double, double, string, bool)>
        {
            (1, 800, 40, "DBS Multiplier Account  Statement period 01 Oct 2026 to 31 Oct 2026", false),
        };
        items.AddRange(BankHeader(1, 740));
        items.AddRange(
        [
            (1, 720, 110, "Balance Brought Forward", false), (1, 720, Balance, "5,000.00", true),
            (1, 700, 40, "01 Oct", false), (1, 700, 110, "NETS QR PAYMENT", false), (1, 700, Withdrawal, "7.80", true), (1, 700, Balance, "4,992.20", true),
            (1, 688, 110, "STARBUCKS JEWEL", false),
            (1, 670, 40, "02 Oct", false), (1, 670, 110, "SALARY ACME PTE LTD", false), (1, 670, Deposit, "4,200.00", true), (1, 670, Balance, "9,192.20", true),
            (1, 650, 40, "03 Oct", false), (1, 650, 110, "BILL PAYMENT DBS VISA CARD", false), (1, 650, Withdrawal, "800.00", true), (1, 650, Balance, "8,392.20", true),
            (1, 60, 260, "Page 1 of 2", false),
        ]);
        items.AddRange(BankHeader(2, 740));
        items.AddRange(
        [
            (2, 700, 40, "05 Oct", false), (2, 700, 110, "SP DIGITAL UTILITIES", false), (2, 700, Withdrawal, "120.00", true), (2, 700, Balance, "8,272.20", true),
            (2, 680, 110, "Balance Carried Forward", false), (2, 680, Balance, "8,272.20", true),
            (2, 660, 110, "Total", false), (2, 660, Withdrawal, "927.80", true), (2, 660, Deposit, "4,200.00", true),
            (2, 60, 260, "Page 2 of 2", false),
        ]);

        var p = PdfStatementReader.Read(Pdf([.. items]), null, positiveIsSpend: true);

        Assert.Null(p.Problem);
        Assert.Null(p.Warning);
        Assert.Equal(4, p.Rows.Count);

        Assert.Equal(new DateOnly(2026, 10, 1), p.Rows[0].Date);
        Assert.Equal("NETS QR PAYMENT STARBUCKS JEWEL", p.Rows[0].Description); // wrapped line joined, page footer not
        Assert.Equal(7.80m, p.Rows[0].Amount);
        Assert.False(p.Rows[0].IsCredit);

        Assert.True(p.Rows[1].IsCredit);
        Assert.Equal(4200m, p.Rows[1].Amount);
        Assert.False(p.Rows[2].IsCredit);
        Assert.Equal("SP DIGITAL UTILITIES", p.Rows[3].Description);

        Assert.Equal(8272.20m, p.ClosingBalance);
        Assert.Equal(new DateOnly(2026, 10, 5), p.ClosingDate);
    }

    [Fact]
    public void Reads_a_card_statement_with_two_dates_foreign_amounts_and_cr()
    {
        const double amount = 560;
        var pdf = Pdf(
            (1, 800, 40, "DBS Altitude Visa Card", false),
            (1, 785, 40, "Statement Date 05 Jan 2027", false),
            (1, 740, 40, "Date", false), (1, 740, 140, "Description", false), (1, 740, amount - 60, "Amount (S$)", false),
            (1, 720, 110, "PREVIOUS BALANCE", false), (1, 720, amount, "1,200.00", true),
            (1, 700, 40, "28 DEC", false), (1, 700, 85, "30 DEC", false), (1, 700, 140, "NETFLIX.COM", false), (1, 700, 400, "USD 15.99", false), (1, 700, amount, "21.30", true),
            (1, 680, 40, "02 JAN", false), (1, 680, 85, "02 JAN", false), (1, 680, 140, "PAYMENT - THANK YOU", false), (1, 680, amount, "1,200.00 CR", true),
            (1, 660, 40, "03 JAN", false), (1, 660, 85, "04 JAN", false), (1, 660, 140, "GRAB* RIDE", false), (1, 660, amount, "12.40", true),
            (1, 640, 110, "NEW BALANCE", false), (1, 640, amount, "33.70", true));

        var p = PdfStatementReader.Read(pdf, null, positiveIsSpend: true);

        Assert.Null(p.Problem);
        Assert.Equal(3, p.Rows.Count);

        Assert.Equal(new DateOnly(2026, 12, 28), p.Rows[0].Date); // December line on a January statement
        Assert.Equal(21.30m, p.Rows[0].Amount);                  // the SGD charge, not the USD figure
        Assert.Contains("NETFLIX.COM", p.Rows[0].Description);
        Assert.False(p.Rows[0].IsCredit);

        Assert.True(p.Rows[1].IsCredit);
        Assert.Equal(1200m, p.Rows[1].Amount);
        Assert.Equal(new DateOnly(2027, 1, 3), p.Rows[2].Date);
    }

    [Fact]
    public void Without_headings_the_running_balance_decides_in_or_out()
    {
        var pdf = Pdf(
            (1, 800, 40, "Statement as at 31 Oct 2026", false),
            (1, 720, 40, "OPENING BALANCE", false), (1, 720, 560, "1,000.00", true),
            (1, 700, 40, "01/10/2026", false), (1, 700, 120, "FAST TRANSFER JANE", false), (1, 700, 470, "250.00", true), (1, 700, 560, "1,250.00", true),
            (1, 680, 40, "02/10/2026", false), (1, 680, 120, "KOPITIAM", false), (1, 680, 470, "3.50", true), (1, 680, 560, "1,246.50", true));

        var p = PdfStatementReader.Read(pdf, null, positiveIsSpend: true);

        Assert.Null(p.Warning);
        Assert.True(p.Rows[0].IsCredit);
        Assert.False(p.Rows[1].IsCredit);
        Assert.Equal(1246.50m, p.ClosingBalance);
    }

    [Fact]
    public void Lines_that_dont_add_up_are_reported()
    {
        var items = new List<(int, double, double, string, bool)>();
        items.Add((1, 800, 40, "Statement 31 Oct 2026", false));
        items.AddRange(BankHeader(1, 740));
        items.AddRange(
        [
            (1, 720, 110, "Balance Brought Forward", false), (1, 720, Balance, "100.00", true),
            (1, 700, 40, "01 Oct", false), (1, 700, 110, "SHOP", false), (1, 700, Withdrawal, "10.00", true), (1, 700, Balance, "80.00", true),
        ]);

        var p = PdfStatementReader.Read(Pdf([.. items]), null, positiveIsSpend: true);
        Assert.True(p.Warning?.Contains("add up") == true, $"{p.Problem}|{p.Warning}|" + string.Join(";", p.Rows.Select(r => $"{r.Date} {r.Description} {r.Amount} {r.IsCredit} {r.Balance}")));
    }

    [Fact]
    public void Explains_a_pdf_with_no_text()
    {
        var p = PdfStatementReader.Read(Pdf((1, 800, 40, "Scan", false)), null, true);
        Assert.Contains("scan", p.Problem);
    }

    [Fact]
    public void Explains_a_file_that_is_not_a_pdf() =>
        Assert.NotNull(PdfStatementReader.Read("hello"u8.ToArray(), null, true).Problem);
}
