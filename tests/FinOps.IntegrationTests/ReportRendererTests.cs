using ClosedXML.Excel;
using FinOps.Application.Reports;
using FinOps.Infrastructure.Reporting;

namespace FinOps.IntegrationTests;

/// <summary>Renderer output checks (no database). Excel files are opened and inspected cell by cell.</summary>
public sealed class ReportRendererTests
{
    private static ReportTable Sample(int rows = 3, string description = "Taxi") => new(
        "expense-summary", "Department expense summary", "Test report",
        [new("department", "Department", ColumnKind.Text), new("description", "Description", ColumnKind.Text),
         new("count", "Expenses", ColumnKind.Integer, true), new("amount", "Amount", ColumnKind.Money, true),
         new("used", "Used %", ColumnKind.Percent), new("at", "When", ColumnKind.DateTime)],
        Enumerable.Range(1, rows).Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["department"] = $"Dept {i}", ["description"] = description, ["count"] = i, ["amount"] = 10.25m * i,
            ["used"] = 12.5m, ["at"] = new DateTimeOffset(2026, 11, 14, 9, 30, 0, TimeSpan.Zero)
        }).ToList(),
        [new("From", "2026-11-01"), new("To", "2026-11-14")], "All departments", "Tester",
        new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Excel_keeps_types_formats_and_formula_totals()
    {
        var bytes = new ExcelReportRenderer().Render(Sample());
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var sheet = workbook.Worksheet(1);

        Assert.Equal("Department expense summary", sheet.Cell(1, 1).GetString());
        Assert.Equal("Department", sheet.Cell(4, 1).GetString());              // header row
        Assert.Equal(10.25m, sheet.Cell(5, 4).GetValue<decimal>());             // money stays a number
        Assert.Equal("$#,##0.00", sheet.Cell(5, 4).Style.NumberFormat.Format);
        Assert.Equal("SUM(D5:D7)", sheet.Cell(8, 4).FormulaA1);                 // totals row
        Assert.Equal(61.5m, sheet.Cell(8, 4).GetValue<decimal>());
        Assert.Equal(new DateTime(2026, 11, 14, 9, 30, 0), sheet.Cell(5, 6).GetDateTime());
        Assert.Equal(4, sheet.SheetView.SplitRow);                              // header frozen
    }

    [Theory]
    [InlineData("=HYPERLINK(\"https://example.invalid\",\"click\")")]
    [InlineData("+SUM(1,2)")]
    [InlineData("@cmd")]
    public void User_text_is_never_turned_into_a_formula(string description)
    {
        var bytes = new ExcelReportRenderer().Render(Sample(1, description));
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var cell = workbook.Worksheet(1).Cell(5, 2);
        Assert.False(cell.HasFormula);
        Assert.Equal(description, cell.GetString());
    }

    [Fact]
    public void Pdf_is_a_pdf_and_handles_empty_and_multi_page_reports()
    {
        var renderer = new PdfReportRenderer();
        var small = renderer.Render(Sample());
        var empty = renderer.Render(Sample(rows: 0));
        var large = renderer.Render(Sample(rows: 400));

        foreach (var pdf in new[] { small, empty, large })
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.True(large.Length > small.Length);
    }

    [Fact]
    public void Shared_formatting_matches_between_formats()
    {
        var money = new ReportColumn("a", "A", ColumnKind.Money);
        Assert.Equal("$1,234.50", ReportFormat.Text(money, 1234.5m));
        Assert.Equal("12.5%", ReportFormat.Text(new ReportColumn("p", "P", ColumnKind.Percent), 12.5m));
        Assert.Equal("", ReportFormat.Text(money, null));
    }
}
